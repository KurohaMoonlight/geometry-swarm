shader_type canvas_item;
render_mode blend_mix, unshaded;

// =========================================================================
// SUPER GIGA DRILL BREAK - ULTIMATE SOLAR-ABYSSAL CORONA CATACLYSM SHADER
// =========================================================================
// Versi Ultimate Move ("GG") dengan Efek Erupsi Korona Bintang Raksasa:
// 1. CORONAL MASS EJECTION (CME) FLARE ARCHES: Busur-busur erupsi korona raksasa yang meledak keluar
// 2. NOZZLE SUPERNOVA CORONA CROWN: Pancaran sinar korona bintang di pangkal mata bor
// 3. 4-STRAND COUNTER-ROTATING ABYSSAL TORNADO: 2 Heliks Putih Menyilaukan + 2 Heliks Hitam Void
// 4. SINGULARITY SHOCK DIAMONDS & INTERNAL VOID LIGHTNING dalam palet Putih, Hitam Void, Ungu Tua HDR

uniform float power : hint_range(0.0, 1.0) = 1.0;
uniform float spin_time = 0.0;
uniform float rpm = 32.0;
uniform float swarm_scale = 1.0;
uniform float diamond_count = 8.0;

uniform vec4 core_white_color : source_color = vec4(8.4, 8.0, 9.5, 1.0);
uniform vec4 hyper_violet_corona : source_color = vec4(5.2, 0.88, 8.4, 1.0);
uniform vec4 deep_purple_corona : source_color = vec4(2.45, 0.18, 5.20, 1.0);
uniform vec4 dark_abyss_purple : source_color = vec4(0.75, 0.04, 1.85, 1.0);
uniform vec4 void_black_color : source_color = vec4(0.008, 0.002, 0.018, 1.0);

float hash21(vec2 p) {
	p = fract(p * vec2(234.34, 435.345));
	p += dot(p, p + 34.23);
	return fract(p.x * p.y);
}

float noise2D(vec2 p) {
	vec2 id = floor(p);
	vec2 lv = fract(p);
	lv = lv * lv * (3.0 - 2.0 * lv);

	float bl = hash21(id);
	float br = hash21(id + vec2(1.0, 0.0));
	float tl = hash21(id + vec2(0.0, 1.0));
	float tr = hash21(id + vec2(1.0, 1.0));

	return mix(mix(bl, br, lv.x), mix(tl, tr, lv.x), lv.y);
}

float fbm(vec2 p) {
	float val = 0.0;
	float amp = 0.55;
	mat2 rot = mat2(vec2(0.8, -0.6), vec2(0.6, 0.8));
	for (int i = 0; i < 3; i++) {
		val += amp * noise2D(p);
		p = rot * p * 2.12;
		amp *= 0.5;
	}
	return val;
}

