shader_type canvas_item;
render_mode blend_mix, unshaded;

// =========================================================================
// SACRIFICIAL PLASMA BOOST - ABYSSAL SOLAR CORONA EXHAUST SHADER [SPACE]
// =========================================================================
// Menggabungkan semburan XF-09 Ares dengan Erupsi Korona Matahari (Solar Corona):
// 1. NOZZLE CORONA CROWN: Pancaran mahkota korona radial & busur magnetik di mulut thruster
// 2. SOLAR PROMINENCE LOOPS: Busur-busur plasma melengkung & lidah api korona yang menjilat keluar
// 3. PALET 3 WARNA KONTRAS TINGGI (HDR BLOOM):
//    - PUTIH MENYILAUKAN (Overexposed Blinding White Core & Corona Flare Spikes)
//    - HITAM PEKAT (Obsidian Void Singularity Spine & Dark-Matter Pockets)
//    - UNGU TUA (Deep Abyssal Purple & Royal Violet HDR Corona Aura)

uniform float power : hint_range(0.0, 1.0) = 1.0;
uniform float time = 0.0;
uniform float flash_boost : hint_range(0.0, 1.0) = 0.0;
uniform float diamond_count = 7.0;

uniform vec4 core_white_color : source_color = vec4(7.6, 7.4, 8.6, 1.0);
uniform vec4 corona_violet_hdr : source_color = vec4(4.4, 0.72, 7.2, 1.0);
uniform vec4 deep_purple_corona : source_color = vec4(2.15, 0.16, 4.65, 1.0);
uniform vec4 dark_abyss_purple : source_color = vec4(0.68, 0.04, 1.65, 1.0);
uniform vec4 void_black_color : source_color = vec4(0.010, 0.002, 0.020, 1.0);
uniform vec4 sacrifice_flash_color : source_color = vec4(7.8, 3.8, 8.8, 1.0);

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
		p = rot * p * 2.08;
		amp *= 0.5;
	}
	return val;
}

