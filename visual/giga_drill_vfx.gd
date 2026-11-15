class_name GigaDrillVFX
extends Node2D

## GigaDrillVFX - Spektakel Visual Super Giga Drill Break (Retro Vector Neon)
## Mengatur:
## 1. Inti AP (Armor Piercing) 25% Defender Swarm: Pendaran kristal titanium di dalam bor
## 2. Efek Charging Spektakuler: Accretion vortex, branching lightning, tip solar corona
## 3. Efek Trail & Ion Beam Berskala: Panjang, ketebalan, shock diamond & pita plasma
##    skala otomatis membesar secara megah sesuai jumlah swarm yang terlibat!
## 4. Efek Core-Ramming Overdrive & Supernova Detonation Spektakuler (maupun ledakan kawah kecil di asteroid)

const IonBeamShader = preload("res://visual/giga_drill_ion_beam.gdshader")

var swarm_sim: Node2D = null
var player_core: Node2D = null
var arena: Node2D = null

# Komponen GPU Shader Ion Beam Rocket Exhaust
var ion_beam_mesh: MeshInstance2D = null
var ion_beam_mat: ShaderMaterial = null

# Partikel loncatan ion exhaust
var ion_particles: Array[Dictionary] = []
var max_particles: int = 120

# Riwayat ledakan pengorbanan swarm
var detonation_rings: Array[Dictionary] = []

# Riwayat pemecahan perisai hidung (Nose Cone AP Breach)
var breach_rings: Array[Dictionary] = []
var breach_shards: Array[Dictionary] = []

# Flash inti supernova saat meledak di Core
var supernova_flashes: Array[Dictionary] = []

func _ready() -> void:
	z_index = 6 # Tepat di atas MultiMesh swarm agar kilau HDR ion beam menyatu
	ion_beam_mesh = MeshInstance2D.new()
	var quad: QuadMesh = QuadMesh.new()
	quad.size = Vector2(1.0, 1.0)
	ion_beam_mesh.mesh = quad
	ion_beam_mat = ShaderMaterial.new()
	ion_beam_mat.shader = IonBeamShader
	ion_beam_mesh.material = ion_beam_mat
	ion_beam_mesh.visible = false
	add_child(ion_beam_mesh)

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
	
	# Update Shader Ion Beam Cyclone
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
	if not (is_active or (is_charging and charge_ratio > 0.28)):
		ion_beam_mesh.visible = false
		return
		
	var beam_power: float = 1.0 if is_active else clampf((charge_ratio - 0.28) / 0.72, 0.0, 1.0)
	var base_beam_len: float = lerpf(75.0, 260.0, beam_power) if is_charging else (360.0 + 120.0 * swarm_scale)
	var beam_len: float = base_beam_len
	var base_pos: Vector2 = swarm_sim.get("GigaDrillBasePosition")
	var aim_dir: Vector2 = swarm_sim.get("GigaDrillDirection")
	var drill_r: float = float(swarm_sim.get("GigaDrillRadius"))
	var spin_time: float = float(swarm_sim.get("GigaDrillSpinTime"))
	var exhaust_dir: Vector2 = -aim_dir
	var plume_width: float = (drill_r * 2.85) * (1.0 + 0.35 * swarm_scale)
	
	ion_beam_mesh.visible = true
	ion_beam_mesh.global_position = base_pos + exhaust_dir * (beam_len * 0.5)
	ion_beam_mesh.global_rotation = exhaust_dir.angle()
	ion_beam_mesh.scale = Vector2(beam_len, plume_width)
	
	ion_beam_mat.set_shader_parameter("power", beam_power)
	ion_beam_mat.set_shader_parameter("spin_time", spin_time)
	ion_beam_mat.set_shader_parameter("rpm", 28.0)
	ion_beam_mat.set_shader_parameter("swarm_scale", swarm_scale)
	ion_beam_mat.set_shader_parameter("diamond_count", clampf(5.0 + 3.2 * swarm_scale, 5.0, 13.0))

func trigger_launch_shake() -> void:
	if arena and arena.has_method("trigger_shake"):
		arena.trigger_shake(16.0)

