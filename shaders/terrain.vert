#version 450
// Terrain chunk vertex shader: chunk-local position + push-constant origin.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // xyz world
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
} frame;

layout(push_constant) uniform Chunk {
    vec4 origin; // xyz chunk world origin
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in uint inMaterial;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) flat out uint vMaterial;

void main()
{
    vec3 world = pc.origin.xyz + inPosition;
    vWorldPos = world;
    vNormal = inNormal;
    vMaterial = inMaterial;
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