void fragment() {
	vec2 uv = UV;

	// Jarak melintang (-1.0 .. 1.0, dist = 0.0 .. 1.0)
	float signed_y = (uv.y - 0.5) * 2.0;
	float dist = abs(signed_y);

	// Pengaman batas quad agar korona luar meluruh mulus tanpa garis kotak
	float quad_safe = smoothstep(0.99, 0.72, dist) * smoothstep(1.0, 0.86, uv.x);
	if (quad_safe <= 0.001 || power <= 0.01) {
		discard;
	}

	// 1. PROFIL KORONA MELEBAR DARI MULUT NOZZLE (XF-09 Ares + Solar Corona Bulge)
	float nozzle_flare = mix(0.26, 1.0, smoothstep(0.0, 0.16, uv.x));
	float corona_belly = 1.0 + 0.24 * sin(clamp(uv.x * 1.35, 0.0, 1.0) * 3.14159);
	float tail_taper = pow(clamp(1.0 - uv.x * 0.35, 0.0, 1.0), 0.48);
	float plume_envelope = nozzle_flare * corona_belly * tail_taper * (0.66 + 0.16 * power);

	float norm_dist = dist / max(0.05, plume_envelope);

	// 2. DOMAIN-WARPED SOLAR CORONA FBM & MAGNETIC PROMINENCE LOOPS
	float scroll_spd = 16.0 + 14.0 * power;
	vec2 warp_uv1 = vec2(uv.x * 4.5 - time * scroll_spd, signed_y * 3.5);
	float w1 = fbm(warp_uv1);

	vec2 warp_uv2 = vec2(
		uv.x * 9.2 - time * (scroll_spd * 1.45) + w1 * 1.55,
		signed_y * 6.8 + (w1 - 0.5) * 1.85
	);
	float w2 = fbm(warp_uv2);

	vec2 flame_uv = vec2(
		uv.x * 16.0 - time * (scroll_spd * 1.95) + w2 * 1.95,
		signed_y * 11.5 + (w2 - 0.5) * 2.45
	);
	float flame_fbm = fbm(flame_uv);

	// Ridge tajam untuk filamen busur korona (Solar Prominence Filaments)
	float ridge = 1.0 - abs(flame_fbm * 2.0 - 1.0);
	float corona_filaments = pow(clamp(ridge, 0.0, 1.0), 1.65);

	// Busur Magnetik Korona (Prominence Loops yang melengkung keluar di sisi kiri-kanan semburan)
	float loop_phase = uv.x * 18.0 - time * (scroll_spd * 1.25) + sin(dist * 6.0 - time * 8.0) * 1.6;
	float prominence_arches = pow(0.5 + 0.5 * sin(loop_phase + w1 * 3.2), 2.2)
		* smoothstep(1.35, 0.45, norm_dist) * smoothstep(0.18, 0.65, norm_dist) * (1.0 - uv.x * 0.55);

	// 3. MAHKOTA KORONA DI PANGKAL NOZZLE (Nozzle Starburst Corona Rays)
	vec2 nozzle_rel = vec2(uv.x * 2.2, signed_y);
	float nozzle_r = length(nozzle_rel);
	float nozzle_ang = atan(nozzle_rel.y, nozzle_rel.x);
	float corona_rays = 0.5 + 0.5 * sin(nozzle_ang * 14.0 + sin(time * 18.0 + nozzle_r * 12.0) * 2.2);
	float corona_rays2 = 0.5 + 0.5 * cos(nozzle_ang * 22.0 - time * 24.0);
	float nozzle_crown = smoothstep(0.85, 0.05, nozzle_r) * pow(corona_rays * corona_rays2, 0.85) * 1.35;

	// 4. EROSI LIDAH API KORONA LUAR (Roaring Coronal Tongues)
	float erosion_amt = mix(0.28, 0.98, smoothstep(0.0, 0.82, uv.x));
	float eroded_dist = norm_dist
		- (flame_fbm - 0.44) * erosion_amt
		- corona_filaments * 0.26 * (1.0 - uv.x * 0.4)
		- prominence_arches * 0.22;

	// Aura korona luar yang luas + lidah api inti
	float outer_corona_halo = smoothstep(1.32, 0.25, eroded_dist) * smoothstep(1.0, 0.04, uv.x * 0.90);
	float corona_tongue_mask = smoothstep(1.02, 0.36, eroded_dist) * smoothstep(1.0, 0.06, uv.x * 0.88);

	// 5. LAPISAN HITAM VOID SINGULARITY (Inti Gelap & Celah Korona Hitam Pekat)
	// Sabuk hitam pekat mengiris di antara inti putih dan korona ungu, ditambah mata void di tengah nozzle
	float void_sheath = smoothstep(0.82, 0.42, eroded_dist) * smoothstep(0.18, 0.42, eroded_dist);
	float void_pockets = smoothstep(0.55, 0.20, flame_fbm) * smoothstep(0.08, 0.72, uv.x);
	float inner_void_spine = exp(-pow(norm_dist * 9.5, 2.0)) * smoothstep(0.42, 0.04, uv.x) * (0.65 + 0.35 * sin(uv.x * 35.0 - time * 30.0));
	float void_mask = clamp(
		void_sheath * (0.78 + 0.65 * void_pockets)
		+ void_pockets * corona_tongue_mask * 0.55
		+ inner_void_spine * 0.92,
		0.0,
		1.0
	);

	// 6. INTI PUTIH MENYILAUKAN & MACH SHOCK DIAMONDS
	float core_eroded = norm_dist - (w2 - 0.5) * 0.38 - (flame_fbm - 0.5) * 0.28 - prominence_arches * 0.14;
	float white_core_mask = smoothstep(0.48, 0.10, core_eroded) * smoothstep(0.96, 0.08, uv.x);
	// Spine hitam void membelah tipis bagian tengah pangkal inti putih agar kontras Hitam-Putih-Ungu sangat garang
	white_core_mask = clamp(white_core_mask * (1.0 - inner_void_spine * 0.85), 0.0, 1.0);

	float d_coord = uv.x * diamond_count - time * (22.0 * power);
	float diamond_x = abs(fract(d_coord) - 0.5) * 2.0;
	float diamond_shape = diamond_x * 1.10 + norm_dist * 2.05;
	float shock_diamond = smoothstep(0.92, 0.18, diamond_shape) * smoothstep(0.04, 0.26, diamond_shape) * (1.0 - uv.x * 0.72);
	float diamond_void_eye = smoothstep(0.24, 0.04, diamond_shape) * (1.0 - uv.x * 0.75);
	void_mask = clamp(void_mask + diamond_void_eye * 0.90, 0.0, 1.0);

	// Gelombang kejut pengorbanan Dart
	float flash_dist = fract(time * 3.8);
	float flash_wave = exp(-pow((uv.x - flash_dist * 0.82) * 4.8, 2.0)) * flash_boost * outer_corona_halo;

	// 7. KOMPOSISI WARNA CORONA PLASMA (UNGU TUA HDR -> HITAM VOID -> VIOLET -> PUTIH MENYILAUKAN)
	// (a) Aura Korona Ungu Tua & Busur Prominence HDR
	vec3 rgb = mix(dark_abyss_purple.rgb, deep_purple_corona.rgb, corona_tongue_mask);
	rgb += corona_violet_hdr.rgb * (
		corona_filaments * 1.15 * corona_tongue_mask
		+ prominence_arches * 1.65
		+ nozzle_crown * 0.95
	);

	// (b) Irisan Hitam Pekat (Obsidian Void) yang kontras di dalam kobaran api
	rgb = mix(rgb, void_black_color.rgb, void_mask * 0.92);

	// (c) Cincin Transisi Hyper-Violet di tepi inti putih & tepi kawah hitam
	float violet_rim = clamp(
		smoothstep(0.68, 0.20, core_eroded) * (1.0 - void_mask * 0.35)
		+ prominence_arches * 0.55
		+ nozzle_crown * 0.45,
		0.0,
		1.0
	);
	rgb = mix(rgb, corona_violet_hdr.rgb * (1.25 + 0.55 * corona_filaments), violet_rim * 0.80);

	// (d) Inti Putih Menyilaukan (Blinding White Core + Corona Spikes + Shock Diamonds)
	float total_white = clamp(
		white_core_mask
		+ shock_diamond * 0.85
		+ nozzle_crown * smoothstep(0.55, 0.08, dist) * 0.75
		+ corona_filaments * smoothstep(0.52, 0.15, eroded_dist) * 0.55,
		0.0,
		1.0
	) * (1.0 - diamond_void_eye * 0.85);
	rgb = mix(rgb, core_white_color.rgb, total_white);

	// (e) Ledakan cahaya saat Dart dikorbankan
	rgb += sacrifice_flash_color.rgb * (flash_wave * 2.8);

	// 8. ALPHA MASK KORONA ORGANIK
	float final_alpha = clamp(
		(outer_corona_halo * 0.78 + corona_tongue_mask * 0.95 + prominence_arches * 0.85 + nozzle_crown * 0.80 + void_mask * 0.96 + total_white)
		* quad_safe * clamp(power * 1.3, 0.0, 1.0),
		0.0,
		1.0
	);

	COLOR = vec4(rgb, final_alpha);
}
