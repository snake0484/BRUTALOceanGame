#version 450
// Splash crown, drawn analytically in the quad (units: the crown's half width at size 1). A drop's impact throws up a
// thin cylindrical sheet, a cup seen from above at the camera's elevation: its near wall, the inside of its far wall
// over the near rim, bright rims where the sheet thickens, and points on the rim that fling droplets. On water the
// cavity then collapses into a central jet (a Worthington jet) that pinches off a drop. On ground only the crown.

layout(location = 0) in vec2 vLocal;
layout(location = 1) in vec4 vSplash;  // x = life 0..1, y = seed, z = 1 on ground, w = opacity
layout(location = 2) in vec3 vColour;
layout(location = 3) in vec2 vElevation; // sine and cosine of the camera's elevation over the splash

layout(location = 0) out vec4 outColor;

const int POINTS = 6;
const float OPACITY = 0.7;
const float GRAVITY = 0.32 * 0.32 * 9.81 / 0.05; // half widths per life² (life 0.32 s, half width 0.05 m)

float hash11(float x)
{
    return fract(sin(x * 127.1 + 311.7) * 43758.5453);
}

float line(float d, float halfWidth, float aa)
{
    return 1.0 - smoothstep(halfWidth - aa, halfWidth + aa, abs(d));
}

float disc(vec2 p, vec2 c, float r, float aa)
{
    return 1.0 - smoothstep(r - aa, r + aa, length(p - c));
}

float capsule(vec2 p, vec2 a, vec2 b, float r, float aa)
{
    vec2 pa = p - a;
    vec2 ba = b - a;
    float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
    return 1.0 - smoothstep(r - aa, r + aa, length(pa - ba * h));
}

void main()
{
    vec2 p = vLocal;
    float a = vSplash.x;
    float seed = vSplash.y;
    bool ground = vSplash.z > 0.5;
    float aa = max(length(fwidth(p)), 1e-3);
    float squash = max(vElevation.x, 0.05); // a depth (across the view) shows this much of itself ...
    float tall = vElevation.y;               // ... and a height this much

    // The crown: up to its full height at a quarter of the life, sunk back by 0.55, its rim spreading all the while.
    float radius = 0.4 + 0.5 * sqrt(a);
    float wall = (ground ? 0.35 : 0.6) * radius * sin(3.14159265 * min(a / 0.55, 1.0)) * tall;
    float shape = 0.0;

    if (wall > 0.0 && abs(p.x) < radius)
    {
        // Rim ellipses at the top and foot of the sheet: the near half lower on screen, the far half higher.
        float across = sqrt(1.0 - (p.x / radius) * (p.x / radius));
        float depth = radius * squash * across;
        float nearTop = wall - depth;
        float farTop = wall + depth;

        // The near wall's outside, thin and clear at its foot, thickening to the rim; the far wall's inside above it.
        float rise = clamp((p.y + depth) / max(wall + depth, 1e-3), 0.0, 1.0);
        float nearWall = step(-depth, p.y) * step(p.y, nearTop) * mix(0.04, 0.3, rise * rise);
        float farWall = step(nearTop, p.y) * step(p.y, farTop) * 0.12;

        // The rim is ragged: its height wanders around the crown, so it never reads as a regular band.
        float ragged = 0.08 * wall * sin(p.x / radius * 9.0 + seed * 40.0);
        float rims = max(0.75 * line(p.y - nearTop - ragged, 0.025, aa), 0.4 * line(p.y - farTop + ragged, 0.02, aa));

        // Where the sheet is seen edge-on, at its sides, it is brightest.
        float side = smoothstep(0.75, 1.0, abs(p.x) / radius) * step(-depth, p.y) * step(p.y, farTop);
        shape = max(max(nearWall, farWall), max(rims, 0.7 * side));
    }

    // Points on the rim, each throwing a droplet from its tip at 0.3 (where the rim was then) that flies out and falls.
    const float LAUNCH = 0.3;
    float launchRadius = 0.4 + 0.5 * sqrt(LAUNCH);
    float launchWall = (ground ? 0.35 : 0.6) * launchRadius * sin(3.14159265 * LAUNCH / 0.55) * tall;

    for (int j = 0; j < POINTS; j++)
    {
        float h = hash11(seed * 13.7 + float(j));
        float theta = 6.2831853 * (float(j) + 0.8 * h) / float(POINTS);
        vec2 dir = vec2(cos(theta), squash * sin(theta));
        float spike = 0.1 + 0.5 * h * h;

        if (wall > 0.0)
        {
            vec2 root = vec2(radius * dir.x, wall + radius * dir.y);
            shape = max(shape, 0.8 * capsule(p, root, root + vec2(0.0, spike * wall), 0.025, aa));
        }

        float t = a - LAUNCH;
        if (t > 0.0)
        {
            vec2 tip = vec2(launchRadius * dir.x, launchWall * (1.0 + spike) + launchRadius * dir.y);
            vec2 drop = tip + vec2(tip.x * 0.8 * t, tall * ((3.0 + 3.0 * h) * t - GRAVITY * t * t));
            if (drop.y > -radius * squash)
                shape = max(shape, disc(p, drop, 0.04 + 0.03 * h, aa));
        }
    }

    // The jet: rising from the collapsed cavity after 0.4, its tip pinching off a drop that flies on up.
    if (!ground && a > 0.4)
    {
        float j = (a - 0.4) / 0.6;
        float rise = (1.2 + 0.6 * hash11(seed * 5.3)) * sin(3.14159265 * j) * tall;
        float pinch = smoothstep(0.45, 0.65, j);
        float stem = rise * (1.0 - 0.4 * pinch);
        shape = max(shape, 0.9 * capsule(p, vec2(0.0, 0.0), vec2(0.0, stem), mix(0.06, 0.035, j), aa));
        shape = max(shape, disc(p, vec2(0.0, stem + 0.07 + 0.5 * pinch * j), 0.06, aa));
    }

    float alpha = OPACITY * shape * vSplash.w * (1.0 - smoothstep(0.8, 1.0, a));
    if (alpha < 0.004)
        discard;

    outColor = vec4(vColour, alpha);
}
