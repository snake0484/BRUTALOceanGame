#version 450
// Designer overlay: brush outlines, gizmos, mesh cages and drag previews. Camera-relative positions, depth tested
// against the hull. params.x pulls depth toward the camera (reverse-Z: larger is nearer) so lines lying on the hull win;
// params.y scales alpha (the hidden pass draws what the hull covers, faint).

layout(push_constant) uniform Overlay {
    mat4 viewProj;
    vec4 params;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColour;

layout(location = 0) out vec4 vColour;

void main()
{
    vColour = vec4(inColour.rgb, inColour.a * pc.params.y);
    gl_Position = pc.viewProj * vec4(inPosition, 1.0);
    gl_Position.z *= pc.params.x;
}
