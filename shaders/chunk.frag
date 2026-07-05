#version 330 core

// Chunk fragment shader with voxel ray-traced sun shadows (plan 02 M3).
// The world's occupancy (1 byte/voxel: solid vs not) lives in a 3D texture
// covering the camera region; a DDA shadow ray toward the sun gives crisp,
// pixel-aligned shadows that rotate with the day. Colored block-light rays
// (M4+) reuse the same occupancy volume.
uniform sampler2DArray uAtlas;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform float uAlphaTest;

// Lighting: ambient floor (skylight + night) always applies; the sun term is
// what the shadow ray removes. ambient + sun = full daylight brightness.
uniform float uAmbient;
uniform float uSunStrength;
uniform vec3 uSunDir;        // toward the sun

// Occupancy volume (1 block = 1 texel; R channel > 0.5 means solid).
uniform sampler3D uOccupancy;
uniform vec3 uOccupancyOrigin; // world min-corner of the volume
uniform float uOccupancySize;  // edge length in voxels

in vec2 vUv;
in vec2 vMeta;
in float vFogDepth;
in vec3 vWorldPos;

out vec4 outColor;

// DDA (Amanatides & Woo) march toward the sun; returns 1 = lit, 0 = shadowed.
float sunShadow(vec3 startWorld) {
    if (uSunStrength < 0.001) return 1.0; // no sun (night): skip the march
    vec3 dir = uSunDir;

    // Start ~1.2 blocks off the surface toward the sun so the fragment's own
    // voxel doesn't self-shadow (no normal available; this is the bias).
    vec3 p = startWorld - uOccupancyOrigin + dir * 1.2;
    ivec3 voxel = ivec3(floor(p));
    ivec3 stp = ivec3(sign(dir));
    vec3 inv = 1.0 / max(abs(dir), vec3(1e-5));
    // Distance along the ray to the next voxel boundary on each axis.
    vec3 tMax = (vec3(voxel) + max(vec3(stp), 0.0) - p) / dir;
    vec3 tDelta = inv;

    int size = int(uOccupancySize);
    for (int i = 0; i < 96; i++) {
        if (voxel.x < 0 || voxel.y < 0 || voxel.z < 0 ||
            voxel.x >= size || voxel.y >= size || voxel.z >= size) {
            return 1.0; // left the volume without hitting anything -> lit
        }
        if (texelFetch(uOccupancy, voxel, 0).r > 0.5) {
            return 0.0; // occluder hit -> shadowed
        }
        // Advance to the next voxel along the smallest tMax.
        if (tMax.x < tMax.y && tMax.x < tMax.z) {
            voxel.x += stp.x; tMax.x += tDelta.x;
        } else if (tMax.y < tMax.z) {
            voxel.y += stp.y; tMax.y += tDelta.y;
        } else {
            voxel.z += stp.z; tMax.z += tDelta.z;
        }
    }
    return 1.0;
}

void main() {
    vec4 texel = texture(uAtlas, vec3(vUv, vMeta.x));
    if (texel.a < uAlphaTest) discard;

    float shadow = sunShadow(vWorldPos);
    float light = uAmbient + uSunStrength * shadow;
    vec3 color = texel.rgb * vMeta.y * light;

    float fogFactor = smoothstep(uFogNear, uFogFar, vFogDepth);
    outColor = vec4(mix(color, uFogColor, fogFactor), texel.a);
}
