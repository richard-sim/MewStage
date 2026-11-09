// Test fixture: a vertex-colored triangle from SV_VertexID, no vertex buffer, no constants. Drives the backend
// smoke tests (pipeline creation, dynamic rendering, draw, readback) on lavapipe and DX12.
// Clip positions are given directly, so the picture is the same on every backend: vertex 0 at the top (red),
// vertex 1 bottom-right (green), vertex 2 bottom-left (blue) in a Y-down raster (Vulkan NDC; the DX12 test flips).
struct VSOut
{
    float4 pos : SV_Position;
    float3 col : COLOR0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    float2 p = id == 0 ? float2(0.0, -0.6) : (id == 1 ? float2(0.6, 0.6) : float2(-0.6, 0.6));
    float3 c = id == 0 ? float3(1, 0, 0) : (id == 1 ? float3(0, 1, 0) : float3(0, 0, 1));
    VSOut o;
    o.pos = float4(p, 0.5, 1.0);
    o.col = c;
    return o;
}

float4 PSMain(VSOut i) : SV_Target0
{
    return float4(i.col, 1.0);
}
