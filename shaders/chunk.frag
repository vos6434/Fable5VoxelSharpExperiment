#version 330 core

// Chunk fragment shader — plan 02 composite:
//   albedo × shade × (ambientFloor + skyAmbient × skyVis
//                     + dirLight × N·L × shadowRay + Σ blockLights)
// where shade is the baked face brightness × vertex AO, skyVis is a vertical
// occupancy ray (column openness), the directional light is the sun by day /
// the moon by night, and block lights come from a clustered light texture
// with per-light shadow rays (capped; overflow lights glow unshadowed).
// Emissive blocks (meta.y >= 4) render fullbright.
uniform sampler2DArray uAtlas;
uniform vec3 uCameraPos;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform float uAlphaTest;

// Lighting terms (all colors are pre-scaled intensities).
uniform vec3 uAmbientFloor;  // always applies (night floor / hell red glow)
uniform vec3 uAmbientSky;    // skylight: multiplied by the vertical openness ray
uniform vec3 uDirColor;      // directional (sun or moon) light color × strength
uniform vec3 uDirDir;        // toward the directional light

// Occupancy volume (1 block = 1 texel; R channel > 0.5 means solid),
// toroidally addressed: texel = world voxel mod size.
uniform sampler3D uOccupancy;
uniform vec3 uOccupancyOrigin; // world min-corner of the volume
uniform float uOccupancySize;  // edge length in voxels

// Clustered block lights (plan 02 M4/M5): 8^3-block clusters over the same
// region as the occupancy volume. Depth slice cz*9+s = light slot s of the
// cluster (xyz = position relative to uLightsOrigin, w = packed color+
// intensity); slice cz*9+8 = unshadowed overflow color. uLightsOrigin is the
// origin the texture was *built* with — rebuilds are async, so it can lag one
// recenter behind uOccupancyOrigin.
uniform sampler3D uLights;
uniform vec3 uLightsOrigin;
uniform int uLightClusters;     // clusters per edge
uniform int uShadowedLightCap;  // block lights beyond this skip the shadow ray

in vec2 vUv;
in vec2 vMeta;
in float vFogDepth;
in vec3 vWorldPos;

out vec4 outColor;

bool solidAt(ivec3 voxel, int size, ivec3 wrapBase) {
    return texelFetch(uOccupancy, (voxel + wrapBase) % size, 0).r > 0.5;
}

