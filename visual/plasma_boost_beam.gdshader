shader_type canvas_item;
render_mode blend_add;

// =========================================================================
// SACRIFICIAL PLASMA BOOST - AEROSPIKE LASER AFTERBURNER SHADER [SPACE]
// =========================================================================
// Mensimulasikan semburan roket plasma aerospike berkecepatan hipersonik,
// dengan deretan Mach Shock Diamonds tajam, aliran laminar frekuensi tinggi,
// dan kilatan gelombang kejut termal saat drone Dart dikorbankan di nozzle.

uniform float power : hint_range(0.0, 1.0) = 1.0;
uniform float time = 0.0;
uniform float flash_boost : hint_range(0.0, 1.0) = 0.0;
uniform float diamond_count = 7.0;

uniform vec4 core_color : source_color = vec4(5.5, 5.5, 6.8, 1.0);
uniform vec4 mid_color : source_color = vec4(1.2, 4.0, 6.2, 0.95);
uniform vec4 outer_color : source_color = vec4(0.4, 1.6, 4.8, 0.55);
uniform vec4 sacrifice_flash_color : source_color = vec4(6.5, 4.4, 1.8, 1.0);

void fragment() {
    vec2 uv = UV;

    // Jarak melintang dari sumbu pusat (0.0 di tengah, 1.0 di tepi)
    float dist = abs(uv.y - 0.5) * 2.0;

    // Profil kontur semburan aerospike (mengembang sedikit dari nozzle lalu meruncing tajam)
    float throat_to_plume = mix(0.40, 1.0, smoothstep(0.0, 0.22, uv.x));
    float aerospike_w = throat_to_plume * pow(clamp(1.0 - uv.x, 0.0, 1.0), 0.72);
    float norm_dist = dist / max(0.001, aerospike_w);
    float plume_mask = smoothstep(1.0, 0.0, norm_dist);
    float tail_fade = pow(clamp(1.0 - uv.x, 0.0, 1.0), 1.25);

    // 1. Berkas Inti Aerospike Berkecepatan Hipersonik (Razor-Thin Centerline)
    float core_intensity = exp(-pow(norm_dist * 6.8, 2.0)) * tail_fade;

    // 2. Rangkaian Mach Shock Diamonds Berdenyut Cepat
    float d_coord = uv.x * diamond_count - time * 26.0 * power;
    float diamond_x = abs(fract(d_coord) - 0.5) * 2.0;
    float diamond_shape = diamond_x * 1.25 + norm_dist * 2.5;
    float shock_diamond = smoothstep(0.90, 0.0, diamond_shape) * tail_fade;

    // Titik simpul kompresi bersuhu ekstrem
    float node_spike = exp(-pow((fract(d_coord) - 0.5) * 8.0, 2.0)) * exp(-pow(norm_dist * 4.2, 2.0)) * tail_fade;

    // 3. Fluktuasi Laminar Gelombang Kejut Frekuensi Tinggi
    float flutter = sin(uv.x * 110.0 - time * 72.0 + uv.y * 28.0) * 0.08 * power;

    // 4. Gelombang Ledakan Termal saat Drone Dikorbankan (Sacrificial Thermal Wave)
    float flash_dist = fract(time * 3.5);
    float flash_wave = exp(-pow((uv.x - flash_dist * 0.8) * 5.0, 2.0)) * flash_boost;

    // 5. Komposisi Warna Pendaran HDR
    vec3 col = outer_color.rgb * (plume_mask * 0.40 + flutter) * tail_fade;
    col += mid_color.rgb * plume_mask * (0.85 + flutter) * tail_fade;
    col += core_color.rgb * core_intensity * 1.85;
    col += vec3(6.2, 6.2, 6.8) * (shock_diamond * 2.2 + node_spike * 1.4);
    col += sacrifice_flash_color.rgb * flash_wave * 3.2;

    float alpha = clamp((plume_mask + core_intensity + shock_diamond) * power * tail_fade, 0.0, 1.0);
    COLOR = vec4(col * power, alpha);
}

