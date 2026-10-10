cbuffer Overlay : register(b0) {
    float4 screen; // width, height, monotonic animation time, overlay opacity
    float4 selection; // selected index, flash corner bitmask, flash progress (-1 when inactive), success
    float4 grid; // grid enabled, label texture width/height, label enabled
    float4 row0,row1,row2;
    float4 label; // center in pixels, selected polygon opacity, point count
};
Texture2D labelTexture : register(t0);
StructuredBuffer<float4> polygonPoints : register(t1); // normalized XY, flash enabled, interaction index
SamplerState labelSampler : register(s0);
struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
float4 Over(float4 back,float4 front) {return front+back*(1-front.a);}
float DistanceToLine(float2 p,float2 a,float2 b) {
    float2 ab=b-a; float t=saturate(dot(p-a,ab)/max(dot(ab,ab),0.00001));return length(p-(a+t*ab));
}
float Bar(float2 p,float2 center,float2 halfSize) {return 1-smoothstep(0,0.7,length(max(abs(p-center)-halfSize,0)));}
float Digit(float2 p,uint index) {
    uint masks[10]={63,6,91,79,102,109,125,7,127,111};
    uint mask=masks[index];
    float value=0;
    if(mask&1) value=max(value,Bar(p,float2(0,-6),float2(3,0.7)));
    if(mask&2) value=max(value,Bar(p,float2(3,-3),float2(0.7,2.4)));
    if(mask&4) value=max(value,Bar(p,float2(3,3),float2(0.7,2.4)));
    if(mask&8) value=max(value,Bar(p,float2(0,6),float2(3,0.7)));
    if(mask&16) value=max(value,Bar(p,float2(-3,3),float2(0.7,2.4)));
    if(mask&32) value=max(value,Bar(p,float2(-3,-3),float2(0.7,2.4)));
    if(mask&64) value=max(value,Bar(p,float2(0,0),float2(3,0.7)));
    return value;
}
float Number(float2 p,uint value) {
    float result=0;
    [loop] do {result=max(result,Digit(p,value%10));p.x+=9;value/=10;} while(value>0);
    return result;
}
float4 PointAt(int i) {
    return polygonPoints[i];
}
float4 PSMain(Vertex input) : SV_Target {
    float2 dimensions=screen.xy;
    float2 p=input.uv*dimensions;
    int count=(int)label.w;
    float distance=1e10;
    bool inside=false;
    [loop] for(int edge=0;edge<count;edge++) {
        float2 a=PointAt(edge).xy*(dimensions-1),b=PointAt(edge+1==count?0:edge+1).xy*(dimensions-1);
        distance=min(distance,DistanceToLine(p,a,b));
        if((a.y>p.y)!=(b.y>p.y)) {
            if(p.x<a.x+(p.y-a.y)*(b.x-a.x)/(b.y-a.y)) inside=!inside;
        }
    }
    float lineAlpha=(0.5*exp(-distance*distance/1.5)+0.13*exp(-distance*distance/24))*screen.w;
    float3 azure=float3(0.05,0.55,1);
    float4 color=float4(azure*lineAlpha,lineAlpha);
    float3 position=float3(input.uv,1);
    float divisor=dot(row2.xyz,position);
    float2 uv=float2(dot(row0.xyz,position),dot(row1.xyz,position))/max(divisor,0.000001);
    if(label.z>0 && inside)
        color=Over(float4(azure*label.z,label.z),color);
    if(grid.x>0) {
        float3 position=float3(input.uv,1);
        float divisor=dot(row2.xyz,position);
        float2 uv=float2(dot(row0.xyz,position),dot(row1.xyz,position))/max(divisor,0.000001);
        if(all(uv>=0) && all(uv<=1)) {
            float2 cell=abs(frac(uv*10+0.5)-0.5)/max(fwidth(uv*10),0.0001);
            float lines=1-smoothstep(0.5,1.5,min(cell.x,cell.y));
            float center=1-smoothstep(1,2,abs(length((uv-0.5)*float2(dimensions.x/dimensions.y,1))-0.18)/max(fwidth(uv.y),0.0001));
            float alpha=max(lines*0.28,center*0.55);
            color=Over(color,float4(float3(0.6,0.85,1)*alpha,alpha));
        }
    }
    float2 labelStart=label.xy-grid.yz*0.5;
    if(grid.w>0 && all(p>=labelStart) && all(p<labelStart+grid.yz))
        color=Over(color,labelTexture.SampleLevel(labelSampler,(p-labelStart)/grid.yz,0));
    [loop] for(int i=0;i<count;i++) {
        float4 vertexData=PointAt(i);
        float2 corner=vertexData.xy*(dimensions-1);
        int index=(int)vertexData.w;
        float radius=max(8,min(dimensions.x,dimensions.y)*0.014)*(1+0.12*sin(screen.z*4));
        float2 local=(p-corner)/radius;
        float d=length(local);
        float sphere=1-smoothstep(0.78,1,d);
        float glow=0.22*exp(-d*d/1.6);
        float alpha=saturate(0.7*sphere+glow)*screen.w;
        float light=0.35+0.65*saturate(dot(normalize(float3(local,sqrt(saturate(1-dot(local,local))))),normalize(float3(-0.4,-0.5,0.8))));
        float highlight=exp(-dot(local-float2(-0.27,-0.32),local-float2(-0.27,-0.32))*40)*0.7;
        float3 hue=index==(int)selection.x?azure:float3(1,0.08,0.14);
        color=Over(color,float4(saturate(hue*light+highlight)*alpha,alpha));
        if (index==(int)selection.x) {
            float2 label=clamp(corner+float2(24,-22),float2(12,12),dimensions-12);
            float digit=Number(p-label,index+1)*screen.w*0.85;
            color=Over(color,float4(digit,digit,digit,digit));
        }
        if (vertexData.z>0 && selection.z>=0 && selection.z<=1) {
            float flash=sin(selection.z*3.14159265)*exp(-dot(p-corner,p-corner)/2500)*0.85;
            float3 flashColor=selection.w>0?float3(1,1,1):float3(1,0.04,0.04);
            color=Over(color,float4(flashColor*flash,flash));
        }
    }
    return color;
}
