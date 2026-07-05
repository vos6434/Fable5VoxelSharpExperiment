#version 330 core

// Screen-space UI: positions in framebuffer pixels, top-left origin.
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in float aLayer;
layout(location = 3) in vec4 aColor;

uniform vec2 uViewport;

out vec2 vUv;
out float vLayer;
out vec4 vColor;

void main() {
    vUv = aUv;
    vLayer = aLayer;
    vColor = aColor;
    gl_Position = vec4(
        aPos.x / uViewport.x * 2.0 - 1.0,
        1.0 - aPos.y / uViewport.y * 2.0,
        0.0, 1.0);
}
