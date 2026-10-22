Shader "Hidden/TitanOrbit/ShipFittedShadowApply"
{
    // Multiplies the ship-fitted shadow onto the already-lit opaque color.
    // The main directional cascades have already shaded every other caster.
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "ShipFittedShadowApply"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend DstColor Zero
            ColorMask RGB

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D_SHADOW(_ShipFittedShadowMap);
            SAMPLER_CMP(sampler_ShipFittedShadowMap);

            // xy: shadow uv, z: receiver depth in the fitted map.
            float4x4 _ShipFittedWorldToShadow;
            // x = 1/resolution, y = light shadow strength, z = receiver depth bias, w = shadowed color floor.
            float4 _ShipFittedParams;
            float4 _ShipFittedToLight;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float SampleFitted(float3 coord)
            {
                float texel = _ShipFittedParams.x;
                float atten = 0.0;
                // 3x3 PCF. At 1024 over a 2-unit map this blur is a few millimeters, not a blob.
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float3 tap = coord + float3(float2(x, y) * texel, 0.0);
                        atten += SAMPLE_TEXTURE2D_SHADOW(_ShipFittedShadowMap, sampler_ShipFittedShadowMap, tap);
                    }
                }
                return atten * (1.0 / 9.0);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float depth = SampleSceneDepth(input.uv);
                #if UNITY_REVERSED_Z
                    if (depth <= 0.0001)
                        return half4(1.0, 1.0, 1.0, 1.0);
                #else
                    if (depth >= 0.9999)
                        return half4(1.0, 1.0, 1.0, 1.0);
                #endif

                float3 positionWS = ComputeWorldSpacePosition(input.uv, depth, UNITY_MATRIX_I_VP);
                float4 shadowCoord = mul(_ShipFittedWorldToShadow, float4(positionWS, 1.0));
                shadowCoord.xyz /= max(shadowCoord.w, 1e-5);

                if (any(shadowCoord.xy < 0.0) || any(shadowCoord.xy > 1.0))
                    return half4(1.0, 1.0, 1.0, 1.0);

                float3 dpdx = ddx(positionWS);
                float3 dpdy = ddy(positionWS);
                float3 normalWS = normalize(cross(dpdx, dpdy));
                float3 viewWS = _WorldSpaceCameraPos.xyz - positionWS;
                if (dot(normalWS, viewWS) < 0.0)
                    normalWS = -normalWS;

                float ndotl = saturate(dot(normalWS, _ShipFittedToLight.xyz));
                float slope = 1.0 - ndotl;
                shadowCoord.z += _ShipFittedParams.z + slope * (_ShipFittedParams.z * 2.0);

                float atten = SampleFitted(shadowCoord.xyz);
                float2 edge = abs(shadowCoord.xy * 2.0 - 1.0);
                float edgeFade = saturate((1.0 - max(edge.x, edge.y)) * 12.0);
                atten = lerp(1.0, atten, edgeFade);

                float strength = saturate(_ShipFittedParams.y) * ndotl;
                float floorLit = saturate(_ShipFittedParams.w);
                float shade = lerp(1.0, lerp(floorLit, 1.0, atten), strength);
                return half4(shade, shade, shade, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
