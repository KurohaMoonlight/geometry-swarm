class_name GridPhysics
extends RefCounted

## GridPhysics - Geometry Wars Style Spring Lattice + Spacetime Rift & Rocket Exhaust
## Simulasi grid pegas elastis 2D (PointMass + Anchor Springs + Structural Springs).
## Mendukung efek hisapan pusaran vortex, robekan ruang-waktu (spacetime tear) di moncong
## Super Giga Drill, dan semburan gelombang kejut jet roket (rocket exhaust) di pangkal belakang.

# Parameter fisika kisi pegas (Spring Lattice)
var anchor_stiffness: float = 24.0      # Kekuatan pegas pengikat ke titik asal (home anchor)
var damping: float = 0.965              # Multiplier redaman per tick (kehilangan energi halus)
var neighbor_relaxation: float = 0.38   # Faktor elastisitas pegas antar tetangga (0.1 - 0.5)
var max_displacement: float = 165.0     # Toleransi pergeseran maksimal untuk efek robekan ruang-waktu

# Dimensi grid
var cell_size: float = 36.0
var cols: int = 0
var rows: int = 0
var origin: Vector2 = Vector2.ZERO
var total_points: int = 0

# Data titik: posisi awal, posisi sekarang, kecepatan, dan intensitas panas/robekan (heat/rift)
var base_positions: PackedVector2Array = []
var positions: PackedVector2Array = []
var velocities: PackedVector2Array = []
var rift_heat: PackedFloat32Array = []

## Inisialisasi grid berpegas melingkupi layar
func setup_grid(size: Vector2, p_cell_size: float = 36.0, margin: float = 70.0) -> void:
	cell_size = p_cell_size
	origin = Vector2(-margin, -margin)
	var extended_size: Vector2 = size + Vector2(margin * 2.0, margin * 2.0)
	
	cols = int(ceil(extended_size.x / cell_size)) + 1
	rows = int(ceil(extended_size.y / cell_size)) + 1
	total_points = cols * rows
	
	base_positions.resize(total_points)
	positions.resize(total_points)
	velocities.resize(total_points)
	rift_heat.resize(total_points)
	
	for r in range(rows):
		var row_offset: int = r * cols
		for c in range(cols):
			var idx: int = row_offset + c
			var p: Vector2 = origin + Vector2(c * cell_size, r * cell_size)
			base_positions[idx] = p
			positions[idx] = p
			velocities[idx] = Vector2.ZERO
			rift_heat[idx] = 0.0

## Langkah simulasi fisika: Integrasi pegas anchor + propagasi pegas tetangga
func step(delta: float) -> void:
	if total_points == 0:
		return
		
	var dt: float = minf(delta, 0.033)
	var max_disp_sq: float = max_displacement * max_displacement
	var decay: float = pow(damping, dt * 60.0)
	var heat_decay: float = pow(0.91, dt * 60.0)
	
	# Tahap 1: Anchor Springs (Tiap titik ditarik pulang ke posisi aslinya)
	for i in range(total_points):
		var to_home: Vector2 = base_positions[i] - positions[i]
		var force: Vector2 = to_home * anchor_stiffness
		
		velocities[i] = (velocities[i] + force * dt) * decay
		positions[i] += velocities[i] * dt
		rift_heat[i] *= heat_decay
		
		# Batasi deformasi ekstrem
		var disp: Vector2 = positions[i] - base_positions[i]
		if disp.length_squared() > max_disp_sq:
			positions[i] = base_positions[i] + disp.normalized() * max_displacement
			velocities[i] *= 0.5

	# Tahap 2: Pegas Antar-Tetangga (Spring Relaxation)
	# Menyebarkan regangan ke tetangga secara elastis.
	# Jika regangan melampaui batas robek (spacetime fracture), ikatan fisik putus sementara
	# sampai titik-titik tersebut ditarik pulang oleh anchor springs dan tersambung kembali!
	var relax_half: float = neighbor_relaxation * 0.5
	var fracture_dist: float = cell_size * 2.15
	
	# Relaksasi Horizontal
	for r in range(rows):
		var row_offset: int = r * cols
		for c in range(cols - 1):
			var idx_a: int = row_offset + c
			var idx_b: int = idx_a + 1
			
			var diff: Vector2 = positions[idx_b] - positions[idx_a]
			var dist: float = diff.length()
			var is_drill_fracture: bool = (rift_heat[idx_a] + rift_heat[idx_b]) > 0.35 and dist >= fracture_dist
			if dist > 0.001 and not is_drill_fracture:
				var err: float = dist - cell_size
				var norm: Vector2 = diff / dist
				var offset: Vector2 = norm * (err * relax_half)
				positions[idx_a] += offset
				positions[idx_b] -= offset
				
	# Relaksasi Vertikal
	for r in range(rows - 1):
		var row_offset_a: int = r * cols
		var row_offset_b: int = (r + 1) * cols
		for c in range(cols):
			var idx_a: int = row_offset_a + c
			var idx_b: int = row_offset_b + c
			
			var diff: Vector2 = positions[idx_b] - positions[idx_a]
			var dist: float = diff.length()
			var is_drill_fracture: bool = (rift_heat[idx_a] + rift_heat[idx_b]) > 0.35 and dist >= fracture_dist
			if dist > 0.001 and not is_drill_fracture:
				var err: float = dist - cell_size
				var norm: Vector2 = diff / dist
				var offset: Vector2 = norm * (err * relax_half)
				positions[idx_a] += offset
				positions[idx_b] -= offset

