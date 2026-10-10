float4 VSMain(float2 position : POSITION) : SV_Position {
    return float4(position,0,1);
}
float4 PSMain() : SV_Target {
    // Opaque black bypasses video chroma key and transition opacity.
    return float4(0,0,0,1);
}
