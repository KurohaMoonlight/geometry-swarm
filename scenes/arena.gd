class_name Arena
extends Node2D

const GridPhysicsClass = preload("res://core/physics/grid_physics.gd")
const GridRendererClass = preload("res://visual/grid_renderer.gd")
const GigaDrillVFXClass = preload("res://visual/giga_drill_vfx.gd")

## Arena Controller (Dioptimalkan untuk 60+ FPS Stabil)
## - Grid belakang menggunakan palet Soft Luminous Neon yang di-snap ke koordinat dunia.
## - Di belakang grid terdapat 3 lapis partikel bintang parallax yang ikut terdistorsi gravitasi
##   secara Optik (Einstein Starlight Deflection) dan secara Fisik (Spacetime Infall).
## - Menghubungkan sumur gravitasi Bintang Mini & Aether Seed (Spacetime Infall) secara fisik
##   ke GridPhysics sehingga jaring ruang-waktu menekuk, memilin, dan melengkung tajam ke pusat massa.

@onready var grid_renderer: GridRendererClass = $GridRenderer
@onready var world_env: WorldEnvironment = $WorldEnvironment
@onready var canvas_layer: CanvasLayer = $HUD

# HUD Nodes (Clean Modern Sci-Fi Interface)
@onready var core_panel: PanelContainer = $HUD/TopLeftMargin/CorePanel
@onready var core_badge: Label = $HUD/TopLeftMargin/CorePanel/VBox/HeaderHBox/CoreBadge
@onready var core_hp_label: Label = $HUD/TopLeftMargin/CorePanel/VBox/HeaderHBox/CoreHpLabel
@onready var core_health_bar: ProgressBar = $HUD/TopLeftMargin/CorePanel/VBox/CoreHealthBar
@onready var core_cells_label: Label = $HUD/TopLeftMargin/CorePanel/VBox/CoreCellsLabel

@onready var fe_pill_panel: PanelContainer = $HUD/TopCenterMargin/ResourceHBox/FePill
@onready var fe_label: Label = $HUD/TopCenterMargin/ResourceHBox/FePill/Margin/Label
@onready var qz_pill_panel: PanelContainer = $HUD/TopCenterMargin/ResourceHBox/QzPill
@onready var qz_label: Label = $HUD/TopCenterMargin/ResourceHBox/QzPill/Margin/Label
@onready var cb_pill_panel: PanelContainer = $HUD/TopCenterMargin/ResourceHBox/CbPill
@onready var cb_label: Label = $HUD/TopCenterMargin/ResourceHBox/CbPill/Margin/Label
@onready var vx_pill_panel: PanelContainer = $HUD/TopCenterMargin/ResourceHBox/VxPill
@onready var vx_label: Label = $HUD/TopCenterMargin/ResourceHBox/VxPill/Margin/Label
@onready var ae_pill_panel: PanelContainer = $HUD/TopCenterMargin/ResourceHBox/AePill
@onready var ae_label: Label = $HUD/TopCenterMargin/ResourceHBox/AePill/Margin/Label

@onready var fleet_panel: PanelContainer = $HUD/TopRightMargin/VBox/FleetPanel
@onready var fleet_header_label: Label = $HUD/TopRightMargin/VBox/FleetPanel/VBox/FleetHeaderLabel
@onready var fleet_comp_label: Label = $HUD/TopRightMargin/VBox/FleetPanel/VBox/FleetCompositionLabel
@onready var ability_status_label: Label = $HUD/TopRightMargin/VBox/FleetPanel/VBox/AbilityStatusLabel
@onready var fps_label: Label = $HUD/TopRightMargin/VBox/FpsLabel

@onready var rival_panel: PanelContainer = $HUD/RightCenterMargin/RivalPanel
@onready var rival_dist_label: Label = $HUD/RightCenterMargin/RivalPanel/VBox/HeaderHBox/RivalDistLabel
@onready var rival_health_bar: ProgressBar = $HUD/RightCenterMargin/RivalPanel/VBox/RivalHealthBar
@onready var rival_state_label: Label = $HUD/RightCenterMargin/RivalPanel/VBox/RivalStateLabel

