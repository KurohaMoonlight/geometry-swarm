class_name GridPhysics
extends RefCounted

## GridPhysics - Dioptimalkan dengan Active Bounds, Dirty Sleep, & World-Space Cell Snapping
## - Saat tidak ada gangguan aktif, simulasi otomatis masuk mode tidur (is_sleeping = true) -> 0 ms CPU.
## - Saat ada riakan kecil atau Giga Drill, simulasi HANYA menghitung titik di dalam kotak area aktif
##   (active_bounds), bukan seluruh ribuan titik grid di layar.
## - Pergeseran kamera menggunakan pencocokan kelipatan cell_size (snap) sehingga grid latar belakang
##   tetap diam di ruang angkasa saat kapal bergerak dan hanya bergeser mikro jika benar-benar disentuh.

var anchor_stiffness: float = 28.0
var damping: float = 0.92
var neighbor_relaxation: float = 0.34
var max_displacement: float = 165.0

var cell_size: float = 68.0
var cols: int = 0
var rows: int = 0
var origin: Vector2 = Vector2.ZERO
var total_points: int = 0

var base_positions: PackedVector2Array = []
var positions: PackedVector2Array = []
var velocities: PackedVector2Array = []
var rift_heat: PackedFloat32Array = []

# Pelacakan area aktif (Dirty Region) agar tidak perlu meloop seluruh grid setiap frame
var is_sleeping: bool = true
var has_active_deformation: bool = false
var active_min_c: int = 0
var active_max_c: int = 0
var active_min_r: int = 0
var active_max_r: int = 0

func setup_grid(size: Vector2, p_cell_size: float = 68.0, margin: float = 68.0) -> void:
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
		var py: float = origin.y + r * cell_size
		for c in range(cols):
			var idx: int = row_offset + c
			var p: Vector2 = Vector2(origin.x + c * cell_size, py)
			base_positions[idx] = p
			positions[idx] = p
			velocities[idx] = Vector2.ZERO
			rift_heat[idx] = 0.0
			
	is_sleeping = true
	has_active_deformation = false
	active_min_c = cols
	active_max_c = -1
	active_min_r = rows
	active_max_r = -1

func _mark_active_bounds(bounds: Rect2i) -> void:
	if bounds.size.x <= 0 or bounds.size.y <= 0:
		return
	var pad_min_c: int = maxi(0, bounds.position.x - 2)
	var pad_max_c: int = mini(cols - 1, bounds.end.x + 2)
	var pad_min_r: int = maxi(0, bounds.position.y - 2)
	var pad_max_r: int = mini(rows - 1, bounds.end.y + 2)
	
	if is_sleeping or active_max_c < active_min_c:
		active_min_c = pad_min_c
		active_max_c = pad_max_c
		active_min_r = pad_min_r
		active_max_r = pad_max_r
	else:
		active_min_c = mini(active_min_c, pad_min_c)
		active_max_c = maxi(active_max_c, pad_max_c)
		active_min_r = mini(active_min_r, pad_min_r)
		active_max_r = maxi(active_max_r, pad_max_r)
		
	is_sleeping = false
	has_active_deformation = true

