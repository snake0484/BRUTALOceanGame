// Placeholder bodies for --orbit-test (PlanetRenderer), shared by planet.vert and planet.frag. Each draw is placed by
// its instance record (instance.glsl): relOrigin = centre - camera, scale = radius, rotation = body axes to camera frame axes.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;  // no translation
    vec4 right;     // xyz camera right * aspect / f (camera frame axes), w = near
    vec4 up;        // xyz camera up / f
    vec4 forward;   // xyz camera forward, w = radians per pixel (2 / (f * height))
    vec4 sun;       // xyz toward the star, w = intensity
} frame;

layout(push_constant) uniform Body {
    vec4 cell0;   // xyz camera position in body axes mod 2 L0, w = L0
    vec4 cell1;   // xyz camera position in body axes mod 20 L0, w = blend toward 10 L0
    vec4 shape;   // x altitude h, y h (2R + h) (both from double), z kind: 0 sphere, 1 marker
    vec4 colour;  // rgb albedo
} pc;