void fragment() {
	vec2 uv = UV;

	float signed_y = (uv.y - 0.5) * 2.0;
	float dist = abs(signed_y);

	float quad_safe = smoothstep(0.99, 0.70, dist) * smoothstep(1.0, 0.85, uv.x);
	if (quad_safe <= 0.001 || power <= 0.01) {
		discard;
	}

	// 1. PROFIL KORONA BINTANG RAKSASA (Solar Corona Bulge + Flared Plume)
	float nozzle_cone = mix(0.30, 1.0, smoothstep(0.0, 0.14, uv.x));
	float corona_belly = 1.0 + 0.28 * sin(clamp(uv.x * 1.25, 0.0, 1.0) * 3.14159);
	float tail_taper = pow(clamp(1.0 - uv.x * 0.32, 0.0, 1.0), 0.46);
	float plume_envelope = nozzle_cone * corona_belly * tail_taper * (0.68 + 0.14 * clamp(swarm_scale * 0.5, 0.0, 1.0));

	float norm_dist = dist / max(0.05, plume_envelope);

	// 2. DOMAIN-WARPED SOLAR CORONA FBM & CME PROMINENCE ARCHES
	float flow_speed = 20.0 + rpm * 0.42;
	vec2 warp_uv1 = vec2(uv.x * 5.0 - spin_time * (flow_speed * 0.65), signed_y * 3.8);
	float w1 = fbm(warp_uv1);

	vec2 warp_uv2 = vec2(
		uv.x * 10.2 - spin_time * flow_speed + w1 * 1.65,
		signed_y * 7.4 + (w1 - 0.5) * 2.05
	);
	float w2 = fbm(warp_uv2);

	vec2 flame_uv = vec2(
		uv.x * 17.5 - spin_time * (flow_speed * 1.48) + w2 * 2.15,
		signed_y * 12.5 + (w2 - 0.5) * 2.65
	);
	float flame_fbm = fbm(flame_uv);

	float flame_ridge = 1.0 - abs(flame_fbm * 2.0 - 1.0);
	float corona_filaments = pow(clamp(flame_ridge, 0.0, 1.0), 1.45);

	// Busur Erupsi Korona Raksasa (Coronal Mass Ejection Loops & Magnetic Flares)
	float cme_phase1 = uv.x * 16.0 - spin_time * 22.0 + sin(dist * 7.5 - spin_time * 11.0) * 2.0;
	float cme_phase2 = uv.x * 26.0 - spin_time * 34.0 - cos(dist * 9.0 + spin_time * 15.0) * 1.8;
	float cme_loops = (pow(0.5 + 0.5 * sin(cme_phase1 + w1 * 3.6), 2.0) + 0.65 * pow(0.5 + 0.5 * cos(cme_phase2 + w2 * 3.0), 2.4))
		* smoothstep(1.42, 0.38, norm_dist) * smoothstep(0.14, 0.62, norm_dist) * (1.0 - uv.x * 0.48);

	// Mahkota Bintang Supernova di Pangkal Nozzle (Nozzle Solar Crown)
	vec2 nozzle_rel = vec2(uv.x * 1.9, signed_y);
	float nozzle_r = length(nozzle_rel);
	float nozzle_ang = atan(nozzle_rel.y, nozzle_rel.x);
	float crown_spokes = 0.5 + 0.5 * sin(nozzle_ang * 18.0 + sin(spin_time * 22.0 + nozzle_r * 14.0) * 2.5);
	float crown_spokes2 = 0.5 + 0.5 * cos(nozzle_ang * 28.0 - spin_time * 30.0);
	float solar_crown = smoothstep(0.95, 0.04, nozzle_r) * pow(crown_spokes * crown_spokes2, 0.78) * 1.65;

	// Erosi siluet korona luar
	float erosion_amt = mix(0.32, 1.05, smoothstep(0.0, 0.80, uv.x));
	float eroded_dist = norm_dist
		- (flame_fbm - 0.43) * erosion_amt
		- corona_filaments * 0.28 * (1.0 - uv.x * 0.35)
		- cme_loops * 0.26;

	float outer_corona_halo = smoothstep(1.38, 0.22, eroded_dist) * smoothstep(1.0, 0.03, uv.x * 0.90);
	float outer_flame_mask = smoothstep(1.08, 0.34, eroded_dist) * smoothstep(1.0, 0.05, uv.x * 0.88);

	// 3. 4 UNTAI PILINAN BADAI SIKLON HELIKS (Counter-Rotating Solar-Abyssal Tornado)
	float twist_freq = 17.0 + 3.5 * swarm_scale;
	float twist_amp = (0.32 + 0.10 * sin(uv.x * 8.0)) * (1.0 - uv.x * 0.35) * plume_envelope;

	float phase_fwd = uv.x * twist_freq - spin_time * rpm;
	float phase_rev = uv.x * (twist_freq * 0.82) + spin_time * (rpm * 0.78);

	float t1 = sin(phase_fwd) * twist_amp;
	float t2 = sin(phase_fwd + 3.14159265) * twist_amp;
	float t3 = sin(phase_rev + 1.57079632) * (twist_amp * 1.18);
	float t4 = sin(phase_rev + 4.71238898) * (twist_amp * 1.18);

	float strand_sharpness = 6.8;
	float rib_white1 = exp(-pow((signed_y - t1) * strand_sharpness, 2.0));
	float rib_white2 = exp(-pow((signed_y - t2) * strand_sharpness, 2.0));
	float rib_void1 = exp(-pow((signed_y - t3) * (strand_sharpness * 0.82), 2.0));
	float rib_void2 = exp(-pow((signed_y - t4) * (strand_sharpness * 0.82), 2.0));

	float tail_fade = pow(clamp(1.0 - uv.x, 0.0, 1.0), 0.80);
	float helix_white = (rib_white1 + rib_white2) * outer_flame_mask * tail_fade;
	float helix_void = (rib_void1 + rib_void2) * outer_flame_mask * tail_fade;

	// 4. RANGKAIAN SINGULARITY SHOCK DIAMONDS (Inti Hitam Void + Cincin Korona Putih-Ungu)
	float d_coord = uv.x * diamond_count - spin_time * 12.5;
	float d_fract = fract(d_coord) - 0.5;
	float diamond_x = abs(d_fract) * 2.0;
	float diamond_metric = diamond_x * 1.02 + norm_dist * 2.05;

	float shock_outer_ring = smoothstep(0.96, 0.24, diamond_metric) * smoothstep(0.08, 0.36, diamond_metric) * (1.0 - uv.x * 0.62);
	float shock_void_eye = smoothstep(0.34, 0.05, diamond_metric) * (1.0 - uv.x * 0.68);

	// 5. SAMBARAN PETIR KORONA INTERNAL (Jagged White-Violet Lightning Filaments)
	float l_wave1 = sin(uv.x * 44.0 - spin_time * 42.0 + sin(signed_y * 15.0 + spin_time * 24.0) * 2.5);
	float l_wave2 = cos(uv.x * 68.0 + spin_time * 48.0 - cos(signed_y * 20.0 - spin_time * 31.0) * 2.7);
	float lightning_arc = (smoothstep(0.91, 0.995, abs(l_wave1)) + smoothstep(0.93, 0.998, abs(l_wave2)))
		* outer_corona_halo * smoothstep(1.05, 0.12, norm_dist);

	// 6. LAPISAN HITAM VOID (Mantel Abyssal + Kantong Turbulensi + Heliks Gelap + Mata Singularity)
	float void_sheath = smoothstep(0.86, 0.40, eroded_dist) * smoothstep(0.14, 0.42, eroded_dist);
	float void_cells = smoothstep(0.56, 0.18, flame_fbm) * smoothstep(0.06, 0.72, uv.x);
	float void_total = clamp(
		void_sheath * (0.78 + 0.65 * void_cells)
		+ helix_void * 0.75
		+ shock_void_eye * 0.96
		+ void_cells * outer_flame_mask * 0.52,
		0.0,
		1.0
	);

	// 7. INTI PUTIH MENYILAUKAN RAKSASA + FLARE KORONA
	float core_eroded = norm_dist - (w2 - 0.5) * 0.40 - (flame_fbm - 0.5) * 0.28 - cme_loops * 0.16;
	float white_core_mask = smoothstep(0.52, 0.10, core_eroded) * smoothstep(0.98, 0.06, uv.x);
	white_core_mask = clamp(white_core_mask * (1.0 - shock_void_eye * 0.86), 0.0, 1.0);

	float violet_core_halo = smoothstep(0.76, 0.20, core_eroded) * smoothstep(0.99, 0.08, uv.x);

	// 8. KOMPOSISI WARNA HDR: KORONA UNGU TUA -> HITAM VOID -> VIOLET PIJAR -> PUTIH MENYILAUKAN
	vec3 rgb = mix(dark_abyss_purple.rgb, deep_purple_corona.rgb, outer_flame_mask);
	rgb += hyper_violet_corona.rgb * (
		corona_filaments * 1.35 * outer_flame_mask
		+ cme_loops * 1.85
		+ solar_crown * 1.25
	);

	// Irisan Hitam Void Pekat
	rgb = mix(rgb, void_black_color.rgb, void_total * 0.93);

	// Halo Hyper-Violet & Busur Korona
	float halo_weight = clamp(
		violet_core_halo * (1.0 - void_total * 0.36)
		+ shock_outer_ring * 0.70
		+ cme_loops * 0.60
		+ solar_crown * 0.50,
		0.0,
		1.0
	);
	rgb = mix(rgb, hyper_violet_corona.rgb * (1.35 + 0.60 * corona_filaments), halo_weight * 0.84);

	// Inti Putih Menyilaukan + Heliks Putih + Petir Internal + Puncak Korona
	float blinding_white = clamp(
		white_core_mask
		+ helix_white * 0.78
		+ shock_outer_ring * 0.88
		+ lightning_arc * 0.98
		+ solar_crown * smoothstep(0.62, 0.08, dist) * 0.82
		+ cme_loops * smoothstep(0.65, 0.20, eroded_dist) * 0.55,
		0.0,
		1.0
	) * (1.0 - shock_void_eye * 0.85);
	rgb = mix(rgb, core_white_color.rgb, blinding_white);

	rgb += hyper_violet_corona.rgb * (lightning_arc * 1.6);

	// 9. ALPHA AKHIR
	float final_alpha = clamp(
		(outer_corona_halo * 0.82 + outer_flame_mask * 0.96 + cme_loops * 0.90 + solar_crown * 0.88 + void_total * 0.96 + blinding_white)
		* quad_safe * clamp(power * 1.35, 0.0, 1.0),
		0.0,
		1.0
	);

	COLOR = vec4(rgb, final_alpha);
}
