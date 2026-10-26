#version 450
#extension GL_GOOGLE_include_directive : require
// Spray: one camera-facing quad per live particle (instance = particle slot); dead slots cull themselves.

#include "ocean_common.glsl"

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec2 vCorner; // -1..1 across the quad
layout(location = 2) out float vFade;

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
    vec2 corner = CORNERS[gl_VertexIndex];

    vWorldPos = centre + (right * corner.x + up * corner.y) * p.velocitySize.w;
    vCorner = corner;
    vFade = smoothstep(0.0, 0.5, p.positionLife.w) * p.motion.w;
    gl_Position = frame.viewProj * vec4(vWorldPos, 1.0);
}
