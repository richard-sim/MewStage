// Floor grid: a world-space grid on the z = 0 plane (the world frame is +X right, +Y forward, +Z up), drawn as an
// overlay pass over the viewport's scene.
//
// How it works: one full-viewport triangle (3 vertices from SV_VertexID, no vertex buffer). Every pixel unprojects
// its NDC position at the near and far planes through the inverse view-projection, intersects that ray with the
// floor plane, and shades the hit point:
//   * minor lines every gParams.x metres and major lines every gParams.y minor cells, antialiased from the screen-space
//     derivatives of the plane position (one line is gParams.z pixels wide whatever the zoom);
//   * the two world axes through the origin in their own colors (gParams.w pixels wide);
//   * minor lines fade out when a cell gets smaller than a few pixels, and everything fades with distance from the
//     eye (gEye.w) and at grazing angles, so the horizon does not alias into noise;
//   * the hit's own depth is written (SV_Depth) so the scene's depth test hides the grid behind geometry; the pipeline
//     tests GreaterEqual (reversed-Z) and does not write depth.
// Output is premultiplied alpha; the pipeline blends One / OneMinusSrcAlpha for color and alpha, so the target's
// alpha stays 1 and a UI pass sampling the viewport never sees through the grid.
//
// Conventions: row-major + row-vector matrices (mul(v, M)), reversed-Z (near = 1, far = 0).
// Binding map (PipelineLayoutDesc: CbvCount = 1): b0 -> gGrid.
struct GridConstants
{
    float4x4 gViewProj;     // world -> clip (reversed-Z), for the depth of the hit point
    float4x4 gInvViewProj;  // clip -> world
    float4   gEye;          // xyz: eye position (world); w: fade distance (m): fully faded at 2w
    float4   gParams;       // x: minor cell (m), y: major every N minor cells, z: line width (px), w: axis width (px)
    float4   gMinorColor;   // straight alpha
    float4   gMajorColor;
    float4   gAxisXColor;   // the line along +X (y = 0)
    float4   gAxisYColor;   // the line along +Y (x = 0)
};
[[vk::binding(0)]] ConstantBuffer<GridConstants> gGrid : register(b0);

struct VSOut
{
    float4 pos : SV_Position;
    noperspective float2 ndc : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    // One triangle covering the viewport: (-1,-1) (3,-1) (-1,3).
    float2 k = float2(id == 1 ? 3.0 : -1.0, id == 2 ? 3.0 : -1.0);
    VSOut o;
    o.pos = float4(k, 0.0, 1.0);
    o.ndc = k;
    return o;
}

float3 Unproject(float2 ndc, float z)
{
    float4 h = mul(float4(ndc, z, 1.0), gGrid.gInvViewProj);
    return h.xyz / h.w;
}

// Coverage of a line of width w pixels at a signed distance d (pixels) from its center.
float LineCoverage(float d, float w)
{
    return saturate(w * 0.5 + 0.5 - abs(d));
}

// Coverage of the lines every `cell` metres, both directions, at plane position p with pixel footprint fw (m/px).
float GridLines(float2 p, float2 fw, float cell, float width)
{
    float2 c = p / cell;
    float2 distCells = abs(frac(c + 0.5) - 0.5);          // distance to the nearest line, in cells
    float2 distPx = distCells * cell / fw;                 // ... in pixels
    return max(LineCoverage(distPx.x, width), LineCoverage(distPx.y, width));
}

struct PSOut
{
    float4 color : SV_Target0;
    float depth : SV_Depth;
};

PSOut PSMain(VSOut i)
{
    float3 pn = Unproject(i.ndc, 1.0);                     // near plane (reversed-Z)
    float3 pf = Unproject(i.ndc, 0.0);                     // far plane
    float3 dir = pf - pn;
    // Derivatives first (uniform control flow across the 2x2 quad), then the pixels that miss the plane leave.
    float t = -pn.z / dir.z;
    float3 p = pn + t * dir;
    float2 fw = abs(ddx(p.xy)) + abs(ddy(p.xy));           // metres per pixel along each plane axis
    fw = max(fw, 1e-6);
    if (abs(dir.z) < 1e-9 || t <= 0.0 || t >= 1.0) discard; // parallel, behind the eye, or beyond the far plane

    float cell = gGrid.gParams.x;
    float majorEvery = max(1.0, gGrid.gParams.y);
    float width = gGrid.gParams.z;
    float axisWidth = gGrid.gParams.w;

    // Minor lines vanish when a cell is only a few pixels wide; major lines carry on.
    float cellPx = cell / max(fw.x, fw.y);
    float minorFade = saturate((cellPx - 3.0) / 9.0);

    float4 minor = gGrid.gMinorColor;
    float4 major = gGrid.gMajorColor;
    float gMinor = GridLines(p.xy, fw, cell, width) * minorFade;
    float gMajor = GridLines(p.xy, fw, cell * majorEvery, width);

    float4 col = float4(minor.rgb, minor.a * gMinor);
    col = lerp(col, float4(major.rgb, major.a), gMajor * major.a);

    // Axes: the line along +X is y = 0, the line along +Y is x = 0.
    float ax = LineCoverage(p.y / fw.y, axisWidth) * gGrid.gAxisXColor.a;
    float ay = LineCoverage(p.x / fw.x, axisWidth) * gGrid.gAxisYColor.a;
    col = lerp(col, float4(gGrid.gAxisXColor.rgb, 1.0), ax);
    col = lerp(col, float4(gGrid.gAxisYColor.rgb, 1.0), ay);

    // Fade with distance and at grazing angles.
    float3 toEye = gGrid.gEye.xyz - p;
    float dist = length(toEye);
    float fadeDist = gGrid.gEye.w;
    float fade = 1.0 - smoothstep(0.0, fadeDist, dist - fadeDist);
    float grazing = abs(toEye.z) / max(dist, 1e-6);        // |cos| between the view ray and the plane normal
    fade *= 1.0 - pow(1.0 - grazing, 4.0);
    col.a *= fade;
    if (col.a <= 0.002) discard;

    float4 clip = mul(float4(p, 1.0), gGrid.gViewProj);
    PSOut o;
    o.color = float4(col.rgb * col.a, col.a);
    o.depth = saturate(clip.z / clip.w);
    return o;
}
