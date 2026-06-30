Shader "WoT/TerrainChunkMesh"
{
    Properties
    {
        [HideInInspector] _Blend0  ("Blend0",  2D) = "black" {}
        [HideInInspector] _Blend1  ("Blend1",  2D) = "black" {}
        [HideInInspector] _Blend2  ("Blend2",  2D) = "black" {}
        [HideInInspector] _Blend3  ("Blend3",  2D) = "black" {}
        [HideInInspector] _Blend4  ("Blend4",  2D) = "black" {}
        [HideInInspector] _Blend5  ("Blend5",  2D) = "black" {}
        [HideInInspector] _Blend6  ("Blend6",  2D) = "black" {}
        [HideInInspector] _Blend7  ("Blend7",  2D) = "black" {}

        [HideInInspector] _Splat0  ("Splat0",  2D) = "white" {}
        [HideInInspector] _Splat1  ("Splat1",  2D) = "white" {}
        [HideInInspector] _Splat2  ("Splat2",  2D) = "white" {}
        [HideInInspector] _Splat3  ("Splat3",  2D) = "white" {}
        [HideInInspector] _Splat4  ("Splat4",  2D) = "white" {}
        [HideInInspector] _Splat5  ("Splat5",  2D) = "white" {}
        [HideInInspector] _Splat6  ("Splat6",  2D) = "white" {}
        [HideInInspector] _Splat7  ("Splat7",  2D) = "white" {}
        [HideInInspector] _Splat8  ("Splat8",  2D) = "white" {}
        [HideInInspector] _Splat9  ("Splat9",  2D) = "white" {}
        [HideInInspector] _Splat10 ("Splat10", 2D) = "white" {}
        [HideInInspector] _Splat11 ("Splat11", 2D) = "white" {}
        [HideInInspector] _Splat12 ("Splat12", 2D) = "white" {}
        [HideInInspector] _Splat13 ("Splat13", 2D) = "white" {}
        [HideInInspector] _Splat14 ("Splat14", 2D) = "white" {}
        [HideInInspector] _Splat15 ("Splat15", 2D) = "white" {}

        [HideInInspector][Normal] _Normal0  ("Normal0",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal1  ("Normal1",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal2  ("Normal2",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal3  ("Normal3",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal4  ("Normal4",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal5  ("Normal5",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal6  ("Normal6",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal7  ("Normal7",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal8  ("Normal8",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal9  ("Normal9",  2D) = "bump" {}
        [HideInInspector][Normal] _Normal10 ("Normal10", 2D) = "bump" {}
        [HideInInspector][Normal] _Normal11 ("Normal11", 2D) = "bump" {}
        [HideInInspector][Normal] _Normal12 ("Normal12", 2D) = "bump" {}
        [HideInInspector][Normal] _Normal13 ("Normal13", 2D) = "bump" {}
        [HideInInspector][Normal] _Normal14 ("Normal14", 2D) = "bump" {}
        [HideInInspector][Normal] _Normal15 ("Normal15", 2D) = "bump" {}

        [HideInInspector] _GlobalMap ("Global AM", 2D) = "white" {}
        _NumLayers      ("Num Layers",         Float) = 0
        _NumBlends      ("Num Blend Textures",  Float) = 0
        _NewBlendFormat ("New Blend Format",    Float) = 1
        _UseGlobalMap   ("Use Global AM",       Float) = 0
        _Brightness     ("Preview Brightness",  Float) = 1
        _BumpScale      ("Normal Scale",        Float) = 1.0
        _UseNormalMaps  ("Use Normal Maps",     Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        // ── Forward Lit ───────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            // URP shadow keywords — без них тени не работают
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Blend0); SAMPLER(sampler_Blend0);
            TEXTURE2D(_Blend1); TEXTURE2D(_Blend2); TEXTURE2D(_Blend3);
            TEXTURE2D(_Blend4); TEXTURE2D(_Blend5); TEXTURE2D(_Blend6); TEXTURE2D(_Blend7);
            TEXTURE2D(_GlobalMap);

            TEXTURE2D(_Splat0);  TEXTURE2D(_Splat1);  TEXTURE2D(_Splat2);  TEXTURE2D(_Splat3);
            TEXTURE2D(_Splat4);  TEXTURE2D(_Splat5);  TEXTURE2D(_Splat6);  TEXTURE2D(_Splat7);
            TEXTURE2D(_Splat8);  TEXTURE2D(_Splat9);  TEXTURE2D(_Splat10); TEXTURE2D(_Splat11);
            TEXTURE2D(_Splat12); TEXTURE2D(_Splat13); TEXTURE2D(_Splat14); TEXTURE2D(_Splat15);
            SAMPLER(sampler_Splat0);

            TEXTURE2D(_Normal0);  TEXTURE2D(_Normal1);  TEXTURE2D(_Normal2);  TEXTURE2D(_Normal3);
            TEXTURE2D(_Normal4);  TEXTURE2D(_Normal5);  TEXTURE2D(_Normal6);  TEXTURE2D(_Normal7);
            TEXTURE2D(_Normal8);  TEXTURE2D(_Normal9);  TEXTURE2D(_Normal10); TEXTURE2D(_Normal11);
            TEXTURE2D(_Normal12); TEXTURE2D(_Normal13); TEXTURE2D(_Normal14); TEXTURE2D(_Normal15);
            SAMPLER(sampler_Normal0);

            CBUFFER_START(UnityPerMaterial)
                float4 _LayerU[16];
                float4 _LayerV[16];
                float4 _LayerMode[16];
                float4 _LayerMap[16];
                float4 _ChunkUV_ST;
                float4 _TerrainGlobal;
                float  _NumLayers;
                float  _NumBlends;
                float  _NewBlendFormat;
                float  _UseGlobalMap;
                float  _Brightness;
                float  _BumpScale;
                float  _UseNormalMaps;
            CBUFFER_END

            struct Attributes {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
            };
            struct Varyings {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 uv          : TEXCOORD2;
                float3 tangentWS   : TEXCOORD3;
                float3 bitangentWS : TEXCOORD4;
                float4 shadowCoord  : TEXCOORD5;
                float  fogFactor    : TEXCOORD6;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS  = p.positionCS;
                OUT.positionWS   = p.positionWS;
                OUT.normalWS     = TransformObjectToWorldNormal(IN.normalOS);
                OUT.tangentWS    = TransformObjectToWorldDir(IN.tangentOS.xyz);
                OUT.bitangentWS  = cross(OUT.normalWS, OUT.tangentWS) * IN.tangentOS.w;
                OUT.uv        = IN.uv;
                OUT.fogFactor = ComputeFogFactor(p.positionCS.z);
                return OUT;
            }

            float2 WrapWoT(float2 uv)
            {
                const float mn = 0.0625, mx = 0.9375, span = mx - mn;
                return mn + frac((uv - mn) / span) * span;
            }
            float2 ChunkMapUV(float2 localUV) { return localUV * _ChunkUV_ST.xy + _ChunkUV_ST.zw; }

            float2 LayerUV(int idx, float3 posWS, float2 localUV)
            {
                if (_LayerMode[idx].x > 0.5) return ChunkMapUV(localUV);
                float2 p = posWS.xz;
                if (_NewBlendFormat > 0.5)
                {
                    float numX = rcp(_ChunkUV_ST.x);
                    float csx  = _TerrainGlobal.z / max(numX, 1.0);
                    float mnX  = _TerrainGlobal.x / max(csx, 1e-5);
                    float mxX  = mnX + numX - 1.0;
                    float cx   = round(_ChunkUV_ST.z * numX + mnX);
                    p.x = (mnX + mxX - cx + localUV.x) * csx;
                }
                float a = _LayerMap[idx].x;
                float2 r = float2(p.x * cos(a) - p.y * sin(a), p.x * sin(a) + p.y * cos(a));
                return WrapWoT(r / _LayerMap[idx].zw);
            }

            // Используем явно инициализированную переменную result = default
            // чтобы избежать warning "potentially uninitialized variable"
            float4 SampleBlend(uint idx, float2 uv)
            {
                float4 result = float4(0,0,0,0);
                if      (idx == 0u) result = SAMPLE_TEXTURE2D(_Blend0, sampler_Blend0, uv);
                else if (idx == 1u) result = SAMPLE_TEXTURE2D(_Blend1, sampler_Blend0, uv);
                else if (idx == 2u) result = SAMPLE_TEXTURE2D(_Blend2, sampler_Blend0, uv);
                else if (idx == 3u) result = SAMPLE_TEXTURE2D(_Blend3, sampler_Blend0, uv);
                else if (idx == 4u) result = SAMPLE_TEXTURE2D(_Blend4, sampler_Blend0, uv);
                else if (idx == 5u) result = SAMPLE_TEXTURE2D(_Blend5, sampler_Blend0, uv);
                else if (idx == 6u) result = SAMPLE_TEXTURE2D(_Blend6, sampler_Blend0, uv);
                else if (idx == 7u) result = SAMPLE_TEXTURE2D(_Blend7, sampler_Blend0, uv);
                return result;
            }

            float3 SampleSplat(uint idx, float3 posWS, float2 localUV)
            {
                float2 uv     = LayerUV((int)idx, posWS, localUV);
                float3 result = float3(1,1,1);
                if      (idx ==  0u) result = SAMPLE_TEXTURE2D(_Splat0,  sampler_Splat0, uv).rgb;
                else if (idx ==  1u) result = SAMPLE_TEXTURE2D(_Splat1,  sampler_Splat0, uv).rgb;
                else if (idx ==  2u) result = SAMPLE_TEXTURE2D(_Splat2,  sampler_Splat0, uv).rgb;
                else if (idx ==  3u) result = SAMPLE_TEXTURE2D(_Splat3,  sampler_Splat0, uv).rgb;
                else if (idx ==  4u) result = SAMPLE_TEXTURE2D(_Splat4,  sampler_Splat0, uv).rgb;
                else if (idx ==  5u) result = SAMPLE_TEXTURE2D(_Splat5,  sampler_Splat0, uv).rgb;
                else if (idx ==  6u) result = SAMPLE_TEXTURE2D(_Splat6,  sampler_Splat0, uv).rgb;
                else if (idx ==  7u) result = SAMPLE_TEXTURE2D(_Splat7,  sampler_Splat0, uv).rgb;
                else if (idx ==  8u) result = SAMPLE_TEXTURE2D(_Splat8,  sampler_Splat0, uv).rgb;
                else if (idx ==  9u) result = SAMPLE_TEXTURE2D(_Splat9,  sampler_Splat0, uv).rgb;
                else if (idx == 10u) result = SAMPLE_TEXTURE2D(_Splat10, sampler_Splat0, uv).rgb;
                else if (idx == 11u) result = SAMPLE_TEXTURE2D(_Splat11, sampler_Splat0, uv).rgb;
                else if (idx == 12u) result = SAMPLE_TEXTURE2D(_Splat12, sampler_Splat0, uv).rgb;
                else if (idx == 13u) result = SAMPLE_TEXTURE2D(_Splat13, sampler_Splat0, uv).rgb;
                else if (idx == 14u) result = SAMPLE_TEXTURE2D(_Splat14, sampler_Splat0, uv).rgb;
                else if (idx == 15u) result = SAMPLE_TEXTURE2D(_Splat15, sampler_Splat0, uv).rgb;
                return result;
            }

            float3 SampleNormal(uint idx, float3 posWS, float2 localUV)
            {
                float2 uv     = LayerUV((int)idx, posWS, localUV);
                float3 result = float3(0, 0, 1);
                if      (idx ==  0u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal0,  sampler_Normal0, uv), _BumpScale);
                else if (idx ==  1u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal1,  sampler_Normal0, uv), _BumpScale);
                else if (idx ==  2u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal2,  sampler_Normal0, uv), _BumpScale);
                else if (idx ==  3u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal3,  sampler_Normal0, uv), _BumpScale);
                else if (idx ==  4u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal4,  sampler_Normal0, uv), _BumpScale);
                else if (idx ==  5u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal5,  sampler_Normal0, uv), _BumpScale);
                else if (idx ==  6u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal6,  sampler_Normal0, uv), _BumpScale);
                else if (idx ==  7u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal7,  sampler_Normal0, uv), _BumpScale);
                else if (idx ==  8u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal8,  sampler_Normal0, uv), _BumpScale);
                else if (idx ==  9u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal9,  sampler_Normal0, uv), _BumpScale);
                else if (idx == 10u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal10, sampler_Normal0, uv), _BumpScale);
                else if (idx == 11u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal11, sampler_Normal0, uv), _BumpScale);
                else if (idx == 12u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal12, sampler_Normal0, uv), _BumpScale);
                else if (idx == 13u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal13, sampler_Normal0, uv), _BumpScale);
                else if (idx == 14u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal14, sampler_Normal0, uv), _BumpScale);
                else if (idx == 15u) result = UnpackNormalScale(SAMPLE_TEXTURE2D(_Normal15, sampler_Normal0, uv), _BumpScale);
                return result;
            }

            float LayerWeight(uint layerIdx, float2 blendUV)
            {
                uint blendIdx = layerIdx >> 1u;
                if (blendIdx >= (uint)_NumBlends) return 0.0;
                float4 b = SampleBlend(blendIdx, blendUV);
                return (layerIdx & 1u) == 0u ? b.a : b.g;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 blendUV = float2(IN.uv.x, 1.0 - IN.uv.y);
                uint   n       = (uint)min(_NumLayers, 16.0);

                float3 albedo   = float3(0,0,0);
                float3 tsNormal = float3(0,0,0);
                float  wsum     = 0;

                [loop] for (uint i = 0u; i < 16u; i++)
                {
                    if (i >= n) break;

                    float w;
                    if (_NewBlendFormat > 0.5)
                    {
                        w = LayerWeight(i, blendUV);
                        albedo   += SampleSplat(i, IN.positionWS, IN.uv) * w;
                        if (_UseNormalMaps > 0.5)
                            tsNormal += SampleNormal(i, IN.positionWS, IN.uv) * w;
                    }
                    else
                    {
                        if (i >= (uint)_NumBlends) break;
                        float3 wb = SampleBlend(i, blendUV).rgb;
                        w = max(wb.r, max(wb.g, wb.b));
                        albedo   += SampleSplat(i, IN.positionWS, IN.uv) * wb;
                        if (_UseNormalMaps > 0.5)
                            tsNormal += SampleNormal(i, IN.positionWS, IN.uv) * w;
                    }
                    wsum += w;
                }

                if (wsum <= 1e-4)
                {
                    albedo   = SampleSplat(0u, IN.positionWS, IN.uv);
                    tsNormal = _UseNormalMaps > 0.5 ? SampleNormal(0u, IN.positionWS, IN.uv) : float3(0,0,1);
                }

                if (_UseGlobalMap > 0.5)
                    albedo *= SAMPLE_TEXTURE2D(_GlobalMap, sampler_Blend0, saturate(ChunkMapUV(IN.uv))).rgb;

                float3 nWS;
                if (_UseNormalMaps > 0.5 && dot(tsNormal, tsNormal) > 1e-6)
                {
                    tsNormal = normalize(tsNormal);
                    nWS = normalize(tsNormal.x * normalize(IN.tangentWS) +
                                    tsNormal.y * normalize(IN.bitangentWS) +
                                    tsNormal.z * normalize(IN.normalWS));
                }
                else
                {
                    nWS = normalize(IN.normalWS);
                }

                // Вычисляем shadowCoord в fragment — точный каскад без видимых кругов
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = ComputeScreenPos(TransformWorldToHClip(IN.positionWS));
                #elif defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    float4 shadowCoord = float4(0,0,0,0);
                #endif
                Light mainLight = GetMainLight(shadowCoord);
                float ndotl = saturate(dot(nWS, mainLight.direction));
                float3 litC = albedo * (mainLight.color * ndotl * mainLight.shadowAttenuation + unity_AmbientSky.rgb + 0.20) * _Brightness;
                litC = MixFog(litC, IN.fogFactor);
                return half4(litC, 1);
            }
            ENDHLSL
        }

        // ── Shadow Caster ─────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Bias значения — стандартные для URP
            // Depth bias: отодвигает shadow map от поверхности
            // Normal bias: смещает вдоль нормали чтобы убрать "сферу" вокруг объектов
            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttr
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct ShadowVary
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float3 ApplyShadowBiasWS(float3 posWS, float3 normalWS, float3 lightDir)
            {
                // Normal bias: сдвиг вдоль нормали пропорционально углу света
                // Убирает "тёмную сферу" вокруг камеры и self-shadowing
                float normalBias = 0.02;
                float depthBias  = 0.001;
                float invNdotL   = 1.0 - saturate(dot(normalWS, lightDir));
                posWS += normalWS * invNdotL * normalBias;
                posWS -= lightDir * depthBias;
                return posWS;
            }

            ShadowVary ShadowVert(ShadowAttr IN)
            {
                ShadowVary OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 nrmWS = TransformObjectToWorldNormal(IN.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDir = normalize(_LightPosition - posWS);
            #else
                float3 lightDir = _LightDirection;
            #endif

                posWS = ApplyShadowBiasWS(posWS, nrmWS, lightDir);

                float4 posCS = TransformWorldToHClip(posWS);

                // Зажимаем z чтобы убрать пропадание теней на near plane
            #if UNITY_REVERSED_Z
                posCS.z = min(posCS.z, posCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
                posCS.z = max(posCS.z, posCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif

                OUT.positionCS = posCS;
                return OUT;
            }

            half4 ShadowFrag(ShadowVary IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Lit"
}
