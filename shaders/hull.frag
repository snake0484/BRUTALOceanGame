#version 450
// Designer hull: Lambert sun + hemispheric sky + small specular, material colour from the palette, ACES + gamma.
// Dual contouring shares one vertex per cell, so a vertex on a crease carries the mean of both faces' normals; where the
// interpolated normal leans more than CREASE_COS from the triangle's own normal the face normal is used: flat faces and
// hard edges stay hard, curved surfaces stay smooth.
// Paint and decals: projector boxes listed per brick (push z = first, w = count). Inside a box and facing its axis, a
// paint box lays its pattern (the strongest paint wins, so overlapping pieces of one band do not double up) and a decal
// lays its image over that, later decals on top.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 sunDir;
    vec4 skyZenith;
    vec4 skyHorizon;
    vec4 palette[32]; // rgb linear albedo, a = opacity (below 1: glazing, drawn in the blended glazing pass)
} frame;

layout(push_constant) uniform Brick {
    vec4 originScale;
    vec4 clipPlane;   // camera-relative: fragments with dot(xyz, pos) > w are cut away (cutaway view)
    vec4 ghost;       // x = opacity of the see-through interior view (0: solid); y = 1: the glazing pass; z, w = projector list
} brick;

struct Projector {
    vec4 centre;  // xyz camera-relative, w kind (1 paint, 2 decal)
    vec4 u;       // xyz axis / half, w half (metres)
    vec4 v;
    vec4 n;       // w: paint's distance along the stroke at the centre
    vec4 colour;  // rgb linear, a opacity
    vec4 params;  // paint: x spacing, y line width, z pattern; decal: w layer
};

layout(set = 0, binding = 1, std430) readonly buffer Projectors { Projector projectors[]; };
layout(set = 0, binding = 2, std430) readonly buffer Lists { uint lists[]; };
layout(set = 0, binding = 3) uniform sampler2DArray decals;

layout(location = 0) in vec3 vRelPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) flat in uint vMaterial;

layout(location = 0) out vec4 outColor;

const vec3 GROUND_BOUNCE = vec3(0.10, 0.09, 0.08);
const float CREASE_COS = 0.85;