@onready var controls_panel: PanelContainer = $HUD/BottomControlsMargin/ControlsPanel

@onready var pause_overlay: Control = $HUD/PauseOverlay
@onready var pause_panel: PanelContainer = $HUD/PauseOverlay/Center/Panel
@onready var resume_btn: Button = $HUD/PauseOverlay/Center/Panel/Margin/VBox/BtnVBox/ResumeBtn
@onready var restart_btn: Button = $HUD/PauseOverlay/Center/Panel/Margin/VBox/BtnVBox/RestartBtn
@onready var main_menu_btn: Button = $HUD/PauseOverlay/Center/Panel/Margin/VBox/BtnVBox/MainMenuBtn

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
		if player_core and player_camera:
			player_camera.global_position = player_core.global_position
			player_camera.reset_smoothing()
			
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
	_setup_hud_styling()
	_update_hud()
	
	if resume_btn:
		resume_btn.pressed.connect(_on_resume_pressed)
	if restart_btn:
		restart_btn.pressed.connect(_on_restart_pressed)
	if main_menu_btn:
		main_menu_btn.pressed.connect(_on_main_menu_pressed)

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
		grid_physics.apply_radial_impulse(nozzle_pos, 920.0 + 780.0 * power_ratio, 215.0)
		grid_physics.apply_radial_impulse(nozzle_pos - aim_dir * 75.0, 680.0 * power_ratio, 170.0)
	trigger_shake(2.2 + 3.2 * power_ratio)

func _on_swarm_clash_impulse(pos: Vector2, power: float, radius: float) -> void:
	if grid_physics:
		grid_physics.apply_radial_impulse(pos, power, minf(radius, 450.0))
	if absf(power) >= 2500.0:
		var cam_pos: Vector2 = player_core.global_position if player_core else Vector2.ZERO
		var dist_to_cam: float = pos.distance_to(cam_pos)
		if dist_to_cam < 2200.0:
			var atten: float = clampf(1.0 - (dist_to_cam - 300.0) / 1900.0, 0.0, 1.0)
			trigger_shake(clampf(absf(power) * 0.0032 * atten, 0.5, 15.0))

func _on_cosmic_rift_infall(rift_pos: Vector2, power: float, radius: float) -> void:
	if grid_physics:
		grid_physics.apply_radial_impulse(rift_pos, -power, radius)
	var shake_val: float = clampf(power * 0.0035, 1.2, 11.0)
	trigger_shake(shake_val)

func _on_cosmic_rift_erupted(rift_pos: Vector2, power: float, radius: float) -> void:
	if grid_physics:
		grid_physics.apply_radial_impulse(rift_pos, power, radius)
	trigger_shake(12.0)

