shader_type canvas_item;
render_mode blend_mix, unshaded;

// Tekstur layar belakang untuk distorsi optik ruang melingkar murni (Gravitational Lensing)
uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;

// Radius Event Horizon (Default 0.102)
uniform float black_hole_radius : hint_range(0.02, 0.25) = 0.102;

// Parameter Distorsi Ruang Relativistik (Einstein Lensing & Kerr Frame-Dragging Swirl)
uniform float lensing_strength : hint_range(0.0, 5.0) = 1.65;
uniform float swirl_strength : hint_range(0.0, 6.0) = 2.40;
uniform float swirl_speed : hint_range(-5.0, 5.0) = 0.35;

// Parameter Fase Charging (0.0 = Normal, 1.0 = Max Supercritical Charge)
uniform float charge_progress : hint_range(0.0, 1.0) = 0.0;
uniform float lightning_intensity : hint_range(0.0, 10.0) = 4.8;
uniform float fire_intensity : hint_range(0.0, 10.0) = 4.2;

// Parameter Transformasi White Hole (0.0 = Black Hole Hitam Pekat, 1.0 = White Hole Putih Menyilaukan)
uniform float white_hole_polarity : hint_range(0.0, 1.0) = 0.0;

// Cincin Foton & Bloom Emas Megah di Tepi Horizon (Tipis tapi Megah)
uniform float photon_ring_width : hint_range(0.001, 0.015) = 0.0030;
uniform float photon_ring_intensity : hint_range(1.0, 15.0) = 5.5;
uniform float gold_bloom_width : hint_range(0.003, 0.030) = 0.0100;
uniform float gold_bloom_intensity : hint_range(0.0, 10.0) = 3.4;

// Palet Warna
uniform vec4 photon_core_color : source_color = vec4(5.8, 5.6, 5.2, 1.0);     // Putih-perak terang HDR
uniform vec4 gold_bloom_color : source_color = vec4(3.4, 2.3, 0.58, 1.0);     // Transisi emas hangat megah

