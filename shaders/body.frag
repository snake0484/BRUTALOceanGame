#version 450
// One body on the detail ladder (planets.md "Detail ladder"), in the body's axes, centred on it:
// - a disc (stage 2): the sphere at its base radius (sea level, or the datum), albedo from the impostor at the mip the
//   pixel footprint asks for, the edge antialiased by its distance in pixels;
// - the impostor (stage 3): the baked cube-map heightfield marched with steps bounded by the steepest baked slope, so it
//   never steps over ground; mountains show on the limb. Relief fades in with the disc's size (relief.z);
// - the atmosphere shell over both, and its rim around them (atmosphere.glsl, as terrain.frag applies it to the octree);
// - the star: an emissive disc with limb darkening.
// Lit by the star; eclipses by up to two other bodies (ray-sphere, penumbra from the sun's size). Output is
// premultiplied (blend One, OneMinusSrcAlpha), tonemapped like the terrain; depth is written where there is ground.
// Ground and rim have their own opacity (shape.z, lod.z): over the focus body's octree the ground fades out across the
// switch band while the rim stays, then the ground returns behind the octree (depth scale < 1) to fill its gaps.
#extension GL_GOOGLE_include_directive : require
#include "body.glsl"
#include "atmosphere.glsl"

layout(set = 1, binding = 0) uniform samplerCube heightMap;
layout(set = 1, binding = 1) uniform samplerCube albedoMap;

layout(location = 0) out vec4 outColor;

const int MAX_STEPS = 96;
const int REFINE = 5;
const float AMBIENT = 0.004;
const float GLINT = 0.5;

