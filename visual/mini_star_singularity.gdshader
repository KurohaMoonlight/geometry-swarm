shader_type canvas_item;
render_mode blend_mix, unshaded;

// --- UNIFORMS: MINING & HARVESTING CONTROL ---
uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;

// Kontrol ukuran bintang mini (kompak & proporsional terhadap kapal pemain)
uniform float star_radius : hint_range(0.03, 0.35) = 0.16;

// Gravitational Lensing (pembelokan ruang latar belakang / grid arena)
uniform float lensing_strength : hint_range(0.0, 0.25) = 0.07;
uniform float lensing_radius : hint_range(0.15, 0.46) = 0.38;

// Instabilitas akumulasi panas / overcharge sebelum meledak (0.0 normal, 1.0 critical supernova)
uniform float instability : hint_range(0.0, 1.0) = 0.0;

// Efek tergerus lokal di titik kontak laser
uniform float hit_angle : hint_range(-3.1416, 3.1416) = 0.0;
uniform float hit_cavitation : hint_range(0.0, 1.0) = 0.0;

// Palet Warna Kosmik Aether & Plasma Fusi (HDR Multiplier > 1.0 memicu Bloom WorldEnvironment)
uniform vec4 core_color : source_color = vec4(3.8, 3.2, 1.15, 1.0);        // Aether White-Gold Pijar
uniform vec4 corona_color : source_color = vec4(2.6, 1.3, 0.30, 0.95);     // Api Fusi Emas-Amber
uniform vec4 rim_electric_color : source_color = vec4(0.4, 2.5, 3.9, 0.85); // Busur Medan Magnetik Sian
uniform vec4 critical_overload_color : source_color = vec4(5.2, 4.9, 3.8, 1.0); // Pijar Putih Menyilaukan saat Overload

// --- FUNGSI PROCEDURAL NOISE CEPAT (Tanpa texture eksternal) ---
float hash21(vec2 p) {
    p = fract(p * vec2(234.34, 435.345));
    p += dot(p, p + 34.23);
    return fract(p.x * p.y);
}

float noise2D(vec2 p) {
    vec2 id = floor(p);
    vec2 lv = fract(p);
    lv = lv * lv * (3.0 - 2.0 * lv); // Hermite curve

    float bl = hash21(id);
    float br = hash21(id + vec2(1.0, 0.0));
    float tl = hash21(id + vec2(0.0, 1.0));
    float tr = hash21(id + vec2(1.0, 1.0));

    float b = mix(bl, br, lv.x);
    float t = mix(tl, tr, lv.x);
    return mix(b, t, lv.y);
}

float fbm(vec2 p) {
    float val = 0.0;
    float amp = 0.55;
    mat2 rot = mat2(vec2(0.8, -0.6), vec2(0.6, 0.8));
    for (int i = 0; i < 3; i++) {
        val += amp * noise2D(p);
        p = rot * p * 2.05;
        amp *= 0.5;
    }
    return val;
}