func _process(delta: float) -> void:
	if screen_shake_intensity > 0.05:
		screen_shake_intensity = lerpf(screen_shake_intensity, 0.0, 12.0 * delta)
	position = Vector2.ZERO

	var grid_dirty: bool = false

	# 0. Sinkronisasi jendela grid & posisi kamera untuk parallax bintang
	if player_core:
		if grid_physics.sync_window_to_center(player_core.global_position, half_grid_size):
			grid_dirty = true
		if grid_renderer.update_view(player_core.global_position, delta):
			grid_dirty = true

	# 0.5. Sinkronisasi Sumur Gravitasi Bintang Mini & Aether Seed (Spacetime Infall) ke Grid & Parallax Starfield
	if asteroid_field:
		var g_pos_raw = asteroid_field.get("GravityWellPositions")
		var g_rad_raw = asteroid_field.get("GravityWellRadii")
		var g_str_raw = asteroid_field.get("GravityWellStrengths")
		var g_inf_raw = asteroid_field.get("GravityWellInfallRatios")
		var g_pull_raw = asteroid_field.get("GravityWellPullAccels")
		var g_swirl_raw = asteroid_field.get("GravityWellSwirlAccels")
		var g_heat_raw = asteroid_field.get("GravityWellHeatRates")
		
		if g_pos_raw != null and g_rad_raw != null and g_str_raw != null and g_inf_raw != null:
			var g_positions: PackedVector2Array = g_pos_raw
			var g_radii: PackedFloat32Array = g_rad_raw
			var g_strengths: PackedFloat32Array = g_str_raw
			var g_infalls: PackedFloat32Array = g_inf_raw
			
			if grid_renderer.update_gravity_sources(g_positions, g_radii, g_strengths, g_infalls):
				grid_dirty = true
				
			if g_pull_raw != null and g_swirl_raw != null and g_heat_raw != null:
				var g_pulls: PackedFloat32Array = g_pull_raw
				var g_swirls: PackedFloat32Array = g_swirl_raw
				var g_heats: PackedFloat32Array = g_heat_raw
				var count: int = mini(g_positions.size(), mini(g_radii.size(), g_pulls.size()))
				var cam_center: Vector2 = player_core.global_position if player_core else Vector2.ZERO
				var max_active_dist_sq: float = 2800.0 * 2800.0
				
				for i in range(count):
					var well_pos: Vector2 = g_positions[i]
					if well_pos.distance_squared_to(cam_center) <= max_active_dist_sq:
						grid_physics.apply_gravity_well_pull(
							well_pos,
							g_pulls[i],
							minf(g_radii[i] * 0.88, 380.0),
							g_swirls[i],
							g_heats[i],
							delta
						)

	# 1. Gerakan Kapal Induk: Grid belakang bergetar tipis tepat di bawah Core
	if player_core and "velocity" in player_core:
		var p_vel: Vector2 = player_core.velocity
		if p_vel.length_squared() > 9000.0:
			grid_physics.apply_wake_impulse(player_core.global_position, p_vel * 0.12, player_wake_radius)
			
	# 2. Efek Riak Plasma Boost [SPACE] & Super Giga Drill [RMB]
	if swarm_sim and swarm_sim.get("PlayerHealth") != null:
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
	
	# 4. Update HUD di-throttle 5x per detik agar stabil & hemat frame
	hud_update_timer += delta
	if hud_update_timer >= 0.20:
		hud_update_timer = 0.0
		_update_hud()

