#version 330 core

// Port of the web client's chunk vertex shader (WebGL2 GLSL 300 es).
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec2 aMeta; // x = atlas layer, y = face brightness

uniform mat4 uViewProj;
uniform mat4 uModel;      // identity for chunks; TRS for physics entities (plan 03)
uniform vec3 uChunkOrigin;
uniform vec3 uCameraPos;

out vec2 vUv;
out vec2 vMeta;
out float vFogDepth;
out vec3 vWorldPos;

void main() {
    // Chunks: uModel = identity, so world = aPosition + uChunkOrigin.
    // Entities: uChunkOrigin = 0 and uModel = the entity's world transform.
    vec3 world = (uModel * vec4(aPosition, 1.0)).xyz + uChunkOrigin;
    vUv = aUv;
    vMeta = aMeta;
    vWorldPos = world;
    vFogDepth = distance(world, uCameraPos);
    gl_Position = uViewProj * vec4(world, 1.0);
}
