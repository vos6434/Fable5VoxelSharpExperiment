#version 330 core

// Port of the web client's chunk fragment shader: texture-array sampling with
// repeat wrapping (greedy quads tile), baked face brightness, distance fog.
uniform sampler2DArray uAtlas;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform float uAlphaTest;

in vec2 vUv;
in vec2 vMeta;
in float vFogDepth;

out vec4 outColor;

void main() {
    vec4 texel = texture(uAtlas, vec3(vUv, vMeta.x));
    if (texel.a < uAlphaTest) discard;
    vec3 color = texel.rgb * vMeta.y;
    float fogFactor = smoothstep(uFogNear, uFogFar, vFogDepth);
    outColor = vec4(mix(color, uFogColor, fogFactor), texel.a);
}
