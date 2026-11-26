#version 450
// Stage 1 of the detail ladder: every body as a point of light, one instanced draw (instance = body). A quad of
// POINT_RADIUS pixels around the body's projected centre; bodies with no point weight are culled.
#extension GL_GOOGLE_include_directive : require
#include "body.glsl"

const float POINT_RADIUS = 2.5;
const vec2 QUAD[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

layout(location = 0) out vec2 vPixel;
layout(location = 1) flat out vec3 vColour;

void main()
{
    Body b = bodies[gl_InstanceIndex];

    if (b.colour.w <= 0.0)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }

    vec2 q = QUAD[gl_VertexIndex];
    vec4 clip = frame.viewProj * vec4(b.centre.xyz, 1.0);
    clip.xy += q * POINT_RADIUS * 2.0 * frame.viewport.zw * clip.w;
    gl_Position = clip;
    vPixel = q * POINT_RADIUS;
    vColour = b.colour.rgb * b.colour.w;
}
