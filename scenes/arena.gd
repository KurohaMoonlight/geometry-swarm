class_name Arena
extends Node2D

const GridPhysicsClass = preload("res://core/physics/grid_physics.gd")
const GridRendererClass = preload("res://visual/grid_renderer.gd")
const GigaDrillVFXClass = preload("res://visual/giga_drill_vfx.gd")

## Arena Controller (Dioptimalkan untuk 60+ FPS Stabil)
## - Grid belakang di-snap ke koordinat dunia (kelipatan cell_size) sehingga TIDAK ikut bergeser
##   saat kapal bergerak.
## - Di belakang grid terdapat 3 lapis partikel bintang parallax yang sangat ringan.
## - Mendukung efek riak & gelombang kejut grid megah saat Sacrificial Dart Boost [SPACE] aktif,
##   serta efek robekan ruang-waktu (Supernova Grid Rupture) saat Giga Drill meledak di Core.

@onready var grid_renderer: GridRendererClass = $GridRenderer
@onready var world_env: WorldEnvironment = $WorldEnvironment
@onready var canvas_layer: CanvasLayer = $HUD
@onready var fps_label: Label = $HUD/RightMargin/VBox/FpsLabel
@onready var swarm_label: Label = $HUD/RightMargin/VBox/SwarmLabel

@onready var swarm_sim: Node2D = get_node_or_null("SwarmSimulation")
@onready var asteroid_field: Node2D = get_node_or_null("AsteroidField")
var player_core: Node2D = null
var player_camera: Camera2D = null

var giga_vfx: GigaDrillVFXClass = null
var screen_shake_intensity: float = 0.0

var grid_physics: GridPhysicsClass = null
var half_grid_size: Vector2 = Vector2(2100.0, 1300.0)
var hud_update_timer: float = 0.0

@export var player_wake_radius: float = 64.0

func _ready() -> void:
	player_camera = get_node_or_null("PlayerCamera")
	grid_physics = GridPhysicsClass.new()
	
	if swarm_sim:
		player_core = swarm_sim.get_node_or_null("PlayerCore")
		if player_core:
			player_core.global_position = Vector2.ZERO
			
		giga_vfx = GigaDrillVFXClass.new()
		add_child(giga_vfx)
		giga_vfx.setup(self, swarm_sim, player_core)
		
		if swarm_sim.has_signal("GigaDrillLaunched"):
			swarm_sim.connect("GigaDrillLaunched", Callable(self, "_on_giga_drill_launched"))
		if swarm_sim.has_signal("GigaDrillDetonated"):
			swarm_sim.connect("GigaDrillDetonated", Callable(self, "_on_giga_drill_detonated"))
		if swarm_sim.has_signal("GigaDrillNoseBreached"):
			swarm_sim.connect("GigaDrillNoseBreached", Callable(self, "_on_giga_drill_nose_breached"))
		if swarm_sim.has_signal("BoostDartSacrificed"):
			swarm_sim.connect("BoostDartSacrificed", Callable(self, "_on_boost_dart_sacrificed"))
		if swarm_sim.has_signal("SwarmClashImpulse"):
			swarm_sim.connect("SwarmClashImpulse", Callable(self, "_on_swarm_clash_impulse"))

	if asteroid_field:
		if asteroid_field.has_signal("CosmicRiftInfall"):
			asteroid_field.connect("CosmicRiftInfall", Callable(self, "_on_cosmic_rift_infall"))
		if asteroid_field.has_signal("CosmicRiftErupted"):
			asteroid_field.connect("CosmicRiftErupted", Callable(self, "_on_cosmic_rift_erupted"))

	_update_grid_size()
	grid_renderer.bind_physics(grid_physics)
	get_tree().root.size_changed.connect(_on_viewport_resized)

func trigger_shake(amount: float) -> void:
	screen_shake_intensity = maxf(screen_shake_intensity, amount)
	if player_camera and player_camera.has_method("TriggerShake"):
		player_camera.TriggerShake(amount)

func _on_giga_drill_launched(_tip_pos: Vector2, base_pos: Vector2, _dir: Vector2, charge: float) -> void:
	if giga_vfx:
		giga_vfx.trigger_launch_shake()
	if grid_physics:
		grid_physics.apply_radial_impulse(base_pos, 1450.0 * charge, 210.0)

func _on_giga_drill_detonated(tip_pos: Vector2, base_pos: Vector2, count: int) -> void:
	if giga_vfx:
		giga_vfx.trigger_detonation(tip_pos, base_pos, count)
	if grid_physics:
		if count < 0:
			# Ledakan kecil akhir saat driller habis menggerus batu asteroid
			grid_physics.apply_radial_impulse(tip_pos, 950.0, 155.0)
			trigger_shake(8.0)
		elif count > 0:
			# Ledakan Spektakuler Supernova saat menghantam Core!
			grid_physics.apply_supernova_core_detonation(tip_pos, base_pos, count)
			trigger_shake(22.0)
		else:
			grid_physics.apply_radial_impulse(tip_pos, 750.0, 145.0)

func _on_giga_drill_nose_breached(breach_pos: Vector2, aim_dir: Vector2, count: int) -> void:
	if giga_vfx:
		giga_vfx.trigger_nose_breach(breach_pos, aim_dir, count)
	if grid_physics:
		grid_physics.apply_radial_impulse(breach_pos, 2100.0, 280.0)
	trigger_shake(11.0)

func _on_boost_dart_sacrificed(nozzle_pos: Vector2, aim_dir: Vector2, power_ratio: float) -> void:
	if grid_physics:
		# Hentakan ledakan pengorbanan Dart di ruang bakar nozzle + gelombang kejut ke belakang
		grid_physics.apply_radial_impulse(nozzle_pos, 920.0 + 780.0 * power_ratio, 215.0)
		grid_physics.apply_radial_impulse(nozzle_pos - aim_dir * 75.0, 680.0 * power_ratio, 170.0)
	trigger_shake(2.2 + 3.2 * power_ratio)

