#version 450
// Terrain chunk vertex shader: decodes PackedTerrainVertex (16-bit fixed-point chunk-local position, octahedral normal)
// and adds the push-constant origin.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // xyz world
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
} frame;

layout(push_constant) uniform Chunk {
    vec4 origin; // xyz chunk world origin, w = voxel size / PackedTerrainVertex.StepsPerVoxel
} pc;

// PackedTerrainVertex.OffsetVoxels * StepsPerVoxel.
const float POSITION_OFFSET = 16384.0;

layout(location = 0) in uvec4 inPositionMaterial;
layout(location = 1) in vec2 inNormalOct;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) flat out uint vMaterial;

vec3 decodeOctahedral(vec2 e)
{
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    float t = max(-n.z, 0.0);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

void main()
{
    // Integer steps minus a power-of-two offset times a power-of-two step: exact, so seams match the float mesh.
    vec3 local = (vec3(inPositionMaterial.xyz) - POSITION_OFFSET) * pc.origin.w;
    vec3 world = pc.origin.xyz + local;
    vWorldPos = world;
    vNormal = decodeOctahedral(inNormalOct);
    vMaterial = inPositionMaterial.w;
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