## Langkah simulasi fisika hanya pada area aktif (Zero-Overhead saat tenang)
func step(delta: float) -> bool:
	if total_points == 0 or is_sleeping:
		return false
		
	var dt: float = minf(delta, 0.033)
	var max_disp_sq: float = max_displacement * max_displacement
	var decay: float = pow(damping, dt * 60.0)
	var heat_decay: float = pow(0.88, dt * 60.0)
	
	var min_c: int = clampi(active_min_c, 0, cols - 1)
	var max_c: int = clampi(active_max_c, 0, cols - 1)
	var min_r: int = clampi(active_min_r, 0, rows - 1)
	var max_r: int = clampi(active_max_r, 0, rows - 1)
	
	var next_min_c: int = cols
	var next_max_c: int = -1
	var next_min_r: int = rows
	var next_max_r: int = -1
	var any_moving: bool = false
	
	# Tahap 1: Anchor Springs hanya di dalam kotak aktif
	for r in range(min_r, max_r + 1):
		var row_offset: int = r * cols
		for c in range(min_c, max_c + 1):
			var idx: int = row_offset + c
			var to_home: Vector2 = base_positions[idx] - positions[idx]
			var vel: Vector2 = (velocities[idx] + to_home * (anchor_stiffness * dt)) * decay
			var pos: Vector2 = positions[idx] + vel * dt
			var heat: float = rift_heat[idx] * heat_decay
			
			var disp: Vector2 = pos - base_positions[idx]
			var disp_sq: float = disp.length_squared()
			var vel_sq: float = vel.length_squared()
			
			if disp_sq > max_disp_sq:
				pos = base_positions[idx] + disp.normalized() * max_displacement
				vel *= 0.5
				disp_sq = max_disp_sq
				
			if disp_sq > 0.08 or vel_sq > 0.4 or heat > 0.02:
				positions[idx] = pos
				velocities[idx] = vel
				rift_heat[idx] = heat
				any_moving = true
				if c < next_min_c: next_min_c = c
				if c > next_max_c: next_max_c = c
				if r < next_min_r: next_min_r = r
				if r > next_max_r: next_max_r = r
			else:
				positions[idx] = base_positions[idx]
				velocities[idx] = Vector2.ZERO
				rift_heat[idx] = 0.0

	if not any_moving:
		is_sleeping = true
		has_active_deformation = false
		active_min_c = cols
		active_max_c = -1
		active_min_r = rows
		active_max_r = -1
		return true

	# Tahap 2: Relaksasi Pegas Antar-Tetangga hanya di dalam kotak aktif
	var relax_half: float = neighbor_relaxation * 0.5
	var fracture_dist: float = cell_size * 2.15
	
	var h_max_c: int = mini(max_c, cols - 2)
	for r in range(min_r, max_r + 1):
		var row_offset: int = r * cols
		for c in range(min_c, h_max_c + 1):
			var idx_a: int = row_offset + c
			var idx_b: int = idx_a + 1
			var diff: Vector2 = positions[idx_b] - positions[idx_a]
			var dist_sq: float = diff.length_squared()
			if absf(dist_sq - cell_size * cell_size) < 0.25:
				continue
			var dist: float = sqrt(dist_sq)
			var is_drill_fracture: bool = (rift_heat[idx_a] + rift_heat[idx_b]) > 0.35 and dist >= fracture_dist
			if dist > 0.001 and not is_drill_fracture:
				var offset: Vector2 = (diff / dist) * ((dist - cell_size) * relax_half)
				positions[idx_a] += offset
				positions[idx_b] -= offset
				
	var v_max_r: int = mini(max_r, rows - 2)
	for r in range(min_r, v_max_r + 1):
		var row_offset_a: int = r * cols
		var row_offset_b: int = (r + 1) * cols
		for c in range(min_c, max_c + 1):
			var idx_a: int = row_offset_a + c
			var idx_b: int = row_offset_b + c
			var diff: Vector2 = positions[idx_b] - positions[idx_a]
			var dist_sq: float = diff.length_squared()
			if absf(dist_sq - cell_size * cell_size) < 0.25:
				continue
			var dist: float = sqrt(dist_sq)
			var is_drill_fracture: bool = (rift_heat[idx_a] + rift_heat[idx_b]) > 0.35 and dist >= fracture_dist
			if dist > 0.001 and not is_drill_fracture:
				var offset: Vector2 = (diff / dist) * ((dist - cell_size) * relax_half)
				positions[idx_a] += offset
				positions[idx_b] -= offset
				
	active_min_c = maxi(0, next_min_c - 1)
	active_max_c = mini(cols - 1, next_max_c + 1)
	active_min_r = maxi(0, next_min_r - 1)
	active_max_r = mini(rows - 1, next_max_r + 1)
	return true

