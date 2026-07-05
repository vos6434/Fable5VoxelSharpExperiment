#version 330 core

// Port of the web client's chunk vertex shader (WebGL2 GLSL 300 es).
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec2 aMeta; // x = atlas layer, y = face brightness

uniform mat4 uViewProj;
uniform vec3 uChunkOrigin;
uniform vec3 uCameraPos;

out vec2 vUv;
out vec2 vMeta;
out float vFogDepth;

void main() {
    vec3 world = aPosition + uChunkOrigin;
    vUv = aUv;
    vMeta = aMeta;
    vFogDepth = distance(world, uCameraPos);
    gl_Position = uViewProj * vec4(world, 1.0);
}
