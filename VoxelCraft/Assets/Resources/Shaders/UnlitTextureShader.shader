Shader "Voxel/UnlitTexture"
{
    // Self-contained unlit texture shader for box-model creatures and props.
    // Lives in Resources so it is loaded with Resources.Load<Shader> and never
    // stripped from builds (unlike Shader.Find("Unlit/Texture")).
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
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
            float4 _VoxelFogRange;
            half4 _VoxelFogColor;
            half _VoxelDayBrightness;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                half2 uv : TEXCOORD0;
                half fog : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float dist = distance(worldPos, _WorldSpaceCameraPos);
                float range = max(_VoxelFogRange.y - _VoxelFogRange.x, 0.001);
                o.fog = saturate((dist - _VoxelFogRange.x) / range);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                c.rgb *= _VoxelDayBrightness;          // creatures dim at night too
                c.rgb = lerp(c.rgb, _VoxelFogColor.rgb, i.fog);
                return fixed4(c.rgb, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