func _setup_hud_styling() -> void:
	# 1. Panel Integritas Core Pemain (Top-Left)
	if core_panel:
		var style = StyleBoxFlat.new()
		style.bg_color = Color(0.04, 0.05, 0.09, 0.88)
		style.border_color = Color(0.25, 0.75, 1.0, 0.5)
		style.set_border_width_all(1)
		style.set_corner_radius_all(8)
		style.content_margin_left = 10
		style.content_margin_right = 10
		style.content_margin_top = 8
		style.content_margin_bottom = 8
		core_panel.add_theme_stylebox_override("panel", style)
		
	if core_health_bar:
		var bg_style = StyleBoxFlat.new()
		bg_style.bg_color = Color(0.08, 0.10, 0.16, 0.9)
		bg_style.set_corner_radius_all(4)
		core_health_bar.add_theme_stylebox_override("background", bg_style)
		
		var fill_style = StyleBoxFlat.new()
		fill_style.bg_color = Color(0.20, 0.85, 1.0, 1.0)
		fill_style.set_corner_radius_all(4)
		core_health_bar.add_theme_stylebox_override("fill", fill_style)
		
	# 2. Pill Bar Resource (Top-Center)
	var pill_configs = [
		{"panel": fe_pill_panel, "border": Color(0.95, 0.85, 0.3, 0.45)},
		{"panel": qz_pill_panel, "border": Color(0.25, 0.90, 1.0, 0.45)},
		{"panel": cb_pill_panel, "border": Color(1.00, 0.45, 0.3, 0.45)},
		{"panel": vx_pill_panel, "border": Color(0.30, 1.00, 0.6, 0.45)},
		{"panel": ae_pill_panel, "border": Color(1.00, 0.90, 0.4, 0.55)}
	]
	for cfg in pill_configs:
		if cfg["panel"]:
			var p_style = StyleBoxFlat.new()
			p_style.bg_color = Color(0.05, 0.07, 0.12, 0.85)
			p_style.border_color = cfg["border"]
			p_style.set_border_width_all(1)
			p_style.set_corner_radius_all(6)
			cfg["panel"].add_theme_stylebox_override("panel", p_style)

	# 3. Panel Armada Swarm (Top-Right)
	if fleet_panel:
		var f_style = StyleBoxFlat.new()
		f_style.bg_color = Color(0.04, 0.05, 0.09, 0.88)
		f_style.border_color = Color(0.28, 0.65, 1.0, 0.45)
		f_style.set_border_width_all(1)
		f_style.set_corner_radius_all(8)
		f_style.content_margin_left = 10
		f_style.content_margin_right = 10
		f_style.content_margin_top = 8
		f_style.content_margin_bottom = 8
		fleet_panel.add_theme_stylebox_override("panel", f_style)

	# 4. Panel Ancaman Rival Hive (Right-Center)
	if rival_panel:
		var r_style = StyleBoxFlat.new()
		r_style.bg_color = Color(0.08, 0.04, 0.05, 0.88)
		r_style.border_color = Color(0.95, 0.30, 0.35, 0.55)
		r_style.set_border_width_all(1)
		r_style.set_corner_radius_all(8)
		r_style.content_margin_left = 10
		r_style.content_margin_right = 10
		r_style.content_margin_top = 8
		r_style.content_margin_bottom = 8
		rival_panel.add_theme_stylebox_override("panel", r_style)
		
	if rival_health_bar:
		var r_bg = StyleBoxFlat.new()
		r_bg.bg_color = Color(0.12, 0.06, 0.08, 0.9)
		r_bg.set_corner_radius_all(3)
		rival_health_bar.add_theme_stylebox_override("background", r_bg)
		
		var r_fill = StyleBoxFlat.new()
		r_fill.bg_color = Color(0.95, 0.28, 0.32, 1.0)
		r_fill.set_corner_radius_all(3)
		rival_health_bar.add_theme_stylebox_override("fill", r_fill)

	# 5. Panel Kontrol Navigasi (Bottom-Center)
	if controls_panel:
		var c_style = StyleBoxFlat.new()
		c_style.bg_color = Color(0.02, 0.03, 0.06, 0.75)
		c_style.border_color = Color(0.30, 0.50, 0.70, 0.35)
		c_style.set_border_width_all(1)
		c_style.set_corner_radius_all(12)
		controls_panel.add_theme_stylebox_override("panel", c_style)

	# 6. Panel Modal Pause (Slow-Motion)
	if pause_panel:
		var p_style = StyleBoxFlat.new()
		p_style.bg_color = Color(0.04, 0.05, 0.09, 0.95)
		p_style.border_color = Color(0.25, 0.75, 1.0, 0.65)
		p_style.set_border_width_all(2)
		p_style.set_corner_radius_all(12)
		p_style.shadow_color = Color(0.0, 0.5, 0.8, 0.25)
		p_style.shadow_size = 14
		pause_panel.add_theme_stylebox_override("panel", p_style)

	var p_buttons = [resume_btn, restart_btn, main_menu_btn]
	for btn in p_buttons:
		if btn:
			var normal_btn = StyleBoxFlat.new()
			normal_btn.bg_color = Color(0.07, 0.10, 0.18, 0.9)
			normal_btn.border_color = Color(0.3, 0.6, 0.9, 0.5)
			normal_btn.set_border_width_all(1)
			normal_btn.set_corner_radius_all(6)
			btn.add_theme_stylebox_override("normal", normal_btn)
			
			var hover_btn = StyleBoxFlat.new()
			hover_btn.bg_color = Color(0.12, 0.18, 0.32, 0.95)
			hover_btn.border_color = Color(0.4, 0.85, 1.0, 0.85)
			hover_btn.set_border_width_all(1)
			hover_btn.set_corner_radius_all(6)
			btn.add_theme_stylebox_override("hover", hover_btn)

