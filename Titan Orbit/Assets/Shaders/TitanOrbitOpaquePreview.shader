// Your Ship card. The portrait camera writes color and leaves alpha at 0.
// A normal UI image multiplies by that alpha, so the live picture is invisible.
// This draws the camera color as a solid rectangle.
Shader "TitanOrbit/UI/OpaquePreview"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend One Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;

            struct AppData
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct VertOut
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            VertOut Vert(AppData v)
            {
                VertOut o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 Frag(VertOut i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                return fixed4(c.rgb, 1);
            }
            ENDCG
        }
    }
}
