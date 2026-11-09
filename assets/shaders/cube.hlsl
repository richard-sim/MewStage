// Lit cube: the demo's 3D content. Position + normal + color vertices, one constant buffer (world, view-projection,
// light direction, tint), Lambert diffuse plus a flat ambient term.
//
// Conventions (docs/architecture.md): row-major + row-vector matrices, so a point transforms as mul(float4(p, 1), M)
// and the C# System.Numerics matrices upload with no transpose (DXC -Zpr). Reversed-Z: the pipeline clears depth to 0
// and tests GreaterEqual; the projection matrix produces that range, this shader needs nothing special for it.
// Front faces are counter-clockwise.
//
// Binding map (PipelineLayoutDesc: CbvCount = 1): b0 -> gCube.
struct CubeConstants
{
    float4x4 gWorld;      // model -> world
    float4x4 gViewProj;   // world -> clip (reversed-Z)
    float4   gLightDir;   // xyz: unit direction TOWARD the light, world space; w: ambient strength (0..1)
    float4   gTint;       // rgb multiplies the vertex color; a: unused (1)
};
[[vk::binding(0)]] ConstantBuffer<CubeConstants> gCube : register(b0);

struct VSIn
{
    [[vk::location(0)]] float3 pos : POSITION;
    [[vk::location(1)]] float3 nrm : NORMAL;
    [[vk::location(2)]] float4 col : COLOR0;
};

struct VSOut
{
    float4 pos : SV_Position;
    float3 nrm : TEXCOORD0;   // world-space normal (the world matrix is a rotation plus a uniform scale)
    float4 col : COLOR0;
};

VSOut VSMain(VSIn i)
{
    VSOut o;
    float4 world = mul(float4(i.pos, 1.0), gCube.gWorld);
    o.pos = mul(world, gCube.gViewProj);
    o.nrm = mul(float4(i.nrm, 0.0), gCube.gWorld).xyz;
    o.col = i.col * gCube.gTint;
    return o;
}

float4 PSMain(VSOut i) : SV_Target0
{
    float3 n = normalize(i.nrm);
    float ndl = saturate(dot(n, normalize(gCube.gLightDir.xyz)));
    float ambient = gCube.gLightDir.w;
    float3 col = i.col.rgb * (ambient + (1.0 - ambient) * ndl);
    return float4(col, 1.0);
}
