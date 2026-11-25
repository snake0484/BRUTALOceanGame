#version 450
// Terrain chunk vertex shader: decodes PackedTerrainVertex (16-bit fixed-point chunk-local position, octahedral normal)
// and places it with the chunk's instance record. Camera-relative: the record's origin is the chunk's minus the camera's
// (terrain_cull.comp, in whole chunks plus the camera's remainder) and viewProj has no translation.
#extension GL_GOOGLE_include_directive : require
#include "instance.glsl"

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // (0, camera height above sea level, 0): fragment positions are camera-relative horizontally
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
} frame;

// PackedTerrainVertex.OffsetVoxels * StepsPerVoxel.
const float POSITION_OFFSET = 16384.0;

layout(location = 0) in uvec4 inPositionMaterial;
layout(location = 1) in vec2 inNormalOct;

layout(location = 0) out vec3 vWorldPos; // camera-relative x, z; height above sea level y
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
    // instance.scale = voxel size / PackedTerrainVertex.StepsPerVoxel.
    Instance chunk = instances[gl_InstanceIndex];
    vec3 local = (vec3(inPositionMaterial.xyz) - POSITION_OFFSET) * chunk.scale;
    vec3 world = chunk.relOrigin + rotateByQuat(chunk.rotation, local);
    vWorldPos = world + vec3(0.0, frame.cameraPos.y, 0.0);
    vNormal = rotateByQuat(chunk.rotation, decodeOctahedral(inNormalOct));
    vMaterial = inPositionMaterial.w;
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