// --- FUNGSI HARMONIK ULTRA-RINGAN (Zero Loop, 100% GPU Fast-Path) ---
float fastNoise(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
    vec2 rel = UV - vec2(0.5);
    float dist = length(rel);
    float angle = atan(rel.y, rel.x);

    // 0. Boundary Mask: Halus memudar di batas terluar quad lingkaran (R: 0.44 - 0.495)
    float boundary_mask = smoothstep(0.495, 0.44, dist);
    if (boundary_mask <= 0.0001) {
        discard;
    }

    // Arah radial dan tangensial mulus
    vec2 rad_dir = (dist > 0.0001) ? (rel / dist) : vec2(1.0, 0.0);
    vec2 tan_dir = vec2(-rad_dir.y, rad_dir.x);

    // 1. Distorsi Ruang Gravitasi Relativistik (Einstein Ring Equation + Kerr Swirl)
    float r_horizon = black_hole_radius;
    float r_einstein = r_horizon * 1.62;
    float r_max = 0.485;

    vec2 pixel_disp = vec2(0.0);

    if (dist > r_horizon) {
        float t = clamp((dist - r_horizon) / (r_max - r_horizon), 0.0, 1.0);
        float outer_fade = smoothstep(1.0, 0.05, t);

        // A. Defleksi Radial Einstein
        float einstein_curve = (pow(r_einstein, 1.7) / pow(max(dist, 0.02), 0.85) - dist);
        float radial_pixels = einstein_curve * (lensing_strength * 380.0) * outer_fade;

        // B. Pusaran Tangensial Frame-Dragging (Kerr Metric Vortex)
        float swirl_falloff = pow(1.0 - t, 1.6);
        float swirl_pixels = (swirl_falloff * 240.0 * swirl_strength) * outer_fade;

        // Rotasi vortex dinamis
        float spin_time = TIME * swirl_speed;
        float cos_spin = cos(spin_time);
        float sin_spin = sin(spin_time);
        vec2 dynamic_tan = vec2(tan_dir.x * cos_spin - tan_dir.y * sin_spin, tan_dir.x * sin_spin + tan_dir.y * cos_spin);

        pixel_disp = (rad_dir * radial_pixels + dynamic_tan * swirl_pixels) * boundary_mask;
    }

    // 2. Sampling Background Layar Asli (Grid Arena & Parallax Bintang)
    vec2 target_screen_uv = clamp(SCREEN_UV + pixel_disp * SCREEN_PIXEL_SIZE, vec2(0.001), vec2(0.999));
    vec4 bg = texture(screen_texture, target_screen_uv);

    // 3. Efek Petir & Api di Event Horizon selama Fase Charging (charge_progress > 0.0)
    vec3 charge_vfx = vec3(0.0);
    if (charge_progress > 0.01 && dist > r_horizon) {
        float edge_dist = dist - r_horizon;
        
        // A. Lidah Api Plasma (Coronal Fire Tongues) menjilat keluar dari Event Horizon
        float fire_reach = 0.015 + 0.038 * charge_progress;
        if (edge_dist < fire_reach) {
            float fire_w1 = sin(angle * 6.0 + TIME * 3.5 + edge_dist * 40.0);
            float fire_w2 = cos(angle * 10.0 - TIME * 5.0 - edge_dist * 55.0);
            float fire_n = (fire_w1 * fire_w2) * 0.5 + 0.5;
            float fire_mask = smoothstep(fire_reach, 0.0, edge_dist) * smoothstep(0.25, 0.70, fire_n);
            
            vec3 fire_color = mix(gold_bloom_color.rgb * 1.8, photon_core_color.rgb, smoothstep(0.012, 0.0, edge_dist));
            charge_vfx += fire_color * (fire_mask * fire_intensity * charge_progress);
        }

        // B. Sambaran Petir Kuantum Berderak (Electric Lightning Arcs)
        float lightning_reach = 0.022 + 0.028 * charge_progress;
        if (edge_dist < lightning_reach) {
            float l1 = sin(angle * 16.0 + sin(TIME * 26.0) * 3.0 + edge_dist * 70.0);
            float arc1 = smoothstep(0.86, 0.98, abs(l1));
            
            float l2 = cos(angle * 26.0 - cos(TIME * 32.0) * 3.5 - edge_dist * 90.0);
            float arc2 = smoothstep(0.88, 0.99, abs(l2));
            
            float arc_mask = (arc1 * 1.5 + arc2 * 1.2) * smoothstep(lightning_reach, 0.0, edge_dist);
            vec3 lightning_color = vec3(6.8, 6.4, 5.8);
            charge_vfx += lightning_color * (arc_mask * lightning_intensity * charge_progress);
        }
    }

    // 4. Cincin Foton & Bloom Transisi Warna di Tepi Event Horizon
    // Black Hole: Cincin putih-perak dengan bloom emas megah
    // White Hole: Black hole versi putih dengan event horizon biru elektrik & bloom transisi warna cyan -> azure -> sapphire/violet
    vec3 photon_emission = vec3(0.0);
    if (dist > r_horizon) {
        float ring_center = r_horizon + photon_ring_width * 0.45;
        float ring_delta = abs(dist - ring_center);
        
        float sharp_core = exp(-pow(ring_delta / max(0.0008, photon_ring_width * 0.45), 2.0));
        float outer_halo = exp(-pow(ring_delta / max(0.0020, gold_bloom_width * 0.65), 2.0));
        
        // Warna Cincin Foton Horizon (Emas vs Biru Elektrik)
        vec3 wh_horizon_ring = vec3(2.5, 6.5, 10.5);
        vec3 active_photon_core = mix(photon_core_color.rgb, wh_horizon_ring, white_hole_polarity);

        // Transisi Warna Bloom Spektakuler: Cyan Elektrik -> Biru Azure -> Safir/Violet Kosmik
        float bloom_t = clamp(ring_delta / max(0.003, gold_bloom_width * 1.6), 0.0, 1.0);
        vec3 wh_bloom_inner = vec3(1.6, 5.4, 9.8);
        vec3 wh_bloom_mid   = vec3(0.8, 2.8, 8.2);
        vec3 wh_bloom_outer = vec3(0.45, 1.1, 4.6);
        vec3 wh_bloom = mix(mix(wh_bloom_inner, wh_bloom_mid, smoothstep(0.0, 0.45, bloom_t)), wh_bloom_outer, smoothstep(0.45, 1.0, bloom_t));
        
        vec3 active_bloom_color = mix(gold_bloom_color.rgb, wh_bloom, white_hole_polarity);

        float intensity_boost = 1.0 + charge_progress * 1.8 + white_hole_polarity * 0.4;
        photon_emission = active_photon_core * (sharp_core * photon_ring_intensity * intensity_boost)
                        + active_bloom_color * (outer_halo * gold_bloom_intensity * intensity_boost);
    }

    // 5. Inti Singularitas: Black Hole (Hitam Pekat) -> White Hole (Inti Putih Murni Bersih)
    float horizon_cut = smoothstep(r_horizon, r_horizon - 0.0035, dist);
    
    vec3 black_hole_core = vec3(0.0);
    vec3 white_hole_core = vec3(8.5, 8.8, 9.2); // Putih murni menyilaukan mutlak (HDR)
    vec3 core_rgb = mix(black_hole_core, white_hole_core, white_hole_polarity);

    // 6. Komposisi Bersih (Murni Distorsi Ruang + Inti Singularitas + Event Horizon)
    vec3 final_rgb = bg.rgb + photon_emission + charge_vfx;
    final_rgb = mix(final_rgb, core_rgb, horizon_cut);

    COLOR = vec4(final_rgb, boundary_mask);
}
