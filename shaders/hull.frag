#version 450
// Designer hull: Lambert sun + hemispheric sky + small specular, material colour from the palette, ACES + gamma.
// Dual contouring shares one vertex per cell, so a vertex on a crease carries the mean of both faces' normals; where the
// interpolated normal leans more than CREASE_COS from the triangle's own normal the face normal is used: flat faces and
// hard edges stay hard, curved surfaces stay smooth.

layout(set = 0, binding = 0, std140) uniform Frame {
    mat4 viewProj;
    vec4 sunDir;
    vec4 skyZenith;
    vec4 skyHorizon;
    vec4 palette[32];
} frame;

layout(push_constant) uniform Brick {
    vec4 originScale;
    vec4 clipPlane;   // camera-relative: fragments with dot(xyz, pos) > w are cut away (cutaway view)
    vec4 ghost;       // x = opacity of the see-through interior view (0: solid)
} brick;

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
    vec3 albedo = frame.palette[min(vMaterial, 31u)].rgb;

    float up = N.y;
    vec3 skyAmbient = mix(frame.skyHorizon.rgb, frame.skyZenith.rgb, clamp(up, 0.0, 1.0));
    vec3 ambient = mix(skyAmbient, GROUND_BOUNCE, clamp(-up, 0.0, 1.0));

    float ndl = max(dot(N, L), 0.0);
    float spec = ndl > 0.0 ? pow(max(dot(N, normalize(L + V)), 0.0), 48.0) * 0.15 : 0.0;
    vec3 colour = albedo * (ndl * intensity + ambient) + spec * intensity;
    outColor = vec4(pow(acesFit(colour), vec3(1.0 / 2.2)), 1.0);
}
