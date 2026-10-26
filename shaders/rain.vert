#version 450
#extension GL_GOOGLE_include_directive : require
// Falling rain: one streak per instance, in world-anchored boxes that wrap around the camera. Every third drop is in
// the near box, the rest in a box three times wider. Stateless: a drop's place is its hash, plus how far the air has
// carried it and how far it has fallen. A drop under terrain (a roof, an overhang, a cave) culls itself.

#include "ocean_common.glsl"
#include "rain_common.glsl"

layout(location = 0) out vec2 vCorner; // x across the streak -1..1, y along it 0 at the head .. 1 at the tail
layout(location = 1) out float vAlpha;
layout(location = 2) out vec3 vColour;

const vec2 CORNERS[6] = vec2[](vec2(-1, 0), vec2(1, 0), vec2(-1, 1), vec2(1, 0), vec2(1, 1), vec2(-1, 1));

const float NEAR_HALF = 8.0;     // m: half width of the near box ...
const float FAR_HALF = 24.0;     // ... and of the far one (the air's drift wraps at its width, a multiple of the near's)
const float NEAR_HEIGHT = 16.0;
const float FAR_HEIGHT = 36.0;
const float BELOW = 0.4;         // share of a box's height below the eye
const float DROP_WIDTH = 0.0025; // m: a 2.5 mm drop
const float MIN_PIXELS = 1.2;    // streaks are drawn at least this wide; thinner ones fade by coverage instead
const float OPACITY = 0.55;

void main()
{
    bool near = gl_InstanceIndex % 3 == 0;
    float halfWidth = near ? NEAR_HALF : FAR_HALF;
    float height = near ? NEAR_HEIGHT : FAR_HEIGHT;
    vec3 box = vec3(2.0 * halfWidth, height, 2.0 * halfWidth);
    vec4 r = rainRandom4(uvec3(uint(gl_InstanceIndex), 0x70u, 0u));

    // Fall speed varies with drop size, 0.75..1.25 of the mean; the air carries every drop alike.
    float speed = rain.air.y * (0.75 + 0.5 * r.w);
    vec3 lo = frame.cameraPos.xyz - vec3(halfWidth, BELOW * height, halfWidth);
    vec3 p = r.xyz * box + vec3(rain.air.x, -speed * rain.params.y, rain.air.z);
    p = lo + mod(p - lo, box);

    if (p.y < rainCoverTop(p.xz))
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // outside the clip volume
        return;
    }

    vec3 velocity = vec3(rain.wind.x, -speed, rain.wind.y);
    vec3 tail = p - velocity * rain.params.z;
    vec3 toCamera = frame.cameraPos.xyz - p;
    float dist = length(toCamera);

    // At least MIN_PIXELS wide, faded by how much of that a real drop covers so distant streaks never alias; by its
    // square root, since a streak's brightness against the dark reads further than its true coverage.
    float width = max(DROP_WIDTH, MIN_PIXELS * rain.params.w * dist);
    vec3 axis = tail - p;
    vec3 side = cross(axis, toCamera);
    float sideLength = length(side);
    side = sideLength > 1e-6 ? side / sideLength : vec3(1.0, 0.0, 0.0);

    vec2 corner = CORNERS[gl_VertexIndex];
    vec3 position = p + axis * corner.y + side * (corner.x * 0.5 * width);

    // Fade at the box's faces so the wrap never shows, and right at the eye.
    vec3 rel = (p - lo) / box;
    float edge = max(abs(rel.x - 0.5), abs(rel.z - 0.5)) * 2.0;
    float fade = (1.0 - smoothstep(0.7, 1.0, edge)) * smoothstep(0.0, 0.1, rel.y) * (1.0 - smoothstep(0.85, 1.0, rel.y));
    fade *= smoothstep(0.3, 1.2, dist);

    vCorner = corner;
    vAlpha = OPACITY * fade * sqrt(DROP_WIDTH / width) * (0.6 + 0.4 * rain.params.x);
    vColour = rainLight(p, 0.4);
    gl_Position = frame.viewProj * vec4(position, 1.0);
}
