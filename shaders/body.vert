#version 450
// One body: a camera-facing quad around its bounding sphere (covers the sphere's silhouette cone), or a fullscreen
// triangle when the camera is close to it. body.frag ray-casts or marches it per pixel.
#extension GL_GOOGLE_include_directive : require
#include "body.glsl"

const vec2 TRIANGLE[3] = vec2[3](vec2(-1, -1), vec2(3, -1), vec2(-1, 3));
const vec2 QUAD[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

void main()
{
    Body b = bodies[pc.body];

    if (b.shape.y > 0.5)
    {
        gl_Position = gl_VertexIndex < 3 ? vec4(TRIANGLE[gl_VertexIndex], 0.0, 1.0) : vec4(0.0);
        return;
    }

    vec3 c = b.centre.xyz;
    float d = length(c);
    float r = b.centre.w;
    vec3 axis = c / d;
    vec3 side = normalize(cross(axis, abs(axis.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
    vec3 up = cross(side, axis);
    float s = 1.05 * r * d / sqrt(max(d * d - r * r, 1e-4 * d * d));
    vec2 q = QUAD[gl_VertexIndex];
    gl_Position = frame.viewProj * vec4(c + (side * q.x + up * q.y) * s, 1.0);
}
