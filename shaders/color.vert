#version 330 core

// Flat-color geometry: block highlight outline and remote player boxes.
layout(location = 0) in vec3 aPosition;

uniform mat4 uViewProj;
uniform vec3 uOrigin;
uniform vec3 uScale;

void main() {
    gl_Position = uViewProj * vec4(aPosition * uScale + uOrigin, 1.0);
}