// DDA (Amanatides & Woo) march toward the directional light; 1 = lit.
float dirShadow(vec3 startWorld, vec3 normal) {
    vec3 dir = uDirDir;

    // Nudge the start slightly off the surface along the face normal so
    // floor() lands in the air voxel in front of the face instead of the
    // fragment's own solid voxel (which would self-shadow everything).
    vec3 p = startWorld - uOccupancyOrigin + normal * 0.01;
    ivec3 voxel = ivec3(floor(p));
    ivec3 stp = ivec3(sign(dir));
    vec3 tMax = (vec3(voxel) + max(vec3(stp), 0.0) - p) / dir;
    vec3 tDelta = 1.0 / max(abs(dir), vec3(1e-5));

    int size = int(uOccupancySize);
    ivec3 wrapBase = ivec3(mod(uOccupancyOrigin, uOccupancySize) + 0.5);
    for (int i = 0; i < 96; i++) {
        if (voxel.x < 0 || voxel.y < 0 || voxel.z < 0 ||
            voxel.x >= size || voxel.y >= size || voxel.z >= size) {
            return 1.0; // left the volume without hitting anything -> lit
        }
        if (solidAt(voxel, size, wrapBase)) return 0.0;
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

// Column openness: straight-up march through the occupancy column. 1 = the
// fragment can see the sky, 0 = roofed over (caves, hell). Drives skylight.
float skyVisibility(vec3 startWorld, vec3 normal) {
    vec3 p = startWorld - uOccupancyOrigin + normal * 0.01;
    ivec3 voxel = ivec3(floor(p));
    int size = int(uOccupancySize);
    if (voxel.x < 0 || voxel.z < 0 || voxel.x >= size || voxel.z >= size || voxel.y < 0) {
        return 1.0;
    }
    ivec3 wrapBase = ivec3(mod(uOccupancyOrigin, uOccupancySize) + 0.5);
    for (int i = 0; i < 96; i++) {
        if (voxel.y >= size) return 1.0;
        if (solidAt(voxel, size, wrapBase)) return 0.0;
        voxel.y += 1;
    }
    return 1.0;
}

// Shadow ray from a fragment toward one block light. Stops when it reaches
// the light's own voxel (emitting blocks are solid in the occupancy volume).
float lightRay(vec3 fromRel, vec3 lightRel, vec3 dir) {
    ivec3 voxel = ivec3(floor(fromRel));
    ivec3 lightVoxel = ivec3(floor(lightRel));
    ivec3 stp = ivec3(sign(dir));
    vec3 tMax = (vec3(voxel) + max(vec3(stp), 0.0) - fromRel) / dir;
    vec3 tDelta = 1.0 / max(abs(dir), vec3(1e-5));

    int size = int(uOccupancySize);
    ivec3 wrapBase = ivec3(mod(uOccupancyOrigin, uOccupancySize) + 0.5);
    for (int i = 0; i < 30; i++) {
        if (voxel == lightVoxel) return 1.0; // reached the emitter
        if (voxel.x < 0 || voxel.y < 0 || voxel.z < 0 ||
            voxel.x >= size || voxel.y >= size || voxel.z >= size) {
            return 1.0;
        }
        if (solidAt(voxel, size, wrapBase)) return 0.0;
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

// Sum of colored block lights hitting this fragment (clustered, plan 02 M4/M5).
vec3 blockLight(vec3 worldPos, vec3 normal) {
    // Cluster addressing is relative to the lights' build origin; shadow rays
    // march in occupancy space (the two origins can differ transiently).
    vec3 rel = worldPos - uLightsOrigin;
    ivec3 cluster = ivec3(floor(rel / 8.0));
    if (cluster.x < 0 || cluster.y < 0 || cluster.z < 0 ||
        cluster.x >= uLightClusters || cluster.y >= uLightClusters || cluster.z >= uLightClusters) {
        return vec3(0.0);
    }

    // Overflow: lights past the per-cluster cap, unshadowed and direction-less.
    vec3 total = texelFetch(uLights, ivec3(cluster.x, cluster.y, cluster.z * 9 + 8), 0).rgb;

    vec3 occRel = worldPos - uOccupancyOrigin + normal * 0.01;
    for (int s = 0; s < 8; s++) {
        vec4 t = texelFetch(uLights, ivec3(cluster.x, cluster.y, cluster.z * 9 + s), 0);
        if (t.w < 1.0) break; // slots fill front-to-back; w=0 means empty

        // Unpack ((r5*32 + g5)*32 + b5)*16 + intensity ("packed" is reserved in GLSL).
        float enc = t.w;
        float intensity = mod(enc, 16.0); enc = floor(enc / 16.0);
        float b5 = mod(enc, 32.0); enc = floor(enc / 32.0);
        float g5 = mod(enc, 32.0);
        float r5 = floor(enc / 32.0);
        vec3 color = vec3(r5, g5, b5) / 31.0;

        vec3 toLight = t.xyz - rel;
        float dist = length(toLight);
        if (dist >= intensity) continue; // linear falloff radius = intensity
        vec3 ldir = toLight / max(dist, 1e-4);
        float ndotl = dot(normal, ldir);
        if (ndotl <= 0.0) continue;

        float vis = 1.0;
        if (s < uShadowedLightCap) {
            vec3 lightOccRel = t.xyz + uLightsOrigin - uOccupancyOrigin;
            vis = lightRay(occRel, lightOccRel, ldir);
        }
        if (vis <= 0.0) continue;
        total += color * ((1.0 - dist / intensity) * (intensity / 15.0) * ndotl * vis);
    }

    // Soft ceiling (hue-preserving): dozens of overlapping lava lights
    // saturate instead of blowing out to white.
    float peak = max(total.r, max(total.g, total.b));
    if (peak > 1.5) total *= 1.5 / peak;
    return total;
}

void main() {
    vec4 texel = texture(uAtlas, vec3(vUv, vMeta.x));
    if (texel.a < uAlphaTest) discard;

    // Emissive faces (glowstone, lava): fullbright, no lighting or shadows.
    if (vMeta.y >= 3.99) {
        float fogEm = smoothstep(uFogNear, uFogFar, vFogDepth);
        outColor = vec4(mix(texel.rgb, uFogColor, fogEm), texel.a);
        return;
    }

    // Face normal from screen-space derivatives — exact (and constant) on
    // flat voxel faces, so no gradients disturb the pixel look. Oriented
    // toward the camera so it always points out of the visible surface.
    vec3 normal = normalize(cross(dFdx(vWorldPos), dFdy(vWorldPos)));
    normal *= sign(dot(normal, uCameraPos - vWorldPos));

    // Directional (sun/moon) term: Lambert × shadow ray.
    float ndotl = dot(normal, uDirDir);
    vec3 dirLight = vec3(0.0);
    if (ndotl > 0.0 && dot(uDirColor, uDirColor) > 1e-6) {
        dirLight = uDirColor * (ndotl * dirShadow(vWorldPos, normal));
    }

    // Skylight: sky ambient only where the column above is open.
    vec3 skyLight = vec3(0.0);
    if (dot(uAmbientSky, uAmbientSky) > 1e-6) {
        skyLight = uAmbientSky * skyVisibility(vWorldPos, normal);
    }

    vec3 light = uAmbientFloor + skyLight + dirLight + blockLight(vWorldPos, normal);
    vec3 color = texel.rgb * vMeta.y * light;

    float fogFactor = smoothstep(uFogNear, uFogFar, vFogDepth);
    outColor = vec4(mix(color, uFogColor, fogFactor), texel.a);
}