## Riakan sangat halus & kecil tepat di bawah kapal saat bergerak (tidak menggeser seluruh grid)
func apply_wake_impulse(world_pos: Vector2, move_velocity: Vector2, radius: float) -> void:
	if total_points == 0 or radius <= 0.0 or move_velocity.length_squared() < 2500.0:
		return
		
	var r2: float = radius * radius
	var bounds: Rect2i = _get_grid_bounds(world_pos, radius)
	_mark_active_bounds(bounds)
	
	var speed: float = move_velocity.length()
	var move_dir: Vector2 = move_velocity / maxf(speed, 0.001)
	
	for r in range(bounds.position.y, bounds.end.y):
		var row_offset: int = r * cols
		for c in range(bounds.position.x, bounds.end.x):
			var idx: int = row_offset + c
			var diff: Vector2 = positions[idx] - world_pos
			var dist2: float = diff.length_squared()
			if dist2 < r2:
				var dist: float = sqrt(dist2)
				var falloff: float = 1.0 - (dist / radius)
				var smooth_f: float = falloff * falloff
				var outward: Vector2 = diff / maxf(dist, 0.01)
				# Dorongan mikro sangat halus (hanya bergetar sedikit di bawah kapal)
				var force: Vector2 = (move_dir * 0.4 + outward * 0.6) * (speed * 0.045 * smooth_f)
				velocities[idx] += force

func apply_radial_impulse(world_pos: Vector2, strength: float, radius: float) -> void:
	if total_points == 0 or radius <= 0.0 or is_zero_approx(strength):
		return
		
	var r2: float = radius * radius
	var bounds: Rect2i = _get_grid_bounds(world_pos, radius)
	_mark_active_bounds(bounds)
	
	for r in range(bounds.position.y, bounds.end.y):
		var row_offset: int = r * cols
		for c in range(bounds.position.x, bounds.end.x):
			var idx: int = row_offset + c
			var diff: Vector2 = positions[idx] - world_pos
			var dist2: float = diff.length_squared()
			if dist2 < r2 and dist2 > 0.01:
				var dist: float = sqrt(dist2)
				var dir: Vector2 = diff / dist
				var falloff: float = 1.0 - (dist / radius)
				var smooth_f: float = falloff * falloff * (3.0 - 2.0 * falloff)
				velocities[idx] += dir * (strength * smooth_f)

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
	
	var exhaust_len: float = (180.0 + 95.0 * density_factor) * (1.25 if is_launched else 0.75)
	var influence_radius: float = drill_len * 0.65 + exhaust_len + drill_radius * 2.2
	var bounds: Rect2i = _get_grid_bounds(center_pos - dir * (exhaust_len * 0.35), influence_radius)
	_mark_active_bounds(bounds)
	
	var power_scale: float = clampf(charge_or_flight_power, 0.15, 1.5) * (0.75 + 0.45 * density_factor)
	var tear_width: float = maxf(48.0, drill_radius * 2.15)
	
	for r in range(bounds.position.y, bounds.end.y):
		var row_offset: int = r * cols
		for c in range(bounds.position.x, bounds.end.x):
			var idx: int = row_offset + c
			var p: Vector2 = base_positions[idx]
			
			var rel: Vector2 = p - base_pos
			var axial: float = rel.dot(dir)
			var lateral: float = rel.dot(tangent)
			var abs_lat: float = absf(lateral)
			
			if axial >= -20.0 and axial <= drill_len + 75.0:
				var norm_axial: float = clampf(axial / drill_len, 0.0, 1.25)
				var local_cone_r: float = lerpf(tear_width * 1.15, tear_width * 0.45, clampf(norm_axial, 0.0, 1.0))
				
				if abs_lat < local_cone_r:
					var lat_falloff: float = 1.0 - (abs_lat / local_cone_r)
					var smooth_lat: float = lat_falloff * lat_falloff
					var side_sign: float = 1.0 if lateral >= 0.0 else -1.0
					var helix_wave: float = sin(axial * 0.11 - spin_time * 1.8)
					var pinch_or_rip: Vector2
					
					if is_launched:
						var rip_outward: float = (1850.0 + 650.0 * helix_wave) * smooth_lat * power_scale
						var forward_drag: float = 720.0 * smooth_lat * power_scale
						pinch_or_rip = tangent * (side_sign * rip_outward) + dir * forward_drag
						rift_heat[idx] = clampf(rift_heat[idx] + smooth_lat * 1.85 * dt * 25.0, 0.0, 2.0)
					else:
						var vortex_pull: float = -side_sign * (680.0 * (1.0 - charge_or_flight_power * 0.55))
						var tear_shear: float = side_sign * (1150.0 * charge_or_flight_power * helix_wave)
						var axial_twist: float = cos(lateral * 0.09 + spin_time * 1.5) * 520.0 * charge_or_flight_power
						pinch_or_rip = tangent * ((vortex_pull + tear_shear) * smooth_lat) + dir * (axial_twist * smooth_lat)
						rift_heat[idx] = clampf(rift_heat[idx] + smooth_lat * charge_or_flight_power * dt * 16.0, 0.0, 1.6)
						
					velocities[idx] += pinch_or_rip * dt
					
			if axial < 10.0 and axial >= -exhaust_len:
				var back_dist: float = -axial
				var back_ratio: float = clampf(back_dist / exhaust_len, 0.0, 1.0)
				var plume_half_width: float = lerpf(drill_radius * 0.85, drill_radius * 2.6 + 55.0, back_ratio)
				
				if abs_lat < plume_half_width:
					var lat_f: float = 1.0 - (abs_lat / plume_half_width)
					var axial_f: float = 1.0 - back_ratio
					var jet_intensity: float = lat_f * lat_f * axial_f * power_scale
					
					if is_launched or charge_or_flight_power > 0.55:
						var thruster_mult: float = 2400.0 if is_launched else (1250.0 * (charge_or_flight_power - 0.45))
						var shock_diamond: float = 1.0 + 0.45 * sin(back_dist * 0.14 - spin_time * 2.8)
						var side_expansion: float = (lateral / maxf(1.0, plume_half_width)) * 0.48
						var jet_dir: Vector2 = (-dir + tangent * side_expansion).normalized()
						
						velocities[idx] += jet_dir * (thruster_mult * jet_intensity * shock_diamond * dt)
						rift_heat[idx] = clampf(rift_heat[idx] + jet_intensity * shock_diamond * dt * 22.0, 0.0, 2.0)

