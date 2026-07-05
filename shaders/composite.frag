#version 330 core

// PostChain composite: scene texture -> default framebuffer.
// (Fog and other post effects insert between scene and composite later.)
uniform sampler2D uScene;

in vec2 vUv;
out vec4 outColor;

void main() {
    outColor = texture(uScene, vUv);
}
