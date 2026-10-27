#version 450
// Voxel water surface vertex shader: chunk-local position + push-constant origin, and the wind's reach at this corner
// for the direction it blows now (the mesher's eight directions, 45 degrees apart from +X).

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // xyz world, w = time
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
    vec4 rain;        // x = intensity 0..1, y = its time (s, looped)
    vec4 wind;        // xy = 10 m wind (m/s, the way it blows toward)
} frame;

layout(push_constant) uniform Chunk {
    vec4 origin; // xyz chunk world origin
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in float inThickness;
layout(location = 3) in vec4 inShelter0; // wind reach for directions 0..3, 0 sheltered .. 1 open
layout(location = 4) in vec4 inShelter1; // directions 4..7

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out float vThickness;
layout(location = 3) out float vExposure;

const float INV_45_DEGREES = 4.0 / 3.14159265;

// The wind's reach for the way it blows, between the two nearest of the eight directions.
float windExposure()
{
    vec2 w = frame.wind.xy;
    if (dot(w, w) < 1e-4)
        return 0.0;

    float s[8] = float[8](inShelter0.x, inShelter0.y, inShelter0.z, inShelter0.w, inShelter1.x, inShelter1.y, inShelter1.z, inShelter1.w);
    float a = mod(atan(w.y, w.x) * INV_45_DEGREES + 8.0, 8.0);
    int k0 = int(floor(a));
    int k1 = (k0 + 1) & 7;
    return mix(s[k0], s[k1], a - float(k0));
}

void main()
{
    vec3 world = pc.origin.xyz + inPosition;
    vWorldPos = world;
    vNormal = inNormal;
    vThickness = inThickness;
    vExposure = windExposure();
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
