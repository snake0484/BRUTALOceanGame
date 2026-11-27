#version 450
// Designer hull bricks: decodes PackedTerrainVertex (16-bit fixed-point brick-local position at 0.25 m cells, octahedral
// normal). Camera-relative: the push constant's origin is the brick's minus the camera's, taken in double.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;
    vec4 palette[32]; // rgb linear albedo per plan material index
} frame;

layout(push_constant) uniform Brick {
    vec4 originScale; // xyz camera-relative brick origin, w = cell size / PackedTerrainVertex.StepsPerVoxel
    vec4 clipPlane;   // cutaway, used by hull.frag
    vec4 ghost;       // interior view, glazing pass and projector cells, used by hull.frag
    vec4 cells;       // projector cell grid, used by hull.frag
    vec4 turn0;       // the moving part's turn: rows of R in xyz, w the offset d (world = R rest + d, camera-relative)
    vec4 turn1;
    vec4 turn2;
} brick;

// PackedTerrainVertex.OffsetVoxels * StepsPerVoxel.
const float POSITION_OFFSET = 16384.0;

layout(location = 0) in uvec4 inPositionMaterial;
layout(location = 1) in vec2 inNormalOct;

layout(location = 0) out vec3 vRelPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) flat out uint vMaterial;
layout(location = 3) out vec3 vRestPos;   // camera-relative position at rest: paint, decals and finishes are worked out here

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
    vec3 local = (vec3(inPositionMaterial.xyz) - POSITION_OFFSET) * brick.originScale.w;
    vec3 rest = brick.originScale.xyz + local;
    mat3 turn = transpose(mat3(brick.turn0.xyz, brick.turn1.xyz, brick.turn2.xyz));
    vec3 world = turn * rest + vec3(brick.turn0.w, brick.turn1.w, brick.turn2.w);
    vRelPos = world;
    vRestPos = rest;
    vNormal = turn * decodeOctahedral(inNormalOct);
    vMaterial = inPositionMaterial.w;
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