func trigger_nose_breach(breach_pos: Vector2, aim_dir: Vector2, count: int) -> void:
	if arena and arena.has_method("trigger_shake"):
		arena.trigger_shake(13.0)
		
	# Gelombang kejut perisai hidung pecah (Shatter Shockwaves)
	breach_rings.append({
		"pos": breach_pos,
		"radius": 10.0,
		"max_radius": 240.0,
		"speed": 750.0,
		"color": Color(2.8, 5.5, 7.0, 1.0),
		"alpha": 1.0
	})
	breach_rings.append({
		"pos": breach_pos,
		"radius": 4.0,
		"max_radius": 170.0,
		"speed": 540.0,
		"color": Color(5.5, 5.5, 6.5, 1.0),
		"alpha": 1.0
	})
	
	# Pecahan tameng kristal terlontar ke depan & samping (shattered AP armor shards)
	var shard_count: int = clampi(count * 3, 18, 48)
	for _i in range(shard_count):
		var spread: float = randf_range(-0.85, 0.85)
		var shard_dir: Vector2 = aim_dir.rotated(spread)
		var speed: float = randf_range(650.0, 1600.0)
		breach_shards.append({
			"pos": breach_pos + shard_dir * randf_range(4.0, 22.0),
			"vel": shard_dir * speed,
			"len": randf_range(8.0, 24.0),
			"life": randf_range(0.20, 0.42),
			"max_life": 0.42,
			"color": Color(2.8, 5.5, 7.0, 1.0) if randf() > 0.35 else Color(5.5, 5.5, 6.5, 1.0)
		})

