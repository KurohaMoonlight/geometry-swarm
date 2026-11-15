class_name MainMenu
extends Control

## Main Menu Controller dengan Background Random Black Hole & Lensing Fisik
## - Black Hole mengembara secara acak di latar belakang.
## - Saat Black Hole mendekati / melewati Judul Game ("GEOMETRY SWARM"),
##   BackBufferCopy menangkap judul dan mendistorsinya secara gravitasional (Einstein Lensing).
## - Tombol-tombol menu dirender di atas Black Hole sehingga selalu jernih dan tidak terdistorsi.

@onready var black_hole_root: Node2D = $BlackHoleRoot
@onready var black_hole_rect: ColorRect = $BlackHoleRoot/BlackHoleRect
@onready var play_btn: Button = $MenuCanvas/VBox/BtnBox/PlayButton
@onready var debug_btn: Button = $MenuCanvas/VBox/BtnBox/DebugButton
@onready var controls_btn: Button = $MenuCanvas/VBox/BtnBox/ControlsButton
@onready var quit_btn: Button = $MenuCanvas/VBox/BtnBox/QuitButton

@onready var controls_modal: Control = $ControlsModal
@onready var close_controls_btn: Button = $ControlsModal/Center/Panel/Margin/VBox/CloseButton

# Mode Debug: Infinite Resources
var _is_debug_infinite_resources: bool = false

# Perilaku Gerakan Acak Black Hole
var _bh_pos: Vector2 = Vector2(960.0, 360.0)
var _bh_vel: Vector2 = Vector2(40.0, -20.0)
var _bh_target: Vector2 = Vector2(960.0, 220.0)
var _target_timer: float = 0.0
var _pulse_timer: float = 0.0

var _screen_size: Vector2 = Vector2(1920.0, 1080.0)

func _ready() -> void:
	Engine.time_scale = 1.0 # Pastikan kecepatan waktu selalu normal saat masuk menu
	
	if ProjectSettings.has_setting("debug/infinite_resources"):
		_is_debug_infinite_resources = bool(ProjectSettings.get_setting("debug/infinite_resources"))
	else:
		_is_debug_infinite_resources = false
		ProjectSettings.set_setting("debug/infinite_resources", false)
	
	if play_btn:
		play_btn.pressed.connect(_on_play_pressed)
	if debug_btn:
		debug_btn.pressed.connect(_on_debug_pressed)
	if controls_btn:
		controls_btn.pressed.connect(_on_controls_pressed)
	if quit_btn:
		quit_btn.pressed.connect(_on_quit_pressed)
	if close_controls_btn:
		close_controls_btn.pressed.connect(_on_close_controls_pressed)
		
	_setup_styles()
	_update_debug_btn_ui()
	_pick_new_black_hole_target(true)
	
	if black_hole_root:
		black_hole_root.position = _bh_pos

func _pick_new_black_hole_target(prefer_title: bool = false) -> void:
	_target_timer = randf_range(5.0, 9.0)
	
	# Kadang arahkan Black Hole mendekati area Judul (Y: 140 - 320, X: 520 - 1400)
	# agar pemain dapat mengamati efek distorsi optik pada teks judul
	if prefer_title or randf() < 0.45:
		_bh_target = Vector2(
			randf_range(540.0, 1380.0),
			randf_range(160.0, 320.0)
		)
	else:
		_bh_target = Vector2(
			randf_range(280.0, 1640.0),
			randf_range(220.0, 860.0)
		)

func _process(delta: float) -> void:
	_target_timer -= delta
	if _target_timer <= 0.0:
		_pick_new_black_hole_target()
		
	_pulse_timer += delta
	
	# Gerakan halus Black Hole mengarah ke target dengan inersia kosmik
	var to_target: Vector2 = _bh_target - _bh_pos
	var dist: float = to_target.length()
	var desired_speed: float = clampf(dist * 0.45, 25.0, 130.0)
	var desired_vel: Vector2 = to_target.normalized() * desired_speed
	
	_bh_vel = _bh_vel.lerp(desired_vel, delta * 1.25)
	_bh_pos += _bh_vel * delta
	
	# Pantulkan lembut jika mendekati tepi layar
	var margin_x: float = 240.0
	var margin_y: float = 140.0
	if _bh_pos.x < margin_x and _bh_vel.x < 0.0:
		_bh_vel.x = absf(_bh_vel.x) * 0.8
		_pick_new_black_hole_target()
	elif _bh_pos.x > _screen_size.x - margin_x and _bh_vel.x > 0.0:
		_bh_vel.x = -absf(_bh_vel.x) * 0.8
		_pick_new_black_hole_target()
	if _bh_pos.y < margin_y and _bh_vel.y < 0.0:
		_bh_vel.y = absf(_bh_vel.y) * 0.8
		_pick_new_black_hole_target()
	elif _bh_pos.y > _screen_size.y - margin_y and _bh_vel.y > 0.0:
		_bh_vel.y = -absf(_bh_vel.y) * 0.8
		_pick_new_black_hole_target()
		
	if black_hole_root:
		black_hole_root.position = _bh_pos
		
	# Denyut gravitasi dinamis pada shader
	if black_hole_rect and black_hole_rect.material is ShaderMaterial:
		var mat: ShaderMaterial = black_hole_rect.material as ShaderMaterial
		var lens_val: float = 1.70 + sin(_pulse_timer * 1.8) * 0.25
		mat.set_shader_parameter("lensing_strength", lens_val)

