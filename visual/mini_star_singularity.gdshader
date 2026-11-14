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

// =========================================================================
// PALET WARNA: VOID LIGHTNING DENGAN PETIR MAGENTA (HIGH DYNAMIC RANGE BLOOM)
// =========================================================================
uniform vec4 void_core_color : source_color = vec4(0.012, 0.006, 0.026, 1.0);        // Obsidian Void Core (Inti Gelap Pekat)
uniform vec4 nebula_violet_color : source_color = vec4(1.7, 0.22, 2.9, 0.88);         // Korona Deep Nebula Violet
uniform vec4 electric_magenta_color : source_color = vec4(5.4, 0.42, 4.3, 1.0);       // Petir & Rim Neon Magenta Supercharged
uniform vec4 hot_pink_accent : source_color = vec4(6.2, 2.2, 5.8, 1.0);              // Kilatan Putih-Pink Pijar Petir Inti
uniform vec4 critical_overload_color : source_color = vec4(6.5, 0.8, 5.5, 1.0);      // Overload Hyper-Magenta

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
    float boundary_mask = smoothstep(0.46, 0.28, dist);
    if (boundary_mask <= 0.001) {
        discard;
    }

    // 1. INSTABILITAS GETARAN KOSMIK & PULSA
    float shake = instability * 0.025 * sin(TIME * 45.0 + dist * 35.0);
    float pulse = 1.0 + 0.05 * sin(TIME * (3.2 + instability * 16.0));

    // 2. EFEK TERGERUS DI TITIK LASER (Local Cavitation / Scoop Dent)
    float ang_diff = abs(atan(sin(angle - hit_angle), cos(angle - hit_angle)));
    float dent = smoothstep(0.55, 0.0, ang_diff) * hit_cavitation * (star_radius * 0.42);
    float active_radius = max(0.020, (star_radius - dent) * pulse + shake);

    float flare_dist = dist / active_radius;

    // 3. PUSARAN PIRINGAN AKRESI NEBULA VIOLET (Swirling Void Vortex)
    float spiral_phase = angle * 3.0 - log(max(dist, 0.005)) * 6.5 + TIME * (2.2 + instability * 2.8);
    float vortex_arms = 0.5 + 0.5 * sin(spiral_phase);
    float accretion_zone = smoothstep(2.45, 1.02, flare_dist) * smoothstep(0.72, 1.15, flare_dist);
    float einstein_ring = exp(-pow((flare_dist - 1.28) / 0.16, 2.0));

    // 4. KORONA PLASMA VOID & KELAP-KELIP ELEKTRIK
    vec2 polar_coords = vec2(angle * 2.5 + TIME * (0.8 + instability * 2.0), dist * 10.0 - TIME * 1.5);
    float plasma_noise = fbm(polar_coords);
    float corona_mask = smoothstep(1.78, 0.94, flare_dist + (plasma_noise - 0.5) * 0.42);

    // 5. INTI VOID PEKAT (Event Horizon Void Singularity - BUKAN PUTIH MENYILAU KAN!)
    // Inti menyerap cahaya (Obsidian Void), dikelilingi batas ergosfer neon yang tajam
    float core_mask = smoothstep(1.02, 0.78, flare_dist);
    float event_horizon = smoothstep(0.92, 0.62, flare_dist);

    // 6. FILAMEN PETIR MAGENTA PROCEDURAL DI SEPANJANG RIM & KORONA
    vec2 lightning_uv1 = vec2(angle * 4.0 + sin(TIME * 7.5) * 0.6, flare_dist * 7.0 - TIME * 4.5);
    float lightning_noise1 = fbm(lightning_uv1);
    float arc_filaments = smoothstep(0.47, 0.52, lightning_noise1) * smoothstep(0.57, 0.52, lightning_noise1);
    float rim_arcs = arc_filaments * smoothstep(1.52, 0.88, flare_dist) * smoothstep(0.60, 0.95, flare_dist);

    // Sambaran percikan petir kedua berkecepatan tinggi (Micro-Arcs)
    float micro_spark = hash21(vec2(floor(angle * 14.0 + sin(TIME * 15.0)), floor(TIME * 22.0)));
    float spark_flash = smoothstep(0.72, 1.0, micro_spark) * smoothstep(1.22, 0.92, flare_dist) * smoothstep(0.72, 0.96, flare_dist);

    // Pendaran cincin magnetik neon magenta di tepi event horizon
    float magnetic_rim = smoothstep(1.22, 0.96, flare_dist) * smoothstep(0.80, 1.02, flare_dist);
    magnetic_rim += 0.42 * sin(angle * 10.0 + TIME * 9.0) * magnetic_rim;

    // 7. EFEK PERCIKAN PETIR TERGERUS DI AREA DENT (Laser Contact)
    float dent_sparks = 0.0;
    if (hit_cavitation > 0.05 && ang_diff < 0.65) {
        float spark_noise = hash21(vec2(angle * 24.0, floor(TIME * 35.0)));
        dent_sparks = smoothstep(0.52, 1.0, spark_noise) * hit_cavitation * 2.8;
    }

    // 8. KOMPOSISI WARNA & ALPHA TRANSPARAN MURNI
    vec3 rgb = vec3(0.0);
    float alpha = 0.0;

    // A. Aura Akresi Materi Void & Cincin Einstein Magenta-Violet
    vec3 accretion_rgb = mix(nebula_violet_color.rgb, electric_magenta_color.rgb, vortex_arms * 0.75);
    float accretion_alpha = (accretion_zone * (0.16 + 0.22 * vortex_arms) + einstein_ring * 0.32) * clamp(lensing_strength * 5.5, 0.25, 1.25);
    rgb += accretion_rgb * accretion_alpha * 1.6;
    alpha = max(alpha, accretion_alpha);

    // B. Pendaran Korona Plasma Nebula Violet
    vec3 corona_rgb = nebula_violet_color.rgb * (0.68 + 0.58 * plasma_noise);
    float c_alpha = corona_mask * nebula_violet_color.a;
    rgb = mix(rgb, corona_rgb, c_alpha);
    alpha = max(alpha, c_alpha);

    // C. Cincin Magnetik Neon Magenta & Filamen Petir
    vec3 rim_rgb = electric_magenta_color.rgb * (magnetic_rim * 1.75);
    vec3 lightning_rgb = hot_pink_accent.rgb * (rim_arcs * 3.4 + spark_flash * 2.4);
    rgb += rim_rgb + lightning_rgb;
    alpha = max(alpha, clamp(magnetic_rim * 0.88 + rim_arcs * 0.95, 0.0, 1.0));

    // D. Inti Void Gelap (Obsidian Void Core) - Menutupi bagian dalam dengan kegelapan kosmik
    vec3 core_rgb = void_core_color.rgb;
    // Tepi dalam event horizon diberi pendaran tipis ungu-magenta
    core_rgb += nebula_violet_color.rgb * 0.18 * (1.0 - event_horizon);
    core_rgb += hot_pink_accent.rgb * (dent_sparks * 2.2);

    rgb = mix(rgb, core_rgb, core_mask);
    alpha = max(alpha, core_mask);

    // E. Efek Kilau Kritis saat Overload (Supernova Magenta Surge)
    if (instability > 0.65) {
        float crit_flash = sin(TIME * 36.0) * 0.5 + 0.5;
        float crit_ratio = (instability - 0.65) / 0.35;
        rgb += critical_overload_color.rgb * (crit_flash * crit_ratio * 2.5 * core_mask);
    }

    COLOR = vec4(rgb, clamp(alpha * boundary_mask, 0.0, 1.0));
}