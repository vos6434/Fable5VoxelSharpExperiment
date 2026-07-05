#version 330 core

// uMode 0: sample uTex (gui textures, icons, font, 1x1 white for solids)
// uMode 1: sample uAtlas layer (block faces for pseudo-3D slot cubes)
uniform int uMode;
uniform sampler2D uTex;
uniform sampler2DArray uAtlas;

in vec2 vUv;
in float vLayer;
in vec4 vColor;

out vec4 outColor;

void main() {
    vec4 texel = uMode == 1 ? texture(uAtlas, vec3(vUv, vLayer)) : texture(uTex, vUv);
    outColor = texel * vColor;
}
