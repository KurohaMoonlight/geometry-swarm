shader_type canvas_item;
render_mode blend_mix, unshaded;

// =========================================================================
// SACRIFICIAL PLASMA BOOST - XF-09 ARES STYLE ABYSSAL FLAME SHADER [SPACE]
// =========================================================================
// Mensimulasikan semburan roket plasma bertenaga tinggi bergaya XF-09 Ares
// dengan 3 lapisan warna ikonik:
// 1. PUTIH MENYILAUKAN (Overexposed Blinding White Core) di pusat semburan
// 2. HITAM PEKAT (Obsidian Void Mantle & Dark-Matter Shear Veins) sebagai kontras
// 3. UNGU TUA (Deep Abyssal Purple & Royal Violet Flame Licks) di lidah api tepi

uniform float power : hint_range(0.0, 1.0) = 1.0;
uniform float time = 0.0;
uniform float flash_boost : hint_range(0.0, 1.0) = 0.0;
uniform float diamond_count = 7.0;

uniform vec4 core_white_color : source_color = vec4(6.4, 6.2, 7.2, 1.0);
uniform vec4 inner_violet_color : source_color = vec4(3.4, 0.75, 5.8, 1.0);
uniform vec4 deep_purple_color : source_color = vec4(1.35, 0.12, 2.95, 1.0);
uniform vec4 dark_purple_edge : source_color = vec4(0.42, 0.03, 0.98, 1.0);
uniform vec4 void_black_color : source_color = vec4(0.012, 0.004, 0.025, 1.0);
uniform vec4 sacrifice_flash_color : source_color = vec4(6.8, 3.2, 7.5, 1.0);

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

