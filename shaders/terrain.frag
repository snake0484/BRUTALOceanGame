#version 450
// Terrain fragment shader: material palette, Lambert sun + hemispheric sky about the planet's radial up, underwater
// absorption below the sea sphere in the generated sea mask (and the water's colour where no ocean surface covers it;
// ground dug below sea level inland stays dry: planets.md "pit rule"), ocean-matched tonemap/fog.
// From altitude (space.x -> 1) it turns into the impostor's look (body.frag): the star alone on the palette, the sea in
// the impostor's colours with its glint, under the same atmosphere, so the impostor-to-octree switch shows no change.
#extension GL_GOOGLE_include_directive : require

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 planet;      // xyz = planet centre relative to the camera, w = sea radius (0 = no sea)
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;   // w = the star's intensity in the space look
    vec4 skyHorizon;  // w = fog density
    vec4 water;       // rgb = seabed fade colour, w = distance past which it applies (no ocean surface there)
    vec4 space;       // x = space look weight (0 ground, 1 orbit), y = atmosphere top radius (0 = none), z = scale height, w = its base radius
    vec4 sea;         // x = the camera's height above the sea sphere (m), differenced in double on the CPU
} frame;

#include "atmosphere.glsl"

float sunVisibility(vec3 p)
{
    return 1.0;
}

layout(location = 0) in vec3 vWorldPos; // camera-relative, camera frame axes
layout(location = 1) in vec3 vNormal;
layout(location = 2) flat in uint vMaterial;

layout(location = 0) out vec4 outColor;

const vec3 ABSORPTION = vec3(0.30, 0.09, 0.06);  // per metre below sea level
const vec3 GROUND_BOUNCE = vec3(0.10, 0.09, 0.08);

// The impostor's (PlanetImpostor, body.frag): sea albedo by depth, night-side ambient, sun glint.
const vec3 SEA_SHALLOW = vec3(0.04, 0.12, 0.13);
const vec3 SEA_DEEP = vec3(0.010, 0.030, 0.060);
const float SEA_RAMP = 200.0;
const float SPACE_AMBIENT = 0.004;
const float GLINT = 0.5;

// PackedTerrainVertex.SeaColumn / MaterialMask: the vertex is in the generated sea mask; the rest is the palette index.
const uint SEA_COLUMN = 0x8000u;
const uint MATERIAL_MASK = 0x7FFFu;

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
    vec3 albedo = PALETTE[min(vMaterial & MATERIAL_MASK, 4u)];

    // Hemispheric ambient about the radial up: zenith above, horizon at the side, dim bounce below.
    vec3 fromCentre = vWorldPos - frame.planet.xyz;
    float radius = length(fromCentre);
    vec3 upDir = fromCentre / radius;
    float up = dot(N, upDir);
    vec3 skyAmbient = mix(frame.skyHorizon.rgb, frame.skyZenith.rgb, clamp(up, 0.0, 1.0));
    vec3 ambient = mix(skyAmbient, GROUND_BOUNCE, clamp(-up, 0.0, 1.0));

    float space = frame.space.x;
    float star = frame.skyZenith.w;
    float lambert = max(dot(N, L), 0.0);
    vec3 colour = albedo * mix(lambert * intensity + ambient, vec3(lambert * star + SPACE_AMBIENT), space);

    float dist = length(vWorldPos);
    vec3 dir = vWorldPos / dist;

    // Light lost through the water column above; past the ocean surface's reach the water's own colour fills in.
    // A float radius at 1e6 m steps by 6-12 cm, which bands the absorption on a shallow beach: the depth is the camera's
    // own height above the sea plus |c + p| - |c| in its stable form, (2 c.p + p.p) / (|c + p| + |c|), c the camera from
    // the centre and p the fragment from the camera.
    if (frame.planet.w > 0.0 && (vMaterial & SEA_COLUMN) != 0u)
    {
        vec3 c = -frame.planet.xyz;
        float rise = (2.0 * dot(c, vWorldPos) + dot(vWorldPos, vWorldPos)) / (radius + length(c));
        float depth = max(-(frame.sea.x + rise), 0.0);
        vec3 through = exp(-ABSORPTION * depth);
        colour *= through;

        if (dist > frame.water.w)
        {
            float day = dot(upDir, L);
            vec3 sea = mix(SEA_SHALLOW, SEA_DEEP, smoothstep(0.0, SEA_RAMP, depth)) * (max(day, 0.0) * star + SPACE_AMBIENT);
            colour += mix(frame.water.rgb, sea, space) * (1.0 - through);
            colour += space * (1.0 - through.b) * GLINT * star * pow(max(dot(upDir, normalize(L - dir)), 0.0), 200.0) * step(0.0, day);
        }
    }

    // The air between the camera and the ground, as the impostor's.
    float top = frame.space.y;

    if (space > 0.0 && top > 0.0)
    {
        vec3 o = -frame.planet.xyz;
        float bo = dot(o, dir);
        float q = length(o - bo * dir);

        if (q < top)
        {
            float a0 = max(-bo - sqrt((top - q) * (top + q)), 0.0);

            if (dist > a0)
            {
                vec3 transmittance;
                vec3 inscatter = atmosphereScatter(o, dir, a0, dist, frame.space.w, frame.space.z, L, star, transmittance);
                colour = mix(colour, colour * transmittance + inscatter, space);
            }
        }
    }

    // Tonemap + gamma (same as ocean).
    colour = pow(acesFit(colour), vec3(1.0 / 2.2));

    // Fog in display space so the far terrain equals the clear colour, like the ocean.
    float f = 1.0 - exp(-dist * frame.skyHorizon.w);
    colour = mix(colour, frame.skyHorizon.rgb, f);

    outColor = vec4(colour, 1.0);
}
