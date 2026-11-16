shader_type canvas_item;
render_mode blend_mix, unshaded;

// =========================================================================
// SUPER GIGA DRILL BREAK - ULTIMATE ABYSSAL OVERDRIVE EXHAUST SHADER
// =========================================================================
// Versi Ultimate Move ("GG") dari shader exhaust XF-09 Ares dengan palet
// PUTIH MENYILAUKAN, HITAM VOID PEKAT, dan UNGU TUA ABYSSAL:
// 1. Semburan Raksasa Domain-Warped FBM Flame Licks dengan erosi lidah api liar
// 2. 4 Untai Pilinan Badai Siklon Heliks (Counter-Rotating Abyssal Corkscrew)
// 3. Rangkaian Singularity Shock Diamonds (Inti Hitam Void + Cincin Putih-Ungu HDR)
// 4. Sambaran Petir Void Internal (Crackling White-Violet Arcs di dalam semburan)

uniform float power : hint_range(0.0, 1.0) = 1.0;
uniform float spin_time = 0.0;
uniform float rpm = 28.0;
uniform float swarm_scale = 1.0;
uniform float diamond_count = 8.0;

uniform vec4 core_white_color : source_color = vec4(7.2, 6.8, 8.2, 1.0);
uniform vec4 hyper_violet_color : source_color = vec4(4.2, 0.85, 6.8, 1.0);
uniform vec4 deep_purple_color : source_color = vec4(1.55, 0.14, 3.45, 1.0);
uniform vec4 dark_abyss_purple : source_color = vec4(0.45, 0.03, 1.05, 1.0);
uniform vec4 void_black_color : source_color = vec4(0.010, 0.003, 0.022, 1.0);

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

	// Jarak melintang dari sumbu tengah (-1.0 .. 1.0, dist = 0.0 .. 1.0)
	float signed_y = (uv.y - 0.5) * 2.0;
	float dist = abs(signed_y);

	// Pengaman batas quad agar pendaran dan lidah api tidak pernah terpotong rata
	float quad_safe = smoothstep(0.99, 0.76, dist) * smoothstep(1.0, 0.88, uv.x);
	if (quad_safe <= 0.001 || power <= 0.01) {
		discard;
	}

	// 1. PROFIL KERUCUT RAKSASA XF-09 ARES OVERDRIVE
	// Mengembang eksplosif dari pangkal mata bor lalu memecah jadi kobaran api plasma raksasa
	float nozzle_cone = mix(0.24, 1.0, smoothstep(0.0, 0.18, uv.x));
	float plume_belly = 1.0 + 0.18 * sin(uv.x * 3.14159);
	float tail_taper = pow(clamp(1.0 - uv.x * 0.36, 0.0, 1.0), 0.52);
	float plume_envelope = nozzle_cone * plume_belly * tail_taper * (0.72 + 0.12 * clamp(swarm_scale * 0.5, 0.0, 1.0));

	// 2. DOMAIN-WARPED FBM TURBULENCE (Lidah Api Plasma Berlapis)
	float flow_speed = 18.0 + rpm * 0.38;
	vec2 warp_uv1 = vec2(uv.x * 5.2 - spin_time * (flow_speed * 0.65), signed_y * 3.8);
	float w1 = fbm(warp_uv1);

	vec2 warp_uv2 = vec2(
		uv.x * 10.5 - spin_time * flow_speed + w1 * 1.55,
		signed_y * 7.2 + (w1 - 0.5) * 1.85
	);
	float w2 = fbm(warp_uv2);

	vec2 flame_uv = vec2(
		uv.x * 18.0 - spin_time * (flow_speed * 1.45) + w2 * 1.9,
		signed_y * 12.0 + (w2 - 0.5) * 2.4
	);
	float flame_fbm = fbm(flame_uv);

	// Puncak punggung lidah api tajam (Sharp Flame Ridges)
	float flame_ridge = 1.0 - abs(flame_fbm * 2.0 - 1.0);
	flame_ridge = pow(clamp(flame_ridge, 0.0, 1.0), 1.35);

	float norm_dist = dist / max(0.05, plume_envelope);

	// Erosi siluet lidah api yang ganas di sisi luar dan ekor
	float erosion_amt = mix(0.26, 0.96, smoothstep(0.0, 0.82, uv.x));
	float eroded_dist = norm_dist - (flame_fbm - 0.45) * erosion_amt - flame_ridge * 0.22 * (1.0 - uv.x * 0.4);

	float outer_flame_mask = smoothstep(1.10, 0.38, eroded_dist) * smoothstep(1.0, 0.05, uv.x * 0.90);

	// 3. 4 UNTAI PILINAN BADAI SIKLON HELIKS (Counter-Rotating Abyssal Tornado)
	float twist_freq = 18.0 + 3.5 * swarm_scale;
	float twist_amp = (0.30 + 0.08 * sin(uv.x * 8.0)) * (1.0 - uv.x * 0.38) * plume_envelope;

	float phase_fwd = uv.x * twist_freq - spin_time * rpm;
	float phase_rev = uv.x * (twist_freq * 0.85) + spin_time * (rpm * 0.75);

	float t1 = sin(phase_fwd) * twist_amp;
	float t2 = sin(phase_fwd + 3.14159265) * twist_amp;
	float t3 = sin(phase_rev + 1.57079632) * (twist_amp * 1.15);
	float t4 = sin(phase_rev + 4.71238898) * (twist_amp * 1.15);

	float strand_sharpness = 7.2;
	float rib_white1 = exp(-pow((signed_y - t1) * strand_sharpness, 2.0));
	float rib_white2 = exp(-pow((signed_y - t2) * strand_sharpness, 2.0));
	// Untai ke-3 dan ke-4 bertindak sebagai pita bayangan Hitam Void + tepi Ungu Tua yang melilit inti!
	float rib_void1 = exp(-pow((signed_y - t3) * (strand_sharpness * 0.85), 2.0));
	float rib_void2 = exp(-pow((signed_y - t4) * (strand_sharpness * 0.85), 2.0));

	float tail_fade = pow(clamp(1.0 - uv.x, 0.0, 1.0), 0.85);
	float helix_white = (rib_white1 + rib_white2) * outer_flame_mask * tail_fade;
	float helix_void = (rib_void1 + rib_void2) * outer_flame_mask * tail_fade;

	// 4. RANGKAIAN SINGULARITY SHOCK DIAMONDS (Inti Hitam Void + Cincin Putih-Ungu)
	float d_coord = uv.x * diamond_count - spin_time * 11.5;
	float d_fract = fract(d_coord) - 0.5;
	float diamond_x = abs(d_fract) * 2.0;
	float diamond_metric = diamond_x * 1.05 + norm_dist * 2.15;

	float shock_outer_ring = smoothstep(0.95, 0.25, diamond_metric) * smoothstep(0.08, 0.36, diamond_metric) * (1.0 - uv.x * 0.65);
	float shock_void_eye = smoothstep(0.32, 0.06, diamond_metric) * (1.0 - uv.x * 0.70);

	// 5. SAMBARAN PETIR VOID INTERNAL (Jagged White-Violet Lightning Filaments)
	float l_wave1 = sin(uv.x * 42.0 - spin_time * 38.0 + sin(signed_y * 14.0 + spin_time * 22.0) * 2.4);
	float l_wave2 = cos(uv.x * 64.0 + spin_time * 46.0 - cos(signed_y * 19.0 - spin_time * 29.0) * 2.6);
	float lightning_arc = (smoothstep(0.92, 0.995, abs(l_wave1)) + smoothstep(0.94, 0.998, abs(l_wave2)))
		* outer_flame_mask * smoothstep(0.95, 0.15, norm_dist);

	// 6. LAPISAN HITAM VOID (Mantel Abyssal + Kantong Turbulensi + Heliks Gelap + Mata Singularity)
	float void_sheath = smoothstep(0.86, 0.42, eroded_dist) * smoothstep(0.14, 0.42, eroded_dist);
	float void_cells = smoothstep(0.56, 0.20, flame_fbm) * smoothstep(0.08, 0.70, uv.x);
	float void_total = clamp(
		void_sheath * (0.75 + 0.65 * void_cells)
		+ helix_void * 0.70
		+ shock_void_eye * 0.95
		+ void_cells * outer_flame_mask * 0.50,
		0.0,
		1.0
	);

	// 7. INTI PUTIH MENYILAUKAN RAKSASA (Blinding XF-09 Ares Core)
	float core_eroded = norm_dist - (w2 - 0.5) * 0.38 - (flame_fbm - 0.5) * 0.26;
	float white_core_mask = smoothstep(0.50, 0.12, core_eroded) * smoothstep(0.98, 0.08, uv.x);
	// Mata void singularity melubangi sedikit bagian tengah diamond agar kontras Hitam-Putih-Ungu makin garang
	white_core_mask = clamp(white_core_mask * (1.0 - shock_void_eye * 0.82), 0.0, 1.0);

	float violet_core_halo = smoothstep(0.72, 0.22, core_eroded) * smoothstep(0.99, 0.10, uv.x);

	// 8. KOMPOSISI WARNA HDR: UNGU TUA -> HITAM VOID -> VIOLET PIJAR -> PUTIH MENYILAUKAN
	// (a) Kobaran lidah api luar Ungu Tua ke Deep Purple
	vec3 rgb = mix(dark_abyss_purple.rgb, deep_purple_color.rgb, smoothstep(0.98, 0.32, eroded_dist));
	rgb += hyper_violet_color.rgb * (flame_ridge * 0.95 * smoothstep(0.95, 0.25, eroded_dist));

	// (b) Lapisan Hitam Pekat (Obsidian Void) yang mengiris plasma
	rgb = mix(rgb, void_black_color.rgb, void_total * 0.92);

	// (c) Halo Hyper-Violet & Cincin Luar Shock Diamond
	float halo_weight = clamp(violet_core_halo * (1.0 - void_total * 0.40) + shock_outer_ring * 0.65, 0.0, 1.0);
	rgb = mix(rgb, hyper_violet_color.rgb * (1.2 + 0.5 * flame_ridge), halo_weight * 0.82);

	// (d) Inti Putih Menyilaukan + Heliks Putih + Petir Internal + Puncak Shock Diamond
	float blinding_white = clamp(
		white_core_mask
		+ helix_white * 0.72
		+ shock_outer_ring * 0.85
		+ lightning_arc * 0.95,
		0.0,
		1.0
	);
	rgb = mix(rgb, core_white_color.rgb, blinding_white);

	// Tambahan kilau pinggir ungu pada petir internal
	rgb += hyper_violet_color.rgb * (lightning_arc * 1.4);

	// 9. ALPHA AKHIR
	float final_alpha = clamp(
		(outer_flame_mask * 0.95 + void_total * 0.96 + blinding_white) * quad_safe * clamp(power * 1.3, 0.0, 1.0),
		0.0,
		1.0
	);

	COLOR = vec4(rgb, final_alpha);
}
