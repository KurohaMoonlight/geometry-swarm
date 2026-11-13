class_name Arena
extends Node2D

const GridPhysicsClass = preload("res://core/physics/grid_physics.gd")
const GridRendererClass = preload("res://visual/grid_renderer.gd")
const GigaDrillVFXClass = preload("res://visual/giga_drill_vfx.gd")

## Arena Controller
## Mengatur sinkronisasi antara Core Engine (GridPhysics), Visual Grid,
## Swarm Simulation (C# Arch ECS), dan Spektakel VFX Giga Drill.

@onready var grid_renderer: GridRendererClass = $GridRenderer
@onready var world_env: WorldEnvironment = $WorldEnvironment
@onready var canvas_layer: CanvasLayer = $HUD
@onready var fps_label: Label = $HUD/RightMargin/VBox/FpsLabel
@onready var swarm_label: Label = $HUD/RightMargin/VBox/SwarmLabel

# Referensi ke modul Swarm C#
@onready var swarm_sim: Node2D = get_node_or_null("SwarmSimulation")
var player_core: Node2D = null
var player_camera: Camera2D = null
var last_player_pos: Vector2 = Vector2.ZERO

# Modul VFX Giga Drill (Ion Beam, lightning arcs, detonation)
var giga_vfx: GigaDrillVFXClass = null
var screen_shake_intensity: float = 0.0

# Core physics engine instance
var grid_physics: GridPhysicsClass = null

# Tracking kursor mouse
var last_mouse_pos: Vector2 = Vector2.ZERO
var mouse_velocity: Vector2 = Vector2.ZERO

@export var cursor_ripple_radius: float = 120.0
@export var player_wake_radius: float = 140.0

func _ready() -> void:
	player_camera = get_node_or_null("PlayerCamera")
	grid_physics = GridPhysicsClass.new()
	
	if swarm_sim:
		player_core = swarm_sim.get_node_or_null("PlayerCore")
		if player_core:
			# Mulai kapal di pusat sektor kosmik (0, 0)
			player_core.global_position = Vector2.ZERO
			last_player_pos = Vector2.ZERO
			
		# Inisialisasi visualizer Ion Beam & spektakel Giga Drill
		giga_vfx = GigaDrillVFXClass.new()
		add_child(giga_vfx)
		giga_vfx.setup(self, swarm_sim, player_core)
		
		# Sambungkan sinyal peluncuran dan peledakan pengorbanan swarm
		if swarm_sim.has_signal("GigaDrillLaunched"):
			swarm_sim.connect("GigaDrillLaunched", Callable(self, "_on_giga_drill_launched"))
		if swarm_sim.has_signal("GigaDrillDetonated"):
			swarm_sim.connect("GigaDrillDetonated", Callable(self, "_on_giga_drill_detonated"))
		if swarm_sim.has_signal("GigaDrillNoseBreached"):
			swarm_sim.connect("GigaDrillNoseBreached", Callable(self, "_on_giga_drill_nose_breached"))

	_update_grid_size()
	grid_renderer.bind_physics(grid_physics)
	get_tree().root.size_changed.connect(_on_viewport_resized)
	last_mouse_pos = get_viewport().get_mouse_position()

func trigger_shake(amount: float) -> void:
	screen_shake_intensity = maxf(screen_shake_intensity, amount)
	if player_camera and player_camera.has_method("TriggerShake"):
		player_camera.TriggerShake(amount)

func _on_giga_drill_launched(_tip_pos: Vector2, base_pos: Vector2, _dir: Vector2, charge: float) -> void:
	if giga_vfx:
		giga_vfx.trigger_launch_shake()
	# Hentakan dorongan reaktif roket exhaust ke belakang pada grid
	if grid_physics:
		grid_physics.apply_radial_impulse(base_pos, 1850.0 * charge, 240.0)

func _on_giga_drill_detonated(tip_pos: Vector2, base_pos: Vector2, count: int) -> void:
	if giga_vfx:
		giga_vfx.trigger_detonation(tip_pos, base_pos, count)
	# Gelombang kejut supernova pemecahan/pengorbanan swarm
	if grid_physics:
		grid_physics.apply_radial_impulse(tip_pos, 2200.0, 320.0)

func _on_giga_drill_nose_breached(breach_pos: Vector2, aim_dir: Vector2, count: int) -> void:
	if giga_vfx:
		giga_vfx.trigger_nose_breach(breach_pos, aim_dir, count)
	# Hentakan radial gelombang kejut pemecahan perisai AP dan penetrasi bor
	if grid_physics:
		grid_physics.apply_radial_impulse(breach_pos, 2800.0, 340.0)
	trigger_shake(13.0)

