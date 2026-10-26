#version 450
// Debug shapes: unit box / UV sphere generated from gl_VertexIndex, one SSBO instance per shape.
// Triangles wind CCW seen from outside (cross(b - a, c - a) points outward).

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;   // xyz world
    vec4 sunDir;      // xyz toward sun, w = intensity
    vec4 skyZenith;
    vec4 skyHorizon;  // w = fog density
} frame;

struct Instance {
    mat4 model;   // scale * rotation * translation, unit shape -> world
    vec4 colour;  // rgb linear albedo
};

layout(set = 0, binding = 1, std430) readonly buffer Instances {
    Instance instances[];
};

layout(push_constant) uniform Shape {
    uint kind; // 0 = box, 1 = sphere
} pc;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec3 vLocal;       // box: metres from centre
layout(location = 3) flat out vec3 vHalf;   // box: half extents, metres
layout(location = 4) flat out vec4 vColour; // w = 1 for box edges

const float PI = 3.14159265;
const uint SEGMENTS = 16u;
const uint RINGS = 12u;

// Per face: normal, u, v with cross(u, v) = normal.
const vec3 FACE_N[6] = vec3[6](vec3(1, 0, 0), vec3(-1, 0, 0), vec3(0, 1, 0), vec3(0, -1, 0), vec3(0, 0, 1), vec3(0, 0, -1));
const vec3 FACE_U[6] = vec3[6](vec3(0, 1, 0), vec3(0, 0, 1), vec3(0, 0, 1), vec3(1, 0, 0), vec3(1, 0, 0), vec3(0, 1, 0));
const vec3 FACE_V[6] = vec3[6](vec3(0, 0, 1), vec3(0, 1, 0), vec3(1, 0, 0), vec3(0, 0, 1), vec3(0, 1, 0), vec3(1, 0, 0));

// Quad corners (a, b, c), (a, c, d) in (u, v) order: cross(b - a, c - a) = cross(u, v).
const vec2 CORNER[6] = vec2[6](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));

vec3 spherePoint(uint seg, uint ring)
{
    float theta = float(ring) / float(RINGS) * PI;
    float phi = float(seg) / float(SEGMENTS) * 2.0 * PI;
    float s = sin(theta);
    return vec3(s * cos(phi), cos(theta), s * sin(phi));
}

void main()
{
    Instance inst = instances[gl_InstanceIndex];
    vec2 corner = CORNER[gl_VertexIndex % 6];
    vec3 local;
    vec3 normal;

    if (pc.kind == 0u)
    {
        uint face = uint(gl_VertexIndex) / 6u;
        vec2 st = corner * 2.0 - 1.0;
        local = FACE_N[face] + FACE_U[face] * st.x + FACE_V[face] * st.y;
        normal = FACE_N[face];
    }
    else
    {
        // u = +phi, v = +theta (downwards); cross is outward. Pole quads collapse one triangle each.
        uint quad = uint(gl_VertexIndex) / 6u;
        uint seg = quad % SEGMENTS + uint(corner.x);
        uint ring = quad / SEGMENTS + uint(corner.y);
        local = spherePoint(seg, ring);
        normal = local;
    }

    vec3 halfExtents = vec3(length(inst.model[0].xyz), length(inst.model[1].xyz), length(inst.model[2].xyz));
    vec4 world = inst.model * vec4(local, 1.0);
    vWorldPos = world.xyz;
    vNormal = mat3(inst.model) * (normal / (halfExtents * halfExtents)); // inverse-transpose of R * S
    vLocal = local * halfExtents;
    vHalf = halfExtents;
    vColour = vec4(inst.colour.rgb, pc.kind == 0u ? 1.0 : 0.0);
    gl_Position = frame.viewProj * world;
}
