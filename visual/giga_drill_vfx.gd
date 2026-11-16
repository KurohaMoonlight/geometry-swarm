class_name GigaDrillVFX
extends Node2D

## GigaDrillVFX - Spektakel Visual Ultimate Super Giga Drill Break (XF-09 Ares Abyssal Overdrive)
## Mengatur:
## 1. Inti AP (Armor Piercing) 25% Defender Swarm: Pendaran kristal void-putih di dalam bor
## 2. Efek Charging Spektakuler: Abyssal Accretion Vortex (Hitam-Ungu-Putih), branching void lightning, apex corona
## 3. Triple-Plume XF-09 Ares Overdrive Exhaust: Semburan utama raksasa + 2 semburan sayap miring (V-Plume)
##    dengan lidah api FBM Putih Menyilaukan, Hitam Void, dan Ungu Tua Pekat!
## 4. Efek Core-Ramming Overdrive & Abyssal Supernova Detonation Spektakuler

const IonBeamShader = preload("res://visual/giga_drill_ion_beam.gdshader")

var swarm_sim: Node2D = null
var player_core: Node2D = null
var arena: Node2D = null

# Komponen GPU Shader Triple-Plume Exhaust (1 Utama Raksasa + 2 Wing Boosters ala XF-09 Ares)
var ion_beam_mesh: MeshInstance2D = null
var ion_beam_mat: ShaderMaterial = null
var wing_beam_left_mesh: MeshInstance2D = null
var wing_beam_left_mat: ShaderMaterial = null
var wing_beam_right_mesh: MeshInstance2D = null
var wing_beam_right_mat: ShaderMaterial = null

# Partikel loncatan plasma & void ember exhaust
var ion_particles: Array[Dictionary] = []
var max_particles: int = 160

# Riwayat ledakan pengorbanan swarm
var detonation_rings: Array[Dictionary] = []

# Riwayat pemecahan perisai hidung (Nose Cone AP Breach)
var breach_rings: Array[Dictionary] = []
var breach_shards: Array[Dictionary] = []

# Flash inti supernova saat meledak di Core
var supernova_flashes: Array[Dictionary] = []

func _ready() -> void:
	z_index = 6 # Tepat di atas MultiMesh swarm agar kontras Hitam Void & kilau HDR Putih-Ungu menyatu
	
	# 1. Mesh Semburan Utama Tengah
	ion_beam_mesh = MeshInstance2D.new()
	var quad: QuadMesh = QuadMesh.new()
	quad.size = Vector2(1.0, 1.0)
	ion_beam_mesh.mesh = quad
	ion_beam_mat = ShaderMaterial.new()
	ion_beam_mat.shader = IonBeamShader
	ion_beam_mesh.material = ion_beam_mat
	ion_beam_mesh.visible = false
	add_child(ion_beam_mesh)
	
	# 2. Mesh Semburan Sayap Kiri (Auxiliary Overdrive Plume)
	wing_beam_left_mesh = MeshInstance2D.new()
	wing_beam_left_mesh.mesh = quad
	wing_beam_left_mat = ShaderMaterial.new()
	wing_beam_left_mat.shader = IonBeamShader
	wing_beam_left_mesh.material = wing_beam_left_mat
	wing_beam_left_mesh.visible = false
	add_child(wing_beam_left_mesh)
	
	# 3. Mesh Semburan Sayap Kanan (Auxiliary Overdrive Plume)
	wing_beam_right_mesh = MeshInstance2D.new()
	wing_beam_right_mesh.mesh = quad
	wing_beam_right_mat = ShaderMaterial.new()
	wing_beam_right_mat.shader = IonBeamShader
	wing_beam_right_mesh.material = wing_beam_right_mat
	wing_beam_right_mesh.visible = false
	add_child(wing_beam_right_mesh)

func setup(p_arena: Node2D, p_sim: Node2D, p_core: Node2D) -> void:
	arena = p_arena
	swarm_sim = p_sim
	player_core = p_core

func _process(delta: float) -> void:
	if not swarm_sim or swarm_sim.get("PlayerHealth") == null:
		return
		
	var is_charging: bool = swarm_sim.get("IsChargingDrill") == true
	var is_active: bool = swarm_sim.get("IsGigaDrillActive") == true
	var total_units: int = int(swarm_sim.get("GigaDrillTotalUnits"))
	var swarm_scale: float = clampf(total_units / 45.0, 0.75, 4.5)
	
	# Update Shader Triple-Plume Abyssal Exhaust
	_update_ion_beam_shader(is_charging, is_active, swarm_scale)
	
	# Update partikel ion exhaust
	_update_ion_particles(delta, is_charging, is_active, swarm_scale)
	
	# Update cincin kejut ledakan
	_update_detonations(delta)
	
	# Update pecahan perisai AP & flash supernova
	_update_breach_fx(delta)
	_update_supernova_flashes(delta)
	
	if is_charging or is_active or ion_particles.size() > 0 or detonation_rings.size() > 0 or breach_rings.size() > 0 or breach_shards.size() > 0 or supernova_flashes.size() > 0:
		queue_redraw()

