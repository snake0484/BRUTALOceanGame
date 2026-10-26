#version 450
// Falling rain: a soft streak, brightest at the drop's head, fading along its motion blur.

layout(location = 0) in vec2 vCorner;
layout(location = 1) in float vAlpha;
layout(location = 2) in vec3 vColour;

layout(location = 0) out vec4 outColor;

void main()
{
    float across = 1.0 - vCorner.x * vCorner.x;
    float along = smoothstep(0.0, 0.12, vCorner.y) * (1.0 - smoothstep(0.35, 1.0, vCorner.y));
    outColor = vec4(vColour, vAlpha * across * along);
}
