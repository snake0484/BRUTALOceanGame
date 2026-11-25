#version 450
// Kind 0, a sphere impostor: a fullscreen triangle whose fragments ray-cast the sphere (planet.frag). Kind 1, a surface
// marker: a camera-facing disc rasterised through viewProj, so its centre lands where a vertex at relOrigin would.
#extension GL_GOOGLE_include_directive : require
#include "instance.glsl"
#include "planet.glsl"

layout(location = 0) out vec2 vUv;           // kind 0: NDC; kind 1: disc coordinates
layout(location = 1) flat out vec4 vOrigin;   // xyz relOrigin, w radius
layout(location = 2) flat out vec4 vRotation;

const vec2 TRIANGLE[3] = vec2[3](vec2(-1, -1), vec2(3, -1), vec2(-1, 3));
const vec2 QUAD[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

void main()
{
    Instance body = instances[gl_InstanceIndex];
    vOrigin = vec4(body.relOrigin, body.scale);
    vRotation = body.rotation;

    if (pc.shape.z < 0.5)
    {
        vUv = TRIANGLE[gl_VertexIndex];
        gl_Position = vec4(vUv, 0.0, 1.0);
        return;
    }

    vUv = QUAD[gl_VertexIndex];
    vec3 offset = normalize(frame.right.xyz) * vUv.x + normalize(frame.up.xyz) * vUv.y;
    gl_Position = frame.viewProj * vec4(body.relOrigin + offset * body.scale, 1.0);
}
