// The ocean surface's FFT detail (ocean.frag): cascade partials. The shading itself (sky, body, foam lace, shadeWater)
// is water_shading.glsl. Include after ocean_common.glsl.

#include "water_shading.glsl"

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
