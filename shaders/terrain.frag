#version 450
// Terrain fragment shader: material palette, Lambert sun + hemispheric sky about the planet's radial up, underwater
// absorption below the sea sphere (and the water's colour where no ocean surface covers it), ocean-matched tonemap/fog.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 planet;      // xyz = planet centre relative to the camera, w = sea radius (0 = no sea)
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
    vec4 water;       // rgb = seabed fade colour, w = distance past which it applies (no ocean surface there)
} frame;

layout(location = 0) in vec3 vWorldPos; // camera-relative, camera frame axes
layout(location = 1) in vec3 vNormal;
layout(location = 2) flat in uint vMaterial;

layout(location = 0) out vec4 outColor;

const vec3 ABSORPTION = vec3(0.30, 0.09, 0.06);  // per metre below sea level
const vec3 GROUND_BOUNCE = vec3(0.10, 0.09, 0.08);

// Linear albedo, indexed by VoxelMaterial (Air, Rock, Sand, Grass, Dirt).
const vec3 PALETTE[5] = vec3[5](
    vec3(1.00, 0.00, 1.00),
    vec3(0.28, 0.25, 0.22),
    vec3(0.62, 0.56, 0.38),
    vec3(0.14, 0.28, 0.07),
    vec3(0.27, 0.18, 0.10)
);

vec3 acesFit(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main()
{
    vec3 N = normalize(vNormal);
    vec3 L = frame.sunDir.xyz;
    float intensity = frame.sunDir.w;
    vec3 albedo = PALETTE[min(vMaterial, 4u)];

    // Hemispheric ambient about the radial up: zenith above, horizon at the side, dim bounce below. Float radii are
    // ~1e6 m here, so heights are good to ~0.1 m: plenty for absorption.
    vec3 fromCentre = vWorldPos - frame.planet.xyz;
    float radius = length(fromCentre);
    float up = dot(N, fromCentre / radius);
    vec3 skyAmbient = mix(frame.skyHorizon.rgb, frame.skyZenith.rgb, clamp(up, 0.0, 1.0));
    vec3 ambient = mix(skyAmbient, GROUND_BOUNCE, clamp(-up, 0.0, 1.0));

    vec3 colour = albedo * (max(dot(N, L), 0.0) * intensity + ambient);

    float dist = length(vWorldPos);

    // Light lost through the water column above; past the ocean surface's reach the water's own colour fills in.
    if (frame.planet.w > 0.0)
    {
        vec3 through = exp(-ABSORPTION * max(frame.planet.w - radius, 0.0));
        colour *= through;

        if (dist > frame.water.w)
            colour += frame.water.rgb * (1.0 - through);
    }

    // Tonemap + gamma (same as ocean).
    colour = pow(acesFit(colour), vec3(1.0 / 2.2));

    // Fog in display space so the far terrain equals the clear colour, like the ocean.
    float f = 1.0 - exp(-dist * frame.skyHorizon.w);
    colour = mix(colour, frame.skyHorizon.rgb, f);

    outColor = vec4(colour, 1.0);
}