// 3-Octave Fractal Brownian Motion untuk lidah api plasma organik
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

	// Jarak melintang dari sumbu tengah (0.0 di pusat, 1.0 di batas quad)
	float signed_y = (uv.y - 0.5) * 2.0;
	float dist = abs(signed_y);

	// Mask pengaman tepi quad agar tidak pernah terpotong kotak
	float quad_safe = smoothstep(0.98, 0.78, dist) * smoothstep(1.0, 0.90, uv.x);
	if (quad_safe <= 0.001 || power <= 0.01) {
		discard;
	}

	// Profil kerucut semburan ala XF-09 Ares:
	// Menyempit tajam di mulut nozzle (uv.x = 0), mengembang cepat menjadi semburan lebar,
	// lalu tercabik menjadi lidah api di bagian ekor.
	float nozzle_cone = mix(0.18, 1.0, smoothstep(0.0, 0.24, uv.x));
	float tail_taper = pow(clamp(1.0 - uv.x * 0.42, 0.0, 1.0), 0.55);
	float plume_envelope = nozzle_cone * tail_taper * (0.68 + 0.16 * power);

	// Koordinat aliran cepat sepanjang semburan (Domain-Warped Scrolling FBM)
	float scroll_spd = 14.0 + 12.0 * power;
	vec2 flow_uv1 = vec2(uv.x * 4.8 - time * scroll_spd, signed_y * 3.2);
	float warp1 = fbm(flow_uv1);

	vec2 flow_uv2 = vec2(
		uv.x * 9.5 - time * (scroll_spd * 1.45) + warp1 * 1.35,
		signed_y * 6.4 + (warp1 - 0.5) * 1.5
	);
	float warp2 = fbm(flow_uv2);

	vec2 flame_uv = vec2(
		uv.x * 16.5 - time * (scroll_spd * 1.95) + warp2 * 1.65,
		signed_y * 10.5 + (warp2 - 0.5) * 2.1
	);
	float flame_fbm = fbm(flame_uv);

	// Ridge turbulence untuk lidah api tajam di pinggiran dan ekor
	float ridge = 1.0 - abs(flame_fbm * 2.0 - 1.0);
	ridge = pow(clamp(ridge, 0.0, 1.0), 1.45);

	// Jarak ternormalisasi terhadap profil kerucut
	float norm_dist = dist / max(0.05, plume_envelope);

	// Erosi FBM yang semakin liar ke arah tepi luar dan ujung ekor (efek lidah api XF-09 Ares)
	float erosion_strength = mix(0.22, 0.88, smoothstep(0.0, 0.85, uv.x));
	float eroded_dist = norm_dist - (flame_fbm - 0.46) * erosion_strength - ridge * 0.18 * (1.0 - uv.x * 0.5);

	// 1. MASK SILUET LIDAH API LUAR (Ungu Tua & Hitam Void)
	float outer_flame_mask = smoothstep(1.08, 0.42, eroded_dist) * smoothstep(1.0, 0.06, uv.x * 0.88);

	// 2. LAPISAN MANTEL & URAT HITAM VOID (Obsidian Black Shear Layer)
	// Muncul di perbatasan antara inti putih dan lidah api ungu, serta di kantong turbulensi FBM
	float void_band = smoothstep(0.84, 0.44, eroded_dist) * smoothstep(0.14, 0.40, eroded_dist);
	float void_pockets = smoothstep(0.54, 0.22, flame_fbm) * smoothstep(0.12, 0.65, uv.x);
	float void_mask = clamp(void_band * (0.72 + 0.65 * void_pockets) + void_pockets * outer_flame_mask * 0.55, 0.0, 1.0);

	// 3. INTI PUTIH MENYILAUKAN (Overexposed Blinding White Core ala XF-09 Ares)
	// Inti putih juga bergerigi mengikuti FBM sehingga menyatu seperti api plasma murni
	float core_eroded = norm_dist - (warp2 - 0.5) * 0.34 - (flame_fbm - 0.5) * 0.24;
	float white_core_mask = smoothstep(0.46, 0.12, core_eroded) * smoothstep(0.96, 0.10, uv.x);
	float inner_violet_sheath = smoothstep(0.66, 0.22, core_eroded) * smoothstep(0.98, 0.14, uv.x);

	// 4. Rangkaian Mach Shock Diamonds & Gelombang Pengorbanan Dart
	float d_coord = uv.x * diamond_count - time * (20.0 * power);
	float diamond_x = abs(fract(d_coord) - 0.5) * 2.0;
	float diamond_shape = diamond_x * 1.15 + norm_dist * 2.1;
	float shock_diamond = smoothstep(0.88, 0.0, diamond_shape) * (1.0 - uv.x * 0.75);

	float flash_dist = fract(time * 3.8);
	float flash_wave = exp(-pow((uv.x - flash_dist * 0.82) * 4.8, 2.0)) * flash_boost * outer_flame_mask;

	// 5. KOMPOSISI WARNA: UNGU TUA -> HITAM VOID -> PUTIH MENYILAUKAN
	// (a) Dasar lidah api Ungu Tua ke Royal Violet
	vec3 purple_flame = mix(dark_purple_edge.rgb, deep_purple_color.rgb, smoothstep(0.95, 0.35, eroded_dist));
	purple_flame += inner_violet_color.rgb * (ridge * 0.85 * smoothstep(0.92, 0.28, eroded_dist));

	vec3 rgb = purple_flame;

	// (b) Urat & Sabuk Hitam Pekat (Obsidian Void) yang mengiris di antara lidah api ungu dan inti putih
	rgb = mix(rgb, void_black_color.rgb, void_mask * 0.90);

	// (c) Selubung Violet Pijar di tepi inti putih
	float sheath_weight = clamp(inner_violet_sheath * (1.0 - void_mask * 0.45), 0.0, 1.0);
	rgb = mix(rgb, inner_violet_color.rgb * (1.15 + 0.45 * ridge), sheath_weight * 0.78);

	// (d) Inti Putih Menyilaukan (HDR Overexposed White) + Shock Diamonds
	float total_white = clamp(white_core_mask + shock_diamond * 0.65, 0.0, 1.0);
	rgb = mix(rgb, core_white_color.rgb, total_white);

	// (e) Tambahan kilatan saat Dart dikorbankan
	rgb += sacrifice_flash_color.rgb * (flash_wave * 2.6);

	// 6. ALPHA MASK ORGANIK
	float final_alpha = clamp(
		(outer_flame_mask * 0.92 + void_mask * 0.95 + white_core_mask + shock_diamond * 0.8) * quad_safe * clamp(power * 1.25, 0.0, 1.0),
		0.0,
		1.0
	);

	COLOR = vec4(rgb, final_alpha);
}