## Dorongan jejak kursor/kapal (Drag & Wake)
func apply_wake_impulse(world_pos: Vector2, move_velocity: Vector2, radius: float) -> void:
	if total_points == 0 or radius <= 0.0 or move_velocity.is_zero_approx():
		return
		
	var r2: float = radius * radius
	var bounds: Rect2i = _get_grid_bounds(world_pos, radius)
	var speed: float = move_velocity.length()
	var move_dir: Vector2 = move_velocity / maxf(speed, 0.001)
	
	for r in range(bounds.position.y, bounds.end.y):
		var row_offset: int = r * cols
		for c in range(bounds.position.x, bounds.end.x):
			var idx: int = row_offset + c
			var p: Vector2 = positions[idx]
			var diff: Vector2 = p - world_pos
			var dist2: float = diff.length_squared()
			if dist2 < r2:
				var dist: float = sqrt(dist2)
				var falloff: float = 1.0 - (dist / radius)
				var smooth_f: float = falloff * falloff * (3.0 - 2.0 * falloff)
				
				# Campuran: dorong ke arah gerak + sedikit membelah ke samping (wake bow)
				var outward: Vector2 = diff.normalized() if dist > 0.01 else Vector2.ZERO
				var force: Vector2 = (move_dir * 0.7 + outward * 0.3) * (speed * 0.45 * smooth_f)
				velocities[idx] += force

## Ledakan gelombang kejut radial (Shockwave / Bomb)
func apply_radial_impulse(world_pos: Vector2, strength: float, radius: float) -> void:
	if total_points == 0 or radius <= 0.0 or is_zero_approx(strength):
		return
		
	var r2: float = radius * radius
	var bounds: Rect2i = _get_grid_bounds(world_pos, radius)
	
	for r in range(bounds.position.y, bounds.end.y):
		var row_offset: int = r * cols
		for c in range(bounds.position.x, bounds.end.x):
			var idx: int = row_offset + c
			var p: Vector2 = positions[idx]
			var diff: Vector2 = p - world_pos
			var dist2: float = diff.length_squared()
			if dist2 < r2 and dist2 > 0.01:
				var dist: float = sqrt(dist2)
				var dir: Vector2 = diff / dist
				var falloff: float = 1.0 - (dist / radius)
				var smooth_f: float = falloff * falloff * (3.0 - 2.0 * falloff)
				velocities[idx] += dir * (strength * smooth_f)

