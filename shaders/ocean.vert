#version 450
#extension GL_GOOGLE_include_directive : require
// Ocean surface vertex shader: CDLOD clipmap grid in the sea frame's plane, bent onto the sea sphere. The surface itself
// comes from ocean_common.glsl (matches the CPU OceanSurface); shell levels (the far sea out to the horizon) are the
// bare sphere, shaded only.

#include "ocean_common.glsl"

layout(push_constant) uniform Level {
    vec4 level; // (centreX, centreZ relative to the camera, spacing, N)
    vec4 morph; // (morphStart, morphEnd, levelIndex, 1 = shell)
} pc;

layout(location = 0) out vec3 vWorldPos;     // final displaced position (CPU swell + GPU cascades): x, z relative to the camera, y above sea level
layout(location = 1) out vec2 vRestXZ;       // x0 after morph (cascade sample coordinate)
layout(location = 2) out vec4 vCpuPartials;  // CPU band (hx, hz, λDxx, λDzz)
layout(location = 3) out float vCpuDxz;      // CPU band λDxz
layout(location = 4) out float vShoreDamp;   // share of the FFT slopes drawn: 0 on the beach .. 1 on open water
layout(location = 5) out float vSurfFoam;
layout(location = 6) out float vSwashEdge;   // 1 at the edge of a swash sheet, 0 half a metre behind it (ocean.frag's edge line)

const float LIMB = 0.999; // shell vertices past the sea sphere's limb (as the frame's plane sees it) are pulled in to it

// Each level overlaps the next finer one by a cell, where both draw the same surface to float rounding. A coarser level
// is set back by this share of its depth per level (0.1 mm at 12 m), so the finer one, drawn first, always wins the
// depth test there: see-through shallows drawn twice showed as dark quads.
const float LEVEL_SETBACK = 8e-6;

void main()
{
    // Grid vertex from index.
    int n = int(pc.level.w);
    int side = 2 * n + 1;
    ivec2 g = ivec2(gl_VertexIndex % side, gl_VertexIndex / side) - n;
    float spacing = pc.level.z;
    vec2 x0 = pc.level.xy + vec2(g) * spacing;

    // CDLOD morph: collapse odd vertices onto even ones toward the outer edge.
    vec2 toCam = abs(x0);
    float d = max(toCam.x, toCam.y) / (float(n) * spacing);
    float m = clamp((d - pc.morph.x) / (pc.morph.y - pc.morph.x), 0.0, 1.0);
    vec2 odd = mod(vec2(g), 2.0);
    x0 -= odd * spacing * m;

    OceanPoint p;

    if (pc.morph.w > 0.5)
    {
        vec2 fromOrigin = frame.planet.xy + x0;
        float limb = LIMB * frame.planet.w;
        float r = length(fromOrigin);
        if (r > limb)
            x0 = fromOrigin * (limb / r) - frame.planet.xy;

        p.position = vec3(x0.x, 0.0, x0.y);
        p.cpuPartials = vec4(0.0);
        p.cpuDxz = 0.0;
        p.shore = 0.0;
        p.surfFoam = 0.0;
        p.swashEdge = 0.0;
    }
    else
    {
        // Anti-alias filter: fade waves shorter than ~2-4 cells of effective spacing.
        p = oceanAt(x0, spacing * (1.0 + m));
    }

    vWorldPos = p.position;
    vRestXZ = x0;
    vCpuPartials = p.cpuPartials;
    vCpuDxz = p.cpuDxz;
    vShoreDamp = p.shore;
    vSurfFoam = p.surfFoam;
    vSwashEdge = p.swashEdge;

    // The sea frame is a tangent plane at its origin: drawn on the sea sphere.
    float drop = seaDrop(frame.planet.xy + vWorldPos.xz);
    gl_Position = frame.viewProj * vec4(vWorldPos.x, vWorldPos.y - drop, vWorldPos.z, 1.0);
    gl_Position.z *= 1.0 - LEVEL_SETBACK * pc.morph.z;
}
