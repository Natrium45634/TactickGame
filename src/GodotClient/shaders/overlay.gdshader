// Soft translucent fill drawn over tiles (reachability, hover). Colour and alpha come
// from the MultiMesh instance colour; the tile surface stays visible underneath.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

void fragment() {
	ALBEDO = COLOR.rgb;
	ALPHA = COLOR.a;
}
