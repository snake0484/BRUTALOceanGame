#version 450
#extension GL_GOOGLE_include_directive : require
// Spray: one camera-facing quad per live particle (instance = particle slot); dead slots cull themselves. A droplet with a
// streak is drawn stretched back along its motion across the screen (the eye's blur of a fast droplet), its opacity
// spread over the length; puffs stay round. Far droplets are drawn at least MIN_RADIUS of the screen, fainter as they
// are enlarged, so a pour's spray seen from 30 m glints rather than vanishing under a pixel.

#include "ocean_common.glsl"

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec2 vCorner; // -1..1 across the quad
layout(location = 2) out float vFade;
layout(location = 3) out float vPhase; // forward scattering g

const float MIN_RADIUS = 0.0025; // NDC: about a pixel at 900 lines

const vec2 CORNERS[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(-1, 1), vec2(1, -1), vec2(1, 1), vec2(-1, 1));

void main()
{
    SprayParticle p = spray.particles[gl_InstanceIndex];
    if (p.positionLife.w <= 0.0)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // outside the clip volume
        return;
    }

    vec3 centre = p.positionLife.xyz;
    vec3 toCamera = normalize(frame.cameraPos.xyz - centre);
    vec3 right = normalize(cross(vec3(0.0, 1.0, 0.0), toCamera) + vec3(1e-5, 0.0, 0.0));
    vec3 up = cross(toCamera, right);
    float size = p.velocitySize.w;
    float spread = 1.0;

    vec4 clipCentre = frame.viewProj * vec4(centre, 1.0);
    vec4 clipEdge = frame.viewProj * vec4(centre + up * size, 1.0);
    float ndc = length(clipEdge.xy / clipEdge.w - clipCentre.xy / clipCentre.w);
    if (clipCentre.w > 0.0 && ndc < MIN_RADIUS)
    {
        float grow = MIN_RADIUS / max(ndc, 1e-6);
        size *= grow;
        spread /= grow;
    }

    float halfLength = size;

    // Motion across the view, not toward it: a droplet flying at the eye stays round.
    vec3 v = p.velocitySize.xyz;
    vec3 across = v - toCamera * dot(v, toCamera);
    float streak = length(across) * p.shape.y;
    if (streak > 0.5 * size)
    {
        up = across / length(across);
        right = normalize(cross(up, toCamera));
        halfLength = size + 0.5 * streak;
        centre -= up * (0.5 * streak);
        spread *= size / halfLength;
    }

    vec2 corner = CORNERS[gl_VertexIndex];
    vWorldPos = centre + right * (corner.x * size) + up * (corner.y * halfLength);
    vCorner = corner;
    vFade = smoothstep(0.0, 0.5, p.positionLife.w) * p.motion.w * sqrt(spread);
    vPhase = p.shape.z;
    gl_Position = frame.viewProj * vec4(vWorldPos, 1.0);
}
