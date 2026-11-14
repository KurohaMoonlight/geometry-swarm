class_name GridPhysics
extends RefCounted

## GridPhysics - Dioptimalkan dengan Active Bounds, Dirty Sleep, & World-Space Cell Snapping
## - Saat tidak ada gangguan aktif, simulasi otomatis masuk mode tidur (is_sleeping = true) -> 0 ms CPU.
## - Saat ada riakan kecil, Sacrificial Dart Boost, atau Giga Drill, simulasi HANYA menghitung titik
##   di dalam kotak area aktif (active_bounds), bukan seluruh ribuan titik grid di layar.
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
				rift_heat[idx] = clampf(rift_heat[idx] + smooth_f * 0.75, 0.0, 2.0)

## Efek Ledakan Supernova Spektakuler pada Grid saat Giga Drill Meledak di Permukaan Core!
## Menciptakan robekan ruang-waktu (grid rupture + electric arcs) di pusat ledakan
## serta gelombang kejut cincin ganda yang merentangkan jaring-jaring grid secara dramatis.
func apply_supernova_core_detonation(tip_pos: Vector2, base_pos: Vector2, sacrificed_count: int) -> void:
	if total_points == 0:
		return
		
	var scale_mult: float = clampf(float(sacrificed_count) / 20.0, 0.85, 2.0)
	var outer_radius: float = 420.0 * clampf(scale_mult, 0.9, 1.35)
	var inner_rupture_radius: float = 210.0 * clampf(scale_mult, 0.9, 1.25)
	var r2: float = outer_radius * outer_radius
	
	var bounds: Rect2i = _get_grid_bounds(tip_pos, outer_radius + 45.0)
	_mark_active_bounds(bounds)
	
	var drill_dir: Vector2 = (tip_pos - base_pos).normalized() if tip_pos.distance_squared_to(base_pos) > 1.0 else Vector2.RIGHT
	
	for r in range(bounds.position.y, bounds.end.y):
		var row_offset: int = r * cols
		for c in range(bounds.position.x, bounds.end.x):
			var idx: int = row_offset + c
			var diff: Vector2 = positions[idx] - tip_pos
			var dist2: float = diff.length_squared()
			if dist2 < r2 and dist2 > 0.001:
				var dist: float = sqrt(dist2)
				var radial_dir: Vector2 = diff / dist
				var tangent_dir: Vector2 = Vector2(-radial_dir.y, radial_dir.x)
				var falloff: float = 1.0 - (dist / outer_radius)
				var smooth_f: float = falloff * falloff * (3.0 - 2.0 * falloff)
				
				# Gelombang kejut cincin ganda + puntiran vorteks supernova
				var ring_wave: float = 1.0 + 0.55 * sin((dist / outer_radius) * TAU * 2.0)
				var blast_strength: float = (3400.0 * smooth_f * ring_wave) * scale_mult
				var swirl_strength: float = (950.0 * smooth_f) * (1.0 if radial_dir.dot(drill_dir) >= 0.0 else -1.0)
				
				velocities[idx] += radial_dir * blast_strength + tangent_dir * swirl_strength
				
				# Di zona inti (< inner_rupture_radius), paksa regangan posisi langsung & rift_heat = 2.0
				# agar jaring grid langsung robek (ruptured) dan memancarkan petir sian-emas!
				if dist < inner_rupture_radius:
					var inner_f: float = 1.0 - (dist / inner_rupture_radius)
					positions[idx] += radial_dir * (cell_size * 0.95 * inner_f)
					rift_heat[idx] = 2.0
				else:
					rift_heat[idx] = clampf(rift_heat[idx] + smooth_f * 1.85, 0.0, 2.0)

