class_name GridPhysics
extends RefCounted

## GridPhysics - Geometry Wars Style Spring Lattice
## Simulasi grid pegas elastis 2D (PointMass + Anchor Springs + Structural Springs).
## Menggunakan integrasi semi-implicit Euler dengan relaksasi pegas antar-tetangga (Verlet/PBD)
## untuk menghasilkan riakan kain/karet elastis autentik ala Geometry Wars.

# Parameter fisika kisi pegas (Spring Lattice)
var anchor_stiffness: float = 24.0      # Kekuatan pegas pengikat ke titik asal (home anchor)
var damping: float = 0.965              # Multiplier redaman per tick (kehilangan energi halus)
var neighbor_relaxation: float = 0.38   # Faktor elastisitas pegas antar tetangga (0.1 - 0.5)
var max_displacement: float = 140.0     # Toleransi pergeseran maksimal agar tidak sobek

# Dimensi grid
var cell_size: float = 36.0
var cols: int = 0
var rows: int = 0
var origin: Vector2 = Vector2.ZERO
var total_points: int = 0

# Data titik: posisi awal, posisi sekarang, kecepatan
var base_positions: PackedVector2Array = []
var positions: PackedVector2Array = []
var velocities: PackedVector2Array = []

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
	
	for r in range(rows):
		var row_offset: int = r * cols
		for c in range(cols):
			var idx: int = row_offset + c
			var p: Vector2 = origin + Vector2(c * cell_size, r * cell_size)
			base_positions[idx] = p
			positions[idx] = p
			velocities[idx] = Vector2.ZERO

## Langkah simulasi fisika: Integrasi pegas anchor + propagasi pegas tetangga
func step(delta: float) -> void:
	if total_points == 0:
		return
		
	var dt: float = minf(delta, 0.033)
	var max_disp_sq: float = max_displacement * max_displacement
	var decay: float = pow(damping, dt * 60.0)
	
	# Tahap 1: Anchor Springs (Tiap titik ditarik pulang ke posisi aslinya)
	for i in range(total_points):
		var to_home: Vector2 = base_positions[i] - positions[i]
		var force: Vector2 = to_home * anchor_stiffness
		
		velocities[i] = (velocities[i] + force * dt) * decay
		positions[i] += velocities[i] * dt
		
		# Batasi deformasi ekstrem
		var disp: Vector2 = positions[i] - base_positions[i]
		if disp.length_squared() > max_disp_sq:
			positions[i] = base_positions[i] + disp.normalized() * max_displacement
			velocities[i] *= 0.5

	# Tahap 2: Pegas Antar-Tetangga (Spring Relaxation)
	# Menyebarkan regangan ke tetangga horizontal & vertikal secara elastis
	var relax_half: float = neighbor_relaxation * 0.5
	
	# Relaksasi Horizontal
	for r in range(rows):
		var row_offset: int = r * cols
		for c in range(cols - 1):
			var idx_a: int = row_offset + c
			var idx_b: int = idx_a + 1
			
			var diff: Vector2 = positions[idx_b] - positions[idx_a]
			var dist: float = diff.length()
			if dist > 0.001:
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
			if dist > 0.001:
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

## Reset seluruh grid ke posisi asal
func reset() -> void:
	for i in range(total_points):
		positions[i] = base_positions[i]
		velocities[i] = Vector2.ZERO

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
