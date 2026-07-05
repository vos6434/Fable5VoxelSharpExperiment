#version 330 core

// Sky pass: gradient keyed on sun elevation + sun/moon discs, drawn as a
// fullscreen background before the world. Colors are computed CPU-side per
// frame (day/night curve + hell override) and arrive as uniforms.
uniform vec3 uCamForward;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform float uTanHalfFov;
uniform float uAspect;

uniform vec3 uSkyZenith;
uniform vec3 uSkyHorizon;
uniform vec3 uSunDir;      // toward the sun
uniform vec3 uSunDiscColor;
uniform float uMoonVisibility;

in vec2 vUv;
out vec4 outColor;

void main() {
    vec2 ndc = vUv * 2.0 - 1.0;
    vec3 ray = normalize(
        uCamForward +
        uCamRight * ndc.x * uTanHalfFov * uAspect +
        uCamUp * ndc.y * uTanHalfFov);

    float up = clamp(ray.y, 0.0, 1.0);
    vec3 sky = mix(uSkyHorizon, uSkyZenith, pow(up, 0.55));

    // Sun disc + glow.
    float sunDot = dot(ray, uSunDir);
    float disc = smoothstep(0.9994, 0.9997, sunDot);
    float glow = pow(clamp(sunDot, 0.0, 1.0), 180.0) * 0.35;
    sky += uSunDiscColor * (disc + glow);

    // Moon disc (opposite the sun), cool and dim.
    float moonDot = dot(ray, -uSunDir);
    float moon = smoothstep(0.9995, 0.9998, moonDot) * uMoonVisibility;
    sky += vec3(0.75, 0.8, 0.9) * moon;

    outColor = vec4(sky, 1.0);
}
