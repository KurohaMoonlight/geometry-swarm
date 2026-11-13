class_name Arena
extends Node2D

const GridPhysicsClass = preload("res://core/physics/grid_physics.gd")
const GridRendererClass = preload("res://visual/grid_renderer.gd")

## Arena Controller
## Mengatur sinkronisasi antara Core Engine (GridPhysics), Visual Grid,
## dan Swarm Simulation (C# Arch ECS).

@onready var grid_renderer: GridRendererClass = $GridRenderer
@onready var world_env: WorldEnvironment = $WorldEnvironment
@onready var canvas_layer: CanvasLayer = $HUD
@onready var fps_label: Label = $HUD/RightMargin/VBox/FpsLabel
@onready var swarm_label: Label = $HUD/RightMargin/VBox/SwarmLabel

# Referensi ke modul Swarm C#
@onready var swarm_sim: Node2D = get_node_or_null("SwarmSimulation")
var player_core: Node2D = null

# Core physics engine instance
var grid_physics: GridPhysicsClass = null

# Tracking kursor mouse
var last_mouse_pos: Vector2 = Vector2.ZERO
var mouse_velocity: Vector2 = Vector2.ZERO

@export var cursor_ripple_radius: float = 120.0
@export var player_wake_radius: float = 140.0

func _ready() -> void:
	grid_physics = GridPhysicsClass.new()
	_update_grid_size()
	
	grid_renderer.bind_physics(grid_physics)
	get_tree().root.size_changed.connect(_on_viewport_resized)
	last_mouse_pos = get_viewport().get_mouse_position()
	
	if swarm_sim:
		player_core = swarm_sim.get_node_or_null("PlayerCore")
		if player_core:
			var vp_size: Vector2 = get_viewport_rect().size
			player_core.global_position = vp_size * 0.5

func _process(delta: float) -> void:
	var current_mouse_pos: Vector2 = get_viewport().get_mouse_position()
	
	if delta > 0.0:
		mouse_velocity = (current_mouse_pos - last_mouse_pos) / delta
	
	# 1. Riakan dari gerakan kursor mouse
	var mouse_speed: float = mouse_velocity.length()
	if mouse_speed > 30.0:
		grid_physics.apply_wake_impulse(current_mouse_pos, mouse_velocity * 0.55, cursor_ripple_radius)
	
	last_mouse_pos = current_mouse_pos
	
	# 2. Riakan dari gerakan Kapal Induk Player (WASD)
	if player_core and "velocity" in player_core:
		var p_vel: Vector2 = player_core.velocity
		if p_vel.length_squared() > 400.0:
			grid_physics.apply_wake_impulse(player_core.global_position, p_vel * 0.45, player_wake_radius)
			
	# 3. Riakan dari Giga Drill Break jika aktif/charging
	if swarm_sim and "IsChargingDrill" in swarm_sim and swarm_sim.IsChargingDrill:
		var renderer = swarm_sim.get_node_or_null("SwarmRenderer")
		if renderer and "GigaDrillPosition" in renderer:
			grid_physics.apply_radial_impulse(renderer.GigaDrillPosition, 250.0, 90.0)
			
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
		var drills: int = swarm_sim.DrillDroneCount
		var charge_ratio: float = swarm_sim.DrillChargeRatio
		var is_charging: bool = swarm_sim.IsChargingDrill
		
		var charge_text: String = ""
		if is_charging:
			charge_text = " ★ [ GIGA DRILL: %d%% ]" % int(charge_ratio * 100.0)
			
		swarm_label.text = "SWARM: %d%s\n[ Orbit: %d | Fired: %d | Return: %d ]\nDRILLS: %d | CORES: %d" % [total, charge_text, orbiting, launched, returning, drills, cores]

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_MIDDLE:
			grid_physics.apply_radial_impulse(event.position, 900.0, 260.0)
			
	elif event is InputEventKey and event.pressed:
		if event.keycode == KEY_SPACE:
			var center_pos: Vector2 = player_core.global_position if player_core else get_viewport().get_mouse_position()
			grid_physics.apply_radial_impulse(center_pos, 1400.0, 360.0)
		elif event.keycode == KEY_R:
			grid_physics.reset()

func _update_grid_size() -> void:
	var viewport_size: Vector2 = get_viewport_rect().size
	if viewport_size.x <= 0 or viewport_size.y <= 0:
		viewport_size = Vector2(1920, 1080)
	grid_physics.setup_grid(viewport_size, 36.0, 70.0)

func _on_viewport_resized() -> void:
	_update_grid_size()
