class_name GridRenderer
extends Node2D

const GridPhysicsClass = preload("res://core/physics/grid_physics.gd")

## GridRenderer - Geometry Wars Style
## Merender wireframe grid halus, gelap & transparan saat diam,
## dan hanya menyala neon terang (HDR Glow) pada bagian yang meregang atau beriak.

@export_group("Palet Warna Geometry Wars")
## Warna grid saat posisi tenang (gelap, redup, tidak menyala)
@export var rest_color: Color = Color(0.14, 0.02, 0.25, 0.32)
## Warna saat grid meregang / beriak (HDR Electric Neon Magenta/Violet)
@export var surge_color: Color = Color(2.4, 0.45, 3.6, 1.0)

@export_group("Garis Grid")
@export var line_width: float = 1.0
@export var antialiased: bool = true

var physics: GridPhysicsClass = null

# Buffer reusable untuk menghindari alokasi memori tiap frame
var segment_points: PackedVector2Array = []
var segment_colors: PackedColorArray = []

func bind_physics(p_physics: GridPhysicsClass) -> void:
	physics = p_physics
	queue_redraw()

func _draw() -> void:
	if not physics or physics.total_points == 0:
		return
		
	var rows: int = physics.rows
	var cols: int = physics.cols
	var cell_size: float = physics.cell_size
	
	# Hitung total segmen garis
	var horiz_segments: int = rows * (cols - 1)
	var vert_segments: int = cols * (rows - 1)
	var total_segments: int = horiz_segments + vert_segments
	
	var total_point_count: int = total_segments * 2
	if segment_points.size() != total_point_count:
		segment_points.resize(total_point_count)
		segment_colors.resize(total_segments)
	
	var seg_idx: int = 0
	var pt_idx: int = 0
	
	# 1. Segmen Horizontal (Baris)
	for r in range(rows):
		var row_offset: int = r * cols
		for c in range(cols - 1):
			var idx_a: int = row_offset + c
			var idx_b: int = idx_a + 1
			
			var p_a: Vector2 = physics.positions[idx_a]
			var p_b: Vector2 = physics.positions[idx_b]
			
			segment_points[pt_idx] = p_a
			segment_points[pt_idx + 1] = p_b
			pt_idx += 2
			
			# Hitung tingkat eksitasi/energi segmen dari regangan & kecepatan
			var dist: float = p_a.distance_to(p_b)
			var stretch: float = absf(dist - cell_size) / cell_size
			var speed: float = (physics.velocities[idx_a].length() + physics.velocities[idx_b].length()) * 0.5
			var energy: float = clampf((stretch * 2.8) + (speed * 0.0035), 0.0, 1.0)
			
			if energy > 0.01:
				segment_colors[seg_idx] = rest_color.lerp(surge_color, energy)
			else:
				segment_colors[seg_idx] = rest_color
			seg_idx += 1
			
	# 2. Segmen Vertikal (Kolom)
	for c in range(cols):
		for r in range(rows - 1):
			var idx_a: int = r * cols + c
			var idx_b: int = (r + 1) * cols + c
			
			var p_a: Vector2 = physics.positions[idx_a]
			var p_b: Vector2 = physics.positions[idx_b]
			
			segment_points[pt_idx] = p_a
			segment_points[pt_idx + 1] = p_b
			pt_idx += 2
			
			var dist: float = p_a.distance_to(p_b)
			var stretch: float = absf(dist - cell_size) / cell_size
			var speed: float = (physics.velocities[idx_a].length() + physics.velocities[idx_b].length()) * 0.5
			var energy: float = clampf((stretch * 2.8) + (speed * 0.0035), 0.0, 1.0)
			
			if energy > 0.01:
				segment_colors[seg_idx] = rest_color.lerp(surge_color, energy)
			else:
				segment_colors[seg_idx] = rest_color
			seg_idx += 1
			
	# Gambar seluruh grid dalam 1 draw call yang sangat efisien
	draw_multiline_colors(segment_points, segment_colors, line_width, antialiased)
