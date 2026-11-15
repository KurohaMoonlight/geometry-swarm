class_name GridRenderer
extends Node2D

const GridPhysicsClass = preload("res://core/physics/grid_physics.gd")

## GridRenderer - Dim Neon Purple Hybrid Renderer + Heavy Gravitational Parallax Starfield
## - Menggunakan palet Neon Ungu Redup (rest_color) agar tenang di mata dan tidak mengganggu objek utama,
##   namun menyala ke Ungu-Magenta Neon terang (surge_color) saat tertarik gravitasi ekstrem Bintang Mini
##   bermateri eksotis maupun saat terjadi gelombang kejut.
## - Menggambar 3 lapis kedalaman bintang parallax di belakang grid yang ikut terhisap kuat secara
##   OPTIK (Einstein Starlight Deflection) dan FISIK (Heavy Exotic Matter Infall).

@export_group("Palet Warna Geometry Wars (Neon Ungu Redup)")
@export var rest_color: Color = Color(0.30, 0.09, 0.52, 0.38)
@export var surge_color: Color = Color(1.95, 0.42, 3.10, 0.95)
@export var rift_color: Color = Color(3.6, 2.3, 1.2, 1.0)

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

# Sumber gravitasi aktif (Bintang Mini Aether & Seed pada tahap Spacetime Infall)
var grav_positions: PackedVector2Array = []
var grav_radii: PackedFloat32Array = []
var grav_strengths: PackedFloat32Array = []
var grav_infall_ratios: PackedFloat32Array = []

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

## Memperbarui daftar sumber gravitasi aktif untuk distorsi optik & fisik pada bintang parallax
func update_gravity_sources(
	p_positions: PackedVector2Array,
	p_radii: PackedFloat32Array,
	p_strengths: PackedFloat32Array,
	p_infall_ratios: PackedFloat32Array
) -> bool:
	var changed: bool = grav_positions.size() != p_positions.size()
	if not changed and p_positions.size() > 0:
		for i in range(mini(grav_positions.size(), 4)):
			if grav_positions[i].distance_squared_to(p_positions[i]) > 4.0:
				changed = true
				break
	grav_positions = p_positions
	grav_radii = p_radii
	grav_strengths = p_strengths
	grav_infall_ratios = p_infall_ratios
	return changed

## Memperbarui posisi kamera untuk efek parallax bintang & kerlipan halus (sangat ringan)
func update_view(p_cam_pos: Vector2, delta: float) -> bool:
	star_time += delta
	_twinkle_accum += delta
	var moved: bool = p_cam_pos.distance_squared_to(camera_pos) > 0.4
	if moved:
		camera_pos = p_cam_pos
		return true
	if _twinkle_accum >= 0.08:
		_twinkle_accum = 0.0
		return true
	return false

func _hash2d(cx: int, cy: int, seed_offset: int) -> int:
	var h: int = (cx * 374761393) + (cy * 668265263) + (seed_offset * 1442695041)
	h = (h ^ (h >> 13)) * 1274126177
	return abs(h ^ (h >> 16))

