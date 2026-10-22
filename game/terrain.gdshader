// The ground (M7e): the colour of each vertex (grass, upland, rock, snow, sand) is the tint, and a texture of ambientCG gives the grain.
// Textures are normalised so that their mean is 0.5; two scales multiplied (x4) keep the mean at 1 and break the tiling.
shader_type spatial;

uniform sampler2D grass_tex : source_color, repeat_enable, filter_linear_mipmap;
uniform sampler2D dirt_tex : source_color, repeat_enable, filter_linear_mipmap;
uniform sampler2D rock_tex : source_color, repeat_enable, filter_linear_mipmap;
uniform bool grain_on = true; // the performance panel can switch the grain off
uniform float tint = 1.0; // 0.45 under a map overlay
uniform float fade_near = 300.0; // beyond this the grain fades to flat colour (no shimmer far away)
uniform float fade_far = 1100.0;

varying vec3 world_pos;
varying vec3 world_normal;

// One read far from the camera, two (a second, larger scale, to hide the tiling) only within 300 m.
vec3 grain(sampler2D t, vec2 uv, float metres, float dist) {
	vec3 a = texture(t, uv / metres).rgb * 2.0;
	if (dist < 300.0) {
		vec3 b = texture(t, uv / (metres * 7.3) + vec2(0.37, 0.61)).rgb * 2.0;
		a *= mix(b, vec3(1.0), smoothstep(150.0, 300.0, dist));
	}
	return a;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	// The texture reads are the cost of the ground (the performance log of 2026-10-22: the grain was most of the terrain's 19 ms at 4471 x 2515), so
	// they sit inside branches that whole screen areas take together: none beyond fade_far, rock only where the ground is steep.
	vec3 detail = vec3(1.0);
	float dist = length(VERTEX);
	float far = smoothstep(fade_near, fade_far, dist);
	if (grain_on && far < 0.999) {
		float steep = smoothstep(0.93, 0.78, world_normal.y);
		vec2 top = world_pos.xz;
		if (steep < 0.999) {
			float sand = COLOR.a;
			vec3 flat_grain = sand > 0.001 ? mix(grain(grass_tex, top, 6.0, dist), grain(dirt_tex, top, 5.0, dist), sand) : grain(grass_tex, top, 6.0, dist);
			detail = flat_grain;
		}
		if (steep > 0.001) {
			vec3 wall = abs(world_normal);
			vec3 rock = vec3(0.0);
			if (wall.x > 0.05) rock += grain(rock_tex, world_pos.zy, 9.0, dist) * wall.x;
			if (wall.z > 0.05) rock += grain(rock_tex, world_pos.xy, 9.0, dist) * wall.z;
			if (wall.y > 0.05) rock += grain(rock_tex, top, 9.0, dist) * wall.y;
			rock /= max((wall.x > 0.05 ? wall.x : 0.0) + (wall.y > 0.05 ? wall.y : 0.0) + (wall.z > 0.05 ? wall.z : 0.0), 0.001);
			detail = steep > 0.999 ? rock : mix(detail, rock, steep);
		}
		detail = mix(detail, vec3(1.0), far);
	}
	ALBEDO = COLOR.rgb * tint * detail;
	ROUGHNESS = 1.0;
}