vec3 acesFit(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

vec3 display(vec3 c)
{
    return pow(acesFit(c), vec3(1.0 / 2.2));
}

float depthAt(float t, vec3 dirCam, float scale)
{
    return clamp(frame.right.w / (t * dot(dirCam, frame.forward.xyz)) * scale, 0.0, 1.0);
}

float heightAt(Body b, vec3 n, float lod)
{
    return b.relief.x + textureLod(heightMap, n, lod).r * b.relief.y;
}

// Height about R as drawn: the relief scaled about the base by relief.z.
float visible(Body b, float h)
{
    return b.radii.y + (h - b.radii.y) * b.relief.z;
}

// Signed distance (m) of p above the drawn surface.
float above(Body b, vec3 p, float lod)
{
    float r = length(p);
    return r - (b.radii.x + visible(b, heightAt(b, p / r, lod)));
}

vec3 reliefNormal(Body b, vec3 n, float lod)
{
    vec3 t1 = normalize(cross(n, abs(n.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
    vec3 t2 = cross(n, t1);
    float delta = b.lod.x * exp2(lod);
    float hx = heightAt(b, normalize(n + t1 * delta), lod) - heightAt(b, normalize(n - t1 * delta), lod);
    float hy = heightAt(b, normalize(n + t2 * delta), lod) - heightAt(b, normalize(n - t2 * delta), lod);
    return normalize(n - (t1 * hx + t2 * hy) * (b.relief.z / (2.0 * delta * b.radii.x)));
}

// Visible share of the star from p past one occluding sphere: a penumbra from the two discs' angular sizes.
float eclipse(vec3 p, vec3 L, vec4 occluder, float sunRadius)
{
    if (occluder.w <= 0.0)
        return 1.0;

    vec3 to = occluder.xyz - p;
    float along = dot(to, L);

    if (along <= 0.0)
        return 1.0;

    float dist = length(to);
    float separation = atan(length(to - along * L), along);
    float size = asin(min(occluder.w / dist, 1.0));
    float s = max(sunRadius, 1e-5);
    float lit = smoothstep(size - s, size + s, separation);
    return size < s ? max(lit, 1.0 - (size * size) / (s * s)) : lit;
}

float sunlight(Body b, vec3 p)
{
    return eclipse(p, b.sun.xyz, b.occluder0, b.emission.w) * eclipse(p, b.sun.xyz, b.occluder1, b.emission.w);
}

float sunVisibility(vec3 p)
{
    return sunlight(bodies[pc.body], p);
}

void star(Body b, vec3 o, vec3 dir, vec3 dirCam)
{
    float radius = b.radii.x;
    float d = length(o);
    float bo = dot(o, dir);
    float q = length(o - bo * dir);
    float edge = (asin(min(radius / d, 1.0)) - asin(min(q / d, 1.0))) / frame.forward.w;
    float coverage = clamp(edge + 0.5, 0.0, 1.0) * b.shape.z;

    if (coverage <= 0.0)
        discard;

    float mu = sqrt(max(1.0 - (q / radius) * (q / radius), 0.0));
    outColor = vec4(display(b.emission.rgb * (0.4 + 0.6 * mu)), 1.0) * coverage;
    float t = max(-bo - sqrt(max((radius - q) * (radius + q), 0.0)), 0.0);
    gl_FragDepth = depthAt(max(t, 1.0), dirCam, b.shape.w);
}

void main()
{
    Body b = bodies[pc.body];
    vec2 ndc = gl_FragCoord.xy * frame.viewport.zw * 2.0 - 1.0;
    vec3 dirCam = normalize(frame.forward.xyz + ndc.x * frame.right.xyz - ndc.y * frame.up.xyz);
    vec3 dir = rotateByQuat(b.rotation, dirCam);
    vec3 o = rotateByQuat(b.rotation, -b.centre.xyz);

    if (b.shape.x > 0.5)
    {
        star(b, o, dir, dirCam);
        return;
    }

    float pixel = frame.forward.w;
    float R = b.radii.x;
    float base = R + b.radii.y;
    float k = b.relief.z;
    float d = length(o);
    float bo = dot(o, dir);
    float q = length(o - bo * dir);
    bool ground = false;
    float coverage = 1.0;
    float t = 0.0;
    float lod = b.lod.y;
    vec3 n = vec3(0.0);

    if (k <= 0.0)
    {
        // Disc: the base sphere; rays up to half a pixel past its edge shade its limb, weighted by coverage.
        float edge = (asin(min(base / d, 1.0)) - asin(min(q / d, 1.0))) / pixel;
        coverage = clamp(edge + 0.5, 0.0, 1.0);

        if (coverage > 0.0)
        {
            ground = true;
            t = max(q < base ? -bo - sqrt((base - q) * (base + q)) : -bo, 0.0);
            n = normalize(o + t * dir);
            lod = clamp(log2(max(max(t, 1.0) * pixel / (b.lod.x * R), 1.0)), 0.0, b.lod.y);
        }
    }
    else
    {
        float rIn = R + visible(b, b.relief.x);
        float rOut = R + visible(b, b.relief.x + b.relief.y);

        if (q < rOut && bo < sqrt((rOut - q) * (rOut + q)))
        {
            float s = sqrt((rOut - q) * (rOut + q));
            float t0 = max(-bo - s, 0.0);
            float t1 = -bo + s;
            bool inner = q < rIn && bo < 0.0;

            // Ground is certain once the ray reaches the inner sphere: the march ends there at the latest.
            if (inner)
                t1 = min(t1, -bo - sqrt((rIn - q) * (rIn + q)));

            lod = clamp(log2(max(max(t0, 1.0) * pixel / (b.lod.x * R), 1.0)), 0.0, b.lod.y);
            float slope = b.relief.w * k + 1e-3;
            float tolerance = 0.25 * pixel;
            float tPrev = t0;
            t = t0;

            for (int i = 0; i < MAX_STEPS; i++)
            {
                vec3 p = o + t * dir;
                float r = length(p);
                float f = r - (R + visible(b, heightAt(b, p / r, lod)));

                if (f <= tolerance * t)
                {
                    ground = true;
                    break;
                }

                float rho = dot(dir, p) / r;
                tPrev = t;
                t += max(f / (max(-rho, 0.0) + slope), 0.5 * pixel * t);

                if (t >= t1)
                {
                    t = t1;
                    ground = inner;
                    break;
                }
            }

            ground = ground || inner;

            if (ground)
            {
                for (int i = 0; i < REFINE; i++)
                {
                    float tm = 0.5 * (tPrev + t);

                    if (above(b, o + tm * dir, lod) > 0.0)
                        tPrev = tm;
                    else
                        t = tm;
                }

                n = normalize(o + t * dir);
            }
        }
    }

    vec3 colour = vec3(0.0);
    vec3 L = b.sun.xyz;

    if (ground)
    {
        vec3 p = n * (R + visible(b, heightAt(b, n, lod)));
        vec3 normal = k > 0.0 ? reliefNormal(b, n, lod) : n;
        vec4 albedo = textureLod(albedoMap, n, lod);
        float light = b.sun.w * sunlight(b, p);
        vec3 halfway = normalize(L - dir);
        colour = albedo.rgb * (max(dot(normal, L), 0.0) * light + AMBIENT);
        colour += albedo.a * GLINT * light * pow(max(dot(n, halfway), 0.0), 200.0) * step(0.0, dot(n, L));
    }

    float top = b.radii.z;
    vec4 result = vec4(display(colour), 1.0) * coverage;

    if (top > 0.0 && q < top)
    {
        float s = sqrt((top - q) * (top + q));
        float a0 = max(-bo - s, 0.0);
        float a1 = ground ? t : -bo + s;

        if (a1 > a0)
        {
            vec3 transmittance;
            vec3 inscatter = atmosphereScatter(o, dir, a0, a1, base, b.radii.w, L, b.sun.w, transmittance);

            if (ground)
                result = vec4(display(colour * transmittance + inscatter), 1.0) * coverage;
            else
                result = vec4(display(inscatter), 1.0 - dot(transmittance, vec3(1.0 / 3.0)));
        }
    }

    result *= ground ? b.shape.z : b.lod.z;

    if (result.a <= 1.0 / 512.0)
        discard;

    outColor = result;
    gl_FragDepth = ground ? depthAt(max(t, 1.0), dirCam, b.shape.w) : 0.0;
}
