class_name GridRenderer
extends Node2D

const GridPhysicsClass = preload("res://core/physics/grid_physics.gd")

## GridRenderer - Geometry Wars Style + Spacetime Rift & Exhaust Overdrive Glow
## Merender wireframe grid halus, gelap & transparan saat diam,
## menyala neon magenta saat beriak, dan memancarkan kilau putih-sian/emas membara
## ketika mengalami robekan ruang-waktu (spacetime tear) atau semburan exhaust Giga Drill.

@export_group("Palet Warna Geometry Wars")
## Warna grid saat posisi tenang (gelap, redup, tidak menyala)
@export var rest_color: Color = Color(0.14, 0.02, 0.25, 0.32)
## Warna saat grid meregang / beriak biasa (HDR Electric Neon Magenta/Violet)
@export var surge_color: Color = Color(2.4, 0.45, 3.6, 1.0)
## Warna saat grid mengalami robekan ruang-waktu / exhaust Giga Drill (Blazing Rift Cyan-Gold HDR)
@export var rift_color: Color = Color(3.8, 2.6, 1.2, 1.0)

@export_group("Garis Grid")
@export var line_width: float = 1.0
@export var antialiased: bool = true

var physics: GridPhysicsClass = null

# Buffer reusable untuk draw_multiline_colors (Zero GC)
var segment_points: PackedVector2Array = []
var segment_colors: PackedColorArray = []

# Buffer robekan fisik ruang-waktu (Garis putus + percikan loncatan listrik antar ujung robek)
const MAX_RUPTURES: int = 512
var stub_points: PackedVector2Array = []
var stub_colors: PackedColorArray = []
var spark_points: PackedVector2Array = []
var spark_colors: PackedColorArray = []

func _ready() -> void:
	stub_points.resize(MAX_RUPTURES * 2)
	stub_colors.resize(MAX_RUPTURES)
	spark_points.resize(MAX_RUPTURES * 4)
	spark_colors.resize(MAX_RUPTURES * 2)

func bind_physics(p_physics: GridPhysicsClass) -> void:
	physics = p_physics
	queue_redraw()

