sampler2D inputSampler : register(s0);
float threshold : register(c0);
float softness : register(c1);
float fadeOpacity : register(c2);
float keyEnabled : register(c3);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 pixel = tex2D(inputSampler, uv);
    // WPF supplies premultiplied color. Compare the original RGB to black.
    float3 rgb = pixel.rgb / max(pixel.a, 0.00001);
    float distanceFromBlack = max(rgb.r, max(rgb.g, rgb.b));
    float coverage = smoothstep(threshold, threshold + max(softness, 0.00001), distanceFromBlack);
    // Scale RGB and alpha together to retain premultiplied alpha at soft edges.
    return pixel * lerp(1.0, coverage, saturate(keyEnabled)) * saturate(fadeOpacity);
}
