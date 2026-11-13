class_name GridRenderer
extends Node2D

const GridPhysicsClass = preload("res://core/physics/grid_physics.gd")

## GridRenderer - Ultra-Fast Hybrid Renderer (Static Full-Line Batch + Active Patch Segments)
## - Saat grid tenang (atau di luar kotak aktif), seluruh baris & kolom digambar sebagai
##   1 garis lurus utuh per baris/kolom (~90 garis total, bukan 10.000+ segmen pendek!).
## - Perhitungan jarak & warna per-segmen HANYA dijalankan pada sel di dalam active_bounds.

@export_group("Palet Warna Geometry Wars")
@export var rest_color: Color = Color(0.14, 0.02, 0.25, 0.30)
@export var surge_color: Color = Color(2.4, 0.45, 3.6, 1.0)
@export var rift_color: Color = Color(3.8, 2.6, 1.2, 1.0)

@export_group("Garis Grid")
@export var line_width: float = 1.0
@export var antialiased: bool = false

var physics: GridPhysicsClass = null

var static_points: PackedVector2Array = []
var active_points: PackedVector2Array = []
var active_colors: PackedColorArray = []

const MAX_RUPTURES: int = 256
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
	
	# =========================================================================
	# FAST PATH: JIKA GRID SEDANG DIAM (SLEEPING), GAMBAR HANYA (ROWS + COLS) GARIS LURUS!
	# Mengurangi beban dari >10.000 segmen menjadi ~95 garis lurus dalam 1 draw call instan.
	# =========================================================================
	if physics.is_sleeping or not physics.has_active_deformation:
		var total_lines: int = rows + cols
		if static_points.size() != total_lines * 2:
			static_points.resize(total_lines * 2)
			
		var idx: int = 0
		for r in range(rows):
			var row_offset: int = r * cols
			static_points[idx] = physics.base_positions[row_offset]
			static_points[idx + 1] = physics.base_positions[row_offset + cols - 1]
			idx += 2
			
		var bottom_row_offset: int = (rows - 1) * cols
		for c in range(cols):
			static_points[idx] = physics.base_positions[c]
			static_points[idx + 1] = physics.base_positions[bottom_row_offset + c]
			idx += 2
			
		draw_multiline(static_points, rest_color, line_width, antialiased)
		return

	# =========================================================================
	# HYBRID PATH: GARIS LURUS UNTUK AREA DIAM + SEGMEN DETAIL HANYA DI AREA AKTIF
	# =========================================================================
	var cell_size: float = physics.cell_size
	var min_c: int = clampi(physics.active_min_c, 0, cols - 1)
	var max_c: int = clampi(physics.active_max_c, 0, cols - 1)
	var min_r: int = clampi(physics.active_min_r, 0, rows - 1)
	var max_r: int = clampi(physics.active_max_r, 0, rows - 1)
	
	var static_list: PackedVector2Array = []
	active_points.clear()
	active_colors.clear()
	
	var stub_idx: int = 0
	var spark_idx: int = 0
	var fracture_color: Color = Color(5.2, 4.4, 2.0, 1.0)
	var arc_color: Color = Color(2.4, 4.8, 6.0, 1.0)
	
	# 1. Baris Horizontal
	for r in range(rows):
		var row_offset: int = r * cols
		if r < min_r or r > max_r:
			static_list.append(physics.base_positions[row_offset])
			static_list.append(physics.base_positions[row_offset + cols - 1])
		else:
			if min_c > 0:
				static_list.append(physics.base_positions[row_offset])
				static_list.append(physics.positions[row_offset + min_c])
			if max_c < cols - 1:
				static_list.append(physics.positions[row_offset + max_c])
				static_list.append(physics.base_positions[row_offset + cols - 1])
				
			var h_end: int = mini(max_c, cols - 2)
			for c in range(min_c, h_end + 1):
				var idx_a: int = row_offset + c
				var idx_b: int = idx_a + 1
				var p_a: Vector2 = physics.positions[idx_a]
				var p_b: Vector2 = physics.positions[idx_b]
				var dist: float = p_a.distance_to(p_b)
				var heat: float = (physics.rift_heat[idx_a] + physics.rift_heat[idx_b]) * 0.5
				var is_ruptured: bool = heat > 0.35 and dist > cell_size * 1.55
				
				if is_ruptured:
					var seg_dir: Vector2 = (p_b - p_a) / maxf(dist, 0.001)
					var stub_len: float = cell_size * 0.30
					var stub_a_end: Vector2 = p_a + seg_dir * stub_len
					var stub_b_start: Vector2 = p_b - seg_dir * stub_len
					
					active_points.append(p_a)
					active_points.append(stub_a_end)
					active_colors.append(fracture_color)
					
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
					var stretch: float = absf(dist - cell_size) / cell_size
					var energy: float = clampf((stretch * 2.5) + (heat * 0.55), 0.0, 1.0)
					if energy <= 0.02 and heat <= 0.05:
						static_list.append(p_a)
						static_list.append(p_b)
					else:
						active_points.append(p_a)
						active_points.append(p_b)
						var col: Color = rest_color.lerp(surge_color, energy)
						if heat > 0.15:
							col = col.lerp(rift_color, clampf(heat * 0.65, 0.0, 1.0))
						active_colors.append(col)

	# 2. Kolom Vertikal
	var last_row_offset: int = (rows - 1) * cols
	for c in range(cols):
		if c < min_c or c > max_c:
			static_list.append(physics.base_positions[c])
			static_list.append(physics.base_positions[last_row_offset + c])
		else:
			if min_r > 0:
				static_list.append(physics.base_positions[c])
				static_list.append(physics.positions[min_r * cols + c])
			if max_r < rows - 1:
				static_list.append(physics.positions[max_r * cols + c])
				static_list.append(physics.base_positions[last_row_offset + c])
				
			var v_end: int = mini(max_r, rows - 2)
			for r in range(min_r, v_end + 1):
				var idx_a: int = r * cols + c
				var idx_b: int = (r + 1) * cols + c
				var p_a: Vector2 = physics.positions[idx_a]
				var p_b: Vector2 = physics.positions[idx_b]
				var dist: float = p_a.distance_to(p_b)
				var heat: float = (physics.rift_heat[idx_a] + physics.rift_heat[idx_b]) * 0.5
				var is_ruptured: bool = heat > 0.35 and dist > cell_size * 1.55
				
				if is_ruptured:
					var seg_dir: Vector2 = (p_b - p_a) / maxf(dist, 0.001)
					var stub_len: float = cell_size * 0.30
					var stub_a_end: Vector2 = p_a + seg_dir * stub_len
					var stub_b_start: Vector2 = p_b - seg_dir * stub_len
					
					active_points.append(p_a)
					active_points.append(stub_a_end)
					active_colors.append(fracture_color)
					
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
					var stretch: float = absf(dist - cell_size) / cell_size
					var energy: float = clampf((stretch * 2.5) + (heat * 0.55), 0.0, 1.0)
					if energy <= 0.02 and heat <= 0.05:
						static_list.append(p_a)
						static_list.append(p_b)
					else:
						active_points.append(p_a)
						active_points.append(p_b)
						var col: Color = rest_color.lerp(surge_color, energy)
						if heat > 0.15:
							col = col.lerp(rift_color, clampf(heat * 0.65, 0.0, 1.0))
						active_colors.append(col)

	if static_list.size() >= 2:
		draw_multiline(static_list, rest_color, line_width, antialiased)
	if active_colors.size() > 0:
		draw_multiline_colors(active_points, active_colors, line_width, antialiased)
	if stub_idx > 0:
		draw_multiline_colors(stub_points.slice(0, stub_idx * 2), stub_colors.slice(0, stub_idx), line_width * 1.35, antialiased)
	if spark_idx > 0:
		draw_multiline_colors(spark_points.slice(0, spark_idx * 2), spark_colors.slice(0, spark_idx), line_width * 1.6, antialiased)