func _update_hud() -> void:
	if fps_label:
		fps_label.text = "%d FPS" % Engine.get_frames_per_second()
		
	if not swarm_sim or swarm_sim.get("PlayerHealth") == null:
		return

	# 1. Update Core Pemain
	var p_hp: float = float(swarm_sim.get("PlayerHealth"))
	var p_max_hp: float = maxf(1.0, float(swarm_sim.get("PlayerMaxHealth")))
	var is_p_pod: bool = swarm_sim.get("IsPlayerEscapePod") == true
	var p_cells: int = int(swarm_sim.get("CoreCellCount"))
	
	if core_health_bar:
		core_health_bar.max_value = p_max_hp
		core_health_bar.value = p_hp
	if core_hp_label:
		core_hp_label.text = "%d / %d HP" % [int(p_hp), int(p_max_hp)]
	if core_badge:
		if is_p_pod:
			core_badge.text = "[ ⚡ ESCAPE POD ]"
			core_badge.modulate = Color(1.0, 0.55, 0.2)
		else:
			core_badge.text = "[ HIVE CORE ]"
			core_badge.modulate = Color(0.3, 0.9, 1.0)
	if core_cells_label:
		core_cells_label.text = "◆ Core Modules: %d" % p_cells

	# 2. Update Live Resource Stock
	var is_infinite_res: bool = swarm_sim.get("IsInfiniteResources") == true
	if is_infinite_res:
		if fe_label: fe_label.text = "◆ Fe: ∞"
		if qz_label: qz_label.text = "◆ Qz: ∞"
		if cb_label: cb_label.text = "◆ Cb: ∞"
		if vx_label: vx_label.text = "◆ Vx: ∞"
		if ae_label: ae_label.text = "⚡ Ae: ∞"
	else:
		if fe_label: fe_label.text = "◆ Fe: %d" % swarm_sim.GetResource(0)
		if qz_label: qz_label.text = "◆ Qz: %d" % swarm_sim.GetResource(1)
		if cb_label: cb_label.text = "◆ Cb: %d" % swarm_sim.GetResource(2)
		if vx_label: vx_label.text = "◆ Vx: %d" % swarm_sim.GetResource(3)
		if ae_label: ae_label.text = "⚡ Ae: %d" % swarm_sim.GetResource(4)

	# 3. Update Swarm Fleet & Ability Status
	var total_swarm: int = swarm_sim.EntityCount
	var darts: int = int(swarm_sim.get("DartNeedleCount"))
	var scanners: int = swarm_sim.ScannerCount
	var miners: int = swarm_sim.MiningCount
	var guardians: int = swarm_sim.GuardianCount
	
	if fleet_header_label:
		fleet_header_label.text = "SWARM FLEET: %d UNITS" % total_swarm
	if fleet_comp_label:
		fleet_comp_label.text = "▲ %d   ◈ %d   ⛏ %d   🛡 %d" % [darts, scanners, miners, guardians]
		
	if ability_status_label:
		var is_charging: bool = swarm_sim.IsChargingDrill
		var is_drill_active: bool = swarm_sim.IsGigaDrillActive
		var is_boosting: bool = swarm_sim.get("IsBoostActive") == true
		
		if is_charging:
			var pct: int = int(swarm_sim.DrillChargeRatio * 100.0)
			ability_status_label.text = "★ GIGA DRILL: CHARGING %d%%" % pct
			ability_status_label.modulate = Color(1.0, 0.85, 0.3)
		elif is_drill_active:
			ability_status_label.text = "★ GIGA DRILL: ACTIVE / BREAK!"
			ability_status_label.modulate = Color(1.0, 0.4, 0.2)
		elif is_boosting:
			var b_pct: int = int(float(swarm_sim.get("BoostPowerRatio")) * 100.0)
			ability_status_label.text = "⚡ PLASMA BOOST: %d%%" % b_pct
			ability_status_label.modulate = Color(0.35, 0.95, 1.0)
		else:
			ability_status_label.text = "★ GIGA DRILL: READY [RMB]"
			ability_status_label.modulate = Color(0.65, 0.85, 0.9)

	# 4. Update Rival Hive Threat Card
	var e_hp: float = float(swarm_sim.get("EnemyHealth"))
	var e_max_hp: float = maxf(1.0, float(swarm_sim.get("EnemyMaxHealth")))
	var e_dist: int = int(swarm_sim.get("DistanceToEnemy"))
	var is_e_pod: bool = swarm_sim.get("IsEnemyEscapePod") == true
	var e_state: String = str(swarm_sim.get("EnemyStateText"))
	
	if rival_dist_label:
		rival_dist_label.text = "%s px" % String.num_int64(e_dist)
	if rival_health_bar:
		rival_health_bar.max_value = e_max_hp
		rival_health_bar.value = e_hp
	if rival_state_label:
		if e_hp <= 0.0:
			rival_state_label.text = "Status: DESTROYED"
			rival_state_label.modulate = Color(0.55, 0.55, 0.55)
		elif is_e_pod:
			rival_state_label.text = "Status: ESCAPE POD FLEEING"
			rival_state_label.modulate = Color(1.0, 0.5, 0.2)
		else:
			rival_state_label.text = "Status: %s" % e_state
			rival_state_label.modulate = Color(0.85, 0.85, 0.95)

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed:
		if event.keycode == KEY_R:
			grid_physics.reset()
			grid_renderer.queue_redraw()
		elif event.keycode == KEY_M:
			var tactical_map = get_node_or_null("HUD/TacticalMap")
			if tactical_map and tactical_map.has_method("ToggleTacticalMap"):
				tactical_map.ToggleTacticalMap()
				get_viewport().set_input_as_handled()
		elif event.keycode == KEY_ESCAPE:
			var tactical_map = get_node_or_null("HUD/TacticalMap")
			if tactical_map and tactical_map.get("IsTacticalMapOpen") == true:
				tactical_map.CloseTacticalMap()
				get_viewport().set_input_as_handled()
				return
			_toggle_pause()

func _toggle_pause() -> void:
	if not pause_overlay:
		return
	var is_open: bool = !pause_overlay.visible
	pause_overlay.visible = is_open
	if is_open:
		Engine.time_scale = 0.10 # Slow-motion bullet time (10% speed)
	else:
		Engine.time_scale = 1.0

func _on_resume_pressed() -> void:
	if pause_overlay:
		pause_overlay.visible = false
	Engine.time_scale = 1.0

func _on_restart_pressed() -> void:
	Engine.time_scale = 1.0
	get_tree().reload_current_scene()

func _on_main_menu_pressed() -> void:
	Engine.time_scale = 1.0
	get_tree().change_scene_to_file("res://scenes/main_menu.tscn")

func _exit_tree() -> void:
	Engine.time_scale = 1.0

func _update_grid_size() -> void:
	var grid_size: Vector2 = half_grid_size * 2.0
	var p_pos: Vector2 = player_core.global_position if player_core else Vector2.ZERO
	grid_physics.setup_grid(grid_size, 68.0, 68.0)
	grid_physics.sync_window_to_center(p_pos, half_grid_size)
	grid_renderer.queue_redraw()

func _on_viewport_resized() -> void:
	_update_grid_size()
