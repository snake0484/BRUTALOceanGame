// Per-draw instance records (InstanceBuffer, review C9), shared by terrain and hull draws. A draw passes its record's
// index as firstInstance; camera-relative position = relOrigin + rotate(rotation, local * scale), in the camera frame's axes.
// Define INSTANCES_WRITE before including to write records (terrain_cull.comp).

#ifdef INSTANCES_WRITE
#define INSTANCES_ACCESS
#else
#define INSTANCES_ACCESS readonly
#endif

struct Instance {
    vec3 relOrigin;
    float scale;
    vec4 rotation; // quaternion xyzw, the draw's axes to the camera frame's
};

layout(set = 1, binding = 0, std430) INSTANCES_ACCESS buffer Instances {
    Instance instances[];
};

vec3 rotateByQuat(vec4 q, vec3 v)
{
    vec3 t = 2.0 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}
