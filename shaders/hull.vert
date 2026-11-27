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
    vec4 ghost;       // x = opacity of the see-through interior view (0: solid), used by hull.frag
} brick;

// PackedTerrainVertex.OffsetVoxels * StepsPerVoxel.
const float POSITION_OFFSET = 16384.0;

layout(location = 0) in uvec4 inPositionMaterial;
layout(location = 1) in vec2 inNormalOct;

layout(location = 0) out vec3 vRelPos;
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
    vec3 local = (vec3(inPositionMaterial.xyz) - POSITION_OFFSET) * brick.originScale.w;
    vec3 world = brick.originScale.xyz + local;
    vRelPos = world;
    vNormal = decodeOctahedral(inNormalOct);
    vMaterial = inPositionMaterial.w;
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
