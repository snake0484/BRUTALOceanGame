#version 450
// Designer hull: Lambert sun + hemispheric sky + specular, ACES + gamma.
// Dual contouring shares one vertex per cell, so a vertex on a crease carries the mean of both faces' normals; where the
// interpolated normal leans more than CREASE_COS from the triangle's own normal the face normal is used: flat faces and
// hard edges stay hard, curved surfaces stay smooth.
// Paint: the plan has paint slots (a finish texture set in a colour). The hull's slot, when set, paints every opaque
// surface; projector boxes listed per cell of the brick's grid (push ghost.z = the cell table, ghost.w = grid dimensions
// packed 5 bits each, cells = grid origin and size) then lay, in order: brush strokes' own paint
// (fills), paint shapes and decal images, and pattern paint. Finishes are sampled triplanar in ship space (the hull has no
// UVs), with whiteout-blended normals; the paint's colour replaces the finish's own, keeping its light and dark, except
// where the finish is scratched to bare metal. Without a hull slot, surfaces keep the structural materials' colours.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 sunDir;
    vec4 skyZenith;
    vec4 skyHorizon;
    vec4 palette[32];    // rgb linear albedo, a = opacity (below 1: glazing, drawn in the blended glazing pass)
    vec4 camera;         // xyz ship-space camera
    vec4 hull;           // x: the hull's paint slot, -1 for the materials' colours
    vec4 slotFinish[32]; // finish layer (-1 none), tile metres, metalness scale, mean luminance
    vec4 slotColour[32]; // linear rgb, a opacity
} frame;

layout(push_constant) uniform Brick {
    vec4 originScale;
    vec4 clipPlane;   // camera-relative: fragments with dot(xyz, pos) > w are cut away (cutaway view)
    vec4 ghost;       // x = opacity of the see-through interior view (0: solid); y = 1: the glazing pass; z = cell table, w = grid dims
    vec4 cells;       // xyz camera-relative grid origin, w cell size (metres)
} brick;

struct Projector {
    vec4 centre;  // xyz camera-relative, w kind (1 pattern paint, 2 decal or paint shape, 3 brush fill)
    vec4 u;       // xyz axis / half, w half (metres)
    vec4 v;
    vec4 n;       // w: pattern paint's distance along the stroke at the centre
    vec4 colour;  // rgb linear, a opacity
    vec4 params;  // pattern paint: x spacing, y line width, z pattern; decal/fill: x shape (0 image), y paint slot, w layer
};

layout(set = 0, binding = 1, std430) readonly buffer Projectors { Projector projectors[]; };
layout(set = 0, binding = 2, std430) readonly buffer Lists { uint lists[]; };
layout(set = 0, binding = 3) uniform sampler2DArray decals;
layout(set = 0, binding = 4) uniform sampler2DArray finishAlbedo;   // sRGB colour, a = scratched to bare metal
layout(set = 0, binding = 5) uniform sampler2DArray finishSurface;  // normal x, y, roughness, metalness

layout(location = 0) in vec3 vRelPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) flat in uint vMaterial;

layout(location = 0) out vec4 outColor;

const vec3 GROUND_BOUNCE = vec3(0.10, 0.09, 0.08);
const float CREASE_COS = 0.85;

struct Surface {
    vec3 albedo;
    float roughness;
    float metalness;
    vec3 normal;
    bool painted;
};

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

// Inside a shape on the unit square (u, v in -1..1), softened over about a pixel (aa in the same units).
float shapeMask(int shape, vec2 l, float aa)
{
    float d;

    if (shape == 2)
        d = length(l) - 1.0;
    else if (shape == 3)
        d = max(abs(l.x) * 2.0 + l.y - 1.0, -1.0 - l.y) * 0.4472;   // triangle, point up
    else if (shape == 4)
        d = (abs(l.x) + abs(l.y) - 1.0) * 0.7071;
    else
        d = max(abs(l.x), abs(l.y)) - 1.0;

    return 1.0 - smoothstep(-aa, aa, d);
}

vec3 tangentNormal(vec4 s)
{
    vec2 xy = s.rg * 2.0 - 1.0;
    return vec3(xy, sqrt(max(1.0 - dot(xy, xy), 0.0)));
}