func _setup_styles() -> void:
	# Styling tombol menu utama
	var buttons = [play_btn, controls_btn, quit_btn]
	for btn in buttons:
		if btn:
			var normal = StyleBoxFlat.new()
			normal.bg_color = Color(0.04, 0.06, 0.12, 0.88)
			normal.border_color = Color(0.28, 0.68, 1.0, 0.55)
			normal.set_border_width_all(1)
			normal.set_corner_radius_all(8)
			btn.add_theme_stylebox_override("normal", normal)
			
			var hover = StyleBoxFlat.new()
			hover.bg_color = Color(0.10, 0.16, 0.28, 0.95)
			hover.border_color = Color(0.40, 0.90, 1.0, 0.95)
			hover.set_border_width_all(2)
			hover.set_corner_radius_all(8)
			btn.add_theme_stylebox_override("hover", hover)
			
			var pressed = StyleBoxFlat.new()
			pressed.bg_color = Color(0.18, 0.28, 0.45, 1.0)
			pressed.border_color = Color(0.6, 1.0, 1.0, 1.0)
			pressed.set_border_width_all(2)
			pressed.set_corner_radius_all(8)
			btn.add_theme_stylebox_override("pressed", pressed)

	if close_controls_btn:
		var c_norm = StyleBoxFlat.new()
		c_norm.bg_color = Color(0.06, 0.10, 0.18, 0.9)
		c_norm.border_color = Color(0.3, 0.7, 1.0, 0.6)
		c_norm.set_border_width_all(1)
		c_norm.set_corner_radius_all(6)
		close_controls_btn.add_theme_stylebox_override("normal", c_norm)

func _on_debug_pressed() -> void:
	_is_debug_infinite_resources = !_is_debug_infinite_resources
	ProjectSettings.set_setting("debug/infinite_resources", _is_debug_infinite_resources)
	_update_debug_btn_ui()

func _update_debug_btn_ui() -> void:
	if not debug_btn:
		return
	
	var norm = StyleBoxFlat.new()
	norm.set_corner_radius_all(8)
	var hov = StyleBoxFlat.new()
	hov.set_corner_radius_all(8)
	var prs = StyleBoxFlat.new()
	prs.set_corner_radius_all(8)
	
	if _is_debug_infinite_resources:
		debug_btn.text = "⚡ MODE DEBUG: RESOURCE INFINITE [ON]"
		debug_btn.add_theme_color_override("font_color", Color(1.0, 0.92, 0.4, 1.0))
		
		norm.bg_color = Color(0.15, 0.12, 0.04, 0.92)
		norm.border_color = Color(1.0, 0.85, 0.3, 0.85)
		norm.set_border_width_all(2)
		
		hov.bg_color = Color(0.24, 0.19, 0.06, 0.95)
		hov.border_color = Color(1.0, 0.95, 0.5, 1.0)
		hov.set_border_width_all(2)
		
		prs.bg_color = Color(0.32, 0.25, 0.08, 1.0)
		prs.border_color = Color(1.0, 1.0, 0.7, 1.0)
		prs.set_border_width_all(2)
	else:
		debug_btn.text = "⚡ MODE DEBUG: RESOURCE INFINITE [OFF]"
		debug_btn.add_theme_color_override("font_color", Color(0.7, 0.75, 0.85, 0.75))
		
		norm.bg_color = Color(0.04, 0.05, 0.10, 0.85)
		norm.border_color = Color(0.35, 0.45, 0.60, 0.45)
		norm.set_border_width_all(1)
		
		hov.bg_color = Color(0.09, 0.12, 0.20, 0.95)
		hov.border_color = Color(0.55, 0.70, 0.90, 0.8)
		hov.set_border_width_all(1)
		
		prs.bg_color = Color(0.14, 0.18, 0.28, 1.0)
		prs.border_color = Color(0.70, 0.85, 1.0, 1.0)
		prs.set_border_width_all(2)
		
	debug_btn.add_theme_stylebox_override("normal", norm)
	debug_btn.add_theme_stylebox_override("hover", hov)
	debug_btn.add_theme_stylebox_override("pressed", prs)

func _on_play_pressed() -> void:
	get_tree().change_scene_to_file("res://scenes/arena.tscn")

func _on_controls_pressed() -> void:
	if controls_modal:
		controls_modal.visible = true

func _on_close_controls_pressed() -> void:
	if controls_modal:
		controls_modal.visible = false

func _on_quit_pressed() -> void:
	get_tree().quit()

