// Titan Orbit — cheap 3-layer parallax starfield.
// Client presentation only. One fullscreen-ish world quad; stars are hashed in the
// fragment shader (no ParticleSystem, no textures). C# writes _FollowXZ (accumulated
// ship travel) and _QuadScale each LateUpdate. GLES / WebGL: target 2.0, no
// MaterialPropertyBlock — same contract as TitanOrbit/SpaceBackgroundUnlit.
Shader "TitanOrbit/ParallaxStarfield"
{
    Properties
    {
        _Tint("Tint", Color) = (1, 1, 1, 1)
        _Brightness("Brightness", Float) = 1
        _Twinkle("Twinkle", Float) = 0.18
        // xy = accumulated wrap-safe ship travel (not raw world XZ).
        _FollowXZ("Follow XZ", Vector) = (0, 0, 0, 0)
        // x = world size of the background quad (keeps star density stable as the camera zooms).
        _QuadScale("Quad Scale", Vector) = (100, 100, 0, 0)
        // xy = occupancy 0-1 (chance a cell has a star). zw unused.
        _Occupancy("Occupancy Far/Mid/Near", Vector) = (0.55, 0.38, 0.28, 0)
        // xy zw packed: density, parallax, size (cell fraction), brightness.
        _LayerFar("Far Density/Parallax/Size/Bright", Vector) = (0.42, 0.0015, 0.028, 0.4)
        _LayerMid("Mid Density/Parallax/Size/Bright", Vector) = (0.22, 0.0035, 0.038, 0.7)
        _LayerNear("Near Density/Parallax/Size/Bright", Vector) = (0.11, 0.007, 0.05, 0.95)
        // URP classifies surface type from this (0 = opaque). Keeps the star quad out of transparent sorting.
        [HideInInspector] _Surface("Surface", Float) = 0
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            // Opaque + Background queue: URP must NOT put this huge follow-quad in the
            // transparent sort (that paints stars on top of ships / planets).
            "RenderType" = "Opaque"
            "Queue" = "Background+1"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            // Stamp stars in the background pass (after the nebula at 1000), then let
            // gameplay opaques cover them. ZTest Always so the opaque nebula does not
            // hide stars; ZWrite Off so ships / planets still win depth.
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One One

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Brightness;
                float _Twinkle;
                float4 _FollowXZ;
                float4 _QuadScale;
                float4 _Occupancy;
                float4 _LayerFar;
                float4 _LayerMid;
                float4 _LayerNear;
                float _Surface;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            // Cheap 2D hash in [0,1]. Stable per integer cell — same star every frame.
            float Hash21(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float2 Hash22(float2 p)
            {
                return float2(Hash21(p), Hash21(p + 17.13));
            }

            // One hashed star in a neighbor cell. Occupancy skips empty cells so the field
            // does not look like a regular grid. Size is a fraction of the cell.
            half StarAt(float2 cell, float2 f, float2 offset, float4 layer, float occupancy, float time)
            {
                float2 c = cell + offset;
                float keep = Hash21(c + 9.17);
                // Empty cell: most of the field stays black (cheap, avoids a regular grid).
                if (keep > occupancy)
                    return 0;

                float2 rnd = Hash22(c);
                // Keep the star inside the cell so the 3x3 window is enough.
                float2 starPos = lerp(0.22, 0.78, rnd);
                float2 d = (offset + starPos) - f;
                float dist = length(d);
                float size = layer.z * (0.55 + 0.45 * Hash21(c + 3.1));
                float star = saturate(1.0 - dist / max(size, 1e-4));
                star *= star;

                float twinkle = 1.0;
                if (_Twinkle > 1e-4)
                {
                    float phase = Hash21(c) * 6.2831853;
                    float rate = 1.6 + Hash21(c + 8.2) * 3.2;
                    twinkle = 1.0 - _Twinkle * 0.5 + _Twinkle * 0.5 * sin(time * rate + phase);
                }

                return (half)(star * layer.w * twinkle);
            }

            // One star layer. Cells are world-sized via density; only some cells spawn a star
            // (occupancy). Soft disc in cell space so we never sample a texture.
            // 3x3 neighborhood is unrolled so GLES / target 2.0 does not rely on dynamic loops.
            half LayerStars(float2 baseUv, float4 layer, float occupancy, float time)
            {
                float density = max(layer.x, 1e-4);
                float2 uv = baseUv * density + _FollowXZ.xy * layer.y;
                float2 cell = floor(uv);
                float2 f = frac(uv);
                half acc = 0;

                // Unrolled 3x3 so a star near a cell edge is not clipped.
                acc += StarAt(cell, f, float2(-1, -1), layer, occupancy, time);
                acc += StarAt(cell, f, float2( 0, -1), layer, occupancy, time);
                acc += StarAt(cell, f, float2( 1, -1), layer, occupancy, time);
                acc += StarAt(cell, f, float2(-1,  0), layer, occupancy, time);
                acc += StarAt(cell, f, float2( 0,  0), layer, occupancy, time);
                acc += StarAt(cell, f, float2( 1,  0), layer, occupancy, time);
                acc += StarAt(cell, f, float2(-1,  1), layer, occupancy, time);
                acc += StarAt(cell, f, float2( 0,  1), layer, occupancy, time);
                acc += StarAt(cell, f, float2( 1,  1), layer, occupancy, time);
                return acc;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Mesh UV is 0-1 on the follow quad. Convert to centered world-ish units so
                // zooming the camera (bigger quad) does not stretch the star grid.
                float2 baseUv = (input.uv - 0.5) * _QuadScale.x;
                float time = _Time.y;

                half stars = 0;
                stars += LayerStars(baseUv, _LayerFar, _Occupancy.x, time);
                stars += LayerStars(baseUv, _LayerMid, _Occupancy.y, time);
                stars += LayerStars(baseUv, _LayerNear, _Occupancy.z, time);

                half3 color = (half3)_Tint.rgb * (stars * (half)_Brightness);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