// Paint slot `slot` at ship-space point p on a surface facing n (geometric), triplanar; dx, dy: p's screen derivatives.
Surface paintAt(int slot, vec3 p, vec3 n, vec3 dx, vec3 dy)
{
    vec4 f = frame.slotFinish[slot];
    vec4 c = frame.slotColour[slot];
    Surface s;
    s.painted = true;

    if (f.x < 0.0)
    {
        s.albedo = c.rgb;
        s.roughness = 0.5;
        s.metalness = 0.0;
        s.normal = n;
        return s;
    }

    vec3 w = pow(abs(n), vec3(4.0));
    w /= dot(w, vec3(1.0));
    float k = 1.0 / f.y;
    vec3 uvx = vec3(p.zy * k, f.x), uvy = vec3(p.xz * k, f.x), uvz = vec3(p.xy * k, f.x);
    vec4 ax = textureGrad(finishAlbedo, uvx, dx.zy * k, dy.zy * k);
    vec4 ay = textureGrad(finishAlbedo, uvy, dx.xz * k, dy.xz * k);
    vec4 az = textureGrad(finishAlbedo, uvz, dx.xy * k, dy.xy * k);
    vec4 sx = textureGrad(finishSurface, uvx, dx.zy * k, dy.zy * k);
    vec4 sy = textureGrad(finishSurface, uvy, dx.xz * k, dy.xz * k);
    vec4 sz = textureGrad(finishSurface, uvz, dx.xy * k, dy.xy * k);
    vec4 a = ax * w.x + ay * w.y + az * w.z;
    vec4 m = sx * w.x + sy * w.y + sz * w.z;

    // The paint's colour, keeping the finish's light and dark; bare metal where it is scratched through.
    float luminance = dot(a.rgb, vec3(0.2126, 0.7152, 0.0722));
    vec3 tinted = c.rgb * clamp(luminance / max(f.w, 1e-3), 0.0, 2.0);
    s.albedo = mix(tinted, a.rgb, a.a);
    s.roughness = clamp(m.b, 0.04, 1.0);
    s.metalness = clamp(m.a * f.z + a.a * (1.0 - f.z), 0.0, 1.0);

    // Whiteout blend of the three tangent-space normals.
    vec3 tx = tangentNormal(sx), ty = tangentNormal(sy), tz = tangentNormal(sz);
    tx = vec3(tx.xy + n.zy, abs(tx.z) * n.x);
    ty = vec3(ty.xy + n.xz, abs(ty.z) * n.y);
    tz = vec3(tz.xy + n.xy, abs(tz.z) * n.z);
    s.normal = normalize(tx.zyx * w.x + ty.xzy * w.y + tz.xyz * w.z);
    return s;
}

Surface blend(Surface a, Surface b, float t)
{
    Surface r;
    r.albedo = mix(a.albedo, b.albedo, t);
    r.roughness = mix(a.roughness, b.roughness, t);
    r.metalness = mix(a.metalness, b.metalness, t);
    r.normal = normalize(mix(a.normal, b.normal, t));
    r.painted = a.painted || (b.painted && t > 0.0);
    return r;
}