## Efek Pembentukan & Peluncuran SUPER GIGA DRILL BREAK di Hologram Grid:
## 1. Robekan Ruang-Waktu (Spacetime Tear):
##    Memilin, menyedot, lalu membelah kisi hologram ke kanan-kiri di sepanjang kerucut mata bor
##    dengan gelombang geser heliks berfrekuensi tinggi (seperti resleting dimensi yang sobek).
## 2. Semburan Jet Exhaust Roket (Rocket Exhaust Plume):
##    Menyemburkan gelombang kejut kerucut berkecepatan tinggi ke belakang dari pangkal bor.
func apply_giga_drill_spacetime_and_exhaust(
	tip_pos: Vector2,
	base_pos: Vector2,
	aim_dir: Vector2,
	drill_radius: float,
	density_factor: float,
	charge_or_flight_power: float,
	spin_time: float,
	is_launched: bool,
	delta: float
) -> void:
	if total_points == 0:
		return
		
	var dt: float = minf(delta, 0.033)
	var dir: Vector2 = aim_dir.normalized() if aim_dir.length_squared() > 0.001 else Vector2.RIGHT
	var tangent: Vector2 = Vector2(-dir.y, dir.x)
	var drill_vec: Vector2 = tip_pos - base_pos
	var drill_len: float = maxf(40.0, drill_vec.length())
	var center_pos: Vector2 = (tip_pos + base_pos) * 0.5
	
	# Radius efektif mencakup moncong bor di depan dan ekor semburan roket di belakang
	var exhaust_len: float = (180.0 + 95.0 * density_factor) * (1.25 if is_launched else 0.75)
	var influence_radius: float = drill_len * 0.65 + exhaust_len + drill_radius * 2.2
	var bounds: Rect2i = _get_grid_bounds(center_pos - dir * (exhaust_len * 0.35), influence_radius)
	
	var power_scale: float = clampf(charge_or_flight_power, 0.15, 1.5) * (0.75 + 0.45 * density_factor)
	var tear_width: float = maxf(48.0, drill_radius * 2.15)
	
	for r in range(bounds.position.y, bounds.end.y):
		var row_offset: int = r * cols
		for c in range(bounds.position.x, bounds.end.x):
			var idx: int = row_offset + c
			var p: Vector2 = base_positions[idx]
			
			# Proyeksi koordinat titik grid terhadap pangkal bor (base_pos)
			# axial >= 0: di sepanjang badan bor menuju tip_pos
			# axial < 0 : di belakang pangkal bor (zona semburan Rocket Exhaust!)
			var rel: Vector2 = p - base_pos
			var axial: float = rel.dot(dir)
			var lateral: float = rel.dot(tangent)
			var abs_lat: float = absf(lateral)
			
			# =====================================================================
			# A. ZONA ROBEKAN RUANG-WAKTU (Sepanjang Badan & Ujung Moncong Bor)
			# =====================================================================
			if axial >= -20.0 and axial <= drill_len + 75.0:
				var norm_axial: float = clampf(axial / drill_len, 0.0, 1.25)
				var local_cone_r: float = lerpf(tear_width * 1.15, tear_width * 0.45, clampf(norm_axial, 0.0, 1.0))
				
				if abs_lat < local_cone_r:
					var lat_falloff: float = 1.0 - (abs_lat / local_cone_r)
					var smooth_lat: float = lat_falloff * lat_falloff
					var side_sign: float = 1.0 if lateral >= 0.0 else -1.0
					
					# Gelombang heliks pemilin ruang-waktu (berputar sepanjang sumbu bor)
					var helix_wave: float = sin(axial * 0.11 - spin_time * 1.8)
					var pinch_or_rip: Vector2
					
					if is_launched:
						# Saat meluncur: Moncong membelah & merobek grid ke samping dengan sangat kuat
						# disertai tarikan vakum di garis tengah (spacetime wake fissure)
						var rip_outward: float = (1850.0 + 650.0 * helix_wave) * smooth_lat * power_scale
						var forward_drag: float = 720.0 * smooth_lat * power_scale
						pinch_or_rip = tangent * (side_sign * rip_outward) + dir * forward_drag
						rift_heat[idx] = clampf(rift_heat[idx] + smooth_lat * 1.85 * dt * 25.0, 0.0, 2.0)
					else:
						# Saat proses charging: Pusaran gravitasi memilin grid masuk lalu bergetar merobek sumbu
						var vortex_pull: float = -side_sign * (680.0 * (1.0 - charge_or_flight_power * 0.55))
						var tear_shear: float = side_sign * (1150.0 * charge_or_flight_power * helix_wave)
						var axial_twist: float = cos(lateral * 0.09 + spin_time * 1.5) * 520.0 * charge_or_flight_power
						pinch_or_rip = tangent * ((vortex_pull + tear_shear) * smooth_lat) + dir * (axial_twist * smooth_lat)
						rift_heat[idx] = clampf(rift_heat[idx] + smooth_lat * charge_or_flight_power * dt * 16.0, 0.0, 1.6)
						
					velocities[idx] += pinch_or_rip * dt
					
			# =====================================================================
			# B. ZONA SEMBURAN JET EXHAUST ROKET (Di Belakang Pangkal Bor)
			# =====================================================================
			if axial < 10.0 and axial >= -exhaust_len:
				var back_dist: float = -axial
				var back_ratio: float = clampf(back_dist / exhaust_len, 0.0, 1.0)
				# Corong semburan jet melebar ke belakang seperti plume roket
				var plume_half_width: float = lerpf(drill_radius * 0.85, drill_radius * 2.6 + 55.0, back_ratio)
				
				if abs_lat < plume_half_width:
					var lat_f: float = 1.0 - (abs_lat / plume_half_width)
					var axial_f: float = 1.0 - back_ratio
					var jet_intensity: float = lat_f * lat_f * axial_f * power_scale
					
					if is_launched or charge_or_flight_power > 0.55:
						var thruster_mult: float = 2400.0 if is_launched else (1250.0 * (charge_or_flight_power - 0.45))
						# Turbulensi kejut akustik roket (shock diamonds) di sepanjang ekor exhaust
						var shock_diamond: float = 1.0 + 0.45 * sin(back_dist * 0.14 - spin_time * 2.8)
						var side_expansion: float = (lateral / maxf(1.0, plume_half_width)) * 0.48
						var jet_dir: Vector2 = (-dir + tangent * side_expansion).normalized()
						
						velocities[idx] += jet_dir * (thruster_mult * jet_intensity * shock_diamond * dt)
						rift_heat[idx] = clampf(rift_heat[idx] + jet_intensity * shock_diamond * dt * 22.0, 0.0, 2.0)