## Menggambar 3 lapisan partikel bintang parallax di belakang grid dengan distorsi gravitasi ekstrem
func _draw_parallax_starfield() -> void:
	if not enable_starfield:
		return
		
	star_points.clear()
	star_colors.clear()
	
	var view_half: Vector2 = Vector2(1280.0, 800.0)
	var tile: float = maxf(120.0, star_tile_size)
	var raw_grav_count: int = mini(
		mini(grav_positions.size(), grav_radii.size()),
		mini(grav_strengths.size(), grav_infall_ratios.size())
	)
	var local_g_pos: PackedVector2Array = []
	var local_g_rad: PackedFloat32Array = []
	var local_g_str: PackedFloat32Array = []
	var local_g_inf: PackedFloat32Array = []
	for g in range(raw_grav_count):
		var gp: Vector2 = grav_positions[g]
		var gr: float = grav_radii[g]
		if absf(gp.x - camera_pos.x) <= view_half.x + gr and absf(gp.y - camera_pos.y) <= view_half.y + gr:
			local_g_pos.append(gp)
			local_g_rad.append(gr)
			local_g_str.append(grav_strengths[g])
			local_g_inf.append(grav_infall_ratios[g])
	var grav_count: int = local_g_pos.size()
	
	var layer_drifts: Array[float] = [0.82, 0.56, 0.28]
	var layer_counts: Array[int] = [2, 1, 1]
	var layer_sizes: Array[float] = [1.5, 2.2, 2.8]
	var layer_alphas: Array[float] = [0.40, 0.64, 0.88]
	
	var palette: Array[Color] = [
		Color(0.75, 0.85, 1.35, 1.0), # Putih Kebiruan
		Color(0.95, 0.72, 1.45, 1.0), # Ungu Nebula Lembut
		Color(1.35, 1.20, 0.85, 1.0), # Emas Bintang Hangat
		Color(0.55, 1.25, 1.45, 1.0)  # Sian Bintang Jauh
	]
	var lensing_tint: Color = Color(2.1, 0.95, 3.2, 1.0)
	
	for layer in range(3):
		var drift: float = layer_drifts[layer]
		var parallax_offset: Vector2 = camera_pos * drift
		var rel_center: Vector2 = camera_pos - parallax_offset
		var depth_weight: float = 0.70 + 0.30 * (float(layer) / 2.0)
		
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
					if layer == 2 and (h % 100) > 55:
						continue
						
					var fx: float = float(h & 1023) / 1024.0
					var fy: float = float((h >> 10) & 1023) / 1024.0
					var world_p: Vector2 = Vector2((cx + fx) * tile, (cy + fy) * tile) + parallax_offset
					
					var phase: float = float((h >> 20) & 255) * 0.0245
					var twinkle: float = 0.72 + 0.28 * sin(star_time * (1.8 + layer * 0.9) + phase)
					var col: Color = palette[h & 3]
					col.a = base_alpha * twinkle
					
					var streak_vec: Vector2 = Vector2.ZERO
					var total_warp_energy: float = 0.0
					
					# Distorsi Gravitasi Materi Eksotis (Hisapan Dalam + Pembelokan Optik Einstein - Teroptimasi)
					for g in range(grav_count):
						var g_pos: Vector2 = local_g_pos[g]
						var g_rad: float = minf(local_g_rad[g], 420.0)
						var diff: Vector2 = world_p - g_pos
						var dx: float = absf(diff.x)
						var dy: float = absf(diff.y)
						if dx >= g_rad or dy >= g_rad:
							continue
						var dist_sq: float = diff.length_squared()
						if dist_sq < g_rad * g_rad and dist_sq > 0.04:
							var dist: float = sqrt(dist_sq)
							var radial_dir: Vector2 = diff / dist
							var tangent_dir: Vector2 = Vector2(-radial_dir.y, radial_dir.x)
							var g_str: float = local_g_str[g]
							var g_infall: float = local_g_inf[g]
							
							var falloff: float = 1.0 - (dist / g_rad)
							var smooth_f: float = falloff * falloff * (1.6 + 1.4 * falloff)
							
							# 1. Secara Optik: Einstein Starlight Deflection (melengkung memutari tepi horizon bintang)
							var einstein_r: float = g_rad * 0.16
							var ring_sigma: float = maxf(24.0, g_rad * 0.10)
							var ring_d: float = absf(dist - einstein_r)
							var einstein_arc: float = 0.0
							if ring_d < ring_sigma * 2.2:
								var norm_d: float = ring_d / (ring_sigma * 2.2)
								var d2: float = 1.0 - norm_d * norm_d
								einstein_arc = d2 * d2
							var optical_bend: float = einstein_arc * (65.0 * g_str)
							
							# 2. Secara Fisik: Tarikan Massa Eksotis Super-Berat ke dalam corong gravitasi
							var pull_fraction: float = clampf(smooth_f * (0.58 + 0.30 * g_infall) * g_str * depth_weight, 0.0, 0.88)
							var inward_pull: float = dist * pull_fraction
							var vortex_swirl: float = smooth_f * (48.0 + 75.0 * g_infall) * g_str * depth_weight
							
							var disp: Vector2 = (-radial_dir * inward_pull) + (tangent_dir * (vortex_swirl + optical_bend))
							world_p += disp
							
							# Regangan cahaya bintang mengikuti lengkungan ruang-waktu
							streak_vec += (-radial_dir * (smooth_f * (14.0 + 22.0 * g_infall)) + tangent_dir * (einstein_arc * 18.0 + smooth_f * 10.0)) * g_str * depth_weight
							total_warp_energy = maxf(total_warp_energy, (smooth_f * 0.85 + einstein_arc * 1.1) * g_str)
					
					if total_warp_energy > 0.04:
						var boost_t: float = clampf(total_warp_energy * 0.65, 0.0, 0.90)
						var orig_a: float = col.a
						col = col.lerp(lensing_tint, boost_t)
						col.a = minf(1.0, orig_a + boost_t * 0.40)
					
					var half_w: float = base_size * 0.5
					star_points.append(world_p - Vector2(half_w, 0.0) - streak_vec * 0.5)
					star_points.append(world_p + Vector2(half_w, 0.0) + streak_vec * 0.5)
					star_colors.append(col)
					
					if layer >= 1:
						star_points.append(world_p - Vector2(0.0, half_w) - streak_vec * 0.25)
						star_points.append(world_p + Vector2(0.0, half_w) + streak_vec * 0.25)
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
					var disp_a: float = p_a.distance_to(physics.base_positions[idx_a]) / cell_size
					var disp_b: float = p_b.distance_to(physics.base_positions[idx_b]) / cell_size
					var well_pull: float = (disp_a + disp_b) * 0.28
					var energy: float = clampf((stretch * 2.8) + well_pull + (heat * 0.65), 0.0, 1.0)
					if energy <= 0.012 and heat <= 0.02:
						static_list.append(p_a)
						static_list.append(p_b)
					else:
						active_points.append(p_a)
						active_points.append(p_b)
						var col: Color = rest_color.lerp(surge_color, energy)
						if heat > 0.14:
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
					var disp_a: float = p_a.distance_to(physics.base_positions[idx_a]) / cell_size
					var disp_b: float = p_b.distance_to(physics.base_positions[idx_b]) / cell_size
					var well_pull: float = (disp_a + disp_b) * 0.28
					var energy: float = clampf((stretch * 2.8) + well_pull + (heat * 0.65), 0.0, 1.0)
					if energy <= 0.012 and heat <= 0.02:
						static_list.append(p_a)
						static_list.append(p_b)
					else:
						active_points.append(p_a)
						active_points.append(p_b)
						var col: Color = rest_color.lerp(surge_color, energy)
						if heat > 0.14:
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
