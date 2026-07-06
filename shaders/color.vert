#version 330 core

// Flat-color geometry: block highlight outline, remote player boxes, entity highlights.
layout(location = 0) in vec3 aPosition;

uniform mat4 uViewProj;
uniform mat4 uModel;
uniform vec3 uOrigin;
uniform vec3 uScale;
uniform int uUseModel;

void main() {
    vec3 pos = aPosition * uScale + uOrigin;
    if (uUseModel != 0) {
        gl_Position = uViewProj * uModel * vec4(pos, 1.0);
    } else {
        gl_Position = uViewProj * vec4(pos, 1.0);
    }
}
