float4 VSMain(float2 position : POSITION) : SV_Position {
    return float4(position,0,1);
}
struct VideoVertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
VideoVertex VideoVSMain(float2 position : POSITION) {
    VideoVertex o; o.position=float4(position,0,1);
    o.uv=position*float2(0.5,-0.5)+0.5;
    return o;
}
float4 PSMain() : SV_Target {
    // Opaque black bypasses video chroma key and transition opacity.
    return float4(0,0,0,1);
}
