#version 450

layout(location = 0) in vec4 vColour;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vColour;
}
