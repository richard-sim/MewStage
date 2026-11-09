// MewVG / NanoVG vector-UI shader: the `ui` render-graph pass. An HLSL port of the canonical NanoVG GL3 fill/stroke
// shader (MewVG.GL/GLNVGContext.cs upstream), driving NanoVgRenderer's stencil-once fills, antialiased strokes,
// per-draw scissor, atlas/text and gradient sampling through MewStage.Graphics' IGraphicsRecorder.
//
// Conventions: row-major + row-vector matrices; the NanoVG paint matrices are applied as mul(float3(p, 1), M).
//
// Why the per-draw selectors live in a CBV (b1) and not in push constants: glslang mis-compiles a global-struct push
// constant into a CBV at set 0 / binding 0, colliding with uFrag, which silently breaks the Vulkan path. A small CBV
// works on both DXC and glslang and costs nothing measurable.
//
// Binding map (matches NanoVgRenderer's PipelineLayoutDesc: CbvCount = 2, TextureSrvCount = 2, SamplerCount = 1,
// RootConstants32 = 0; the Vulkan backend numbers the bindings CBVs, then texture SRVs, then samplers):
//   b0 -> uFrag  (NanoVG 13 x float4 paint/scissor/gradient block, one ring slot per draw)
//   b1 -> uView  (view size + clip-Y flip sign + per-draw paint-type / tex-type selectors + the coverage-mask window)
//   t0 -> uTex   (glyph atlas / gradient LUT / image)
//   t1 -> uMask  (MewVG coverage mask for fills of 2000+ points, R8, read texel-exact; a 1x1 dummy otherwise)
//   s0 -> uSamp

// ---- per-draw paint/scissor/gradient uniforms (NanoVG's 13 x vec4 frag[] block) ----
// Laid out to match the renderer's fragment-uniform struct exactly so the C# side can copy the float[] straight in.
struct UiFrag
{
    float4 frag[13];
};
[[vk::binding(0)]] ConstantBuffer<UiFrag> uFrag : register(b0);

// ---- per-frame / per-draw selectors (CBV b1) ----
// Three 16-byte registers; no member straddles one (HLSL cbuffer packing), so it matches the C# UiView exactly.
struct UiView
{
    float2 gViewSize;     // reg0.xy  frame size in frame units (the MewVG window size)
    int    gType;         // reg0.z   paint type (0 grad-rect, 1 image, 2 stencil-only, 3 text glyph, 4 simple, 6 radial, 7 linear)
    int    gTexType;      // reg0.w   0 alpha, 1 rgba (premultiply from straight), 2 alpha-as-red
    float  gFlipY;        // reg1.x   clip-space Y sign: +1 Vulkan NDC (Y-down, no flip), -1 DX12/Metal NDC (Y-up, flip)
    float  gMaskScale;    // reg1.y   mask texels per frame unit (the device pixel ratio)
    int    gMaskEnabled;  // reg1.z   1: multiply by the uMask coverage
    float  gPad;          // reg1.w
    float2 gMaskOrigin;   // reg2.xy  mask top-left in frame units
    float2 gMaskSize;     // reg2.zw  mask size in texels
};
[[vk::binding(1)]] ConstantBuffer<UiView> uView : register(b1);

// ---- atlas / gradient-LUT texture, coverage mask, sampler ----
[[vk::binding(2)]] Texture2D    uTex  : register(t0);
[[vk::binding(3)]] Texture2D    uMask : register(t1);
[[vk::binding(4)]] SamplerState uSamp : register(s0);

struct VSIn  { [[vk::location(0)]] float2 pos : POSITION; [[vk::location(1)]] float2 uv : TEXCOORD0; };
struct VSOut { float4 pos : SV_Position; float2 ftcoord : TEXCOORD0; float2 fpos : TEXCOORD1; };

// View-size-relative NDC map (NanoVG: x in [-1, 1]); a pure affine map, no matrix upload. NanoVG vertices have a
// top-left origin; gFlipY selects the clip-Y sign per backend.
VSOut VSMain(VSIn i)
{
    VSOut o;
    o.ftcoord = i.uv;
    o.fpos    = i.pos;
    float ndcX = 2.0 * i.pos.x / uView.gViewSize.x - 1.0;
    float ndcY = 2.0 * i.pos.y / uView.gViewSize.y - 1.0;   // top-left-origin NanoVG Y -> NDC (Vulkan Y-down)
    o.pos     = float4(ndcX, ndcY * uView.gFlipY, 0.0, 1.0); // gFlipY = -1 on DX12/Metal (Y-up) flips clip Y only
    return o;
}

// --- NanoVG frag[] accessors (mirror the GLSL #defines) ---
float3x3 ScissorMat() { return float3x3(uFrag.frag[0].xyz, uFrag.frag[1].xyz, uFrag.frag[2].xyz); }
float3x3 PaintMat()   { return float3x3(uFrag.frag[3].xyz, uFrag.frag[4].xyz, uFrag.frag[5].xyz); }
#define innerCol        uFrag.frag[6]
#define outerCol        uFrag.frag[7]
#define scissorExt      uFrag.frag[8].xy
#define scissorScale    uFrag.frag[8].zw
#define extent          uFrag.frag[9].xy
#define radius          uFrag.frag[9].z
#define feather         uFrag.frag[9].w
#define gradCenter      uFrag.frag[10].xy
#define gradRadii       uFrag.frag[10].zw
#define gradFocal       uFrag.frag[11].xy
#define gradSpread      ((int)uFrag.frag[11].z)
#define strokeMult      uFrag.frag[12].x
#define strokeThr       uFrag.frag[12].y