func _update_ion_beam_shader(is_charging: bool, is_active: bool, swarm_scale: float) -> void:
	if not ion_beam_mesh or not ion_beam_mat or not swarm_sim:
		return
		
	var charge_ratio: float = float(swarm_sim.get("DrillChargeRatio"))
	if not (is_active or (is_charging and charge_ratio > 0.20)):
		ion_beam_mesh.visible = false
		if wing_beam_left_mesh:
			wing_beam_left_mesh.visible = false
		if wing_beam_right_mesh:
			wing_beam_right_mesh.visible = false
		return
		
	var beam_power: float = 1.0 if is_active else clampf((charge_ratio - 0.20) / 0.80, 0.0, 1.0)
	var base_beam_len: float = lerpf(110.0, 340.0, beam_power) if is_charging else (450.0 + 150.0 * swarm_scale)
	var beam_len: float = base_beam_len
	var base_pos: Vector2 = swarm_sim.get("GigaDrillBasePosition")
	var aim_dir: Vector2 = swarm_sim.get("GigaDrillDirection")
	var drill_r: float = float(swarm_sim.get("GigaDrillRadius"))
	var spin_time: float = float(swarm_sim.get("GigaDrillSpinTime"))
	var exhaust_dir: Vector2 = -aim_dir
	var tangent: Vector2 = Vector2(-aim_dir.y, aim_dir.x)
	
	# Lebar semburan utama diperbesar agar lidah api FBM Putih-Hitam-Ungu Tua berkobar megah
	var plume_width: float = (drill_r * 4.15) * (1.0 + 0.38 * swarm_scale)
	var diamonds: float = clampf(5.5 + 3.4 * swarm_scale, 5.5, 14.0)
	
	ion_beam_mesh.visible = true
	ion_beam_mesh.global_position = base_pos + exhaust_dir * (beam_len * 0.48)
	ion_beam_mesh.global_rotation = exhaust_dir.angle()
	ion_beam_mesh.scale = Vector2(beam_len, plume_width)
	
	ion_beam_mat.set_shader_parameter("power", beam_power)
	ion_beam_mat.set_shader_parameter("spin_time", spin_time)
	ion_beam_mat.set_shader_parameter("rpm", 32.0)
	ion_beam_mat.set_shader_parameter("swarm_scale", swarm_scale)
	ion_beam_mat.set_shader_parameter("diamond_count", diamonds)
	
	# Semburan Sayap Kiri & Kanan (Triple-Plume Overdrive ala multi-nozzle XF-09 Ares)
	var show_wings: bool = is_active or (is_charging and charge_ratio > 0.55)
	if wing_beam_left_mesh and wing_beam_right_mesh and wing_beam_left_mat and wing_beam_right_mat:
		wing_beam_left_mesh.visible = show_wings
		wing_beam_right_mesh.visible = show_wings
		if show_wings:
			var wing_power: float = 0.92 if is_active else clampf((charge_ratio - 0.55) / 0.45, 0.0, 0.85)
			var wing_len: float = beam_len * (0.76 if is_active else 0.62)
			var wing_width: float = plume_width * 0.62
			var wing_spread: float = 0.20 if is_active else 0.28
			var dir_left: Vector2 = exhaust_dir.rotated(-wing_spread)
			var dir_right: Vector2 = exhaust_dir.rotated(wing_spread)
			var nozzle_left: Vector2 = base_pos + tangent * (drill_r * 0.68)
			var nozzle_right: Vector2 = base_pos - tangent * (drill_r * 0.68)
			
			wing_beam_left_mesh.global_position = nozzle_left + dir_left * (wing_len * 0.46)
			wing_beam_left_mesh.global_rotation = dir_left.angle()
			wing_beam_left_mesh.scale = Vector2(wing_len, wing_width)
			wing_beam_left_mat.set_shader_parameter("power", wing_power)
			wing_beam_left_mat.set_shader_parameter("spin_time", spin_time + 1.57)
			wing_beam_left_mat.set_shader_parameter("rpm", 36.0)
			wing_beam_left_mat.set_shader_parameter("swarm_scale", swarm_scale * 0.85)
			wing_beam_left_mat.set_shader_parameter("diamond_count", diamonds * 0.75)
			
			wing_beam_right_mesh.global_position = nozzle_right + dir_right * (wing_len * 0.46)
			wing_beam_right_mesh.global_rotation = dir_right.angle()
			wing_beam_right_mesh.scale = Vector2(wing_len, wing_width)
			wing_beam_right_mat.set_shader_parameter("power", wing_power)
			wing_beam_right_mat.set_shader_parameter("spin_time", spin_time + 3.14)
			wing_beam_right_mat.set_shader_parameter("rpm", 36.0)
			wing_beam_right_mat.set_shader_parameter("swarm_scale", swarm_scale * 0.85)
			wing_beam_right_mat.set_shader_parameter("diamond_count", diamonds * 0.75)

