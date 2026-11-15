extends Node2D

var _stars: Array = []
var _time: float = 0.0

func _ready() -> void:
	var rng = RandomNumberGenerator.new()
	rng.randomize()
	for i in range(140):
		_stars.append({
			"pos": Vector2(rng.randf_range(0.0, 1920.0), rng.randf_range(0.0, 1080.0)),
			"size": rng.randf_range(1.2, 3.4),
			"alpha": rng.randf_range(0.35, 0.95),
			"pulse_speed": rng.randf_range(1.0, 3.5),
			"color": Color(rng.randf_range(0.7, 1.0), rng.randf_range(0.85, 1.0), 1.0)
		})

func _process(delta: float) -> void:
	_time += delta
	queue_redraw()

func _draw() -> void:
	# 1. Grid koordinat ruang angkasa halus
	var grid_step: float = 120.0
	var grid_col: Color = Color(0.12, 0.18, 0.32, 0.22)
	for x in range(0, 1920, int(grid_step)):
		draw_line(Vector2(x, 0), Vector2(x, 1080), grid_col, 1.0)
	for y in range(0, 1080, int(grid_step)):
		draw_line(Vector2(0, y), Vector2(1920, y), grid_col, 1.0)
		
	# 2. Bintang parallax berkedip dinamis
	for s in _stars:
		var p_alpha: float = s["alpha"] * (0.75 + 0.25 * sin(_time * s["pulse_speed"]))
		var c: Color = s["color"]
		c.a = p_alpha
		draw_circle(s["pos"], s["size"], c)