func _process(delta: float) -> void:
	# Screen Shake Redaman Cepat
	if screen_shake_intensity > 0.05:
		var offset: Vector2 = Vector2(randf_range(-1.0, 1.0), randf_range(-1.0, 1.0)) * screen_shake_intensity
		position = offset
		screen_shake_intensity = lerpf(screen_shake_intensity, 0.0, 12.0 * delta)
	else:
		position = Vector2.ZERO
	var current_mouse_pos: Vector2 = get_viewport().get_mouse_position()
	
	if delta > 0.0:
		mouse_velocity = (current_mouse_pos - last_mouse_pos) / delta
	
	# 0. Geser kisi fisika pegas secara mulus mengikuti penjelajahan di peta luas
	if player_core:
		var current_p_pos: Vector2 = player_core.global_position
		var delta_pos: Vector2 = current_p_pos - last_player_pos
		last_player_pos = current_p_pos
		if not delta_pos.is_zero_approx():
			grid_physics.shift_origin(delta_pos)

	# 1. Riakan dari gerakan Kapal Induk Player (WASD)
	if player_core and "velocity" in player_core:
		var p_vel: Vector2 = player_core.velocity
		if p_vel.length_squared() > 400.0:
			grid_physics.apply_wake_impulse(player_core.global_position, p_vel * 0.45, player_wake_radius)
			
	# 2. Efek Robekan Ruang-Waktu (Spacetime Tear) & Semburan Rocket Exhaust dari Super Giga Drill
	if swarm_sim:
		var is_charging: bool = swarm_sim.get("IsChargingDrill") == true
		var is_active: bool = swarm_sim.get("IsGigaDrillActive") == true
		
		if is_charging or is_active:
			var tip_pos: Vector2 = swarm_sim.get("GigaDrillTipPosition")
			var base_pos: Vector2 = swarm_sim.get("GigaDrillBasePosition")
			var aim_dir: Vector2 = swarm_sim.get("GigaDrillDirection")
			var drill_radius: float = float(swarm_sim.get("GigaDrillRadius"))
			var density_factor: float = float(swarm_sim.get("GigaDrillDensityFactor"))
			var spin_time: float = float(swarm_sim.get("GigaDrillSpinTime"))
			var power: float = 1.25 if is_active else float(swarm_sim.get("DrillChargeRatio"))
			
			grid_physics.apply_giga_drill_spacetime_and_exhaust(
				tip_pos,
				base_pos,
				aim_dir,
				drill_radius,
				density_factor,
				power,
				spin_time,
				is_active,
				delta
			)
			
	# 4. Integrasi step fisika pegas
	grid_physics.step(delta)
	
	# 5. Gambar ulang grid
	grid_renderer.queue_redraw()
	
	# 6. Update HUD FPS & State Swarm Real-Time di pojok kanan atas
	if fps_label:
		fps_label.text = "FPS: %d" % Engine.get_frames_per_second()
		
	if swarm_label and swarm_sim:
		var total: int = swarm_sim.EntityCount
		var orbiting: int = swarm_sim.OrbitingCount
		var launched: int = swarm_sim.LaunchedCount
		var returning: int = swarm_sim.ReturningCount
		var cores: int = swarm_sim.CoreCellCount
		var _drills: int = swarm_sim.DrillDroneCount
		var guardians: int = swarm_sim.GuardianCount
		var scanners: int = swarm_sim.ScannerCount
		var miners: int = swarm_sim.MiningCount
		var enemies: int = swarm_sim.EnemyCount
		var charge_ratio: float = swarm_sim.DrillChargeRatio
		var is_charging: bool = swarm_sim.IsChargingDrill
		
		var charge_text: String = ""
		if is_charging:
			charge_text = " ★ [ GIGA DRILL: %d%% ]" % int(charge_ratio * 100.0)
		elif swarm_sim.IsGigaDrillActive:
			charge_text = " ★ [ GIGA DRILL LAUNCHED! ]"
			
		var enemy_text: String = (" | ENEMY: %d" % enemies) if enemies > 0 else ""
		swarm_label.text = "SWARM: %d%s\n[ Orbit: %d | Fired: %d | Return: %d%s ]\nSCANNERS: %d | MINERS: %d | DEFENDERS: %d | CORES: %d" % [
			total, charge_text, orbiting, launched, returning, enemy_text, scanners, miners, guardians, cores
		]

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed:
		if event.keycode == KEY_SPACE:
			var center_pos: Vector2 = player_core.global_position if player_core else get_viewport().get_mouse_position()
			grid_physics.apply_radial_impulse(center_pos, 1400.0, 360.0)
		elif event.keycode == KEY_R:
			grid_physics.reset()

func _update_grid_size() -> void:
	# Grid melingkupi area kamera luas yang dapat di-zoom out
	var grid_size: Vector2 = Vector2(4600, 2800)
	var p_pos: Vector2 = player_core.global_position if player_core else Vector2.ZERO
	grid_physics.setup_grid(grid_size, 52.0, 100.0)
	grid_physics.shift_origin(p_pos - grid_size * 0.5)

func _on_viewport_resized() -> void:
	_update_grid_size()