func trigger_launch_shake() -> void:
	if arena and arena.has_method("trigger_shake"):
		arena.trigger_shake(18.0)

func trigger_nose_breach(breach_pos: Vector2, aim_dir: Vector2, count: int) -> void:
	if arena and arena.has_method("trigger_shake"):
		arena.trigger_shake(14.0)
		
	# Gelombang kejut perisai hidung pecah (Putih Menyilaukan & Ungu Tua Abyssal)
	breach_rings.append({
		"pos": breach_pos,
		"radius": 10.0,
		"max_radius": 250.0,
		"speed": 780.0,
		"color": Color(6.4, 6.0, 7.2, 1.0),
		"alpha": 1.0
	})
	breach_rings.append({
		"pos": breach_pos,
		"radius": 4.0,
		"max_radius": 185.0,
		"speed": 560.0,
		"color": Color(2.2, 0.25, 4.8, 1.0),
		"alpha": 1.0
	})
	
	var shard_count: int = clampi(count * 3, 20, 52)
	for _i in range(shard_count):
		var spread: float = randf_range(-0.88, 0.88)
		var shard_dir: Vector2 = aim_dir.rotated(spread)
		var speed: float = randf_range(680.0, 1650.0)
		var roll: float = randf()
		var s_col: Color = Color(6.5, 6.2, 7.4, 1.0) if roll > 0.55 else (Color(2.4, 0.28, 5.2, 1.0) if roll > 0.20 else Color(0.02, 0.005, 0.04, 0.95))
		breach_shards.append({
			"pos": breach_pos + shard_dir * randf_range(4.0, 22.0),
			"vel": shard_dir * speed,
			"len": randf_range(9.0, 26.0),
			"life": randf_range(0.22, 0.44),
			"max_life": 0.44,
			"color": s_col
		})