func trigger_detonation(tip_pos: Vector2, base_pos: Vector2, count: int) -> void:
	# Kasus 1: Ledakan Kecil Pembuat Lubang saat Driller Habis Menggerus Asteroid (count < 0)
	if count < 0:
		if arena and arena.has_method("trigger_shake"):
			arena.trigger_shake(8.0)
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 8.0,
			"max_radius": 125.0,
			"speed": 520.0,
			"color": Color(4.8, 3.2, 1.1, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 3.0,
			"max_radius": 85.0,
			"speed": 380.0,
			"color": Color(5.5, 5.0, 2.8, 1.0),
			"alpha": 1.0
		})
		for _i in range(18):
			var ang: float = randf() * TAU
			var dir: Vector2 = Vector2(cos(ang), sin(ang))
			var spd: float = randf_range(320.0, 820.0)
			breach_shards.append({
				"pos": tip_pos + dir * randf_range(2.0, 10.0),
				"vel": dir * spd,
				"len": randf_range(7.0, 16.0),
				"life": randf_range(0.16, 0.30),
				"max_life": 0.30,
				"color": Color(4.8, 3.2, 1.1, 1.0) if randf() > 0.4 else Color(5.5, 5.2, 3.5, 1.0)
			})
		return

	# Kasus 2: Ledakan Spektakuler Supernova saat Menghantam Core (count > 0)
	if count > 0:
		if arena and arena.has_method("trigger_shake"):
			arena.trigger_shake(22.0)
			
		var scale_f: float = clampf(float(count) / 18.0, 1.0, 1.65)
		
		# Flash Bola Inti Supernova & Petir Radial
		supernova_flashes.append({
			"pos": tip_pos,
			"radius": 42.0 * scale_f,
			"max_radius": 240.0 * scale_f,
			"life": 0.42,
			"max_life": 0.42
		})
		
		# 5 cincin gelombang kejut supernova bertingkat berpusat tepat di titik tumbukan Core!
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 18.0,
			"max_radius": 420.0 * scale_f,
			"speed": 1050.0,
			"color": Color(5.8, 5.6, 4.2, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 12.0,
			"max_radius": 340.0 * scale_f,
			"speed": 820.0,
			"color": Color(5.5, 3.8, 1.2, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 6.0,
			"max_radius": 270.0 * scale_f,
			"speed": 640.0,
			"color": Color(2.6, 5.2, 6.8, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 2.0,
			"max_radius": 195.0 * scale_f,
			"speed": 460.0,
			"color": Color(4.8, 1.2, 5.8, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": base_pos,
			"radius": 8.0,
			"max_radius": 220.0,
			"speed": 580.0,
			"color": Color(5.2, 2.6, 1.0, 1.0),
			"alpha": 1.0
		})
		
		# 56 Lontaran Pecahan Plasma Supernova 360 Derajat Berkecepatan Tinggi
		for _i in range(56):
			var ang: float = randf() * TAU
			var dir: Vector2 = Vector2(cos(ang), sin(ang))
			var spd: float = randf_range(550.0, 1850.0)
			var roll: float = randf()
			var s_col: Color = Color(5.8, 5.2, 2.5, 1.0) if roll < 0.4 else (Color(2.6, 5.2, 6.8, 1.0) if roll < 0.75 else Color(5.2, 1.4, 4.8, 1.0))
			breach_shards.append({
				"pos": tip_pos + dir * randf_range(4.0, 24.0),
				"vel": dir * spd,
				"len": randf_range(14.0, 36.0),
				"life": randf_range(0.28, 0.58),
				"max_life": 0.58,
				"color": s_col
			})
		return

	# Kasus 3: Habis Terbakar di Ruang Hampa (count == 0)
	if arena and arena.has_method("trigger_shake"):
		arena.trigger_shake(10.0)
	detonation_rings.append({
		"pos": tip_pos,
		"radius": 10.0,
		"max_radius": 180.0,
		"speed": 520.0,
		"color": Color(4.5, 3.2, 1.5, 1.0),
		"alpha": 1.0
	})

func _update_supernova_flashes(delta: float) -> void:
	var to_remove: Array[int] = []
	for i in range(supernova_flashes.size()):
		var fl = supernova_flashes[i]
		fl["life"] -= delta
		var p: float = 1.0 - clampf(fl["life"] / fl["max_life"], 0.0, 1.0)
		fl["radius"] = lerpf(36.0, fl["max_radius"], pow(p, 0.6))
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
		
	var active_limit: int = int(40 * clampf(swarm_scale, 1.0, 3.0))
	if (is_active or (is_charging and float(swarm_sim.get("DrillChargeRatio")) > 0.40)) and ion_particles.size() < active_limit:
		var base_pos: Vector2 = swarm_sim.get("GigaDrillBasePosition")
		var aim_dir: Vector2 = swarm_sim.get("GigaDrillDirection")
		var drill_r: float = float(swarm_sim.get("GigaDrillRadius"))
		var tangent: Vector2 = Vector2(-aim_dir.y, aim_dir.x)
		
		var spawn_count: int = int((6 if is_active else 3) * clampf(swarm_scale, 0.9, 2.5))
		for _s in range(spawn_count):
			var r_offset: float = (randf() - 0.5) * drill_r * 1.35
			var start_p: Vector2 = base_pos + tangent * r_offset
			var speed: float = randf_range(900.0, 1900.0) if is_active else randf_range(450.0, 950.0)
			var spread: float = randf_range(-0.32, 0.32)
			var vel: Vector2 = (-aim_dir).rotated(spread) * speed
			
			ion_particles.append({
				"pos": start_p,
				"vel": vel,
				"life": randf_range(0.14, 0.32),
				"max_life": 0.32,
				"color": Color(2.6, 5.0, 6.5, 1.0) if randf() > 0.35 else Color(5.2, 3.6, 1.2, 1.0)
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
	var _swarm_scale: float = clampf(total_units / 45.0, 0.75, 4.5)
	
	# =========================================================================
	# 1. EFEK CHARGING MEGAH: ACCRETION VORTEX, MULTI-POINT LIGHTNING, SOLAR APEX
	# =========================================================================
	if is_charging:
		var core_pos: Vector2 = player_core.global_position if player_core else base_pos
		
		# (a) Accretion Vortex: Cincin gravitasi menyusut ke ujung bor (seperti lubang hitam)
		var ring_count: int = 4
		for r in range(ring_count):
			var phase: float = fmod(spin_time * 2.2 + float(r) / ring_count, 1.0)
			var shrink_r: float = (1.0 - phase) * (135.0 * charge_ratio + 20.0)
			var ring_alpha: float = sin(phase * PI) * (0.45 + 0.55 * charge_ratio)
			var ring_col: Color = Color(1.8, 0.6, 4.5, ring_alpha).lerp(Color(2.4, 4.8, 6.0, ring_alpha), phase)
			draw_arc(tip_pos, shrink_r, 0, TAU, 28, ring_col, 1.4, true)
			
			# Garis lengan spiral yang terhisap ke pusat
			var arm_count: int = 6
			for a in range(arm_count):
				var a_angle: float = spin_time * 4.5 + (a * TAU / arm_count) + phase * 2.5
				var arm_dir: Vector2 = Vector2(cos(a_angle), sin(a_angle))
				var p_start: Vector2 = tip_pos + arm_dir * shrink_r
				var p_end: Vector2 = tip_pos + arm_dir.rotated(0.4) * (shrink_r * 0.45)
				draw_line(p_start, p_end, Color(2.5, 4.5, 6.0, ring_alpha * 0.7), 1.2, true)
		
		# (b) Multi-Point Branching Magnetic Lightning Arcs:
		# Dari Core ke Inti AP Defender (sian-putih) dan ke Kerucut Bor Luar (emas-oranye)
		var arc_count: int = 3 + int(charge_ratio * 5.0)
		for a in range(arc_count):
			var is_ap_arc: bool = (a % 2 == 0) and def_units > 0
			var target_d: float = (drill_len * 0.35) if is_ap_arc else (drill_len * 0.75)
			var target_r: float = (drill_r * 0.45) if is_ap_arc else (drill_r * 1.1)
			var target_p: Vector2 = tip_pos - aim_dir * target_d + tangent * ((randf() - 0.5) * target_r * 2.0)
			var arc_col: Color = Color(2.6, 5.0, 6.5, 0.95) if is_ap_arc else Color(5.2, 4.0, 1.2, 0.95)
			_draw_electric_arc(core_pos, target_p, 6, 14.0 * charge_ratio, arc_col, 1.6)
			
		# (c) Second Cone AP Shield di Ujung Bor: Kerucut tameng balistik terpasang di depan mata bor
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
			
			# Garis rusuk kerucut perisai AP kristal
			draw_line(nose_apex, nose_base_left, Color(2.8, 5.5, 7.0, 0.85 * charge_ratio), 2.2, true)
			draw_line(nose_apex, nose_base_right, Color(2.8, 5.5, 7.0, 0.85 * charge_ratio), 2.2, true)
			draw_line(nose_base_left, nose_base_right, Color(2.8, 5.5, 7.0, 0.55 * charge_ratio), 1.5, true)
			draw_line(tip_pos, nose_apex, Color(5.5, 5.5, 6.5, 0.95 * charge_ratio), 1.8, true)
			
			# Garis kisi faset pelindung
			var facet_count: int = 3
			for f in range(1, facet_count):
				var f_ratio: float = float(f) / facet_count
				var f_pt: Vector2 = tip_pos.lerp(nose_apex, f_ratio)
				var f_w: float = nose_r * (1.0 - f_ratio * 0.7)
				draw_line(f_pt + tangent * f_w, f_pt - tangent * f_w, Color(2.6, 5.0, 6.5, 0.6 * charge_ratio), 1.2, true)
			
		# (d) Tip Gravitational Singularity Corona & Solar Apex Starburst
		var tip_glow_r: float = 8.0 + charge_ratio * 18.0
		draw_arc(tip_pos, tip_glow_r, 0, TAU, 28, Color(5.2, 4.5, 2.0, 0.85), 1.6, true)
		draw_arc(tip_pos, tip_glow_r * 0.52, spin_time * 9.0, spin_time * 9.0 + TAU * 0.85, 20, Color(5.5, 5.5, 4.5, 1.0), 2.5, true)
		
		# Pancaran sinar starburst (semakin banyak saat mendekati 100%)
		var spike_count: int = 8 if charge_ratio < 0.9 else 16
		for s in range(spike_count):
			var angle: float = spin_time * 7.5 + (s * TAU / spike_count)
			var spike_dir: Vector2 = Vector2(cos(angle), sin(angle))
			var spike_len: float = tip_glow_r * (1.2 + 0.6 * sin(s * 2.5 + spin_time * 12.0))
			draw_line(tip_pos + spike_dir * (tip_glow_r * 0.35), tip_pos + spike_dir * spike_len, Color(5.5, 4.8, 2.8, 0.9), 1.4, true)
			
		# Screen rumble saat charge (meningkat secara organik)
		if arena and arena.has_method("trigger_shake") and charge_ratio > 0.15:
			arena.trigger_shake(charge_ratio * 4.2)

	# =========================================================================
	# 2. EFEK TRAIL & ION BEAM ROCKET EXHAUST BERSKALA
	# Di-render secara volumetrik & ultra-smooth oleh GPU Shader Mesh (ion_beam_mesh)
	# =========================================================================

	# =========================================================================
	# 3. EFEK SUPERSONIC BOW SHOCK & CORE-RAMMING OVERDRIVE SAAT MELUNCUR
	# =========================================================================
	if is_active:
		var has_breached: bool = swarm_sim.get("HasDefenderNoseBreached") == true
		var is_ramming_core: bool = swarm_sim.get("IsRammingEnemyCore") == true
		var nose_len: float = float(swarm_sim.get("GigaDrillNoseLength"))
		var active_apex: Vector2 = (tip_pos + aim_dir * nose_len) if (not has_breached and nose_len > 0.0) else tip_pos

		# 2 gelombang kejut kerucut di depan ujung bor / ujung nose cone
		for b in range(2):
			var offset_f: float = float(b) * 16.0
			var shock_pts: PackedVector2Array = [
				active_apex + aim_dir * (22.0 + offset_f),
				active_apex - aim_dir * (35.0 - offset_f) + tangent * (52.0 + offset_f * 1.5),
				active_apex - aim_dir * (35.0 - offset_f) - tangent * (52.0 + offset_f * 1.5)
			]
			var alpha: float = 0.95 - b * 0.35
			var col: Color = Color(2.8, 5.5, 7.0, alpha) if (not has_breached) else Color(5.5, 4.8, 2.5, alpha)
			draw_line(shock_pts[0], shock_pts[1], col, 2.2 - b * 0.5, true)
			draw_line(shock_pts[0], shock_pts[2], col, 2.2 - b * 0.5, true)

		# Jika sedang menempel & menggerus permukaan Core (0.5s Overdrive sebelum meledak),
		# gambar cincin kompresi plasma & petir liar di titik kontak Core!
		if is_ramming_core:
			var pulse_r: float = 18.0 + 12.0 * absf(sin(spin_time * 3.5))
			draw_circle(tip_pos, pulse_r * 0.65, Color(5.8, 5.2, 2.4, 0.85))
			draw_arc(tip_pos, pulse_r * 1.35, 0, TAU, 24, Color(5.5, 3.2, 1.2, 0.92), 2.6, true)
			for a in range(5):
				var ang: float = spin_time * 2.2 + a * (TAU / 5.0)
				var arc_end: Vector2 = tip_pos + Vector2(cos(ang), sin(ang)) * randf_range(28.0, 58.0)
				_draw_electric_arc(tip_pos, arc_end, 4, 10.0, Color(5.8, 4.8, 1.8, 0.95), 1.8)

		# Jika perisai hidung belum pecah, gambar rusuk kerucut kedua
		if not has_breached and nose_len > 0.0:
			var nose_r: float = float(swarm_sim.get("GigaDrillNoseRadius"))
			draw_line(active_apex, tip_pos + tangent * nose_r, Color(2.8, 5.5, 7.0, 0.85), 2.0, true)
			draw_line(active_apex, tip_pos - tangent * nose_r, Color(2.8, 5.5, 7.0, 0.85), 2.0, true)
			draw_line(tip_pos, active_apex, Color(5.5, 5.5, 6.5, 0.9), 1.6, true)

	# =========================================================================
	# 4. PARTIKEL LONCATAN ION
	# =========================================================================
	for p in ion_particles:
		var alpha: float = clampf(p["life"] / p["max_life"], 0.0, 1.0)
		var col: Color = p["color"]
		col.a = alpha
		var end_pt: Vector2 = p["pos"] - p["vel"].normalized() * (16.0 * alpha)
		draw_line(p["pos"], end_pt, col, 1.8, true)

	# =========================================================================
	# 5. FLASH BOLA INTI SUPERNOVA & CINCIN KEJUT DETONATION
	# =========================================================================
	for fl in supernova_flashes:
		var a: float = clampf(fl["life"] / fl["max_life"], 0.0, 1.0)
		var pos_fl: Vector2 = fl["pos"]
		var rad_fl: float = fl["radius"]
		draw_circle(pos_fl, rad_fl * 0.55, Color(5.8, 5.6, 4.2, a * 0.85))
		draw_arc(pos_fl, rad_fl, 0, TAU, 36, Color(2.8, 5.2, 6.8, a * 0.95), 4.2 * a, true)
		for l in range(8):
			var l_ang: float = l * (TAU / 8.0) + a * 3.5
			var l_end: Vector2 = pos_fl + Vector2(cos(l_ang), sin(l_ang)) * (rad_fl * 1.15)
			_draw_electric_arc(pos_fl, l_end, 5, 14.0 * a, Color(5.8, 4.8, 2.2, a), 1.8)

	for ring in detonation_rings:
		var c: Color = ring["color"]
		c.a = ring["alpha"]
		draw_arc(ring["pos"], ring["radius"], 0, TAU, 36, c, 3.4, true)
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
		c.a = ring["alpha"]
		draw_arc(ring["pos"], ring["radius"], 0, TAU, 32, c, 3.0, true)

	for shard in breach_shards:
		var alpha: float = clampf(shard["life"] / shard["max_life"], 0.0, 1.0)
		var col: Color = shard["color"]
		col.a = alpha
		var end_p: Vector2 = shard["pos"] + shard["vel"].normalized() * (shard["len"] * alpha)
		draw_line(shard["pos"], end_p, col, 2.2, true)

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
