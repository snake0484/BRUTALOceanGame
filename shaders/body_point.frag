#version 450
// A point of light: a Gaussian of about a pixel, added (blend One, One). Colour is display-space, set on the CPU from
// the body's size, albedo, phase angle to the star and distance (BodyRenderer).

layout(location = 0) in vec2 vPixel;
layout(location = 1) flat in vec3 vColour;

layout(location = 0) out vec4 outColor;

const float SIGMA = 0.6;

void main()
{
    float g = exp(-dot(vPixel, vPixel) / (2.0 * SIGMA * SIGMA));
    outColor = vec4(vColour * g, 0.0);
}