func _draw() -> void:
	if not physics or physics.total_points == 0:
		return
		
	var rows: int = physics.rows
	var cols: int = physics.cols
	var cell_size: float = physics.cell_size
	var has_heat: bool = physics.rift_heat.size() == physics.total_points
	
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
	var stub_idx: int = 0
	var spark_idx: int = 0
	
	# Warna ujung robekan putus (Blazing Fracture White-Gold HDR)
	var fracture_color: Color = Color(5.2, 4.4, 2.0, 1.0)
	# Warna loncatan petir antar ujung putus yang hendak menyatu kembali (Electric Arc Cyan-White HDR)
	var arc_color: Color = Color(2.4, 4.8, 6.0, 1.0)
	
	# 1. Segmen Horizontal (Baris)
	for r in range(rows):
		var row_offset: int = r * cols
		for c in range(cols - 1):
			var idx_a: int = row_offset + c
			var idx_b: int = idx_a + 1
			
			var p_a: Vector2 = physics.positions[idx_a]
			var p_b: Vector2 = physics.positions[idx_b]
			var dist: float = p_a.distance_to(p_b)
			var heat: float = (physics.rift_heat[idx_a] + physics.rift_heat[idx_b]) * 0.5 if has_heat else 0.0
			# Grid HANYA putus pada jalur yang dilewati langsung oleh Giga Drill (terdeteksi lewat rift_heat)
			var is_ruptured: bool = has_heat and heat > 0.35 and dist > cell_size * 1.55
			
			if is_ruptured:
				# =========================================================================
				# GARIS GRID BENAR-BENAR PUTUS (TORN SPACETIME VOID)!
				# Garis patah menjadi dua ujung pendek dengan lubang vakum di tengahnya.
				# =========================================================================
				var seg_dir: Vector2 = (p_b - p_a) / maxf(dist, 0.001)
				var stub_len: float = cell_size * 0.30
				var stub_a_end: Vector2 = p_a + seg_dir * stub_len
				var stub_b_start: Vector2 = p_b - seg_dir * stub_len
				
				# Ujung A
				segment_points[pt_idx] = p_a
				segment_points[pt_idx + 1] = stub_a_end
				segment_colors[seg_idx] = fracture_color
				
				# Ujung B (simpan di buffer stub sekunder)
				if stub_idx < MAX_RUPTURES:
					var s_pt: int = stub_idx * 2
					stub_points[s_pt] = stub_b_start
					stub_points[s_pt + 1] = p_b
					stub_colors[stub_idx] = fracture_color
					stub_idx += 1
					
				# Percikan loncatan listrik antar celah yang hampir tersambung kembali
				if dist < cell_size * 2.45 and spark_idx < MAX_RUPTURES - 1:
					var normal: Vector2 = Vector2(-seg_dir.y, seg_dir.x)
					var mid: Vector2 = (stub_a_end + stub_b_start) * 0.5 + normal * randf_range(-6.5, 6.5)
					
					var sp_pt: int = spark_idx * 2
					spark_points[sp_pt] = stub_a_end
					spark_points[sp_pt + 1] = mid
					spark_colors[spark_idx] = arc_color
					spark_idx += 1
					
					sp_pt = spark_idx * 2
					spark_points[sp_pt] = mid
					spark_points[sp_pt + 1] = stub_b_start
					spark_colors[spark_idx] = arc_color
					spark_idx += 1
			else:
				# Garis utuh normal / baru saja tersambung kembali
				segment_points[pt_idx] = p_a
				segment_points[pt_idx + 1] = p_b
				
				var stretch: float = absf(dist - cell_size) / cell_size
				var speed: float = (physics.velocities[idx_a].length() + physics.velocities[idx_b].length()) * 0.5
				var energy: float = clampf((stretch * 2.8) + (speed * 0.0035) + (heat * 0.55), 0.0, 1.0)
				
				if heat > 0.15:
					var base_col: Color = rest_color.lerp(surge_color, energy)
					segment_colors[seg_idx] = base_col.lerp(rift_color, clampf(heat * 0.65, 0.0, 1.0))
				elif energy > 0.01:
					segment_colors[seg_idx] = rest_color.lerp(surge_color, energy)
				else:
					segment_colors[seg_idx] = rest_color
					
			pt_idx += 2
			seg_idx += 1
			
	# 2. Segmen Vertikal (Kolom)
	for c in range(cols):
		for r in range(rows - 1):
			var idx_a: int = r * cols + c
			var idx_b: int = (r + 1) * cols + c
			
			var p_a: Vector2 = physics.positions[idx_a]
			var p_b: Vector2 = physics.positions[idx_b]
			var dist: float = p_a.distance_to(p_b)
			var heat: float = (physics.rift_heat[idx_a] + physics.rift_heat[idx_b]) * 0.5 if has_heat else 0.0
			# Grid HANYA putus pada jalur yang dilewati langsung oleh Giga Drill (terdeteksi lewat rift_heat)
			var is_ruptured: bool = has_heat and heat > 0.35 and dist > cell_size * 1.55
			
			if is_ruptured:
				var seg_dir: Vector2 = (p_b - p_a) / maxf(dist, 0.001)
				var stub_len: float = cell_size * 0.30
				var stub_a_end: Vector2 = p_a + seg_dir * stub_len
				var stub_b_start: Vector2 = p_b - seg_dir * stub_len
				
				segment_points[pt_idx] = p_a
				segment_points[pt_idx + 1] = stub_a_end
				segment_colors[seg_idx] = fracture_color
				
				if stub_idx < MAX_RUPTURES:
					var s_pt: int = stub_idx * 2
					stub_points[s_pt] = stub_b_start
					stub_points[s_pt + 1] = p_b
					stub_colors[stub_idx] = fracture_color
					stub_idx += 1
					
				if dist < cell_size * 2.45 and spark_idx < MAX_RUPTURES - 1:
					var normal: Vector2 = Vector2(-seg_dir.y, seg_dir.x)
					var mid: Vector2 = (stub_a_end + stub_b_start) * 0.5 + normal * randf_range(-6.5, 6.5)
					
					var sp_pt: int = spark_idx * 2
					spark_points[sp_pt] = stub_a_end
					spark_points[sp_pt + 1] = mid
					spark_colors[spark_idx] = arc_color
					spark_idx += 1
					
					sp_pt = spark_idx * 2
					spark_points[sp_pt] = mid
					spark_points[sp_pt + 1] = stub_b_start
					spark_colors[spark_idx] = arc_color
					spark_idx += 1
			else:
				segment_points[pt_idx] = p_a
				segment_points[pt_idx + 1] = p_b
				
				var stretch: float = absf(dist - cell_size) / cell_size
				var speed: float = (physics.velocities[idx_a].length() + physics.velocities[idx_b].length()) * 0.5
				var energy: float = clampf((stretch * 2.8) + (speed * 0.0035) + (heat * 0.55), 0.0, 1.0)
				
				if heat > 0.15:
					var base_col: Color = rest_color.lerp(surge_color, energy)
					segment_colors[seg_idx] = base_col.lerp(rift_color, clampf(heat * 0.65, 0.0, 1.0))
				elif energy > 0.01:
					segment_colors[seg_idx] = rest_color.lerp(surge_color, energy)
				else:
					segment_colors[seg_idx] = rest_color
					
			pt_idx += 2
			seg_idx += 1
			
	# Gambar seluruh grid utama dalam 1 draw call yang sangat efisien
	draw_multiline_colors(segment_points, segment_colors, line_width, antialiased)
	
	# Gambar ujung stub kedua dari garis-garis yang putus
	if stub_idx > 0:
		draw_multiline_colors(stub_points.slice(0, stub_idx * 2), stub_colors.slice(0, stub_idx), line_width * 1.35, antialiased)
		
	# Gambar percikan loncatan listrik antar celah yang hampir tersambung
	if spark_idx > 0:
		draw_multiline_colors(spark_points.slice(0, spark_idx * 2), spark_colors.slice(0, spark_idx), line_width * 1.6, antialiased)
