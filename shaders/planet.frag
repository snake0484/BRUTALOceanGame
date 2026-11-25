#version 450
// Kind 0: ray-casts the sphere in camera-relative float. The camera's altitude h and c = h (2R + h) come from double,
// so t = c / (sqrt(b^2 - c) - b) keeps full precision metres above a 1000 km surface (CameraProjection.SphereDistance
// mirrors it). A 3D checker at two decades of cell size, anchored by the camera's position mod the period (double on
// the CPU), shows motion at any altitude. Kind 1: the marker disc, biased toward the camera so the surface never cuts it.
#extension GL_GOOGLE_include_directive : require
#include "planet.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 1) flat in vec4 vOrigin;
layout(location = 2) flat in vec4 vRotation;

layout(location = 0) out vec4 outColor;

const vec3 DARK = vec3(0.26, 0.28, 0.32);
const vec3 LIGHT = vec3(0.44, 0.42, 0.37);
const float AMBIENT = 0.03;
const float MARKER_DEPTH_BIAS = 1.05;

vec3 acesFit(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

vec3 rotate(vec4 q, vec3 v)
{
    vec3 t = 2.0 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

vec3 shade(vec3 albedo, vec3 normal, float emissive)
{
    float light = max(dot(normal, frame.sun.xyz), 0.0) * frame.sun.w + AMBIENT + emissive;
    return pow(acesFit(albedo * light), vec3(1.0 / 2.2));
}

// 0 or 1 per cell, faded to 0.5 once a cell spans less than a few pixels.
float checker(vec3 q, float cell, float footprint)
{
    vec3 i = floor(q / cell);
    float parity = mod(i.x + i.y + i.z, 2.0);
    return mix(parity, 0.5, smoothstep(0.15, 0.4, footprint / cell));
}

void main()
{
    vec3 right = frame.right.xyz;
    vec3 up = frame.up.xyz;
    vec3 forward = frame.forward.xyz;

    if (pc.shape.z > 0.5)
    {
        float r2 = dot(vUv, vUv);

        if (r2 > 1.0)
            discard;

        vec3 normal = normalize(normalize(right) * vUv.x + normalize(up) * vUv.y - forward * sqrt(1.0 - r2));
        outColor = vec4(shade(pc.colour.rgb, normal, 0.6), 1.0);
        gl_FragDepth = min(gl_FragCoord.z * MARKER_DEPTH_BIAS, 1.0);
        return;
    }

    vec3 dir = normalize(forward + vUv.x * right - vUv.y * up);
    float radius = vOrigin.w;
    float altitude = pc.shape.x;
    float c = pc.shape.y;
    vec3 cameraUp = -normalize(vOrigin.xyz);
    float b = (radius + altitude) * dot(cameraUp, dir);
    float disc = b * b - c;

    if (b >= 0.0 || disc < 0.0)
        discard;

    float t = c / (sqrt(disc) - b);
    vec3 hit = t * dir;
    vec3 normal = normalize(cameraUp * (radius + altitude) + hit);
    vec3 local = rotate(vec4(-vRotation.xyz, vRotation.w), hit);
    float footprint = t * frame.forward.w / max(-dot(dir, normal), 0.05);
    float k0 = checker(pc.cell0.xyz + local, pc.cell0.w, footprint);
    float k1 = checker(pc.cell1.xyz + local, pc.cell0.w * 10.0, footprint);

    outColor = vec4(shade(mix(DARK, LIGHT, mix(k0, k1, pc.cell1.w)), normal, 0.0), 1.0);
    gl_FragDepth = frame.right.w / (t * dot(dir, forward));
}
