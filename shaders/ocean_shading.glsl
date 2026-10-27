// The ocean surface's FFT detail (ocean.frag, ocean_lip.frag): cascade partials, flow-mapped over a breaking wave, and the
// bathymetry sample. The shading itself (sky, body, foam lace, shadeWater) is water_shading.glsl, shared with voxel water.
// Include after ocean_common.glsl.

#include "water_shading.glsl"

// Full bathymetry sample: r = depth (m), g = sim level offset, b = sea-zone flag, a = flow whitewater.
vec4 waterMap(vec2 xz)
{
    vec2 uv = (xz - frame.depthRect.xy) / frame.depthRect.z;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
        return vec4(frame.depthRect.w, 0.0, 0.0, 0.0);
    return depthMapAt(uv);
}

// FFT cascade partials (hx, hz, λDxx, λDzz) and λDxz at rest position restXZ, faded where the pixel footprint cannot
// resolve a cascade, and per cascade over the beach and to the wind sea there (shoreDamp, OceanPoint.shore);
// persistent foam from the same cascades, faded alike.
vec4 cascadePartials(vec2 restXZ, float footprint, vec3 shoreDamp, out float pxz, out float persistent)
{
    vec4 p = vec4(0.0);
    pxz = 0.0;
    persistent = 0.0;
    int cascades = min(int(frame.cascadeL.w), 3);
    for (int i = 0; i < cascades; ++i)
    {
        float Li = frame.cascadeL[i];
        float wf = clamp(1.0 - (footprint / (Li / CASCADE_N) - 1.0) / 3.0, 0.0, 1.0);
        if (wf <= 0.0)
            continue;
        vec3 uv = vec3(restXZ / Li, float(i));
        float w = wf * shoreDamp[i];
        p += w * texture(derivMap, uv);
        pxz += w * texture(dispMap, uv).w;
        persistent += w * texture(foamMap, uv).r;
    }

    return p;
}

vec4 flowingPartials(vec2 restXZ, vec2 dir, float speed, float footprint, vec3 shoreDamp, out float pxz, out float persistent)
{
    if (speed < 0.01)
        return cascadePartials(restXZ, footprint, shoreDamp, pxz, persistent);

    vec3 ph = flowPhases();
    vec2 slide = dir * (speed * FLOW_PERIOD);
    float pxz0, pxz1, per0, per1;
    vec4 p0 = cascadePartials(restXZ - slide * ph.x, footprint, shoreDamp, pxz0, per0);
    vec4 p1 = cascadePartials(restXZ - slide * ph.y, footprint, shoreDamp, pxz1, per1);
    pxz = mix(pxz1, pxz0, ph.z);
    persistent = mix(per1, per0, ph.z);
    return mix(p1, p0, ph.z);
}
