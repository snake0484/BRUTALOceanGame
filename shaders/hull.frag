#version 450
// Designer hull: Lambert sun + hemispheric sky + small specular, material colour from the palette, ACES + gamma.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 sunDir;
    vec4 skyZenith;
    vec4 skyHorizon;
    vec4 palette[32];
} frame;

layout(push_constant) uniform Brick {
    vec4 originScale;
} brick;

layout(location = 0) in vec3 vRelPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) flat in uint vMaterial;

layout(location = 0) out vec4 outColor;

const vec3 GROUND_BOUNCE = vec3(0.10, 0.09, 0.08);

vec3 acesFit(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main()
{
    vec3 N = normalize(vNormal);
    vec3 L = frame.sunDir.xyz;
    vec3 V = normalize(-vRelPos);
    float intensity = frame.sunDir.w;
    vec3 albedo = frame.palette[min(vMaterial, 31u)].rgb;

    float up = N.y;
    vec3 skyAmbient = mix(frame.skyHorizon.rgb, frame.skyZenith.rgb, clamp(up, 0.0, 1.0));
    vec3 ambient = mix(skyAmbient, GROUND_BOUNCE, clamp(-up, 0.0, 1.0));

    float ndl = max(dot(N, L), 0.0);
    float spec = ndl > 0.0 ? pow(max(dot(N, normalize(L + V)), 0.0), 48.0) * 0.15 : 0.0;
    vec3 colour = albedo * (ndl * intensity + ambient) + spec * intensity;
    outColor = vec4(pow(acesFit(colour), vec3(1.0 / 2.2)), 1.0);
}
