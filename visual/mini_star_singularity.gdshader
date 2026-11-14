shader_type canvas_item;
render_mode blend_mix, unshaded;

// Kontrol ukuran bintang mini (kompak & proporsional terhadap kapal pemain)
uniform float star_radius : hint_range(0.02, 0.35) = 0.16;

// Kekuatan cincin akresi gravitasi & pusaran materi eksotis
uniform float lensing_strength : hint_range(0.0, 0.35) = 0.16;
uniform float lensing_radius : hint_range(0.15, 0.46) = 0.42;

// Instabilitas akumulasi panas / overcharge sebelum meledak (0.0 normal, 1.0 critical supernova)
uniform float instability : hint_range(0.0, 1.0) = 0.0;

// Efek tergerus lokal di titik kontak laser
uniform float hit_angle : hint_range(-3.1416, 3.1416) = 0.0;
uniform float hit_cavitation : hint_range(0.0, 1.0) = 0.0;

// Palet Warna Kosmik Aether & Plasma Fusi (HDR Multiplier > 1.0 memicu Bloom WorldEnvironment)
uniform vec4 core_color : source_color = vec4(3.8, 3.2, 1.15, 1.0);        // Aether White-Gold Pijar
uniform vec4 corona_color : source_color = vec4(2.6, 1.3, 0.30, 0.95);     // Api Fusi Emas-Amber
uniform vec4 rim_electric_color : source_color = vec4(0.55, 2.3, 3.8, 0.90); // Busur Medan Magnetik Sian-Ungu
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
    lv = lv * lv * (3.0 - 2.0 * lv);

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

    // 0. BOUNDARY ALPHA MASK: 100% Transparan di luar jangkauan korona & cincin akresi
    // Tanpa BackBufferCopy yang menghitamkan layar, sehingga lengkungan Grid Ungu & Bintang Parallax
    // di belakang bintang terlihat tembus sepenuhnya sampai ke tepi inti bintang!
    float boundary_mask = smoothstep(0.46, 0.28, dist);
    if (boundary_mask <= 0.001) {
        discard;
    }

    // 1. INSTABILITAS GETARAN KOSMIK
    float shake = instability * 0.02 * sin(TIME * 45.0 + dist * 35.0);
    float pulse = 1.0 + 0.06 * sin(TIME * (3.5 + instability * 18.0));

    // 2. EFEK TERGERUS DI TITIK LASER (Local Cavitation / Scoop Dent)
    float ang_diff = abs(atan(sin(angle - hit_angle), cos(angle - hit_angle)));
    float dent = smoothstep(0.55, 0.0, ang_diff) * hit_cavitation * (star_radius * 0.42);
    float active_radius = max(0.020, (star_radius - dent) * pulse + shake);

    float flare_dist = dist / active_radius;

    // 3. PUSARAN PIRINGAN AKRESI MATERI EKSOTIS (Translucent Exotic Matter Vortex)
    float spiral_phase = angle * 3.0 - log(max(dist, 0.005)) * 6.5 + TIME * (2.4 + instability * 3.0);
    float vortex_arms = 0.5 + 0.5 * sin(spiral_phase);
    float accretion_zone = smoothstep(2.45, 1.02, flare_dist) * smoothstep(0.75, 1.15, flare_dist);
    float einstein_ring = exp(-pow((flare_dist - 1.32) / 0.18, 2.0));

    // 4. KORONA PLASMA SURYA & PUSARAN FUSI (Solar Prominence / Flares)
    vec2 polar_coords = vec2(angle * 2.5 + TIME * (0.8 + instability * 2.0), dist * 10.0 - TIME * 1.5);
    float plasma_noise = fbm(polar_coords);
    float corona_mask = smoothstep(1.78, 0.94, flare_dist + (plasma_noise - 0.5) * 0.42);

    // 5. INTI FUSI PADAT (Micro-Star Core)
    float core_mask = smoothstep(1.02, 0.80, flare_dist);
    float center_hotspot = smoothstep(0.45, 0.0, flare_dist);

    // Pendaran medan magnet di tepi fusi
    float magnetic_rim = smoothstep(1.20, 0.96, flare_dist) * smoothstep(0.82, 1.02, flare_dist);
    magnetic_rim += 0.38 * sin(angle * 12.0 + TIME * 8.0) * magnetic_rim;

    // 6. EFEK PERCIKAN TERGERUS DI AREA DENT
    float dent_sparks = 0.0;
    if (hit_cavitation > 0.05 && ang_diff < 0.6) {
        float spark_noise = hash21(vec2(angle * 20.0, floor(TIME * 30.0)));
        dent_sparks = smoothstep(0.65, 1.0, spark_noise) * hit_cavitation * 2.5;
    }

    // 7. KOMPOSISI WARNA & ALPHA TRANSPARAN MURNI (BEBAS LINGKARAN HITAM!)
    vec4 dynamic_core = mix(core_color, critical_overload_color, instability * 0.85);
    vec4 dynamic_corona = mix(corona_color, core_color, instability * 0.5);

    vec3 rgb = vec3(0.0);
    float alpha = 0.0;

    // A. Aura Akresi Materi Eksotis & Cincin Foton (tipis & tembus pandang agar grid di belakang tetap terlihat)
    vec3 accretion_rgb = mix(vec3(0.55, 0.18, 1.15), rim_electric_color.rgb, vortex_arms * 0.65);
    float accretion_alpha = (accretion_zone * (0.14 + 0.18 * vortex_arms) + einstein_ring * 0.28) * clamp(lensing_strength * 5.5, 0.25, 1.25);
    rgb += accretion_rgb * accretion_alpha * 1.8;
    alpha = max(alpha, accretion_alpha);

    // B. Pendaran Korona Plasma
    vec3 corona_rgb = dynamic_corona.rgb * (0.72 + 0.62 * plasma_noise);
    float c_alpha = corona_mask * dynamic_corona.a;
    rgb = mix(rgb, corona_rgb, c_alpha);
    alpha = max(alpha, c_alpha);

    // C. Cincin Magnetik Sian-Elektrik
    rgb += rim_electric_color.rgb * (magnetic_rim * 1.55);
    alpha = max(alpha, clamp(magnetic_rim * 0.85, 0.0, 1.0));

    // D. Inti Fusi Putih-Emas Padat
    vec3 core_rgb = mix(dynamic_corona.rgb, dynamic_core.rgb, core_mask);
    core_rgb += vec3(1.5, 1.5, 1.5) * center_hotspot;
    core_rgb += vec3(2.0, 2.0, 2.0) * dent_sparks;
    rgb = mix(rgb, core_rgb, core_mask);
    alpha = max(alpha, core_mask);

    // E. Efek Kilau Kritis saat Overload
    if (instability > 0.7) {
        float crit_flash = sin(TIME * 35.0) * 0.5 + 0.5;
        rgb += critical_overload_color.rgb * (crit_flash * (instability - 0.7) * 2.2 * core_mask);
    }

    COLOR = vec4(rgb, clamp(alpha * boundary_mask, 0.0, 1.0));
}
