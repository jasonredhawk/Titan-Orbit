// Minimap disc and rim.
// The expanded map is most of the screen. A small circle texture stretched to that
// size shows stair-steps. This shader draws the circle from the image's UV each
// pixel, so the edge stays smooth at any widget size.
// Paired with MinimapController.SetupCircularBackground / SetupCircularBorder / SetupMask.
Shader "TitanOrbit/UI/MinimapDisc"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        // 0 = filled disc. Greater than 0 = hole, so the same shader can draw the rim.
        _InnerRadius ("Inner radius (UV)", Float) = 0
        // 0.5 reaches the middle of each edge of the rect. Corners stay outside the circle.
        _OuterRadius ("Outer radius (UV)", Float) = 0.5

        // Stencil block is what Unity's Mask component writes. Without these properties
        // the minimap content would not clip to the circle.
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        // [UNITY] Canvas sets this so overlay and camera-space UI both depth-test correctly.
        ZTest [unity_GUIZTestMode]
        // Premultiplied, same as UI/Default. The fragment multiplies rgb by alpha.
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            // fwidth (the smooth edge) needs shader model 3.0.
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct AppData
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VertOut
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 mask : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float _InnerRadius;
            float _OuterRadius;
            float4 _ClipRect;
            float _UIMaskSoftnessX;
            float _UIMaskSoftnessY;

            VertOut Vert(AppData v)
            {
                VertOut o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

                // Same soft rect-mask math as UI/Default, so a parent RectMask2D still clips us.
                float2 pixelSize = o.pos.w.xx;
                pixelSize /= abs(mul((float2x2)UNITY_MATRIX_P, _ScreenParams.xy));
                float4 clampedRect = clamp(_ClipRect, -2e10, 2e10);
                o.mask = float4(
                    v.vertex.xy * 2 - clampedRect.xy - clampedRect.zw,
                    0.25 / (0.25 * half2(_UIMaskSoftnessX, _UIMaskSoftnessY) + abs(pixelSize)));

                // Image.color is authored like a texture (gamma). This project is linear,
                // and the old rim was an sRGB texture, so convert the same way a texture sample would.
                if (!IsGammaSpace())
                    v.color.rgb = UIGammaToLinear(v.color.rgb);
                o.color = v.color;
                return o;
            }

            float4 Frag(VertOut i) : SV_Target
            {
                // UV 0.5 is the center of the rect. Distance is in "fraction of the rect width".
                float dist = length(i.uv - 0.5);
                // About one and a third screen pixels of fade. Wide enough to hide stair-steps,
                // narrow enough that the rim still reads as a crisp stroke.
                float aa = max(fwidth(dist) * 1.35, 1e-4);

                // Outer edge: solid inside the radius, clear outside, half-alpha on the radius.
                float outerA = saturate(((_OuterRadius - dist) / aa) + 0.5);
                // Inner edge punches the hole for the rim. A radius of 0 means "filled disc"
                // (the center must stay solid — a hole of radius 0 would dim the middle).
                float innerA = _InnerRadius <= 0.0001
                    ? 1.0
                    : saturate(((dist - _InnerRadius) / aa) + 0.5);
                float shape = outerA * innerA;

                float4 color = i.color;
                color.a *= shape;

                #ifdef UNITY_UI_CLIP_RECT
                half2 m = saturate((_ClipRect.zw - _ClipRect.xy - abs(i.mask.xy)) * i.mask.zw);
                color.a *= m.x * m.y;
                #endif

                // Mask turns this on. Pixels outside the circle are discarded, so they do not
                // write the stencil. That is what clips the blips to the disc.
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif

                color.rgb *= color.a;
                return color;
            }
            ENDCG
        }
    }
}
