#version 450
#extension GL_GOOGLE_include_directive : require
// Splash crowns: one camera-facing quad per live slot (rain_splash.comp), its "up" the world's vertical as seen, its
// origin where the drop hit. The fragment shader draws the crown in projection: heights shrink with the cosine of the
// view's elevation, the rim's depth grows with its sine, so a crown seen from above is a ring. Empty slots cull themselves.

#include "ocean_common.glsl"
#include "rain_common.glsl"

layout(location = 0) out vec2 vLocal;     // in units of the crown's half width at size 1
layout(location = 1) out vec4 vSplash;    // x = life 0..1, y = seed, z = 1 on ground, w = opacity
layout(location = 2) out vec3 vColour;
layout(location = 3) out vec2 vElevation; // sine and cosine of the camera's elevation over the splash

const vec2 CORNERS[6] = vec2[](vec2(-1, 0), vec2(1, 0), vec2(-1, 1), vec2(1, 0), vec2(1, 1), vec2(-1, 1));

const float UNIT = 0.05;   // m: the crown's half width at size 1
const float SIDE = 1.5;    // the quad's half width in units: droplets fly out past the rim
const float BOTTOM = -1.0; // and its foot and top: a rim seen from above, a jet seen side on
const float TOP = 2.2;

void main()
{
    vec4 a = splashes.slot[2 * gl_InstanceIndex];
    vec4 b = splashes.slot[2 * gl_InstanceIndex + 1];
    if (a.w < 0.0)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // outside the clip volume
        return;
    }

    vec3 base = a.xyz;
    vec3 toCamera = frame.cameraPos.xyz - base;
    float dist = length(toCamera);
    vec3 view = toCamera / max(dist, 1e-4);
    float sine = clamp(view.y, -1.0, 1.0);
    vec3 up = vec3(0.0, 1.0, 0.0) - view * sine;
    float upLength = length(up);
    up = upLength > 1e-4 ? up / upLength : vec3(0.0, 0.0, 1.0);
    vec3 right = cross(up, view);

    float size = UNIT * b.x * (b.z > 0.5 ? 0.6 : 1.0);
    vec2 corner = CORNERS[gl_VertexIndex];
    vec2 local = vec2(corner.x * SIDE, mix(BOTTOM, TOP, corner.y));
    vec3 position = base + (right * local.x + up * local.y) * size;

    // A crown under a few pixels tall is a speck the fizz already draws: fade it out rather than alias.
    float pixels = 1.5 * size / max(rain.params.w * dist, 1e-6);

    vLocal = local;
    vSplash = vec4(a.w, b.y, b.z, smoothstep(2.0, 6.0, pixels));
    vColour = rainLight(base, 0.6);
    vElevation = vec2(max(sine, 0.0), sqrt(max(1.0 - sine * sine, 0.0)));
    gl_Position = frame.viewProj * vec4(position, 1.0);
}