func _on_swarm_clash_impulse(pos: Vector2, power: float, radius: float) -> void:
	if grid_physics:
		grid_physics.apply_radial_impulse(pos, power, radius)

func _on_cosmic_rift_infall(rift_pos: Vector2, power: float, radius: float) -> void:
	if grid_physics:
		grid_physics.apply_radial_impulse(rift_pos, -power, radius)

func _on_cosmic_rift_erupted(rift_pos: Vector2, power: float, radius: float) -> void:
	if grid_physics:
		grid_physics.apply_radial_impulse(rift_pos, power, radius)
	trigger_shake(8.5)

func _process(delta: float) -> void:
	if screen_shake_intensity > 0.05:
		var offset: Vector2 = Vector2(randf_range(-1.0, 1.0), randf_range(-1.0, 1.0)) * screen_shake_intensity
		position = offset
		screen_shake_intensity = lerpf(screen_shake_intensity, 0.0, 12.0 * delta)
	else:
		position = Vector2.ZERO

	var grid_dirty: bool = false

	# 0. Sinkronisasi jendela grid & posisi kamera untuk parallax bintang
	if player_core:
		if grid_physics.sync_window_to_center(player_core.global_position, half_grid_size):
			grid_dirty = true
		if grid_renderer.update_view(player_core.global_position, delta):
			grid_dirty = true

	# 1. Gerakan Kapal Induk: Grid belakang bergetar tipis tepat di bawah Core
	if player_core and "velocity" in player_core:
		var p_vel: Vector2 = player_core.velocity
		if p_vel.length_squared() > 9000.0:
			grid_physics.apply_wake_impulse(player_core.global_position, p_vel * 0.12, player_wake_radius)
			
	# 2. Efek Riak Plasma Boost [SPACE] & Super Giga Drill [RMB]
	if swarm_sim:
		var is_boosting: bool = swarm_sim.get("IsBoostActive") == true
		if is_boosting:
			var nozzle_pos: Vector2 = swarm_sim.get("BoostNozzlePosition")
			var b_aim_dir: Vector2 = swarm_sim.get("BoostAimDirection")
			var b_power: float = float(swarm_sim.get("BoostPowerRatio"))
			var b_pulse: float = float(swarm_sim.get("BoostPulseTime"))
			grid_physics.apply_boost_jet_impulse(nozzle_pos, b_aim_dir, b_power, b_pulse, delta)

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
			
	# 3. Step fisika hanya jika ada titik aktif, dan queue_redraw jika ada perubahan grid atau parallax bintang
	if grid_physics.step(delta) or grid_dirty:
		grid_renderer.queue_redraw()
	
	# 4. Update HUD di-throttle 5x per detik agar pembuatan string tidak membebani frame
	hud_update_timer += delta
	if hud_update_timer >= 0.20:
		hud_update_timer = 0.0
		if fps_label:
			fps_label.text = "FPS: %d" % Engine.get_frames_per_second()
			
		if swarm_label and swarm_sim:
			var total: int = swarm_sim.EntityCount
			var orbiting: int = swarm_sim.OrbitingCount
			var launched: int = swarm_sim.LaunchedCount
			var returning: int = swarm_sim.ReturningCount
			var cores: int = swarm_sim.CoreCellCount
			var darts: int = int(swarm_sim.get("DartNeedleCount"))
			var guardians: int = swarm_sim.GuardianCount
			var scanners: int = swarm_sim.ScannerCount
			var miners: int = swarm_sim.MiningCount
			var charge_ratio: float = swarm_sim.DrillChargeRatio
			var is_charging: bool = swarm_sim.IsChargingDrill
			var is_boosting: bool = swarm_sim.get("IsBoostActive") == true
			
			var status_text: String = ""
			if is_charging:
				status_text = " ★ [ GIGA DRILL: %d%% ]" % int(charge_ratio * 100.0)
			elif swarm_sim.IsGigaDrillActive:
				status_text = " ★ [ GIGA DRILL LAUNCHED! ]"
			elif is_boosting:
				var b_pct: int = int(float(swarm_sim.get("BoostPowerRatio")) * 100.0)
				status_text = " ⚡ [ SACRIFICIAL PLASMA BOOST: %d%% ]" % b_pct
				
			var p_hp: int = int(float(swarm_sim.get("PlayerHealth")))
			var p_max_hp: int = int(float(swarm_sim.get("PlayerMaxHealth")))
			var is_p_pod: bool = swarm_sim.get("IsPlayerEscapePod") == true
			var p_status: String = " [⚡ ESCAPE POD]" if is_p_pod else " [HIVE]"

			swarm_label.text = "CORE: %d/%d%s\nSWARM: %d%s\n[ Orbit: %d | Fired: %d | Return: %d ]\nDARTS: %d | SCANNERS: %d | MINERS: %d | DEFENDERS: %d | CORES: %d" % [
				p_hp, p_max_hp, p_status,
				total, status_text, orbiting, launched, returning, darts, scanners, miners, guardians, cores
			]

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed:
		if event.keycode == KEY_R:
			grid_physics.reset()
			grid_renderer.queue_redraw()

func _update_grid_size() -> void:
	var grid_size: Vector2 = half_grid_size * 2.0
	var p_pos: Vector2 = player_core.global_position if player_core else Vector2.ZERO
	grid_physics.setup_grid(grid_size, 68.0, 68.0)
	grid_physics.sync_window_to_center(p_pos, half_grid_size)
	grid_renderer.queue_redraw()

func _on_viewport_resized() -> void:
	_update_grid_size()