func trigger_detonation(tip_pos: Vector2, base_pos: Vector2, count: int) -> void:
	# Kasus 1: Ledakan Kecil Pembuat Lubang saat Driller Habis Menggerus Asteroid (count < 0)
	if count < 0:
		if arena and arena.has_method("trigger_shake"):
			arena.trigger_shake(8.5)
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 8.0,
			"max_radius": 135.0,
			"speed": 540.0,
			"color": Color(6.2, 5.8, 7.0, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 3.0,
			"max_radius": 95.0,
			"speed": 390.0,
			"color": Color(1.8, 0.18, 4.2, 1.0),
			"alpha": 1.0
		})
		for _i in range(20):
			var ang: float = randf() * TAU
			var dir: Vector2 = Vector2(cos(ang), sin(ang))
			var spd: float = randf_range(340.0, 860.0)
			breach_shards.append({
				"pos": tip_pos + dir * randf_range(2.0, 10.0),
				"vel": dir * spd,
				"len": randf_range(8.0, 18.0),
				"life": randf_range(0.18, 0.32),
				"max_life": 0.32,
				"color": Color(6.4, 6.0, 7.2, 1.0) if randf() > 0.45 else Color(2.0, 0.20, 4.5, 1.0)
			})
		return

	# Kasus 2: Ledakan Spektakuler Abyssal Supernova saat Menghantam Core (count > 0)
	if count > 0:
		if arena and arena.has_method("trigger_shake"):
			arena.trigger_shake(24.0)
			
		var scale_f: float = clampf(float(count) / 18.0, 1.0, 1.75)
		
		# Flash Bola Inti Void Supernova & Petir Radial
		supernova_flashes.append({
			"pos": tip_pos,
			"radius": 46.0 * scale_f,
			"max_radius": 260.0 * scale_f,
			"life": 0.46,
			"max_life": 0.46
		})
		
		# 5 cincin gelombang kejut supernova bertingkat (Putih -> Ungu Pijar -> Hitam Void -> Ungu Tua)
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 18.0,
			"max_radius": 440.0 * scale_f,
			"speed": 1080.0,
			"color": Color(6.8, 6.5, 7.6, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 12.0,
			"max_radius": 360.0 * scale_f,
			"speed": 850.0,
			"color": Color(3.6, 0.65, 6.2, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 8.0,
			"max_radius": 285.0 * scale_f,
			"speed": 660.0,
			"color": Color(0.02, 0.005, 0.04, 0.96),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 4.0,
			"max_radius": 210.0 * scale_f,
			"speed": 480.0,
			"color": Color(1.65, 0.14, 3.8, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": base_pos,
			"radius": 8.0,
			"max_radius": 235.0,
			"speed": 600.0,
			"color": Color(4.5, 1.2, 6.5, 1.0),
			"alpha": 1.0
		})
		
		# 64 Lontaran Pecahan Plasma Abyssal 360 Derajat Berkecepatan Tinggi
		for _i in range(64):
			var ang: float = randf() * TAU
			var dir: Vector2 = Vector2(cos(ang), sin(ang))
			var spd: float = randf_range(580.0, 1920.0)
			var roll: float = randf()
			var s_col: Color = Color(6.8, 6.5, 7.6, 1.0) if roll < 0.42 else (Color(2.4, 0.24, 5.2, 1.0) if roll < 0.78 else Color(0.02, 0.005, 0.04, 0.95))
			breach_shards.append({
				"pos": tip_pos + dir * randf_range(4.0, 24.0),
				"vel": dir * spd,
				"len": randf_range(15.0, 38.0),
				"life": randf_range(0.30, 0.62),
				"max_life": 0.62,
				"color": s_col
			})
		return

	# Kasus 3: Habis Terbakar di Ruang Hampa (count == 0)
	if arena and arena.has_method("trigger_shake"):
		arena.trigger_shake(10.0)
	detonation_rings.append({
		"pos": tip_pos,
		"radius": 10.0,
		"max_radius": 190.0,
		"speed": 540.0,
		"color": Color(2.4, 0.28, 5.0, 1.0),
		"alpha": 1.0
	})

func _update_supernova_flashes(delta: float) -> void:
	var to_remove: Array[int] = []
	for i in range(supernova_flashes.size()):
		var fl = supernova_flashes[i]
		fl["life"] -= delta
		var p: float = 1.0 - clampf(fl["life"] / fl["max_life"], 0.0, 1.0)
		fl["radius"] = lerpf(38.0, fl["max_radius"], pow(p, 0.6))
		if fl["life"] <= 0.0:
			to_remove.append(i)
	for i in range(to_remove.size() - 1, -1, -1):
		supernova_flashes.remove_at(to_remove[i])

func _update_breach_fx(delta: float) -> void:
	var to_remove_rings: Array[int] = []
	for i in range(breach_rings.size()):
		var ring = breach_rings[i]
		ring["radius"] += ring["speed"] * delta
		ring["alpha"] = clampf(1.0 - (ring["radius"] / ring["max_radius"]), 0.0, 1.0)
		if ring["radius"] >= ring["max_radius"]:
			to_remove_rings.append(i)
	for i in range(to_remove_rings.size() - 1, -1, -1):
		breach_rings.remove_at(to_remove_rings[i])

	var to_remove_shards: Array[int] = []
	for i in range(breach_shards.size()):
		var s = breach_shards[i]
		s["pos"] += s["vel"] * delta
		s["vel"] = s["vel"].lerp(Vector2.ZERO, delta * 3.5)
		s["life"] -= delta
		if s["life"] <= 0.0:
			to_remove_shards.append(i)
	for i in range(to_remove_shards.size() - 1, -1, -1):
		breach_shards.remove_at(to_remove_shards[i])

func _update_detonations(delta: float) -> void:
	var to_remove: Array[int] = []
	for i in range(detonation_rings.size()):
		var ring = detonation_rings[i]
		ring["radius"] += ring["speed"] * delta
		ring["alpha"] = clampf(1.0 - (ring["radius"] / ring["max_radius"]), 0.0, 1.0)
		if ring["radius"] >= ring["max_radius"]:
			to_remove.append(i)
			
	for i in range(to_remove.size() - 1, -1, -1):
		detonation_rings.remove_at(to_remove[i])

func _update_ion_particles(delta: float, is_charging: bool, is_active: bool, swarm_scale: float) -> void:
	var to_remove: Array[int] = []
	for i in range(ion_particles.size()):
		var p = ion_particles[i]
		p["pos"] += p["vel"] * delta
		p["life"] -= delta
		if p["life"] <= 0.0:
			to_remove.append(i)
	for i in range(to_remove.size() - 1, -1, -1):
		ion_particles.remove_at(to_remove[i])
		
	var active_limit: int = int(56 * clampf(swarm_scale, 1.0, 3.2))
	if (is_active or (is_charging and float(swarm_sim.get("DrillChargeRatio")) > 0.30)) and ion_particles.size() < active_limit:
		var base_pos: Vector2 = swarm_sim.get("GigaDrillBasePosition")
		var aim_dir: Vector2 = swarm_sim.get("GigaDrillDirection")
		var drill_r: float = float(swarm_sim.get("GigaDrillRadius"))
		var tangent: Vector2 = Vector2(-aim_dir.y, aim_dir.x)
		
		var spawn_count: int = int((8 if is_active else 4) * clampf(swarm_scale, 1.0, 2.6))
		for _s in range(spawn_count):
			var r_offset: float = (randf() - 0.5) * drill_r * 1.65
			var start_p: Vector2 = base_pos + tangent * r_offset
			var speed: float = randf_range(950.0, 2100.0) if is_active else randf_range(480.0, 1050.0)
			var spread: float = randf_range(-0.38, 0.38)
			var vel: Vector2 = (-aim_dir).rotated(spread) * speed
			var roll: float = randf()
			var p_col: Color = Color(6.6, 6.4, 7.4, 1.0) if roll > 0.52 else (Color(2.2, 0.22, 4.8, 1.0) if roll > 0.18 else Color(0.02, 0.005, 0.04, 0.95))
			
			ion_particles.append({
				"pos": start_p,
				"vel": vel,
				"life": randf_range(0.16, 0.36),
				"max_life": 0.36,
				"color": p_col
			})

func _draw() -> void:
	if not swarm_sim or swarm_sim.get("PlayerHealth") == null:
		return
		
	var is_charging: bool = swarm_sim.get("IsChargingDrill") == true
	var is_active: bool = swarm_sim.get("IsGigaDrillActive") == true
	var charge_ratio: float = float(swarm_sim.get("DrillChargeRatio"))
	
	var tip_pos: Vector2 = swarm_sim.get("GigaDrillTipPosition")
	var base_pos: Vector2 = swarm_sim.get("GigaDrillBasePosition")
	var aim_dir: Vector2 = swarm_sim.get("GigaDrillDirection")
	var drill_r: float = float(swarm_sim.get("GigaDrillRadius"))
	var drill_len: float = float(swarm_sim.get("GigaDrillLength"))
	var spin_time: float = float(swarm_sim.get("GigaDrillSpinTime"))
	var tangent: Vector2 = Vector2(-aim_dir.y, aim_dir.x)
	
	var total_units: int = int(swarm_sim.get("GigaDrillTotalUnits"))
	var def_units: int = int(swarm_sim.get("GigaDrillDefenderCoreCount"))
	var swarm_scale: float = clampf(total_units / 45.0, 0.75, 4.5)
	
	# =========================================================================
	# 1. EFEK CHARGING MEGAH: ABYSSAL ACCRETION VORTEX, VOID LIGHTNING, APEX CORONA
	# =========================================================================
	if is_charging:
		var core_pos: Vector2 = player_core.global_position if player_core else base_pos
		
		# (a) Abyssal Accretion Vortex: Cincin gravitasi Ungu Tua -> Hitam Void -> Putih Menyilaukan
		var ring_count: int = 5
		for r in range(ring_count):
			var phase: float = fmod(spin_time * 2.4 + float(r) / ring_count, 1.0)
			var shrink_r: float = (1.0 - phase) * (145.0 * charge_ratio + 22.0)
			var ring_alpha: float = sin(phase * PI) * (0.50 + 0.50 * charge_ratio)
			var ring_col: Color = Color(1.4, 0.12, 3.2, ring_alpha).lerp(Color(6.4, 6.2, 7.2, ring_alpha), phase * phase)
			draw_arc(tip_pos, shrink_r, 0, TAU, 32, ring_col, 1.8, true)
			
			# Cincin bayangan Hitam Void pendamping agar kontras gelapnya terasa pekat
			draw_arc(tip_pos, shrink_r * 0.92, 0, TAU, 28, Color(0.015, 0.004, 0.03, ring_alpha * 0.85), 2.4, true)
			
			# Garis lengan spiral yang terhisap ke pusat
			var arm_count: int = 6
			for a in range(arm_count):
				var a_angle: float = spin_time * 4.8 + (a * TAU / arm_count) + phase * 2.6
				var arm_dir: Vector2 = Vector2(cos(a_angle), sin(a_angle))
				var p_start: Vector2 = tip_pos + arm_dir * shrink_r
				var p_end: Vector2 = tip_pos + arm_dir.rotated(0.45) * (shrink_r * 0.42)
				draw_line(p_start, p_end, Color(2.4, 0.25, 4.8, ring_alpha * 0.8), 1.5, true)
		
		# (b) Multi-Point Branching Void Lightning Arcs (Putih Menyilaukan & Ungu Tua):
		var arc_count: int = 4 + int(charge_ratio * 6.0)
		for a in range(arc_count):
			var is_ap_arc: bool = (a % 2 == 0) and def_units > 0
			var target_d: float = (drill_len * 0.35) if is_ap_arc else (drill_len * 0.78)
			var target_r: float = (drill_r * 0.45) if is_ap_arc else (drill_r * 1.15)
			var target_p: Vector2 = tip_pos - aim_dir * target_d + tangent * ((randf() - 0.5) * target_r * 2.0)
			var arc_col: Color = Color(6.4, 6.2, 7.2, 0.95) if is_ap_arc else Color(2.2, 0.22, 4.8, 0.95)
			_draw_electric_arc(core_pos, target_p, 6, 15.0 * charge_ratio, arc_col, 1.8)
			
		# (c) Second Cone AP Shield di Ujung Bor: Kerucut tameng balistik Putih-Ungu
		if def_units > 0:
			var nose_len: float = float(swarm_sim.get("GigaDrillNoseLength"))
			if nose_len <= 0.0:
				nose_len = clampf(20.0 + sqrt(def_units) * 6.5, 20.0, 56.0)
			var nose_r: float = float(swarm_sim.get("GigaDrillNoseRadius"))
			if nose_r <= 0.0:
				nose_r = clampf(12.0 + sqrt(def_units) * 3.6, 12.0, 28.0)
				
			var nose_apex: Vector2 = tip_pos + aim_dir * (nose_len * charge_ratio)
			var nose_base_left: Vector2 = tip_pos + tangent * (nose_r * 0.85)
			var nose_base_right: Vector2 = tip_pos - tangent * (nose_r * 0.85)
			
			draw_line(nose_apex, nose_base_left, Color(2.6, 0.35, 5.4, 0.88 * charge_ratio), 2.4, true)
			draw_line(nose_apex, nose_base_right, Color(2.6, 0.35, 5.4, 0.88 * charge_ratio), 2.4, true)
			draw_line(nose_base_left, nose_base_right, Color(1.6, 0.15, 3.6, 0.65 * charge_ratio), 1.6, true)
			draw_line(tip_pos, nose_apex, Color(6.4, 6.2, 7.2, 0.96 * charge_ratio), 2.0, true)
			
			var facet_count: int = 3
			for f in range(1, facet_count):
				var f_ratio: float = float(f) / facet_count
				var f_pt: Vector2 = tip_pos.lerp(nose_apex, f_ratio)
				var f_w: float = nose_r * (1.0 - f_ratio * 0.7)
				draw_line(f_pt + tangent * f_w, f_pt - tangent * f_w, Color(3.2, 0.55, 5.8, 0.65 * charge_ratio), 1.3, true)
			
		# (d) Tip Gravitational Singularity Corona (Inti Hitam Void + Bintang Putih-Ungu)
		var tip_glow_r: float = 9.0 + charge_ratio * 20.0
		draw_circle(tip_pos, tip_glow_r * 0.48, Color(0.012, 0.004, 0.025, 0.95))
		draw_arc(tip_pos, tip_glow_r, 0, TAU, 28, Color(2.2, 0.22, 4.8, 0.90), 2.0, true)
		draw_arc(tip_pos, tip_glow_r * 0.54, spin_time * 9.5, spin_time * 9.5 + TAU * 0.85, 22, Color(6.6, 6.4, 7.4, 1.0), 2.6, true)
		
		var spike_count: int = 8 if charge_ratio < 0.9 else 16
		for s in range(spike_count):
			var angle: float = spin_time * 8.0 + (s * TAU / spike_count)
			var spike_dir: Vector2 = Vector2(cos(angle), sin(angle))
			var spike_len: float = tip_glow_r * (1.25 + 0.65 * sin(s * 2.5 + spin_time * 12.0))
			var sp_col: Color = Color(6.6, 6.4, 7.4, 0.92) if (s % 2 == 0) else Color(2.4, 0.25, 5.2, 0.90)
			draw_line(tip_pos + spike_dir * (tip_glow_r * 0.35), tip_pos + spike_dir * spike_len, sp_col, 1.5, true)
			
		if arena and arena.has_method("trigger_shake") and charge_ratio > 0.15:
			arena.trigger_shake(charge_ratio * 4.5)

	# =========================================================================
	# 2. NOZZLE SINGULARITY FLARE & PETIR EXHAUST OVERDRIVE DI PANGKAL BOR
	# Memperkuat pangkal semburan Triple-Plume XF-09 Ares dengan cincin Void-Putih-Ungu
	# =========================================================================
	if is_active or (is_charging and charge_ratio > 0.25):
		var ex_power: float = 1.0 if is_active else clampf((charge_ratio - 0.25) / 0.75, 0.0, 1.0)
		var exhaust_dir: Vector2 = -aim_dir
		var nozzle_orb_r: float = (14.0 + 10.0 * swarm_scale) * ex_power * (0.90 + 0.10 * sin(spin_time * 14.0))
		
		# Halo Ungu Tua -> Cincin Putih Menyilaukan -> Inti Hitam Void di mulut nozzle Giga Drill
		draw_circle(base_pos, nozzle_orb_r * 1.95, Color(1.35, 0.10, 3.1, 0.42 * ex_power))
		draw_circle(base_pos, nozzle_orb_r * 1.15, Color(6.6, 6.4, 7.5, 0.94 * ex_power))
		draw_circle(base_pos, nozzle_orb_r * 0.68, Color(0.012, 0.004, 0.025, 0.96 * ex_power))
		draw_circle(base_pos, nozzle_orb_r * 0.26, Color(6.8, 6.6, 7.8, 0.98 * ex_power))
		
		# Sambaran petir plasma Putih & Ungu Tua yang menyembur liar ke sepanjang ekor exhaust
		var tail_arc_count: int = (6 if is_active else 3) + int(swarm_scale * 1.5)
		var max_arc_reach: float = (260.0 + 95.0 * swarm_scale) * ex_power
		for t_idx in range(tail_arc_count):
			var spread_f: float = (float(t_idx) / maxf(1.0, float(tail_arc_count - 1)) - 0.5) * 2.0
			var start_n: Vector2 = base_pos + tangent * (spread_f * drill_r * 0.85)
			var end_n: Vector2 = base_pos + exhaust_dir * randf_range(95.0, max_arc_reach) + tangent * (spread_f * drill_r * 1.9 + randf_range(-28.0, 28.0))
			var bolt_col: Color = Color(6.6, 6.4, 7.5, 0.92 * ex_power) if (t_idx % 2 == 0) else Color(2.6, 0.28, 5.4, 0.88 * ex_power)
			_draw_electric_arc(start_n, end_n, 6, 18.0 * ex_power, bolt_col, 2.0 if (t_idx % 2 == 0) else 1.5)

	# =========================================================================
	# 3. EFEK SUPERSONIC BOW SHOCK & CORE-RAMMING OVERDRIVE SAAT MELUNCUR
	# =========================================================================
	if is_active:
		var has_breached: bool = swarm_sim.get("HasDefenderNoseBreached") == true
		var is_ramming_core: bool = swarm_sim.get("IsRammingEnemyCore") == true
		var nose_len: float = float(swarm_sim.get("GigaDrillNoseLength"))
		var active_apex: Vector2 = (tip_pos + aim_dir * nose_len) if (not has_breached and nose_len > 0.0) else tip_pos

		# 3 lapis gelombang kejut kerucut di depan ujung bor (Putih Menyilaukan, Hitam Void, Ungu Tua)
		for b in range(3):
			var offset_f: float = float(b) * 14.0
			var shock_pts: PackedVector2Array = [
				active_apex + aim_dir * (24.0 + offset_f),
				active_apex - aim_dir * (38.0 - offset_f) + tangent * (56.0 + offset_f * 1.6),
				active_apex - aim_dir * (38.0 - offset_f) - tangent * (56.0 + offset_f * 1.6)
			]
			var col: Color
			if b == 0:
				col = Color(6.6, 6.4, 7.5, 0.96)
			elif b == 1:
				col = Color(0.015, 0.004, 0.03, 0.92)
			else:
				col = Color(2.2, 0.22, 4.8, 0.82)
			var w: float = 2.6 if b == 1 else (2.2 - b * 0.35)
			draw_line(shock_pts[0], shock_pts[1], col, w, true)
			draw_line(shock_pts[0], shock_pts[2], col, w, true)

		# Jika sedang menempel & menggerus permukaan Core (0.5s Overdrive sebelum meledak)
		if is_ramming_core:
			var pulse_r: float = 20.0 + 14.0 * absf(sin(spin_time * 4.0))
			draw_circle(tip_pos, pulse_r * 0.95, Color(0.012, 0.004, 0.025, 0.94))
			draw_circle(tip_pos, pulse_r * 0.55, Color(6.8, 6.5, 7.6, 0.95))
			draw_arc(tip_pos, pulse_r * 1.40, 0, TAU, 28, Color(2.6, 0.28, 5.5, 0.95), 3.0, true)
			for a in range(6):
				var ang: float = spin_time * 2.5 + a * (TAU / 6.0)
				var arc_end: Vector2 = tip_pos + Vector2(cos(ang), sin(ang)) * randf_range(32.0, 68.0)
				var r_col: Color = Color(6.8, 6.5, 7.6, 0.96) if (a % 2 == 0) else Color(2.6, 0.28, 5.4, 0.94)
				_draw_electric_arc(tip_pos, arc_end, 4, 12.0, r_col, 2.0)

		# Jika perisai hidung belum pecah, gambar rusuk kerucut kedua
		if not has_breached and nose_len > 0.0:
			var nose_r: float = float(swarm_sim.get("GigaDrillNoseRadius"))
			draw_line(active_apex, tip_pos + tangent * nose_r, Color(2.6, 0.32, 5.4, 0.90), 2.2, true)
			draw_line(active_apex, tip_pos - tangent * nose_r, Color(2.6, 0.32, 5.4, 0.90), 2.2, true)
			draw_line(tip_pos, active_apex, Color(6.6, 6.4, 7.5, 0.95), 1.8, true)

	# =========================================================================
	# 4. PARTIKEL LONCATAN PLASMA & VOID EMBER
	# =========================================================================
	for p in ion_particles:
		var alpha: float = clampf(p["life"] / p["max_life"], 0.0, 1.0)
		var col: Color = p["color"]
		col.a *= alpha
		var end_pt: Vector2 = p["pos"] - p["vel"].normalized() * (18.0 * alpha)
		draw_line(p["pos"], end_pt, col, 2.1, true)

	# =========================================================================
	# 5. FLASH BOLA INTI ABYSSAL SUPERNOVA & CINCIN KEJUT DETONATION
	# =========================================================================
	for fl in supernova_flashes:
		var a: float = clampf(fl["life"] / fl["max_life"], 0.0, 1.0)
		var pos_fl: Vector2 = fl["pos"]
		var rad_fl: float = fl["radius"]
		draw_circle(pos_fl, rad_fl * 0.72, Color(0.012, 0.004, 0.025, a * 0.94))
		draw_circle(pos_fl, rad_fl * 0.42, Color(6.8, 6.5, 7.6, a * 0.95))
		draw_arc(pos_fl, rad_fl, 0, TAU, 36, Color(2.8, 0.32, 5.8, a * 0.96), 4.6 * a, true)
		for l in range(10):
			var l_ang: float = l * (TAU / 10.0) + a * 3.8
			var l_end: Vector2 = pos_fl + Vector2(cos(l_ang), sin(l_ang)) * (rad_fl * 1.22)
			var l_col: Color = Color(6.8, 6.5, 7.6, a) if (l % 2 == 0) else Color(2.6, 0.28, 5.5, a)
			_draw_electric_arc(pos_fl, l_end, 5, 15.0 * a, l_col, 2.0)

	for ring in detonation_rings:
		var c: Color = ring["color"]
		c.a *= ring["alpha"]
		draw_arc(ring["pos"], ring["radius"], 0, TAU, 36, c, 3.6, true)
		var ray_count: int = 16
		for r in range(ray_count):
			var ang: float = r * TAU / ray_count
			var r_dir: Vector2 = Vector2(cos(ang), sin(ang))
			draw_line(ring["pos"] + r_dir * (ring["radius"] * 0.68), ring["pos"] + r_dir * ring["radius"], c, 2.0, true)

	# =========================================================================
	# 6. EFEK PECAHAN PERISAI AP & SHRAPNEL SUPERNOVA
	# =========================================================================
	for ring in breach_rings:
		var c: Color = ring["color"]
		c.a *= ring["alpha"]
		draw_arc(ring["pos"], ring["radius"], 0, TAU, 32, c, 3.2, true)

	for shard in breach_shards:
		var alpha: float = clampf(shard["life"] / shard["max_life"], 0.0, 1.0)
		var col: Color = shard["color"]
		col.a *= alpha
		var end_p: Vector2 = shard["pos"] + shard["vel"].normalized() * (shard["len"] * alpha)
		draw_line(shard["pos"], end_p, col, 2.3, true)

func _draw_electric_arc(from_pos: Vector2, to_pos: Vector2, segments: int, jitter: float, color: Color, width: float) -> void:
	var pts: PackedVector2Array = []
	pts.resize(segments + 1)
	pts[0] = from_pos
	pts[segments] = to_pos
	
	var vec: Vector2 = to_pos - from_pos
	var norm: Vector2 = Vector2(-vec.y, vec.x).normalized()
	
	for i in range(1, segments):
		var t: float = float(i) / segments
		var base_p: Vector2 = from_pos + vec * t
		var offset: float = (randf() - 0.5) * jitter
		pts[i] = base_p + norm * offset
		
	draw_polyline(pts, color, width, true)
