Shader "Voxel/Blocks"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _VoxelFogRange;   // x = start, y = end (set via Shader.SetGlobalVector)
            half4 _VoxelFogColor;    // set via Shader.SetGlobalColor

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                half2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                half fog : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float dist = distance(worldPos, _WorldSpaceCameraPos);
                float range = max(_VoxelFogRange.y - _VoxelFogRange.x, 0.001);
                o.fog = saturate((dist - _VoxelFogRange.x) / range);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - 0.5);                       // cutout for glass frames etc.
                c.rgb = lerp(c.rgb, _VoxelFogColor.rgb, i.fog);
                return fixed4(c.rgb, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