## Menggeser posisi grid di ruang dunia saat kamera bergerak melintasi peta luas
## Menjaga regangan pegas, riak gelombang, dan robekan ruang-waktu tetap utuh tanpa distorsi
func shift_origin(delta_pos: Vector2) -> void:
	if delta_pos.is_zero_approx() or total_points == 0:
		return
	origin += delta_pos
	for i in range(total_points):
		base_positions[i] += delta_pos
		positions[i] += delta_pos

## Reset seluruh grid ke posisi asal
func reset() -> void:
	for i in range(total_points):
		positions[i] = base_positions[i]
		velocities[i] = Vector2.ZERO
		rift_heat[i] = 0.0

# --- Helper Query Methods ---

func get_point_position(c: int, r: int) -> Vector2:
	return positions[r * cols + c]

func get_point_speed(c: int, r: int) -> float:
	return velocities[r * cols + c].length()

func get_point_displacement(c: int, r: int) -> float:
	var idx: int = r * cols + c
	return (positions[idx] - base_positions[idx]).length()

func get_row_points(r: int) -> PackedVector2Array:
	var pts: PackedVector2Array = []
	pts.resize(cols)
	var row_offset: int = r * cols
	for c in range(cols):
		pts[c] = positions[row_offset + c]
	return pts

func get_col_points(c: int) -> PackedVector2Array:
	var pts: PackedVector2Array = []
	pts.resize(rows)
	for r in range(rows):
		pts[r] = positions[r * cols + c]
	return pts

func _get_grid_bounds(world_pos: Vector2, radius: float) -> Rect2i:
	var min_c: int = clampi(int(floor((world_pos.x - origin.x - radius) / cell_size)), 0, cols - 1)
	var max_c: int = clampi(int(ceil((world_pos.x - origin.x + radius) / cell_size)) + 1, min_c, cols)
	var min_r: int = clampi(int(floor((world_pos.y - origin.y - radius) / cell_size)), 0, rows - 1)
	var max_r: int = clampi(int(ceil((world_pos.y - origin.y + radius) / cell_size)) + 1, min_r, rows)
	return Rect2i(min_c, min_r, max_c - min_c, max_r - min_r)
