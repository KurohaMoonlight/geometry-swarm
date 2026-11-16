class_name GigaDrillVFX
extends Node2D

## GigaDrillVFX - Spektakel Visual Super Giga Drill Break (Proporsional & Tajam)
## Mengatur:
## 1. Inti AP (Armor Piercing) 25% Defender Swarm: Pendaran kristal void-putih di dalam bor
## 2. Efek Charging Spektakuler: Abyssal Accretion Vortex (Hitam-Ungu-Putih), branching void lightning, solar corona apex
## 3. Triple-Plume Solar-Abyssal Corona Exhaust yang Proporsional (pas dengan diameter pangkal bor, tidak kebesaran!)
## 4. Efek Core-Ramming Overdrive & Abyssal Supernova Detonation Spektakuler

const IonBeamShader = preload("res://visual/giga_drill_ion_beam.gdshader")

var swarm_sim: Node2D = null
var player_core: Node2D = null
var arena: Node2D = null

var ion_beam_mesh: MeshInstance2D = null
var ion_beam_mat: ShaderMaterial = null
var wing_beam_left_mesh: MeshInstance2D = null
var wing_beam_left_mat: ShaderMaterial = null
var wing_beam_right_mesh: MeshInstance2D = null
var wing_beam_right_mat: ShaderMaterial = null

var ion_particles: Array[Dictionary] = []
var max_particles: int = 120

var detonation_rings: Array[Dictionary] = []
var breach_rings: Array[Dictionary] = []
var breach_shards: Array[Dictionary] = []
var supernova_flashes: Array[Dictionary] = []

var _was_charging: bool = false
var _was_active: bool = false

func _ready() -> void:
	z_index = 6
	
	ion_beam_mesh = MeshInstance2D.new()
	var quad: QuadMesh = QuadMesh.new()
	quad.size = Vector2(1.0, 1.0)
	ion_beam_mesh.mesh = quad
	ion_beam_mat = ShaderMaterial.new()
	ion_beam_mat.shader = IonBeamShader
	ion_beam_mesh.material = ion_beam_mat
	ion_beam_mesh.visible = false
	add_child(ion_beam_mesh)
	
	wing_beam_left_mesh = MeshInstance2D.new()
	wing_beam_left_mesh.mesh = quad
	wing_beam_left_mat = ShaderMaterial.new()
	wing_beam_left_mat.shader = IonBeamShader
	wing_beam_left_mesh.material = wing_beam_left_mat
	wing_beam_left_mesh.visible = false
	add_child(wing_beam_left_mesh)
	
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
	var swarm_scale: float = clampf(total_units / 45.0, 0.75, 3.2)
	
	_update_ion_beam_shader(is_charging, is_active, swarm_scale)
	_update_ion_particles(delta, is_charging, is_active, swarm_scale)
	_update_detonations(delta)
	_update_breach_fx(delta)
	_update_supernova_flashes(delta)
	
	var needs_redraw: bool = is_charging or is_active or _was_charging or _was_active \
		or ion_particles.size() > 0 or detonation_rings.size() > 0 \
		or breach_rings.size() > 0 or breach_shards.size() > 0 \
		or supernova_flashes.size() > 0
		
	_was_charging = is_charging
	_was_active = is_active
	
	if needs_redraw:
		queue_redraw()

