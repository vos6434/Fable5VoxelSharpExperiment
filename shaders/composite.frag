#version 330 core

// PostChain composite: reconstruct sky for open pixels, scene elsewhere.
// The scene FBO is cleared black and only world geometry is drawn there, so
// mesh cracks (depth still clear) no longer carry sky color — they stay black
// until the crack-fill below copies the nearest surface over them.
uniform sampler2D uScene;
uniform sampler2D uDepth;

uniform vec3 uCamForward;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform float uTanHalfFov;
uniform float uAspect;

uniform vec3 uSkyZenith;
uniform vec3 uSkyHorizon;
uniform vec3 uSunDir;
uniform vec3 uSunDiscColor;
uniform float uMoonVisibility;
// 0 underground / hell — clear-depth pixels that miss crack-fill stay black
// instead of showing the sky gradient through mesh pinholes.
uniform float uSkyOpen;

in vec2 vUv;
out vec4 outColor;

vec3 skyColor(vec2 uv) {
    vec2 ndc = uv * 2.0 - 1.0;
    vec3 ray = normalize(
        uCamForward +
        uCamRight * ndc.x * uTanHalfFov * uAspect +
        uCamUp * ndc.y * uTanHalfFov);

    float up = clamp(ray.y, 0.0, 1.0);
    vec3 sky = mix(uSkyHorizon, uSkyZenith, pow(up, 0.55));

    float sunDot = dot(ray, uSunDir);
    float disc = smoothstep(0.9994, 0.9997, sunDot);
    float glow = pow(clamp(sunDot, 0.0, 1.0), 180.0) * 0.35;
    sky += uSunDiscColor * (disc + glow);

    float moonDot = dot(ray, -uSunDir);
    float moon = smoothstep(0.9995, 0.9998, moonDot) * uMoonVisibility;
    sky += vec3(0.75, 0.8, 0.9) * moon;

    return sky;
}

// Greedy-mesh T-junctions leave 1–2 px holes (depth still clear). Copy the
// nearest surface color within a small cross + diagonals so cracks go dark
// instead of showing sky.
bool nearestSurface(ivec2 p, out vec3 color) {
    ivec2 size = textureSize(uDepth, 0);
    float bestDepth = 1.0;
    bool found = false;
    for (int oy = -2; oy <= 2; oy++) {
        for (int ox = -2; ox <= 2; ox++) {
            if (ox == 0 && oy == 0) continue;
            ivec2 q = clamp(p + ivec2(ox, oy), ivec2(0), size - ivec2(1));
            float dq = texelFetch(uDepth, q, 0).r;
            if (dq < bestDepth) {
                bestDepth = dq;
                color = texelFetch(uScene, q, 0).rgb;
                found = true;
            }
        }
    }
    return found;
}

void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    float d = texelFetch(uDepth, p, 0).r;

    if (d < 1.0) {
        outColor = texelFetch(uScene, p, 0);
        return;
    }

    vec3 surface;
    if (nearestSurface(p, surface)) {
        outColor = vec4(surface, 1.0);
        return;
    }

    outColor = vec4(skyColor(vUv) * uSkyOpen, 1.0);
}