// The hull's paint, then this brick's boxes in order. Derivatives are taken before the loop (it diverges).
Surface decorate(Surface s, vec3 rel, vec3 n)
{
    vec3 dx = dFdx(rel);
    vec3 dy = dFdy(rel);
    vec3 p = rel + frame.camera.xyz;

    int hull = int(frame.hull.x);

    if (hull >= 0)
        s = blend(s, paintAt(hull, p, n, dx, dy), 1.0);

    int dims = int(brick.ghost.w);

    if (dims == 0)
        return s;

    // This fragment's cell: its (first, count) in the table, then that many box indices.
    ivec3 grid = ivec3(dims & 31, (dims >> 5) & 31, (dims >> 10) & 31);
    ivec3 c = clamp(ivec3(floor((rel - brick.cells.xyz) / brick.cells.w)), ivec3(0), grid - 1);
    int entry = int(brick.ghost.z) + (c.x + grid.x * (c.y + grid.y * c.z)) * 2;
    int first = int(lists[entry]);
    int count = int(lists[entry + 1]);

    float pixel = max(length(dx), length(dy));
    float pattern = 0.0;
    vec3 patternColour = vec3(0.0);
    vec4 decal = vec4(0.0);

    for (int i = 0; i < count; i++)
    {
        Projector pr = projectors[lists[first + i]];
        vec3 d = rel - pr.centre.xyz;
        vec3 l = vec3(dot(d, pr.u.xyz), dot(d, pr.v.xyz), dot(d, pr.n.xyz));

        if (any(greaterThan(abs(l), vec3(1.0))))
            continue;

        float kind = pr.centre.w;

        // A brush stroke's own paint covers its flair whatever way it faces.
        if (kind > 2.5)
        {
            s = blend(s, paintAt(int(pr.params.y), p, n, dx, dy), 1.0);
            continue;
        }

        float facing = dot(n, normalize(pr.n.xyz));

        if (facing < 0.05)
            continue;

        float fade = smoothstep(0.05, 0.3, facing);

        if (kind < 1.5)
        {
            float along = pr.n.w + l.x * pr.u.w;
            float across = l.y * pr.v.w;
            float half_ = pr.params.y * 0.5;
            float cover = 1.0 - smoothstep(half_ - pixel, half_ + pixel, lineCross(along, across, pr.params.x));
            cover *= 1.0 - smoothstep(0.94, 1.0, abs(l.y));
            float a = cover * pr.colour.a * fade;

            if (a > pattern)
            {
                pattern = a;
                patternColour = pr.colour.rgb;
            }

            continue;
        }

        int shape = int(pr.params.x);

        if (shape > 0)
        {
            int slot = int(pr.params.y);
            float aa = pixel / min(pr.u.w, pr.v.w);
            float t = shapeMask(shape, l.xy, aa) * frame.slotColour[slot].a * pr.colour.a * fade;

            if (t > 0.0)
                s = blend(s, paintAt(slot, p, n, dx, dy), t);

            continue;
        }

        vec2 uv = vec2(l.x, -l.y) * 0.5 + 0.5;
        vec2 gx = vec2(dot(dx, pr.u.xyz), -dot(dx, pr.v.xyz)) * 0.5;
        vec2 gy = vec2(dot(dy, pr.u.xyz), -dot(dy, pr.v.xyz)) * 0.5;
        vec4 texel = textureGrad(decals, vec3(uv, pr.params.w), gx, gy);
        float a = texel.a * pr.colour.a * fade;
        decal = vec4(texel.rgb * pr.colour.rgb * a + decal.rgb * (1.0 - a), a + decal.a * (1.0 - a));
    }

    s.albedo = mix(s.albedo, patternColour, pattern);
    s.albedo = s.albedo * (1.0 - decal.a) + decal.rgb;
    return s;
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
    vec3 shading = normalize(vNormal);
    shading = dot(shading, face) < CREASE_COS ? face : shading;
    float intensity = frame.sunDir.w;

    Surface s;
    s.albedo = frame.palette[min(vMaterial, 31u)].rgb;
    s.roughness = 0.6;
    s.metalness = 0.0;
    s.normal = shading;
    s.painted = false;
    s = decorate(s, vRelPos, face);

    vec3 N = s.painted ? normalize(s.normal + (shading - face)) : s.normal;
    float up = N.y;
    vec3 skyAmbient = mix(frame.skyHorizon.rgb, frame.skyZenith.rgb, clamp(up, 0.0, 1.0));
    vec3 ambient = mix(skyAmbient, GROUND_BOUNCE, clamp(-up, 0.0, 1.0));
    float ndl = max(dot(N, L), 0.0);
    vec3 colour;

    if (s.painted)
    {
        // Roughness sets the highlight's tightness; metal reflects in its own colour and has no diffuse.
        float r = max(s.roughness, 0.05);
        float power = clamp(2.0 / (r * r * r * r) - 2.0, 2.0, 2048.0);
        vec3 f0 = mix(vec3(0.04), s.albedo, s.metalness);
        float highlight = ndl > 0.0 ? pow(max(dot(N, normalize(L + V)), 0.0), power) * (power + 8.0) / 25.13 : 0.0;
        vec3 diffuse = s.albedo * (1.0 - s.metalness);
        colour = diffuse * (ndl * intensity + ambient) + f0 * (highlight * ndl * intensity + ambient * (1.0 - r));
    }
    else
    {
        float spec = ndl > 0.0 ? pow(max(dot(N, normalize(L + V)), 0.0), 48.0) * 0.15 : 0.0;
        colour = s.albedo * (ndl * intensity + ambient) + spec * intensity;
    }

    outColor = vec4(pow(acesFit(colour), vec3(1.0 / 2.2)), 1.0);
}
