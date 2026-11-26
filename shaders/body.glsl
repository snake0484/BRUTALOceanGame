// Celestial bodies on the detail ladder (BodyRenderer, planets.md "Detail ladder"): shared by body.vert/frag (discs and
// impostors, one draw per body) and body_point.vert/frag (points of light, one instanced draw for all).

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;  // no translation
    vec4 right;     // xyz camera right * aspect / f (camera frame axes), w = near
    vec4 up;        // xyz camera up / f
    vec4 forward;   // xyz camera forward, w = radians per pixel
    vec4 viewport;  // xy render target size, zw = 1 / size
} frame;

// BodyRenderer.BodyDraw: camera frame = the camera's frame axes; body axes = the body's surface frame.
struct Body {
    vec4 centre;    // xyz centre - camera (camera frame), w = bounding radius
    vec4 rotation;  // quaternion: camera frame axes to body axes
    vec4 sun;       // xyz toward the star (body axes), w = intensity
    vec4 shape;     // x kind (0 rocky, 1 star), y fullscreen, z ground alpha, w depth scale (> 1 draws in front, < 1 behind)
    vec4 radii;     // x datum radius R, y base height (sea or 0, m about R), z atmosphere top radius (0: none), w scale height
    vec4 relief;    // x height at 0, y height span (R16 decode), z relief 0..1, w steepest slope
    vec4 lod;       // x texel angle at level 0 (rad), y finest-to-coarsest levels - 1, z atmosphere rim alpha
    vec4 colour;    // rgb point of light (display), w point weight
    vec4 emission;  // rgb star emission (linear), w star's angular radius seen from the body (rad)
    vec4 occluder0; // xyz centre (body axes, from the body's centre), w radius (0: none)
    vec4 occluder1;
    vec4 reserved;
};

layout(set = 0, binding = 1, std430) readonly buffer Bodies {
    Body bodies[];
};

layout(push_constant) uniform Draw {
    uint body;
} pc;

vec3 rotateByQuat(vec4 q, vec3 v)
{
    vec3 t = 2.0 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}
