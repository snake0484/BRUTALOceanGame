#version 450
// Voxel water surface vertex shader: chunk-local position + push-constant origin.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // xyz world, w = time
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
} frame;

layout(push_constant) uniform Chunk {
    vec4 origin; // xyz chunk world origin
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in float inThickness;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out float vThickness;

void main()
{
    vec3 world = pc.origin.xyz + inPosition;
    vWorldPos = world;
    vNormal = inNormal;
    vThickness = inThickness;
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