func _update_ion_beam_shader(is_charging: bool, is_active: bool, swarm_scale: float) -> void:
	if not ion_beam_mesh or not ion_beam_mat or not swarm_sim:
		return
		
	var is_aligning: bool = swarm_sim.get("IsGigaDrillAligning") == true
	# Thruster belakang hanya aktif setelah arah pas (alignment selesai) dan bor resmi meluncur!
	# Tidak ada semburan thruster belakang selama priming maupun saat alignment.
	if not (is_active and not is_aligning):
		ion_beam_mesh.visible = false
		if wing_beam_left_mesh:
			wing_beam_left_mesh.visible = false
		if wing_beam_right_mesh:
			wing_beam_right_mesh.visible = false
		return
		
	var beam_power: float = 1.0
	var beam_len: float = 180.0 + 45.0 * swarm_scale
	var base_pos: Vector2 = swarm_sim.get("GigaDrillBasePosition")
	var aim_dir: Vector2 = swarm_sim.get("GigaDrillDirection")
	var drill_r: float = float(swarm_sim.get("GigaDrillRadius"))
	var spin_time: float = float(swarm_sim.get("GigaDrillSpinTime"))
	var exhaust_dir: Vector2 = -aim_dir
	var tangent: Vector2 = Vector2(-aim_dir.y, aim_dir.x)
	
	# Lebar semburan pas mengikuti diameter pangkal bor (2.0 * drill_r + margin tipis korona)
	var plume_width: float = (drill_r * 2.15) * (1.0 + 0.12 * swarm_scale)
	var diamonds: float = clampf(5.0 + 2.2 * swarm_scale, 5.0, 11.0)
	
	ion_beam_mesh.visible = true
	ion_beam_mesh.global_position = base_pos + exhaust_dir * (beam_len * 0.46)
	ion_beam_mesh.global_rotation = exhaust_dir.angle()
	ion_beam_mesh.scale = Vector2(beam_len, plume_width)
	
	ion_beam_mat.set_shader_parameter("power", beam_power)
	ion_beam_mat.set_shader_parameter("spin_time", spin_time)
	ion_beam_mat.set_shader_parameter("rpm", 36.0)
	ion_beam_mat.set_shader_parameter("swarm_scale", swarm_scale)
	ion_beam_mat.set_shader_parameter("diamond_count", diamonds)
	
	if wing_beam_left_mesh and wing_beam_right_mesh and wing_beam_left_mat and wing_beam_right_mat:
		wing_beam_left_mesh.visible = true
		wing_beam_right_mesh.visible = true
		var wing_power: float = 0.92
		var wing_len: float = beam_len * 0.62
		var wing_width: float = plume_width * 0.48
		var wing_spread: float = 0.12
		var dir_left: Vector2 = exhaust_dir.rotated(-wing_spread)
		var dir_right: Vector2 = exhaust_dir.rotated(wing_spread)
		var nozzle_left: Vector2 = base_pos + tangent * (drill_r * 0.45)
		var nozzle_right: Vector2 = base_pos - tangent * (drill_r * 0.45)
		
		wing_beam_left_mesh.global_position = nozzle_left + dir_left * (wing_len * 0.45)
		wing_beam_left_mesh.global_rotation = dir_left.angle()
		wing_beam_left_mesh.scale = Vector2(wing_len, wing_width)
		wing_beam_left_mat.set_shader_parameter("power", wing_power)
		wing_beam_left_mat.set_shader_parameter("spin_time", spin_time + 1.57)
		wing_beam_left_mat.set_shader_parameter("rpm", 40.0)
		wing_beam_left_mat.set_shader_parameter("swarm_scale", swarm_scale * 0.85)
		wing_beam_left_mat.set_shader_parameter("diamond_count", diamonds * 0.75)
		
		wing_beam_right_mesh.global_position = nozzle_right + dir_right * (wing_len * 0.45)
		wing_beam_right_mesh.global_rotation = dir_right.angle()
		wing_beam_right_mesh.scale = Vector2(wing_len, wing_width)
		wing_beam_right_mat.set_shader_parameter("power", wing_power)
		wing_beam_right_mat.set_shader_parameter("spin_time", spin_time + 3.14)
		wing_beam_right_mat.set_shader_parameter("rpm", 40.0)
		wing_beam_right_mat.set_shader_parameter("swarm_scale", swarm_scale * 0.85)
		wing_beam_right_mat.set_shader_parameter("diamond_count", diamonds * 0.75)

func trigger_launch_shake() -> void:
	if arena and arena.has_method("trigger_shake"):
		arena.trigger_shake(18.0)

func trigger_nose_breach(breach_pos: Vector2, aim_dir: Vector2, count: int) -> void:
	if arena and arena.has_method("trigger_shake"):
		arena.trigger_shake(14.0)
		
	breach_rings.append({
		"pos": breach_pos,
		"radius": 10.0,
		"max_radius": 250.0,
		"speed": 780.0,
		"color": Color(7.2, 6.8, 8.2, 1.0),
		"alpha": 1.0
	})
	breach_rings.append({
		"pos": breach_pos,
		"radius": 4.0,
		"max_radius": 185.0,
		"speed": 560.0,
		"color": Color(2.6, 0.25, 5.4, 1.0),
		"alpha": 1.0
	})
	
	var shard_count: int = clampi(count * 3, 20, 52)
	for _i in range(shard_count):
		var spread: float = randf_range(-0.88, 0.88)
		var shard_dir: Vector2 = aim_dir.rotated(spread)
		var speed: float = randf_range(680.0, 1650.0)
		var roll: float = randf()
		var s_col: Color = Color(7.4, 7.0, 8.4, 1.0) if roll > 0.55 else (Color(2.6, 0.28, 5.5, 1.0) if roll > 0.20 else Color(0.015, 0.003, 0.03, 0.95))
		breach_shards.append({
			"pos": breach_pos + shard_dir * randf_range(4.0, 22.0),
			"vel": shard_dir * speed,
			"len": randf_range(9.0, 26.0),
			"life": randf_range(0.22, 0.44),
			"max_life": 0.44,
			"color": s_col
		})