void fragment() {
    vec2 rel = UV - vec2(0.5);
    float dist = length(rel);
    float angle = atan(rel.y, rel.x);

    // 0. BOUNDARY ALPHA MASK (MENGHILANGKAN GARIS KOTAK TEPIAN SECARA SEMPURNA!)
    // Di dist >= 0.46, seluruh efek gravitasi dan pendaran pudar ke 0 sehingga transparan penuh
    float boundary_mask = smoothstep(0.47, 0.35, dist);
    if (boundary_mask <= 0.001) {
        discard; // Hemat pixel fill-rate GPU di luar lingkaran
    }

    // 1. INSTABILITAS GETARAN KOSMIK (Makin panas, bintang makin bergetar & tremor)
    float shake = instability * 0.02 * sin(TIME * 45.0 + dist * 35.0);
    float pulse = 1.0 + 0.06 * sin(TIME * (3.5 + instability * 18.0));

    // 2. EFEK TERGERUS DI TITIK LASER (Local Cavitation / Scoop Dent)
    float ang_diff = abs(atan(sin(angle - hit_angle), cos(angle - hit_angle)));
    float dent = smoothstep(0.55, 0.0, ang_diff) * hit_cavitation * (star_radius * 0.42);
    float active_radius = max(0.025, (star_radius - dent) * pulse + shake);

    // 3. GRAVITATIONAL LENSING (Ruangan & Grid di sekeliling menekuk ke pusat)
    // Ruang di sekitar radius terluar dibelokkan radial ke arah pusat gravitasi
    float lens_mask = smoothstep(lensing_radius, active_radius * 0.85, dist);
    float einstein_peak = exp(-pow((dist - active_radius * 1.25) / 0.08, 2.0));
    float lens_warp = (lens_mask * 0.45 + einstein_peak * 0.85) * lensing_strength;

    // Distorsi bertambah liar & beriak saat instabilitas tinggi
    lens_warp *= (1.0 + instability * 1.1 * sin(TIME * 28.0 + angle * 5.0));
    
    // Pastikan distorsi lensa memudar halus ke 0 sebelum mencapai batas lingkaran
    lens_warp *= boundary_mask;

    vec2 dir = (dist > 0.001) ? (rel / dist) : vec2(0.0);
    vec2 warped_screen_uv = SCREEN_UV - dir * lens_warp;
    vec4 bg_sample = texture(screen_texture, warped_screen_uv);

    // 4. KORONA PLASMA SURYA & PUSARAN FUSI (Solar Prominence / Flares)
    vec2 polar_coords = vec2(angle * 2.5 + TIME * (0.8 + instability * 2.0), dist * 10.0 - TIME * 1.5);
    float plasma_noise = fbm(polar_coords);

    // Lidah api plasma yang menyembur keluar dari bintang
    float flare_dist = dist / active_radius;
    float corona_mask = smoothstep(1.75, 0.95, flare_dist + (plasma_noise - 0.5) * 0.40);

    // 5. INTI FUSI PADAT (Micro-Star Core)
    float core_mask = smoothstep(1.02, 0.80, flare_dist);
    float center_hotspot = smoothstep(0.42, 0.0, flare_dist);

    // Pendaran medan magnet di tepi fusi (Fluks Sian Elektrik)
    float magnetic_rim = smoothstep(1.18, 0.98, flare_dist) * smoothstep(0.82, 1.0, flare_dist);
    magnetic_rim += 0.35 * sin(angle * 12.0 + TIME * 8.0) * magnetic_rim;

    // 6. EFEK PERCIKAN TERGERUS DI AREA DENT
    float dent_sparks = 0.0;
    if (hit_cavitation > 0.05 && ang_diff < 0.6) {
        float spark_noise = hash21(vec2(angle * 20.0, floor(TIME * 30.0)));
        dent_sparks = smoothstep(0.65, 1.0, spark_noise) * hit_cavitation * 2.5;
    }

    // 7. KOMPOSISI WARNA FINAL
    vec4 dynamic_core = mix(core_color, critical_overload_color, instability * 0.85);
    vec4 dynamic_corona = mix(corona_color, core_color, instability * 0.5);

    // Mulai dari sampel latar belakang terdistorsi
    vec4 final_color = bg_sample;

    // Tambahkan pendaran korona plasma
    vec4 corona_rgb = dynamic_corona * (0.7 + 0.6 * plasma_noise);
    final_color = mix(final_color, corona_rgb + final_color * 0.4, corona_mask * corona_color.a);

    // Tambahkan cincin magnetik sian
    final_color += rim_electric_color * (magnetic_rim * 1.6);

    // Tambahkan inti fusi putih-emas padat
    vec4 core_rgb = mix(dynamic_corona, dynamic_core, core_mask);
    core_rgb += vec4(1.5, 1.5, 1.5, 0.0) * center_hotspot;
    core_rgb += vec4(2.0, 2.0, 2.0, 0.0) * dent_sparks;
    final_color = mix(final_color, core_rgb, core_mask);

    // Efek kilau kritis saat stabilitas nyaris 0 (flash putih berkedip)
    if (instability > 0.7) {
        float crit_flash = sin(TIME * 35.0) * 0.5 + 0.5;
        final_color += critical_overload_color * (crit_flash * (instability - 0.7) * 2.2 * core_mask);
    }

    // Blend secara halus di tepian luar menggunakan boundary_mask agar 100% bebas garis kotak
    vec4 unwarped_bg = texture(screen_texture, SCREEN_UV);
    COLOR = mix(unwarped_bg, final_color, boundary_mask);
}