vec3 acesFit(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

// Distance (metres) to the nearer of the two diagonal line families crossing at spacing s.
float lineCross(float along, float across, float s)
{
    float d1 = abs(fract((along + across) / s + 0.5) - 0.5);
    float d2 = abs(fract((along - across) / s + 0.5) - 0.5);
    return min(d1, d2) * s * 0.70710678;
}

// The surface's albedo with this brick's paint and decals laid on. Derivatives are taken before the loop (it diverges).
vec3 decorate(vec3 albedo, vec3 rel, vec3 N)
{
    int first = int(brick.ghost.z);
    int count = int(brick.ghost.w);

    if (count == 0)
        return albedo;

    vec3 dx = dFdx(rel);
    vec3 dy = dFdy(rel);
    float pixel = max(length(dx), length(dy));
    float paint = 0.0;
    vec3 paintColour = vec3(0.0);
    vec4 decal = vec4(0.0);

    for (int i = 0; i < count; i++)
    {
        Projector p = projectors[lists[first + i]];
        vec3 d = rel - p.centre.xyz;
        vec3 l = vec3(dot(d, p.u.xyz), dot(d, p.v.xyz), dot(d, p.n.xyz));

        if (any(greaterThan(abs(l), vec3(1.0))))
            continue;

        float facing = dot(N, normalize(p.n.xyz));

        if (facing < 0.05)
            continue;

        float fade = smoothstep(0.05, 0.3, facing);

        if (p.centre.w < 1.5)
        {
            float along = p.n.w + l.x * p.u.w;
            float across = l.y * p.v.w;
            float half_ = p.params.y * 0.5;
            float cover = 1.0 - smoothstep(half_ - pixel, half_ + pixel, lineCross(along, across, p.params.x));
            cover *= 1.0 - smoothstep(0.94, 1.0, abs(l.y));
            float a = cover * p.colour.a * fade;

            if (a > paint)
            {
                paint = a;
                paintColour = p.colour.rgb;
            }
        }
        else
        {
            vec2 uv = vec2(l.x, -l.y) * 0.5 + 0.5;
            vec2 gx = vec2(dot(dx, p.u.xyz), -dot(dx, p.v.xyz)) * 0.5;
            vec2 gy = vec2(dot(dy, p.u.xyz), -dot(dy, p.v.xyz)) * 0.5;
            vec4 texel = textureGrad(decals, vec3(uv, p.params.w), gx, gy);
            float a = texel.a * p.colour.a * fade;
            decal = vec4(texel.rgb * p.colour.rgb * a + decal.rgb * (1.0 - a), a + decal.a * (1.0 - a));
        }
    }

    albedo = mix(albedo, paintColour, paint);
    return albedo * (1.0 - decal.a) + decal.rgb;
}

void main()
{
    if (dot(brick.clipPlane.xyz, vRelPos) > brick.clipPlane.w)
        discard;

    // Interior view: every face, front or back, faint and lit by the view, stronger where the surface turns edge on, so
    // walls, rooms and hallways read as outlines one can see through.
    if (brick.ghost.x > 0.0)
    {
        vec3 g = normalize(cross(dFdx(vRelPos), dFdy(vRelPos)));
        float facing = abs(dot(g, normalize(-vRelPos)));
        vec3 tint = frame.palette[min(vMaterial, 31u)].rgb * (0.35 + 0.65 * facing) + vec3(0.12, 0.16, 0.2);
        float alpha = brick.ghost.x * (0.45 + 1.4 * pow(1.0 - facing, 3.0));
        outColor = vec4(pow(acesFit(tint), vec3(1.0 / 2.2)), clamp(alpha, 0.0, 0.85));
        return;
    }

    // Glazing: the opaque pass leaves it out; the glazing pass draws only it, tinted, with a glint where it turns edge on.
    float opacity = frame.palette[min(vMaterial, 31u)].a;

    if (brick.ghost.y > 0.5)
    {
        if (opacity >= 0.999)
            discard;

        vec3 gn = normalize(cross(dFdx(vRelPos), dFdy(vRelPos)));
        vec3 gv = normalize(-vRelPos);
        float facing = abs(dot(gn, gv));
        vec3 gl = frame.sunDir.xyz;
        float glint = pow(max(dot(reflect(-gv, dot(gn, gv) < 0.0 ? -gn : gn), gl), 0.0), 64.0) * frame.sunDir.w;
        vec3 tint = frame.palette[min(vMaterial, 31u)].rgb * (0.4 + 0.6 * facing) + glint;
        float alpha = opacity + (1.0 - opacity) * pow(1.0 - facing, 4.0) * 0.6 + glint * 0.5;
        outColor = vec4(pow(acesFit(tint), vec3(1.0 / 2.2)), clamp(alpha, 0.0, 0.95));
        return;
    }

    if (opacity < 0.999)
        discard;

    // Back faces show only through a cutaway: there they cap the cut solid, flat and darkened in the material's colour.
    if (!gl_FrontFacing)
    {
        if (brick.clipPlane.w > 1e30)
            discard;

        vec3 cap = frame.palette[min(vMaterial, 31u)].rgb * 0.45;
        outColor = vec4(pow(acesFit(cap), vec3(1.0 / 2.2)), 1.0);
        return;
    }

    vec3 L = frame.sunDir.xyz;
    vec3 V = normalize(-vRelPos);
    vec3 face = normalize(cross(dFdx(vRelPos), dFdy(vRelPos)));
    face = dot(face, V) < 0.0 ? -face : face;
    vec3 N = normalize(vNormal);
    N = dot(N, face) < CREASE_COS ? face : N;
    float intensity = frame.sunDir.w;
    vec3 albedo = decorate(frame.palette[min(vMaterial, 31u)].rgb, vRelPos, N);

    float up = N.y;
    vec3 skyAmbient = mix(frame.skyHorizon.rgb, frame.skyZenith.rgb, clamp(up, 0.0, 1.0));
    vec3 ambient = mix(skyAmbient, GROUND_BOUNCE, clamp(-up, 0.0, 1.0));

    float ndl = max(dot(N, L), 0.0);
    float spec = ndl > 0.0 ? pow(max(dot(N, normalize(L + V)), 0.0), 48.0) * 0.15 : 0.0;
    vec3 colour = albedo * (ndl * intensity + ambient) + spec * intensity;
    outColor = vec4(pow(acesFit(colour), vec3(1.0 / 2.2)), 1.0);
}
