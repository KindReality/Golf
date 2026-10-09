cbuffer Settings : register(b0) {
    float4 row0; float4 row1; float4 row2;
    float4 effects; // opacity, threshold, softness, black key enabled
    float4 aspect; // letterbox content width and height
};
Texture2D video : register(t0);
SamplerState linearClamp : register(s0);
struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Vertex VSMain(uint id : SV_VertexID) {
    Vertex o; o.uv = float2((id << 1) & 2, id & 2);
    o.position = float4(o.uv * float2(2,-2) + float2(-1,1),0,1); return o;
}
float4 PSMain(Vertex input) : SV_Target {
    float3 p = float3(input.uv,1);
    float divisor = dot(row2.xyz,p);
    if (abs(divisor) < 0.000001) return 0;
    float2 uv = float2(dot(row0.xyz,p),dot(row1.xyz,p))/divisor;
    uv = (uv-0.5)/aspect.xy+0.5;
    if (any(uv<0) || any(uv>1)) return 0;
    float3 rgb = video.Sample(linearClamp,uv).rgb;
    float brightness = max(rgb.r,max(rgb.g,rgb.b));
    float key = effects.w == 0 ? 1 : (effects.z == 0 ? step(effects.y,brightness) : smoothstep(effects.y,effects.y+effects.z,brightness));
    float alpha = effects.x*key;
    return float4(rgb*alpha,alpha);
}