## Efek Megah Gelombang Kejut Kubah Parabola (Bow Shock) + Hisapan Venturi + Semburan Plasma Jet
func apply_boost_jet_impulse(
	nozzle_pos: Vector2,
	aim_dir: Vector2,
	power_ratio: float,
	pulse_time: float,
	delta: float
) -> void:
	if total_points == 0 or power_ratio <= 0.01:
		return
		
	var dt: float = minf(delta, 0.033)
	var dir: Vector2 = aim_dir.normalized() if aim_dir.length_squared() > 0.001 else Vector2.RIGHT
	var tangent: Vector2 = Vector2(-dir.y, dir.x)
	
	var exhaust_len: float = lerpf(240.0, 490.0, power_ratio)
	var plume_max_r: float = lerpf(92.0, 195.0, power_ratio)
	var bow_len: float = lerpf(110.0, 210.0, power_ratio)
	var center_pos: Vector2 = nozzle_pos - dir * (exhaust_len * 0.28)
	var bounds: Rect2i = _get_grid_bounds(center_pos, exhaust_len * 0.72 + plume_max_r + 65.0)
	_mark_active_bounds(bounds)
	
	for r in range(bounds.position.y, bounds.end.y):
		var row_offset: int = r * cols
		for c in range(bounds.position.x, bounds.end.x):
			var idx: int = row_offset + c
			var p: Vector2 = base_positions[idx]
			var rel: Vector2 = p - nozzle_pos
			var axial: float = rel.dot(dir)
			var lateral: float = rel.dot(tangent)
			var abs_lat: float = absf(lateral)
			var side_sign: float = 1.0 if lateral >= 0.0 else -1.0
			
			# 1. Gelombang Kejut Kubah Parabola Depan (Parabolic Mach Cone Bow Shockwave)
			#    Membelah grid ruang angkasa ke samping mengikuti kubah parabola swarm di depan Core!
			if axial >= -25.0 and axial <= bow_len:
				var parabola_curve_x: float = bow_len * 0.72 - 0.0052 * (lateral * lateral)
				var dist_to_front: float = absf(axial - parabola_curve_x)
				var wave_thickness: float = lerpf(48.0, 82.0, power_ratio)
				
				if dist_to_front < wave_thickness and abs_lat < 190.0:
					var front_f: float = 1.0 - (dist_to_front / wave_thickness)
					var lat_f: float = 1.0 - clampf(abs_lat / 190.0, 0.0, 1.0)
					var shock_intensity: float = front_f * front_f * lat_f * power_ratio
					
					var bow_normal: Vector2 = (dir * 0.48 + tangent * (side_sign * 0.88)).normalized()
					var shimmer: float = 1.0 + 0.35 * sin(abs_lat * 0.12 - pulse_time * 2.2)
					velocities[idx] += bow_normal * (1450.0 * shock_intensity * shimmer * dt)
					rift_heat[idx] = clampf(rift_heat[idx] + shock_intensity * shimmer * dt * 19.0, 0.0, 1.95)
			
			# 2. Hisapan Venturi Pinch di Leher Ruang Bakar Nozzle (Tepat di belakang Core)
			if axial >= -75.0 and axial <= 20.0 and abs_lat < 120.0:
				var neck_ax: float = 1.0 - clampf(absf(axial + 24.0) / 52.0, 0.0, 1.0)
				var neck_lat: float = 1.0 - (abs_lat / 120.0)
				var pinch_f: float = neck_ax * neck_lat * power_ratio
				velocities[idx] += (-tangent * side_sign * 780.0 - dir * 420.0) * (pinch_f * dt)
				rift_heat[idx] = clampf(rift_heat[idx] + pinch_f * dt * 14.0, 0.0, 1.8)
			
			# 3. Semburan Utama Plasma Jet Thrust & Cincin Mach Shock Diamonds di Buritan (-aim_dir)
			if axial < 10.0 and axial >= -exhaust_len:
				var back_dist: float = maxf(0.0, -axial)
				var back_ratio: float = clampf(back_dist / exhaust_len, 0.0, 1.0)
				var plume_half_w: float = lerpf(32.0, plume_max_r, pow(back_ratio, 0.78))
				
				if abs_lat < plume_half_w:
					var lat_f: float = 1.0 - (abs_lat / plume_half_w)
					var axial_f: float = 1.0 - back_ratio
					var intensity: float = lat_f * lat_f * axial_f * power_ratio
					
					# Gelombang berdiri Mach Shock Diamonds & turbulensi heliks plasma
					var mach_diamond: float = 1.0 + 0.68 * sin(back_dist * 0.14 - pulse_time * 2.6)
					var vortex_swirl: float = cos(back_dist * 0.09 - pulse_time * 1.9) * side_sign * 0.38
					var side_spread: float = (lateral / maxf(1.0, plume_half_w)) * 0.65 + vortex_swirl
					var jet_dir: Vector2 = (-dir + tangent * side_spread).normalized()
					
					velocities[idx] += jet_dir * (2650.0 * intensity * mach_diamond * dt)
					rift_heat[idx] = clampf(rift_heat[idx] + intensity * mach_diamond * dt * 28.0, 0.0, 2.0)

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
	var shift_cols: int = int(round(delta_snap.x / cell_size))
	var shift_rows: int = int(round(delta_snap.y / cell_size))
	
	if shift_cols == 0 and shift_rows == 0:
		return false
		
	origin = snapped_origin
	
	if is_sleeping or not has_active_deformation or abs(shift_cols) >= cols or abs(shift_rows) >= rows:
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
		return true

	var new_positions: PackedVector2Array = PackedVector2Array()
	var new_velocities: PackedVector2Array = PackedVector2Array()
	var new_heat: PackedFloat32Array = PackedFloat32Array()
	new_positions.resize(total_points)
	new_velocities.resize(total_points)
	new_heat.resize(total_points)
	
	for r in range(rows):
		var old_r: int = r + shift_rows
		var row_offset: int = r * cols
		var py: float = origin.y + r * cell_size
		for c in range(cols):
			var old_c: int = c + shift_cols
			var idx: int = row_offset + c
			var base_p: Vector2 = Vector2(origin.x + c * cell_size, py)
			base_positions[idx] = base_p
			
			if old_r >= 0 and old_r < rows and old_c >= 0 and old_c < cols:
				var old_idx: int = old_r * cols + old_c
				new_positions[idx] = positions[old_idx]
				new_velocities[idx] = velocities[old_idx]
				new_heat[idx] = rift_heat[old_idx]
			else:
				new_positions[idx] = base_p
				new_velocities[idx] = Vector2.ZERO
				new_heat[idx] = 0.0
				
	positions = new_positions
	velocities = new_velocities
	rift_heat = new_heat
	
	active_min_c = clampi(active_min_c - shift_cols, 0, cols - 1)
	active_max_c = clampi(active_max_c - shift_cols, 0, cols - 1)
	active_min_r = clampi(active_min_r - shift_rows, 0, rows - 1)
	active_max_r = clampi(active_max_r - shift_rows, 0, rows - 1)
	return true

func reset() -> void:
	for i in range(total_points):
		positions[i] = base_positions[i]
		velocities[i] = Vector2.ZERO
		rift_heat[i] = 0.0
	is_sleeping = true
	has_active_deformation = false
	active_min_c = cols
	active_max_c = -1
	active_min_r = rows
	active_max_r = -1

func _get_grid_bounds(world_pos: Vector2, radius: float) -> Rect2i:
	var local: Vector2 = world_pos - origin
	var min_c: int = maxi(0, int(floor((local.x - radius) / cell_size)))
	var max_c: int = mini(cols, int(ceil((local.x + radius) / cell_size)) + 1)
	var min_r: int = maxi(0, int(floor((local.y - radius) / cell_size)))
	var max_r: int = mini(rows, int(ceil((local.y + radius) / cell_size)) + 1)
	return Rect2i(min_c, min_r, maxi(0, max_c - min_c), maxi(0, max_r - min_r))
