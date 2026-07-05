#version 330 core

// PostChain composite: scene texture -> default framebuffer.
// (Fog and other post effects insert between scene and composite later.)
//
// Also fills hairline cracks: greedy-mesh T-junctions and chunk borders
// occasionally miss a pixel during rasterization, and the sky background
// bleeds through (bright blue speckle in dark caves). A background pixel
// (depth == 1) sandwiched horizontally or vertically between geometry is a
// crack, not sky — cover it with the nearer neighbor's color.
uniform sampler2D uScene;
uniform sampler2D uDepth;

in vec2 vUv;
out vec4 outColor;

void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    outColor = texelFetch(uScene, p, 0);

    float d = texelFetch(uDepth, p, 0).r;
    if (d == 1.0) {
        ivec2 size = textureSize(uDepth, 0);
        ivec2 l = ivec2(max(p.x - 1, 0), p.y);
        ivec2 r = ivec2(min(p.x + 1, size.x - 1), p.y);
        ivec2 b = ivec2(p.x, max(p.y - 1, 0));
        ivec2 t = ivec2(p.x, min(p.y + 1, size.y - 1));
        float dl = texelFetch(uDepth, l, 0).r;
        float dr = texelFetch(uDepth, r, 0).r;
        float db = texelFetch(uDepth, b, 0).r;
        float dt = texelFetch(uDepth, t, 0).r;
        if (dl < 1.0 && dr < 1.0) {
            outColor = texelFetch(uScene, dl < dr ? l : r, 0);
        } else if (db < 1.0 && dt < 1.0) {
            outColor = texelFetch(uScene, db < dt ? b : t, 0);
        }
    }
}
