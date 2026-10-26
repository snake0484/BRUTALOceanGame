#version 450
// Debug shapes: Lambert sun + hemispheric sky + small specular, box edge lines, ocean-matched tonemap/fog.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // xyz world
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
} frame;

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec3 vLocal;
layout(location = 3) flat in vec3 vHalf;
layout(location = 4) flat in vec4 vColour;

layout(location = 0) out vec4 outColor;

const vec3 GROUND_BOUNCE = vec3(0.10, 0.09, 0.08);
const float EDGE_DARKEN = 0.45;

vec3 acesFit(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main()
{
    vec3 N = normalize(vNormal);
    vec3 L = frame.sunDir.xyz;
    vec3 V = normalize(frame.cameraPos.xyz - vWorldPos);
    float intensity = frame.sunDir.w;
    vec3 albedo = vColour.rgb;

    float up = N.y;
    vec3 skyAmbient = mix(frame.skyHorizon.rgb, frame.skyZenith.rgb, clamp(up, 0.0, 1.0));
    vec3 ambient = mix(skyAmbient, GROUND_BOUNCE, clamp(-up, 0.0, 1.0));

    float ndl = max(dot(N, L), 0.0);
    float spec = ndl > 0.0 ? pow(max(dot(N, normalize(L + V)), 0.0), 48.0) * 0.12 : 0.0;
    vec3 colour = albedo * (ndl * intensity + ambient) + spec * intensity;

    // Box edges: within edge width of two faces at once.
    if (vColour.w > 0.5)
    {
        float width = min(0.04, 0.12 * min(vHalf.x, min(vHalf.y, vHalf.z)));
        vec3 near = step(vHalf - width, abs(vLocal));
        colour *= near.x + near.y + near.z >= 2.0 ? EDGE_DARKEN : 1.0;
    }

    // Tonemap + gamma (same as ocean).
    colour = pow(acesFit(colour), vec3(1.0 / 2.2));

    // Fog in display space so far shapes equal the clear colour, like the ocean.
    float dist = length(frame.cameraPos.xyz - vWorldPos);
    float f = 1.0 - exp(-dist * frame.skyHorizon.w);
    colour = mix(colour, frame.skyHorizon.rgb, f);

    outColor = vec4(colour, 1.0);
}
