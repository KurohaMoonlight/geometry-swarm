shader_type canvas_item;
render_mode blend_add;

// =========================================================================
// SUPER GIGA DRILL BREAK - ION CYCLONE EXHAUST SHADER
// =========================================================================
// Mensimulasikan semburan ion rocket & badai siklon plasma berpilin (corkscrew vortex)
// yang berputar mengikuti RPM bor raksasa dengan rangkaian Mach Shock Diamonds supersonik.

uniform float power : hint_range(0.0, 1.0) = 1.0;
uniform float spin_time = 0.0;
uniform float rpm = 28.0;
uniform float swarm_scale = 1.0;
uniform float diamond_count = 8.0;

uniform vec4 core_color : source_color = vec4(5.8, 5.6, 6.8, 1.0);
uniform vec4 mid_color : source_color = vec4(2.4, 4.8, 6.8, 0.95);
uniform vec4 outer_color : source_color = vec4(1.4, 0.5, 4.8, 0.65);

void fragment() {
    vec2 uv = UV;

    // Jarak tegak lurus dari sumbu tengah (0.0 di tengah, 1.0 di tepi)
    float dist = abs(uv.y - 0.5) * 2.0;

    // Pemudaran sepanjang ekor jet
    float tail_fade = pow(clamp(1.0 - uv.x, 0.0, 1.0), 1.15);
    float edge_falloff = smoothstep(1.0, 0.0, dist);

    // 1. Pilinan Badai Heliks Ganda / 4 Untai (Corkscrew Tornado Plasma)
    float twist_freq = 22.0 + 4.0 * swarm_scale;
    float twist_amp = (0.28 + 0.08 * sin(uv.x * 9.0)) * (1.0 - uv.x * 0.45);

    float twist1 = sin(uv.x * twist_freq - spin_time * rpm) * twist_amp;
    float twist2 = sin(uv.x * twist_freq - spin_time * rpm + 3.14159265) * twist_amp;
    float twist3 = sin(uv.x * twist_freq - spin_time * rpm + 1.57079632) * twist_amp;
    float twist4 = sin(uv.x * twist_freq - spin_time * rpm + 4.71238898) * twist_amp;

    float strand_w = 8.5;
    float ribbon1 = exp(-pow((uv.y - 0.5 - twist1) * strand_w, 2.0));
    float ribbon2 = exp(-pow((uv.y - 0.5 - twist2) * strand_w, 2.0));
    float ribbon3 = exp(-pow((uv.y - 0.5 - twist3) * strand_w, 2.0));
    float ribbon4 = exp(-pow((uv.y - 0.5 - twist4) * strand_w, 2.0));

    float mult_strands = (swarm_scale > 1.2) ? (ribbon3 + ribbon4) * 0.85 : 0.0;
    float combined_ribbons = ribbon1 + ribbon2 + mult_strands;

    // 2. Rangkaian Mach Shock Diamonds Supersonik Berdenyut di Sumbu Tengah
    float d_coord = uv.x * diamond_count - spin_time * 8.0;
    float diamond_x = abs(fract(d_coord) - 0.5) * 2.0;
    float diamond_shape = diamond_x + dist * 2.2;
    float shock_diamond = smoothstep(0.92, 0.0, diamond_shape) * (1.0 - uv.x * 0.65);

    // Cincin kompresi di simpul diamond
    float node_ring = exp(-pow((fract(d_coord) - 0.5) * 6.0, 2.0)) * smoothstep(0.8, 0.0, dist);

    // 3. Berkas Laser Ion Inti (High-Velocity Centerline Core)
    float core_intensity = exp(-pow(dist * 7.5, 2.0)) * (1.0 - uv.x * 0.35);

    // 4. Turbulensi Mikro Arus Ion (High-Frequency Ionized Flutter)
    float flutter = sin(uv.x * 65.0 - spin_time * 42.0 + uv.y * 18.0) * 0.12;

    // 5. Komposisi Warna Bertingkat (HDR Neon Blend)
    vec3 col = outer_color.rgb * (edge_falloff * 0.45 + flutter) * tail_fade;
    col += mid_color.rgb * combined_ribbons * tail_fade * 1.25;
    col += core_color.rgb * core_intensity * tail_fade * 1.6;
    col += vec3(6.5, 6.2, 5.5) * (shock_diamond * 1.8 + node_ring * 0.8) * tail_fade;

    float alpha = clamp((edge_falloff * 0.6 + combined_ribbons + core_intensity + shock_diamond) * power * tail_fade, 0.0, 1.0);
    COLOR = vec4(col * power, alpha);
}

