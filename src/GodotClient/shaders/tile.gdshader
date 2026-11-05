// Board tiles: flat-shaded "default 3D primitive" look with thin pale edge lines.
// UV.x holds the distance from the fragment to the tile border in world units
// (apothem at the tile centre, 0 on the border, large on side faces), so line width
// scales with the map instead of being tied to screen pixels.
shader_type spatial;
render_mode diffuse_lambert, specular_schlick_ggx;

uniform vec4 line_color : source_color = vec4(0.60, 0.62, 0.66, 1.0);
// Half of the visible line width, per tile (two neighbouring tiles form one line).
uniform float line_half_width = 0.011;
// Minimal half width in pixels so lines never disappear in small windows.
uniform float line_min_pixels = 0.55;
uniform float line_strength = 0.85;
uniform float roughness_value = 0.82;

void fragment() {
	float d = UV.x;
	float fw = max(fwidth(d), 1e-5);
	float hw = max(line_half_width, fw * line_min_pixels);
	float mask = 1.0 - smoothstep(hw - fw * 0.5, hw + fw * 0.5, d);
	ALBEDO = mix(COLOR.rgb, line_color.rgb, mask * line_strength);
	ROUGHNESS = roughness_value;
	SPECULAR = 0.25;
}
