class_name GridRenderer
extends Node2D

const GridPhysicsClass = preload("res://core/physics/grid_physics.gd")

## GridRenderer - Ultra-Fast Hybrid Renderer + 3-Layer Parallax Starfield
## - Menggambar partikel titik bintang (3 lapis kedalaman parallax) tepat di belakang garis grid
##   menggunakan deterministic spatial cell hashing dalam 1 draw call batch (draw_multiline_colors).
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

@export_group("Parallax Starfield")
@export var enable_starfield: bool = true
@export var star_tile_size: float = 240.0

var physics: GridPhysicsClass = null

var camera_pos: Vector2 = Vector2.ZERO
var star_time: float = 0.0
var _twinkle_accum: float = 0.0

var star_points: PackedVector2Array = []
var star_colors: PackedColorArray = []

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

## Memperbarui posisi kamera untuk efek parallax bintang & kerlipan halus (sangat ringan)
func update_view(p_cam_pos: Vector2, delta: float) -> bool:
	star_time += delta
	_twinkle_accum += delta
	var moved: bool = p_cam_pos.distance_squared_to(camera_pos) > 0.15
	if moved:
		camera_pos = p_cam_pos
		return true
	if _twinkle_accum >= 0.05:
		_twinkle_accum = 0.0
		return true
	return false

func _hash2d(cx: int, cy: int, seed_offset: int) -> int:
	var h: int = (cx * 374761393) + (cy * 668265263) + (seed_offset * 1442695041)
	h = (h ^ (h >> 13)) * 1274126177
	return abs(h ^ (h >> 16))

## Menggambar 3 lapisan partikel bintang parallax di belakang grid dalam 1 draw call batch
func _draw_parallax_starfield() -> void:
	if not enable_starfield:
		return
		
	star_points.clear()
	star_colors.clear()
	
	var view_half: Vector2 = Vector2(1280.0, 800.0)
	var tile: float = maxf(120.0, star_tile_size)
	
	# 3 Lapis Kedalaman Parallax:
	# - Layer 0 (Jauh): drift = 0.82 (tampak bergerak 18% kecepatan kamera), titik kecil redup
	# - Layer 1 (Sedang): drift = 0.56 (tampak bergerak 44% kecepatan kamera), titik sedang
	# - Layer 2 (Dekat): drift = 0.28 (tampak bergerak 72% kecepatan kamera), bintang terang berkilau
	var layer_drifts: Array[float] = [0.82, 0.56, 0.28]
	var layer_counts: Array[int] = [2, 1, 1]
	var layer_sizes: Array[float] = [1.5, 2.2, 2.8]
	var layer_alphas: Array[float] = [0.38, 0.62, 0.88]
	
	var palette: Array[Color] = [
		Color(0.75, 0.85, 1.35, 1.0), # Putih Kebiruan (Astrophysical Blue-White)
		Color(0.95, 0.72, 1.45, 1.0), # Ungu Nebula Lembut
		Color(1.35, 1.20, 0.85, 1.0), # Emas Bintang Hangat
		Color(0.55, 1.25, 1.45, 1.0)  # Sian Bintang Jauh
	]
	
	for layer in range(3):
		var drift: float = layer_drifts[layer]
		var parallax_offset: Vector2 = camera_pos * drift
		var rel_center: Vector2 = camera_pos - parallax_offset
		
		var min_cx: int = int(floor((rel_center.x - view_half.x) / tile)) - 1
		var max_cx: int = int(ceil((rel_center.x + view_half.x) / tile)) + 1
		var min_cy: int = int(floor((rel_center.y - view_half.y) / tile)) - 1
		var max_cy: int = int(ceil((rel_center.y + view_half.y) / tile)) + 1
		
		var stars_per_cell: int = layer_counts[layer]
		var base_size: float = layer_sizes[layer]
		var base_alpha: float = layer_alphas[layer]
		
		for cy in range(min_cy, max_cy + 1):
			for cx in range(min_cx, max_cx + 1):
				for s in range(stars_per_cell):
					var h: int = _hash2d(cx, cy, layer * 97 + s * 31)
					# Untuk layer terdekat (layer 2), hanya munculkan di ~55% sel agar tidak terlalu ramai
					if layer == 2 and (h % 100) > 55:
						continue
						
					var fx: float = float(h & 1023) / 1024.0
					var fy: float = float((h >> 10) & 1023) / 1024.0
					var world_p: Vector2 = Vector2((cx + fx) * tile, (cy + fy) * tile) + parallax_offset
					
					var phase: float = float((h >> 20) & 255) * 0.0245
					var twinkle: float = 0.72 + 0.28 * sin(star_time * (1.8 + layer * 0.9) + phase)
					var col: Color = palette[h & 3]
					col.a = base_alpha * twinkle
					
					var half_w: float = base_size * 0.5
					star_points.append(world_p - Vector2(half_w, 0.0))
					star_points.append(world_p + Vector2(half_w, 0.0))
					star_colors.append(col)
					
					# Garis vertikal silang tipis (+) untuk bintang di layer dekat/sedang agar tampak seperti titik bintang tajam
					if layer >= 1:
						star_points.append(world_p - Vector2(0.0, half_w))
						star_points.append(world_p + Vector2(0.0, half_w))
						star_colors.append(col)
						
	if star_colors.size() > 0:
		draw_multiline_colors(star_points, star_colors, 1.6, false)

func _draw() -> void:
	# 0. Gambar partikel bintang parallax terlebih dahulu agar berada DI BELAKANG garis grid!
	_draw_parallax_starfield()
	
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
