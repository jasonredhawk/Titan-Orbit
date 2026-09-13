// Titan Orbit — cheap 3-layer parallax starfield.
// Client presentation only. One fullscreen-ish world quad; stars are hashed in the
// fragment shader (no ParticleSystem, no textures). Each star picks a hashed silhouette
// (disc, diamond, oval, plus, 4-point sparkle, needle). Two FBM gas wisps composite
// after the stars so the nebula sits in front. C# writes _FollowXZ (accumulated
// ship travel) and _QuadScale each LateUpdate. GLES / WebGL: target 2.0, no
// MaterialPropertyBlock — same contract as TitanOrbit/SpaceBackgroundUnlit.
Shader "TitanOrbit/ParallaxStarfield"
{
    Properties
    {
        _Tint("Tint", Color) = (1, 1, 1, 1)
        _Brightness("Brightness", Float) = 0.9
        _Twinkle("Twinkle", Float) = 0.18
        // xy = accumulated wrap-safe ship travel (not raw world XZ).
        _FollowXZ("Follow XZ", Vector) = (0, 0, 0, 0)
        // x = world size of the background quad (keeps star density stable as the camera zooms).
        _QuadScale("Quad Scale", Vector) = (100, 100, 0, 0)
        // xy = occupancy 0-1 (chance a cell has a star). zw unused.
        _Occupancy("Occupancy Far/Mid/Near", Vector) = (0.55, 0.38, 0.28, 0)
        // xy zw packed: density, parallax, size (cell fraction), brightness.
        _LayerFar("Far Density/Parallax/Size/Bright", Vector) = (0.42, 0.0022, 0.028, 0.45)
        _LayerMid("Mid Density/Parallax/Size/Bright", Vector) = (0.22, 0.005, 0.038, 0.7)
        _LayerNear("Near Density/Parallax/Size/Bright", Vector) = (0.11, 0.01, 0.05, 0.95)
        _GasTintA("Gas Tint A", Color) = (0.32, 0.42, 0.82, 1)
        _GasTintB("Gas Tint B", Color) = (0.58, 0.26, 0.52, 1)
        _GasIntensity("Gas Intensity", Float) = 0.36
        _GasOcclude("Gas Occlude Stars", Float) = 0.42
        // xy = far/near world scale, zw unused.
        _GasScale("Gas Scale Far/Near", Vector) = (0.024, 0.038, 0, 0)
        // xy = far/near parallax (faster than stars so clouds read in front).
        _GasParallax("Gas Parallax Far/Near", Vector) = (0.014, 0.022, 0, 0)
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

            // Stars + procedural gas in the background pass, then gameplay opaques cover
            // them. ZWrite Off so ships / planets still win depth.
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
                float4 _GasTintA;
                float4 _GasTintB;
                float _GasIntensity;
                float _GasOcclude;
                float4 _GasScale;
                float4 _GasParallax;
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

            // Soft 4-spike (axis-aligned plus). Core disc keeps a bright center so thin
            // arms still read at a few pixels. p is already scaled by 1/size.
            float ShapePlus(float2 p, float armThin)
            {
                float ax = abs(p.x);
                float ay = abs(p.y);
                float arms = saturate(1.0 - min(ax, ay) * armThin) * saturate(1.0 - max(ax, ay));
                float core = saturate(1.0 - length(p) * 1.35);
                core *= core;
                return max(arms * arms, core * 0.7);
            }

            // One hashed silhouette in cell space. pick is [0,1) from the cell hash so
            // neighboring stars do not all look like the same disc. No textures.
            float StarShape(float2 d, float size, float pick, float tilt)
            {
                float2 p = d / max(size, 1e-4);
                // Per-star yaw in [0, 2π) — any angle, not just 0° / 45°.
                // Discs ignore this (radial); plus / oval / sparkle need it so the field
                // does not lock to a diagonal grid.
                float s;
                float c;
                sincos(tilt * 6.2831853, s, c);
                p = float2(c * p.x - s * p.y, s * p.x + c * p.y);

                float r = length(p);
                float disc = saturate(1.0 - r);
                disc *= disc;

                float shape;
                if (pick < 0.20)
                {
                    // Pinpoint disc — still used so the field is not all spikes.
                    shape = disc;
                }
                else if (pick < 0.36)
                {
                    // Diamond (Manhattan).
                    float diamond = saturate(1.0 - (abs(p.x) + abs(p.y)));
                    shape = diamond * diamond;
                }
                else if (pick < 0.52)
                {
                    // Soft oval: stretch X or Y so some stars feel elongated.
                    float2 pe = float2(p.x * 1.65, p.y * 0.72);
                    float oval = saturate(1.0 - length(pe));
                    shape = oval * oval;
                }
                else if (pick < 0.68)
                {
                    // Plus / cross.
                    shape = ShapePlus(p, 7.0);
                }
                else if (pick < 0.84)
                {
                    // Four-point sparkle: plus plus an X.
                    float2 pd = float2(p.x + p.y, p.x - p.y) * 0.70710678;
                    shape = max(ShapePlus(p, 8.0), ShapePlus(pd, 8.0));
                }
                else
                {
                    // Longer needle spikes (classic distant “twinkle” star).
                    shape = ShapePlus(p, 11.0);
                }

                return shape;
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
                float size = layer.z * (0.55 + 0.45 * Hash21(c + 3.1));
                // Spikes need a little extra reach so plus / sparkle arms stay visible.
                float pick = Hash21(c + 21.7);
                float tilt = Hash21(c + 5.5);
                float star = StarShape(d, size, pick, tilt);

                // Per-star dimness (stable with the cell). Squared hash = many faint
                // pinpoints and a few brighter ones, not one flat brightness for the field.
                float dimPick = Hash21(c + 13.91);
                float dim = lerp(0.45, 1.15, dimPick * dimPick);

                float twinkle = 1.0;
                if (_Twinkle > 1e-4)
                {
                    float phase = Hash21(c) * 6.2831853;
                    float rate = 1.6 + Hash21(c + 8.2) * 3.2;
                    twinkle = 1.0 - _Twinkle * 0.5 + _Twinkle * 0.5 * sin(time * rate + phase);
                }

                return (half)(star * layer.w * twinkle * dim);
            }

            // One star layer. Cells are world-sized via density; only some cells spawn a star
            // (occupancy). Shape is hashed in cell space so we never sample a texture.
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

            // Value noise in [0,1]. Smooth hermite so the gas looks like smoke, not cells.
            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Four octaves, unrolled for GLES / target 2.0. Extra octave = fine mist, not blobs.
            float Fbm4(float2 p)
            {
                float v = ValueNoise(p) * 0.5;
                p *= 2.02;
                v += ValueNoise(p) * 0.25;
                p *= 2.03;
                v += ValueNoise(p) * 0.125;
                p *= 2.01;
                v += ValueNoise(p) * 0.0625;
                return v;
            }

            // Ridged noise — thin filaments instead of round islands.
            float Ridge(float n)
            {
                n = 1.0 - abs(n * 2.0 - 1.0);
                return n * n;
            }

            // Domain-warped veil + ridged threads. Soft powers keep the field misty;
            // we do not hard-threshold into blobs.
            void SampleGas(float2 baseUv, out float gasAmt, out half3 gasCol)
            {
                float2 uvFar = baseUv * max(_GasScale.x, 1e-4) + _FollowXZ.xy * _GasParallax.x;
                float2 uvNear = baseUv * max(_GasScale.y, 1e-4) + _FollowXZ.xy * _GasParallax.y + float2(17.1, 9.3);

                // Warp the sample domain so clouds stretch into flowing ribbons.
                float2 warp = float2(
                    ValueNoise(uvFar * 0.65 + float2(3.1, 8.7)),
                    ValueNoise(uvFar * 0.65 + float2(11.2, 2.4))) - 0.5;
                uvFar += warp * 1.55;
                uvNear += warp * 2.1;

                float fFar = Fbm4(uvFar);
                float fNear = Fbm4(uvNear);
                float ridge = Ridge(fNear);
                float ridgeFar = Ridge(fFar);

                // Wide faint haze + mid wisps + thin bright threads.
                float veil = pow(saturate(fFar), 1.45) * 0.42;
                float wisps = pow(saturate(fNear * ridgeFar), 1.25) * 0.58;
                float threads = pow(saturate(ridge), 2.35) * 0.5;
                gasAmt = saturate(veil + wisps + threads);
                gasCol = (half3)lerp(_GasTintA.rgb, _GasTintB.rgb, saturate(fNear * 0.55 + ridge * 0.45));
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

                float gasAmt;
                half3 gasCol;
                SampleGas(baseUv, gasAmt, gasCol);
                // Clouds in front: hide stars where the gas is thick.
                stars *= (half)(1.0 - saturate(gasAmt * _GasOcclude));

                half3 color = (half3)_Tint.rgb * (stars * (half)_Brightness);
                color += gasCol * (half)(gasAmt * _GasIntensity);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