// NanoVG stores a row-vector 2x3 affine lifted to 3x3; apply as mul(float3(p, 1), M).
float2 XformPt(float3x3 m, float2 p) { return mul(float3(p, 1.0), m).xy; }

float Sdroundrect(float2 pt, float2 ext, float rad)
{
    float2 ext2 = ext - float2(rad, rad);
    float2 d = abs(pt) - ext2;
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - rad;
}

float ScissorMask(float2 p)
{
    float2 sc = abs(XformPt(ScissorMat(), p)) - scissorExt;
    sc = float2(0.5, 0.5) - sc * scissorScale;
    return clamp(sc.x, 0.0, 1.0) * clamp(sc.y, 0.0, 1.0);
}

float GradientRadialT(float2 p, float2 center, float2 focal, float2 radii, int spread)
{
    float2 np = (p - center) / radii;
    float2 nf = (focal - center) / radii;
    float2 d = np - nf;
    float a = dot(d, d);
    if (a <= 1e-6) return 0.0;
    float b = 2.0 * dot(nf, d);
    float c = dot(nf, nf) - 1.0;
    float disc = b * b - 4.0 * a * c;
    if (disc <= 0.0) return 1.0;
    float u = (-b + sqrt(disc)) / (2.0 * a);
    if (u <= 1e-6) return 1.0;
    float t = 1.0 / u;
    if (spread == 0) return clamp(t, 0.0, 1.0);
    if (spread == 2) return frac(max(t, 0.0));
    float r = fmod(max(t, 0.0), 2.0);
    return r <= 1.0 ? r : 2.0 - r;
}

float GradientLinearT(float2 p, float2 startPt, float2 endPt, int spread)
{
    float2 axis = endPt - startPt;
    float len2 = dot(axis, axis);
    if (len2 <= 1e-6) return 0.0;
    float t = dot(p - startPt, axis) / len2;
    if (spread == 0) return clamp(t, 0.0, 1.0);
    if (spread == 2) return frac(max(t, 0.0));
    float r = fmod(max(t, 0.0), 2.0);
    return r <= 1.0 ? r : 2.0 - r;
}

// Edge-antialiasing stroke coverage from the fringe tcoord (always compiled in: MewStage renders with Antialias).
float StrokeMask(float2 ftcoord)
{
    if (strokeMult < 0.0)
        return clamp(ftcoord.x + 0.5, 0.0, 1.0) * min(1.0, ftcoord.y);
    return clamp((1.0 - abs(ftcoord.x * 2.0 - 1.0)) * strokeMult, 0.0, 1.0) * min(1.0, ftcoord.y);
}

// MewVG coverage mask (RenderMaskFill): the texel under this fragment, 0 outside the mask, 1 when no mask is bound.
float MaskCoverage(float2 fpos)
{
    if (uView.gMaskEnabled == 0) return 1.0;
    float2 p = (fpos - uView.gMaskOrigin) * uView.gMaskScale;
    if (p.x < 0.0 || p.y < 0.0 || p.x >= uView.gMaskSize.x || p.y >= uView.gMaskSize.y) return 0.0;
    return uMask.Load(int3(int2(p), 0)).r;
}

float4 PSMain(VSOut i) : SV_Target0
{
    int type    = uView.gType;
    int texType = uView.gTexType;

    float scissor = ScissorMask(i.fpos);
    float strokeAlpha = StrokeMask(i.ftcoord);
    if (strokeAlpha < strokeThr) discard;

    float4 result = float4(0, 0, 0, 0);

    if (type == 0)                      // gradient/solid filled rounded-rect (cover/AA fill)
    {
        float2 pt = XformPt(PaintMat(), i.fpos);
        float d = clamp((Sdroundrect(pt, extent, radius) + feather * 0.5) / feather, 0.0, 1.0);
        float4 color = lerp(innerCol, outerCol, d);
        result = color * (strokeAlpha * scissor);
    }
    else if (type == 1)                 // image pattern
    {
        float2 pt = XformPt(PaintMat(), i.fpos) / extent;
        float4 color = uTex.Sample(uSamp, pt);
        if (texType == 1) color = float4(color.xyz * color.w, color.w);
        if (texType == 2) color = float4(color.x, color.x, color.x, color.x);
        color *= innerCol;
        result = color * (strokeAlpha * scissor);
    }
    else if (type == 6)                 // radial gradient (LUT)
    {
        float2 pt = XformPt(PaintMat(), i.fpos);
        float t = GradientRadialT(pt, gradCenter, gradFocal, gradRadii, gradSpread);
        float4 color = uTex.Sample(uSamp, float2(t, 0.5)) * innerCol;
        result = color * (strokeAlpha * scissor);
    }
    else if (type == 7)                 // linear gradient (LUT)
    {
        float2 pt = XformPt(PaintMat(), i.fpos);
        float t = GradientLinearT(pt, gradCenter, gradFocal, gradSpread);
        float4 color = uTex.Sample(uSamp, float2(t, 0.5)) * innerCol;
        result = color * (strokeAlpha * scissor);
    }
    else if (type == 2)                 // stencil pass: write-only, color masked off by blend/colormask
    {
        result = float4(1, 1, 1, 1);
    }
    else if (type == 3)                 // text glyph (atlas alpha)
    {
        float4 color = uTex.Sample(uSamp, i.ftcoord);
        if (texType == 1) color = float4(color.xyz * color.w, color.w);
        if (texType == 2) color = float4(color.x, color.x, color.x, color.x);
        color *= scissor;
        result = color * innerCol;
    }
    else if (type == 4)                 // simple coverage (used by some clip paths)
    {
        result = float4(strokeAlpha, strokeAlpha, strokeAlpha, strokeAlpha);
    }
    return result * MaskCoverage(i.fpos);
}