## Menggeser jendela grid HANYA dalam kelipatan utuh cell_size saat kamera berpindah jauh.
## Dengan kelipatan cell_size, garis-garis grid tetap 100% DIAM di tempatnya di ruang dunia!
func sync_window_to_center(world_center: Vector2, half_grid_size: Vector2) -> bool:
	if total_points == 0:
		return false
		
	var desired_origin: Vector2 = world_center - half_grid_size
	var snapped_origin: Vector2 = Vector2(
		floor(desired_origin.x / cell_size) * cell_size,
		floor(desired_origin.y / cell_size) * cell_size
	)
	
	var delta_snap: Vector2 = snapped_origin - origin
	# Hanya geser jika kamera sudah berpindah minimal 2 sel penuh
	if absf(delta_snap.x) < cell_size * 2.0 and absf(delta_snap.y) < cell_size * 2.0:
		return false
		
	origin = snapped_origin
	for r in range(rows):
		var row_offset: int = r * cols
		var py: float = origin.y + r * cell_size
		for c in range(cols):
			var idx: int = row_offset + c
			var p: Vector2 = Vector2(origin.x + c * cell_size, py)
			base_positions[idx] = p
			if is_sleeping:
				positions[idx] = p
			else:
				positions[idx] += delta_snap
	return true

func reset() -> void:
	for i in range(total_points):
		positions[i] = base_positions[i]
		velocities[i] = Vector2.ZERO
		rift_heat[i] = 0.0
	is_sleeping = true
	has_active_deformation = false

func _get_grid_bounds(world_pos: Vector2, radius: float) -> Rect2i:
	var min_c: int = clampi(int(floor((world_pos.x - origin.x - radius) / cell_size)), 0, cols - 1)
	var max_c: int = clampi(int(ceil((world_pos.x - origin.x + radius) / cell_size)) + 1, min_c, cols)
	var min_r: int = clampi(int(floor((world_pos.y - origin.y - radius) / cell_size)), 0, rows - 1)
	var max_r: int = clampi(int(ceil((world_pos.y - origin.y + radius) / cell_size)) + 1, min_r, rows)
	return Rect2i(min_c, min_r, max_c - min_c, max_r - min_r)