func trigger_detonation(tip_pos: Vector2, base_pos: Vector2, count: int) -> void:
	if count <= 0:
		if arena and arena.has_method("trigger_shake"):
			arena.trigger_shake(8.5)
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 8.0,
			"max_radius": 135.0,
			"speed": 540.0,
			"color": Color(7.2, 6.8, 8.2, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 3.0,
			"max_radius": 95.0,
			"speed": 390.0,
			"color": Color(2.2, 0.20, 4.8, 1.0),
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
				"color": Color(7.2, 6.8, 8.2, 1.0) if randf() > 0.45 else Color(2.4, 0.22, 5.0, 1.0)
			})
		return

	if count > 0:
		if arena and arena.has_method("trigger_shake"):
			arena.trigger_shake(24.0)
			
		var scale_f: float = clampf(float(count) / 18.0, 1.0, 1.75)
		
		supernova_flashes.append({
			"pos": tip_pos,
			"radius": 48.0 * scale_f,
			"max_radius": 270.0 * scale_f,
			"life": 0.46,
			"max_life": 0.46
		})
		
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 18.0,
			"max_radius": 440.0 * scale_f,
			"speed": 1080.0,
			"color": Color(7.6, 7.2, 8.6, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 12.0,
			"max_radius": 360.0 * scale_f,
			"speed": 850.0,
			"color": Color(4.2, 0.72, 6.8, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 8.0,
			"max_radius": 285.0 * scale_f,
			"speed": 660.0,
			"color": Color(0.015, 0.003, 0.03, 0.96),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": tip_pos,
			"radius": 4.0,
			"max_radius": 210.0 * scale_f,
			"speed": 480.0,
			"color": Color(2.0, 0.16, 4.4, 1.0),
			"alpha": 1.0
		})
		detonation_rings.append({
			"pos": base_pos,
			"radius": 8.0,
			"max_radius": 235.0,
			"speed": 600.0,
			"color": Color(4.8, 1.2, 7.2, 1.0),
			"alpha": 1.0
		})
		
		for _i in range(64):
			var ang: float = randf() * TAU
			var dir: Vector2 = Vector2(cos(ang), sin(ang))
			var spd: float = randf_range(580.0, 1920.0)
			var roll: float = randf()
			var s_col: Color = Color(7.6, 7.2, 8.6, 1.0) if roll < 0.42 else (Color(2.6, 0.25, 5.6, 1.0) if roll < 0.78 else Color(0.015, 0.003, 0.03, 0.95))
			breach_shards.append({
				"pos": tip_pos + dir * randf_range(4.0, 24.0),
				"vel": dir * spd,
				"len": randf_range(15.0, 38.0),
				"life": randf_range(0.30, 0.62),
				"max_life": 0.62,
				"color": s_col
			})
		return

	if arena and arena.has_method("trigger_shake"):
		arena.trigger_shake(10.0)
	detonation_rings.append({
		"pos": tip_pos,
		"radius": 10.0,
		"max_radius": 190.0,
		"speed": 540.0,
		"color": Color(2.6, 0.28, 5.4, 1.0),
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
		
	var is_aligning: bool = swarm_sim.get("IsGigaDrillAligning") == true
	var active_limit: int = int(42 * clampf(swarm_scale, 1.0, 2.4))
	if (is_active and not is_aligning) and ion_particles.size() < active_limit:
		var base_pos: Vector2 = swarm_sim.get("GigaDrillBasePosition")
		var aim_dir: Vector2 = swarm_sim.get("GigaDrillDirection")
		var drill_r: float = float(swarm_sim.get("GigaDrillRadius"))
		var tangent: Vector2 = Vector2(-aim_dir.y, aim_dir.x)
		
		var spawn_count: int = int(5 * clampf(swarm_scale, 0.9, 1.8))
		for _s in range(spawn_count):
			var r_offset: float = (randf() - 0.5) * drill_r * 1.15
			var start_p: Vector2 = base_pos + tangent * r_offset
			var speed: float = randf_range(750.0, 1550.0)
			var spread: float = randf_range(-0.24, 0.24)
			var vel: Vector2 = (-aim_dir).rotated(spread) * speed
			var roll: float = randf()
			var p_col: Color = Color(7.4, 7.0, 8.4, 1.0) if roll > 0.50 else (Color(2.6, 0.24, 5.4, 1.0) if roll > 0.18 else Color(0.015, 0.003, 0.03, 0.95))
			
			ion_particles.append({
				"pos": start_p,
				"vel": vel,
				"life": randf_range(0.12, 0.26),
				"max_life": 0.26,
				"color": p_col
			})

func _draw() -> void:
	if not swarm_sim or swarm_sim.get("PlayerHealth") == null:
		return
		
	var is_charging: bool = swarm_sim.get("IsChargingDrill") == true
	var is_active: bool = swarm_sim.get("IsGigaDrillActive") == true
	var is_aligning: bool = swarm_sim.get("IsGigaDrillAligning") == true
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
	var swarm_scale: float = clampf(total_units / 45.0, 0.75, 3.2)
	
	# =========================================================================
	# 1. EFEK CHARGING MEGAH: ABYSSAL ACCRETION VORTEX, VOID LIGHTNING, APEX CORONA
	# =========================================================================
	if is_charging:
		var core_pos: Vector2 = player_core.global_position if player_core else base_pos
		
		# (0) Cetak Biru Holografik (Holographic Blueprint Grid): Proyeksi kisi wireframe sepanjang bentuk bor
		if charge_ratio < 0.48:
			var bp_fade: float = clampf((0.48 - charge_ratio) / 0.48, 0.0, 1.0)
			var bp_alpha: float = bp_fade * (0.42 + 0.12 * sin(spin_time * 10.0))
			var bp_cyan: Color = Color(0.25, 0.90, 1.0, bp_alpha)
			var bp_purple: Color = Color(0.70, 0.28, 1.0, bp_alpha * 0.65)
			
			var bp_apex: Vector2 = tip_pos + aim_dir * (drill_len * 0.28)
			var bp_base_l: Vector2 = base_pos + tangent * drill_r
			var bp_base_r: Vector2 = base_pos - tangent * drill_r
			
			draw_line(bp_apex, bp_base_l, bp_cyan, 1.3, false)
			draw_line(bp_apex, bp_base_r, bp_cyan, 1.3, false)
			draw_line(bp_base_l, bp_base_r, bp_purple, 1.0, false)
			
			for rib in range(1, 5):
				var rib_t: float = float(rib) / 5.0
				var rib_pos: Vector2 = bp_apex.lerp(base_pos, rib_t)
				var rib_r: float = drill_r * pow(rib_t, 0.82)
				draw_arc(rib_pos, rib_r, 0, TAU, 24, bp_purple, 1.1, false)
				draw_line(rib_pos + tangent * (rib_r + 5.0), rib_pos + tangent * (rib_r - 5.0), bp_cyan, 1.2, false)
				draw_line(rib_pos - tangent * (rib_r + 5.0), rib_pos - tangent * (rib_r - 5.0), bp_cyan, 1.2, false)
		
		# (0b) Scanner Seeker Forward Radar Pulse (Gelombang pulsa sonar kerucut ke depan)
		var scan_count: int = int(swarm_sim.get("GigaDrillScannerCount"))
		if scan_count > 0 and charge_ratio >= 0.08:
			var scanner_pos: Vector2 = swarm_sim.get("GigaDrillScannerPosition")
			if scanner_pos == Vector2.ZERO:
				scanner_pos = tip_pos + aim_dir * 18.0
			
			var fwd_angle: float = aim_dir.angle()
			for p in range(3):
				var p_phase: float = fmod(spin_time * 2.4 + float(p) / 3.0, 1.0)
				var p_dist: float = lerpf(12.0, 260.0, p_phase)
				var p_alpha: float = sin(p_phase * PI) * (0.35 + 0.45 * clampf(charge_ratio, 0.0, 1.0))
				var p_col: Color = Color(0.25, 0.92, 1.0, p_alpha)
				draw_arc(scanner_pos, p_dist, fwd_angle - 0.44, fwd_angle + 0.44, 20, p_col, 1.5, false)
				draw_line(scanner_pos, scanner_pos + aim_dir * (p_dist * 0.95), Color(0.3, 0.95, 1.0, p_alpha * 0.5), 1.0, false)
		
		var ring_count: int = 4
		for r in range(ring_count):
			var phase: float = fmod(spin_time * 2.4 + float(r) / ring_count, 1.0)
			var shrink_r: float = (1.0 - phase) * (125.0 * charge_ratio + 18.0)
			var ring_alpha: float = sin(phase * PI) * (0.50 + 0.50 * charge_ratio)
			var ring_col: Color = Color(1.8, 0.14, 3.8, ring_alpha).lerp(Color(7.2, 6.8, 8.2, ring_alpha), phase * phase)
			draw_arc(tip_pos, shrink_r, 0, TAU, 32, ring_col, 1.6, true)
			draw_arc(tip_pos, shrink_r * 0.92, 0, TAU, 28, Color(0.012, 0.003, 0.025, ring_alpha * 0.88), 2.2, true)
			
			var arm_count: int = 6
			for a in range(arm_count):
				var a_angle: float = spin_time * 4.8 + (a * TAU / arm_count) + phase * 2.6
				var arm_dir: Vector2 = Vector2(cos(a_angle), sin(a_angle))
				var p_start: Vector2 = tip_pos + arm_dir * shrink_r
				var p_end: Vector2 = tip_pos + arm_dir.rotated(0.45) * (shrink_r * 0.42)
				draw_line(p_start, p_end, Color(2.6, 0.25, 5.2, ring_alpha * 0.82), 1.4, true)
		
		var arc_count: int = 4 + int(charge_ratio * 5.0)
		for a in range(arc_count):
			var is_ap_arc: bool = (a % 2 == 0) and def_units > 0
			var target_d: float = (drill_len * 0.35) if is_ap_arc else (drill_len * 0.78)
			var target_r: float = (drill_r * 0.45) if is_ap_arc else (drill_r * 1.05)
			var target_p: Vector2 = tip_pos - aim_dir * target_d + tangent * ((randf() - 0.5) * target_r * 2.0)
			var arc_col: Color = Color(7.2, 6.8, 8.2, 0.95) if is_ap_arc else Color(2.6, 0.25, 5.4, 0.95)
			_draw_electric_arc(core_pos, target_p, 6, 13.0 * charge_ratio, arc_col, 1.6)
			
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
			
			draw_line(nose_apex, nose_base_left, Color(2.8, 0.38, 5.8, 0.88 * charge_ratio), 2.2, true)
			draw_line(nose_apex, nose_base_right, Color(2.8, 0.38, 5.8, 0.88 * charge_ratio), 2.2, true)
			draw_line(nose_base_left, nose_base_right, Color(1.8, 0.16, 4.0, 0.65 * charge_ratio), 1.5, true)
			draw_line(tip_pos, nose_apex, Color(7.2, 6.8, 8.2, 0.96 * charge_ratio), 1.8, true)
			
			var facet_count: int = 3
			for f in range(1, facet_count):
				var f_ratio: float = float(f) / facet_count
				var f_pt: Vector2 = tip_pos.lerp(nose_apex, f_ratio)
				var f_w: float = nose_r * (1.0 - f_ratio * 0.7)
				draw_line(f_pt + tangent * f_w, f_pt - tangent * f_w, Color(3.6, 0.60, 6.4, 0.65 * charge_ratio), 1.2, true)
			
		var tip_glow_r: float = 8.0 + charge_ratio * 16.0
		draw_circle(tip_pos, tip_glow_r * 0.48, Color(0.010, 0.002, 0.022, 0.96))
		draw_arc(tip_pos, tip_glow_r, 0, TAU, 28, Color(2.6, 0.25, 5.4, 0.90), 1.8, true)
		draw_arc(tip_pos, tip_glow_r * 0.54, spin_time * 9.5, spin_time * 9.5 + TAU * 0.85, 22, Color(7.4, 7.0, 8.4, 1.0), 2.4, true)
		
		var spike_count: int = 8 if charge_ratio < 0.9 else 14
		for s in range(spike_count):
			var angle: float = spin_time * 8.0 + (s * TAU / spike_count)
			var spike_dir: Vector2 = Vector2(cos(angle), sin(angle))
			var spike_len: float = tip_glow_r * (1.20 + 0.55 * sin(s * 2.5 + spin_time * 12.0))
			var sp_col: Color = Color(7.4, 7.0, 8.4, 0.92) if (s % 2 == 0) else Color(2.6, 0.28, 5.6, 0.90)
			draw_line(tip_pos + spike_dir * (tip_glow_r * 0.35), tip_pos + spike_dir * spike_len, sp_col, 1.4, true)
			
		if arena and arena.has_method("trigger_shake") and charge_ratio > 0.15:
			arena.trigger_shake(charge_ratio * 4.5)

	# =========================================================================
	# 2. SOLAR-ABYSSAL CORONA ERUPTION COMPACT DI PANGKAL BOR (PROPORSIONAL)
	# Hanya menyembur saat bor sudah resmi meluncur (setelah alignment selesai)!
	# =========================================================================
	if is_active and not is_aligning:
		var ex_power: float = 1.0
		var exhaust_dir: Vector2 = -aim_dir
		var nozzle_orb_r: float = (7.5 + 3.5 * swarm_scale) * ex_power * (0.92 + 0.08 * sin(spin_time * 15.0))
		
		# (a) 2 Cincin Korona Kompak di Mulut Nozzle
		for c_ring in range(2):
			var c_phase: float = fmod(spin_time * 3.0 + float(c_ring) * 0.5, 1.0)
			var c_rad: float = lerpf(nozzle_orb_r * 0.75, drill_r * (0.95 + 0.15 * swarm_scale), c_phase)
			var c_alpha: float = sin(c_phase * PI) * 0.78 * ex_power
			var c_col: Color = Color(2.8, 0.28, 5.8, c_alpha) if (c_ring % 2 == 0) else Color(7.4, 7.0, 8.4, c_alpha * 0.85)
			draw_arc(base_pos + exhaust_dir * (c_phase * 22.0), c_rad, 0, TAU, 28, c_col, 1.8, true)
		
		# (b) 6 Busur Magnetik Solar Prominence Loops Proporsional di Pangkal Thruster
		var loop_count: int = 6
		for l_idx in range(loop_count):
			var side_s: float = 1.0 if (l_idx % 2 == 0) else -1.0
			var tier_idx: int = int(l_idx / 2)
			var l_phase: float = fmod(spin_time * 2.6 + l_idx * 0.31, 1.0)
			var l_sin: float = sin(l_phase * PI)
			var spread_ang: float = side_s * (0.26 + tier_idx * 0.16 + 0.06 * sin(spin_time * 6.0 + l_idx))
			var arch_dir: Vector2 = exhaust_dir.rotated(spread_ang)
			var arch_norm: Vector2 = Vector2(-arch_dir.y, arch_dir.x) * side_s
			
			var arch_reach: float = (20.0 + 14.0 * swarm_scale) * ex_power * l_sin
			var p_start: Vector2 = base_pos + tangent * (side_s * drill_r * (0.28 + tier_idx * 0.14))
			var p_ctrl: Vector2 = base_pos + arch_dir * arch_reach + arch_norm * (10.0 * ex_power)
			var p_end: Vector2 = base_pos + exhaust_dir * (arch_reach * 1.20) + tangent * (side_s * drill_r * 0.18)
			
			var col_purple: Color = Color(2.8, 0.24, 5.8, l_sin * 0.88 * ex_power)
			var col_void: Color = Color(0.010, 0.002, 0.022, l_sin * 0.90 * ex_power)
			var col_white: Color = Color(7.8, 7.5, 8.8, l_sin * 0.94 * ex_power)
			
			draw_line(p_start, p_ctrl, col_purple, 3.0, true)
			draw_line(p_ctrl, p_end, col_purple, 2.6, true)
			draw_line(p_start, p_ctrl, col_void, 1.8, true)
			draw_line(p_ctrl, p_end, col_void, 1.6, true)
			draw_line(p_start, p_ctrl, col_white, 1.0, true)
			draw_line(p_ctrl, p_end, col_white, 0.9, true)
		
		# (c) Inti Singularitas Korona Kompak di Mulut Nozzle
		draw_circle(base_pos, nozzle_orb_r * 1.75, Color(1.85, 0.14, 4.2, 0.44 * ex_power))
		draw_circle(base_pos, nozzle_orb_r * 1.05, Color(7.8, 7.4, 8.8, 0.95 * ex_power))
		draw_circle(base_pos, nozzle_orb_r * 0.65, Color(0.010, 0.002, 0.022, 0.97 * ex_power))
		draw_circle(base_pos, nozzle_orb_r * 0.26, Color(8.2, 8.0, 9.2, 0.99 * ex_power))
		
		# (d) Sambaran petir plasma Putih & Ungu Tua yang rapi di dalam ekor exhaust
		var tail_arc_count: int = 4
		var max_arc_reach: float = (110.0 + 28.0 * swarm_scale) * ex_power
		for t_idx in range(tail_arc_count):
			var spread_f: float = (float(t_idx) / maxf(1.0, float(tail_arc_count - 1)) - 0.5) * 2.0
			var start_n: Vector2 = base_pos + tangent * (spread_f * drill_r * 0.65)
			var end_n: Vector2 = base_pos + exhaust_dir * randf_range(55.0, max_arc_reach) + tangent * (spread_f * drill_r * 0.95 + randf_range(-14.0, 14.0))
			var bolt_col: Color = Color(7.6, 7.2, 8.6, 0.92 * ex_power) if (t_idx % 2 == 0) else Color(3.2, 0.35, 6.2, 0.88 * ex_power)
			_draw_electric_arc(start_n, end_n, 5, 11.0 * ex_power, bolt_col, 1.7 if (t_idx % 2 == 0) else 1.3)

	# =========================================================================
	# 2B. SCANNER RADAR PULSE SAAT ALIGNMENT (TANPA CROSSHAIR PADA MUSUH)
	# Hanya aktif jika Scanner Drone masih utuh (scan_count > 0)!
	# =========================================================================
	if is_aligning:
		var scan_count: int = int(swarm_sim.get("GigaDrillScannerCount"))
		if scan_count > 0:
			var scanner_pos: Vector2 = swarm_sim.get("GigaDrillScannerPosition")
			if scanner_pos == Vector2.ZERO:
				scanner_pos = tip_pos + aim_dir * 18.0
			
			# Gelombang sonar radar frekuensi tinggi memancar ke depan moncong bor (bersih tanpa crosshair pada target)
			for p in range(3):
				var p_phase: float = fmod(spin_time * 5.0 + float(p) / 3.0, 1.0)
				var p_dist: float = p_phase * 220.0
				var p_alpha: float = (1.0 - p_phase) * 0.72
				var p_col: Color = Color(0.2, 0.95, 1.0, p_alpha)
				draw_arc(scanner_pos, p_dist, aim_dir.angle() - 0.32, aim_dir.angle() + 0.32, 16, p_col, 1.8, false)

	# =========================================================================
	# 3. EFEK SUPERSONIC BOW SHOCK & CORE-RAMMING OVERDRIVE SAAT MELUNCUR
	# =========================================================================
	if is_active and not is_aligning:
		var has_breached: bool = swarm_sim.get("HasDefenderNoseBreached") == true
		var is_ramming_core: bool = swarm_sim.get("IsRammingEnemyCore") == true
		var nose_len: float = float(swarm_sim.get("GigaDrillNoseLength"))
		var active_apex: Vector2 = (tip_pos + aim_dir * nose_len) if (not has_breached and nose_len > 0.0) else tip_pos

		for b in range(3):
			var offset_f: float = float(b) * 12.0
			var shock_pts: PackedVector2Array = [
				active_apex + aim_dir * (20.0 + offset_f),
				active_apex - aim_dir * (32.0 - offset_f) + tangent * (46.0 + offset_f * 1.4),
				active_apex - aim_dir * (32.0 - offset_f) - tangent * (46.0 + offset_f * 1.4)
			]
			var col: Color
			if b == 0:
				col = Color(7.6, 7.2, 8.6, 0.96)
			elif b == 1:
				col = Color(0.012, 0.003, 0.025, 0.94)
			else:
				col = Color(2.6, 0.25, 5.4, 0.85)
			var w: float = 2.6 if b == 1 else (2.1 - b * 0.35)
			draw_line(shock_pts[0], shock_pts[1], col, w, true)
			draw_line(shock_pts[0], shock_pts[2], col, w, true)

		if is_ramming_core:
			var pulse_r: float = 18.0 + 12.0 * absf(sin(spin_time * 4.0))
			draw_circle(tip_pos, pulse_r * 0.95, Color(0.010, 0.002, 0.022, 0.95))
			draw_circle(tip_pos, pulse_r * 0.55, Color(7.8, 7.4, 8.8, 0.96))
			draw_arc(tip_pos, pulse_r * 1.40, 0, TAU, 28, Color(3.2, 0.35, 6.2, 0.95), 2.8, true)
			for a in range(6):
				var ang: float = spin_time * 2.5 + a * (TAU / 6.0)
				var arc_end: Vector2 = tip_pos + Vector2(cos(ang), sin(ang)) * randf_range(28.0, 58.0)
				var r_col: Color = Color(7.8, 7.4, 8.8, 0.96) if (a % 2 == 0) else Color(3.2, 0.35, 6.2, 0.94)
				_draw_electric_arc(tip_pos, arc_end, 4, 10.0, r_col, 1.8)

		if not has_breached and nose_len > 0.0:
			var nose_r: float = float(swarm_sim.get("GigaDrillNoseRadius"))
			draw_line(active_apex, tip_pos + tangent * nose_r, Color(2.8, 0.35, 5.8, 0.90), 2.0, true)
			draw_line(active_apex, tip_pos - tangent * nose_r, Color(2.8, 0.35, 5.8, 0.90), 2.0, true)
			draw_line(tip_pos, active_apex, Color(7.6, 7.2, 8.6, 0.95), 1.6, true)

	# =========================================================================
	# 3B. DART CANARD MINI-THRUSTERS (MINIATURISASI SPACE BAR THRUSTER)
	# Menggunakan palet & struktur lengkap XF-09 Ares Abyssal Plasma Boost:
	# - Mahkota Korona Nozzle (Radial Starburst Crown)
	# - 3 Node Berlian Kejut Supersonik (Mach Shock Diamonds)
	# - Selubung Kelopak Jet Aerospike (Royal Purple Sheath)
	# - Spina Void Hitam Pekat + Inti Putih Menyilaukan (HDR Bloom)
	# - Busur Lidah Api Prominence Korona yang dinamis melengkung
	# HANYA sisi yang membelokkan bor yang menyembur (fire_side sesuai)!
	# =========================================================================
	var right_count: int = int(swarm_sim.get("GigaDrillCanardRightCount"))
	var left_count: int = int(swarm_sim.get("GigaDrillCanardLeftCount"))
	if (right_count > 0 or left_count > 0) and is_active:
		var fire_side: float = float(swarm_sim.get("GigaDrillCanardFireSide"))
		var pulse_active: bool = swarm_sim.get("GigaDrillCanardPulseActive") == true
		
		# 1. Gambar Canard Sisi Kanan (side = +1.0)
		for r in range(right_count):
			var side: float = 1.0
			var slot_rank: int = r
			var d: float = drill_len * (0.05 + slot_rank * 0.055)
			var body_r_at_d: float = drill_r * pow(maxf(0.04, d / maxf(1.0, drill_len)), 0.82)
			var r_canard: float = body_r_at_d + 14.0 + slot_rank * 4.5
			var c_pos: Vector2 = (tip_pos - aim_dir * d) + (tangent * (side * r_canard))
			var c_fwd: Vector2 = aim_dir.rotated(side * 0.18)
			var c_exhaust: Vector2 = -c_fwd
			var c_tangent: Vector2 = Vector2(-c_exhaust.y, c_exhaust.x)
			var c_nozzle: Vector2 = c_pos + c_exhaust * 6.5
			
			# Hanya nyala jika kemudi memerintahkan belok KIRI (fire_side > 0.0 -> thruster kanan nyala)
			var should_fire: bool = pulse_active and fire_side > 0.0
			if should_fire:
				var power: float = 1.0 if not is_aligning else (0.85 + 0.15 * sin(spin_time * 35.0 + float(r)))
				_draw_mini_spacebar_thruster(c_nozzle, c_exhaust, c_tangent, power, is_aligning, r, spin_time, swarm_scale)
					
		# 2. Gambar Canard Sisi Kiri (side = -1.0)
		for l in range(left_count):
			var side: float = -1.0
			var slot_rank: int = l
			var d: float = drill_len * (0.05 + slot_rank * 0.055)
			var body_r_at_d: float = drill_r * pow(maxf(0.04, d / maxf(1.0, drill_len)), 0.82)
			var r_canard: float = body_r_at_d + 14.0 + slot_rank * 4.5
			var c_pos: Vector2 = (tip_pos - aim_dir * d) + (tangent * (side * r_canard))
			var c_fwd: Vector2 = aim_dir.rotated(side * 0.18)
			var c_exhaust: Vector2 = -c_fwd
			var c_tangent: Vector2 = Vector2(-c_exhaust.y, c_exhaust.x)
			var c_nozzle: Vector2 = c_pos + c_exhaust * 6.5
			
			# Hanya nyala jika kemudi memerintahkan belok KANAN (fire_side < 0.0 -> thruster kiri nyala)
			var should_fire: bool = pulse_active and fire_side < 0.0
			if should_fire:
				var power: float = 1.0 if not is_aligning else (0.85 + 0.15 * sin(spin_time * 35.0 + float(l) + 10.0))
				_draw_mini_spacebar_thruster(c_nozzle, c_exhaust, c_tangent, power, is_aligning, l + 10, spin_time, swarm_scale)

	# =========================================================================
	# 4. PARTIKEL LONCATAN PLASMA & VOID EMBER
	# =========================================================================
	for p in ion_particles:
		var alpha: float = clampf(p["life"] / p["max_life"], 0.0, 1.0)
		var col: Color = p["color"]
		col.a *= alpha
		var end_pt: Vector2 = p["pos"] - p["vel"].normalized() * (14.0 * alpha)
		draw_line(p["pos"], end_pt, col, 1.8, true)

	# =========================================================================
	# 5. FLASH BOLA INTI ABYSSAL SUPERNOVA & CINCIN KEJUT DETONATION
	# =========================================================================
	for fl in supernova_flashes:
		var a: float = clampf(fl["life"] / fl["max_life"], 0.0, 1.0)
		var pos_fl: Vector2 = fl["pos"]
		var rad_fl: float = fl["radius"]
		draw_circle(pos_fl, rad_fl * 0.72, Color(0.010, 0.002, 0.022, a * 0.95))
		draw_circle(pos_fl, rad_fl * 0.42, Color(7.8, 7.4, 8.8, a * 0.96))
		draw_arc(pos_fl, rad_fl, 0, TAU, 36, Color(3.2, 0.35, 6.2, a * 0.96), 4.6 * a, true)
		for l in range(10):
			var l_ang: float = l * (TAU / 10.0) + a * 3.8
			var l_end: Vector2 = pos_fl + Vector2(cos(l_ang), sin(l_ang)) * (rad_fl * 1.22)
			var l_col: Color = Color(7.8, 7.4, 8.8, a) if (l % 2 == 0) else Color(3.2, 0.35, 6.2, a)
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

func _draw_mini_spacebar_thruster(nozzle_pos: Vector2, exhaust_dir: Vector2, tangent_dir: Vector2, power: float, is_align_puff: bool, seed_idx: int, spin_time: float, swarm_scale: float) -> void:
	if power <= 0.01:
		return
		
	# Panjang dan lebar semburan yang proporsional dengan jarum Dart Needle (~14px)
	var base_len: float = randf_range(18.0, 28.0) if is_align_puff else (26.0 + 8.0 * swarm_scale) * (0.92 + 0.08 * sin(spin_time * 26.0 + float(seed_idx)))
	var plume_len: float = base_len * power
	var plume_max_w: float = 6.0 * power
	
	# 1. KELOPAK JET PLASMA AEROSPIKE (OUTER SHEATH POLYGON)
	# Poligon aerospike yang melebar lalu meruncing ke ekor semburan
	var pts: PackedVector2Array = PackedVector2Array([
		nozzle_pos + tangent_dir * (plume_max_w * 0.35),
		nozzle_pos + exhaust_dir * (plume_len * 0.28) + tangent_dir * (plume_max_w * 0.95),
		nozzle_pos + exhaust_dir * (plume_len * 0.65) + tangent_dir * (plume_max_w * 0.52),
		nozzle_pos + exhaust_dir * plume_len,
		nozzle_pos + exhaust_dir * (plume_len * 0.65) - tangent_dir * (plume_max_w * 0.52),
		nozzle_pos + exhaust_dir * (plume_len * 0.28) - tangent_dir * (plume_max_w * 0.95),
		nozzle_pos - tangent_dir * (plume_max_w * 0.35)
	])
	# Deep Abyssal Violet/Purple Sheath
	draw_colored_polygon(pts, Color(2.15, 0.16, 4.65, 0.68 * power))
	draw_polyline(pts, Color(4.4, 0.72, 7.2, 0.85 * power), 1.2, true)
	
	# 2. BUSUR PROMINENCE KORONA (SOLAR PROMINENCE LOOPS)
	# Busur plasma melengkung yang menjilat keluar dari sisi kiri dan kanan mulut nozzle
	for s_sign in [-1.0, 1.0]:
		var loop_phase: float = fmod(spin_time * 3.5 + float(seed_idx) * 0.4 + s_sign * 0.2, 1.0)
		var loop_reach: float = plume_len * 0.45 * sin(loop_phase * PI)
		var p0: Vector2 = nozzle_pos + tangent_dir * (s_sign * 2.2)
		var p_mid: Vector2 = nozzle_pos + exhaust_dir * (loop_reach * 0.65) + tangent_dir * (s_sign * (plume_max_w * 1.35))
		var p1: Vector2 = nozzle_pos + exhaust_dir * (loop_reach * 1.05)
		draw_line(p0, p_mid, Color(2.45, 0.20, 5.20, 0.80 * power), 1.6, false)
		draw_line(p_mid, p1, Color(2.45, 0.20, 5.20, 0.80 * power), 1.4, false)
		draw_line(p0, p_mid, Color(7.4, 7.2, 8.6, 0.92 * power), 0.8, false)
		draw_line(p_mid, p1, Color(7.4, 7.2, 8.6, 0.92 * power), 0.7, false)
	
	# 3. SPINA OBSIDIAN VOID HITAM PEKAT & INTI PLASMA PUTIH MENYILAUKAN (HDR)
	# Garis hitam pekat obsidian di tengah semburan (signature void)
	draw_line(nozzle_pos, nozzle_pos + exhaust_dir * (plume_len * 0.86), Color(0.010, 0.002, 0.020, 0.95 * power), 2.4, false)
	# Garis putih menyilaukan super-panas di dalam void
	draw_line(nozzle_pos, nozzle_pos + exhaust_dir * (plume_len * 0.70), Color(7.8, 7.5, 9.2, 0.98 * power), 1.1, false)
	
	# 4. MACH SHOCK DIAMONDS (3 BUKULAN BERLIAN KEJUT SUPERSONIK)
	for k in range(1, 4):
		var d_k: float = plume_len * (0.16 + float(k) * 0.22)
		if d_k >= plume_len * 0.90:
			continue
		var dia_center: Vector2 = nozzle_pos + exhaust_dir * d_k
		var dia_scale: float = (1.0 - float(k) * 0.22) * power
		var dia_len: float = 3.6 * dia_scale
		var dia_w: float = 2.2 * dia_scale
		var dia_pts: PackedVector2Array = PackedVector2Array([
			dia_center + exhaust_dir * dia_len,
			dia_center + tangent_dir * dia_w,
			dia_center - exhaust_dir * dia_len,
			dia_center - tangent_dir * dia_w
		])
		draw_colored_polygon(dia_pts, Color(7.8, 7.5, 9.2, 0.98 * power))
		draw_polyline(dia_pts, Color(4.4, 0.72, 7.2, 0.90 * power), 1.0, true)
	
	# 5. NOZZLE STARBURST CORONA CROWN (MAHKOTA KORONA MULUT NOZZLE)
	var r_noz: float = 3.4 * power
	draw_circle(nozzle_pos, r_noz * 1.8, Color(2.15, 0.16, 4.65, 0.55 * power))
	draw_arc(nozzle_pos, r_noz * 1.35, 0, TAU, 16, Color(4.4, 0.72, 7.2, 0.85 * power), 1.3, false)
	draw_circle(nozzle_pos, r_noz * 0.92, Color(7.6, 7.4, 8.6, 0.96 * power))
	draw_circle(nozzle_pos, r_noz * 0.55, Color(0.010, 0.002, 0.020, 0.98 * power))
	draw_circle(nozzle_pos, r_noz * 0.24, Color(8.0, 7.8, 9.5, 1.0 * power))
	
	# Percikan ion mikro (Micro-Sparks)
	for _sp in range(2):
		var sp_dist: float = randf_range(4.0, plume_len * 0.85)
		var sp_lat: float = randf_range(-plume_max_w * 0.4, plume_max_w * 0.4)
		var sp_pos: Vector2 = nozzle_pos + exhaust_dir * sp_dist + tangent_dir * sp_lat
		draw_circle(sp_pos, randf_range(0.8, 1.4), Color(7.8, 7.5, 9.2, 0.90 * power))
