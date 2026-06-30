Shader "WoT/ObjectPBS"
{
    Properties
    {
        _MainTex      ("Diffuse",             2D)    = "white" {}
        _Tile0        ("Tile 0",              2D)    = "white" {}
        _Tile1        ("Tile 1",              2D)    = "white" {}
        _Tile2        ("Tile 2",              2D)    = "white" {}
        _BlendMask    ("Blend Mask",          2D)    = "black" {}
        _AtlasTex     ("Atlas Albedo/Height", 2D)    = "white" {}
        _AtlasBlend   ("Atlas Blend",         2D)    = "black" {}
        [Normal] _NormalMap ("Normal Map",    2D)    = "bump"  {}
        _BumpScale    ("Normal Scale",       Float)  = 1.0
        _ObjectColor  ("Object Color",       Color)  = (1,1,1,1)
        _Tile0Tint    ("Tile 0 Tint",        Vector) = (1,1,1,1)
        _Tile1Tint    ("Tile 1 Tint",        Vector) = (1,1,1,1)
        _Tile2Tint    ("Tile 2 Tint",        Vector) = (1,1,1,1)
        _AtlasIndexes ("Atlas Indexes",      Vector) = (0,1,2,3)
        _AtlasGrid    ("Atlas Grid",         Vector) = (1,1,0,0)
        _UseUv2Blend  ("Use UV2 For Blend",  Float)  = 1
        _Mode         ("Mode",               Float)  = 0
        _Brightness   ("Preview Brightness", Float)  = 1

        _Cutoff       ("Alpha Cutoff",       Float)  = 0.5
        _AlphaClip    ("Alpha Clip Enable",  Float)  = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _SHADOW_CASCADES_BLEND
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);   SAMPLER(sampler_MainTex);
            TEXTURE2D(_Tile0);     TEXTURE2D(_Tile1); TEXTURE2D(_Tile2);
            TEXTURE2D(_BlendMask);
            TEXTURE2D(_AtlasTex);  TEXTURE2D(_AtlasBlend);
            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Tile0_ST;   float4 _Tile1_ST;   float4 _Tile2_ST;
                float4 _BlendMask_ST;
                float4 _AtlasTex_ST; float4 _AtlasBlend_ST;
                float4 _NormalMap_ST;
                float4 _ObjectColor;
                float4 _Tile0Tint;  float4 _Tile1Tint;  float4 _Tile2Tint;
                float4 _AtlasIndexes;
                float4 _AtlasGrid;
                float  _UseUv2Blend;
                float  _Mode;
                float  _Brightness;
                float  _BumpScale;
                float  _Cutoff;
                float  _AlphaClip;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 uv2        : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS  : SV_POSITION;
                float3 positionWS   : TEXCOORD0;
                float3 normalWS     : TEXCOORD1;
                float2 uv           : TEXCOORD2;
                float2 uv2          : TEXCOORD3;
                float3 tangentWS    : TEXCOORD4;
                float3 bitangentWS  : TEXCOORD5;
                float  fogFactor    : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   nrmInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionHCS  = posInputs.positionCS;
                OUT.positionWS   = posInputs.positionWS;
                OUT.fogFactor    = ComputeFogFactor(posInputs.positionCS.z);
                OUT.normalWS     = nrmInputs.normalWS;
                OUT.tangentWS    = nrmInputs.tangentWS;
                OUT.bitangentWS  = nrmInputs.bitangentWS;
                OUT.uv           = IN.uv;
                OUT.uv2          = IN.uv2;
                return OUT;
            }

            float3 ResolveNormal(Varyings IN)
            {
                float4 s  = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, TRANSFORM_TEX(IN.uv, _NormalMap));
                float3 ts = UnpackNormalScale(s, _BumpScale);
                return normalize(ts.x * IN.tangentWS + ts.y * IN.bitangentWS + ts.z * IN.normalWS);
            }

            float2 WrapWoTUV(float2 uv)
            {
                const float mn = 0.0625, mx = 0.9375, span = mx - mn;
                return mn + frac((uv - mn) / span) * span;
            }
            float2 AtlasCellUV(float index, float2 localUV)
            {
                float cols = max(_AtlasGrid.x, 1.0), rows = max(_AtlasGrid.y, 1.0);
                float idx  = max(floor(index + 0.5), 0.0);
                float y = floor(idx / cols), x = idx - y * cols;
                y = rows - 1.0 - y;
                return (float2(x, y) + localUV) / float2(cols, rows);
            }
            float3 SampleAtlasTile(float index, float2 uv)
            {
                return SAMPLE_TEXTURE2D(_AtlasTex, sampler_MainTex, AtlasCellUV(index, WrapWoTUV(uv))).rgb;
            }
            float4 SampleAtlasBlend(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_AtlasBlend, sampler_MainTex, AtlasCellUV(_AtlasIndexes.w, uv));
            }
            float2 BlendUV(Varyings IN) { return _UseUv2Blend > 0.5 ? IN.uv2 : IN.uv; }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float3 albedo;
                float alpha = _ObjectColor.a;
                float2 uv = TRANSFORM_TEX(IN.uv, _MainTex);

                if (_Mode < 0.5)
                {
                    float4 s = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                    albedo = s.rgb * _ObjectColor.rgb;
                    alpha  = s.a * _ObjectColor.a;
                }
                else if (_Mode < 1.5)
                {
                    float4 blend = SAMPLE_TEXTURE2D(_BlendMask, sampler_MainTex, TRANSFORM_TEX(BlendUV(IN), _BlendMask));
                    float4 s0 = SAMPLE_TEXTURE2D(_Tile0, sampler_MainTex, TRANSFORM_TEX(IN.uv, _Tile0));
                    float4 s1 = SAMPLE_TEXTURE2D(_Tile1, sampler_MainTex, TRANSFORM_TEX(IN.uv, _Tile1));
                    float4 s2 = SAMPLE_TEXTURE2D(_Tile2, sampler_MainTex, TRANSFORM_TEX(IN.uv, _Tile2));
                    albedo = (s0.rgb * _Tile0Tint.rgb) * blend.r + (s1.rgb * _Tile1Tint.rgb) * blend.g + (s2.rgb * _Tile2Tint.rgb) * blend.b;
                    if (dot(blend.rgb, 1.0) <= 1e-4) albedo = s0.rgb * _Tile0Tint.rgb;
                    albedo *= _ObjectColor.rgb;
                    alpha = s0.a * _ObjectColor.a;
                }
                else if (_Mode < 2.5)
                {
                    float4 blend = SampleAtlasBlend(BlendUV(IN));
                    float w0 = blend.r, w1 = blend.g, w2 = saturate(1.0 - w0 - w1);
                    albedo = SampleAtlasTile(_AtlasIndexes.x, IN.uv) * _Tile0Tint.rgb * w0
                           + SampleAtlasTile(_AtlasIndexes.y, IN.uv) * _Tile1Tint.rgb * w1
                           + SampleAtlasTile(_AtlasIndexes.z, IN.uv) * _Tile2Tint.rgb * w2;
                    albedo *= _ObjectColor.rgb;
                }
                else
                {
                    float4 blend = SampleAtlasBlend(BlendUV(IN));
                    float w0 = blend.r, w1 = blend.g, w2 = saturate(1.0 - w0 - w1);
                    float2 tuv = WrapWoTUV(IN.uv);
                    albedo = SAMPLE_TEXTURE2D(_Tile0, sampler_MainTex, TRANSFORM_TEX(tuv, _Tile0)).rgb * _Tile0Tint.rgb * w0
                           + SAMPLE_TEXTURE2D(_Tile1, sampler_MainTex, TRANSFORM_TEX(tuv, _Tile1)).rgb * _Tile1Tint.rgb * w1
                           + SAMPLE_TEXTURE2D(_Tile2, sampler_MainTex, TRANSFORM_TEX(tuv, _Tile2)).rgb * _Tile2Tint.rgb * w2;
                    albedo *= _ObjectColor.rgb;
                }

                if (_AlphaClip > 0.5)
                {
                    clip(alpha - _Cutoff);
                }

                float3 nWS = ResolveNormal(IN);

                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = ComputeScreenPos(TransformWorldToHClip(IN.positionWS));
                #elif defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    float4 shadowCoord = float4(0,0,0,0);
                #endif
                Light mainLight = GetMainLight(shadowCoord);
                float ndotl = saturate(dot(nWS, mainLight.direction));
                float3 lit = albedo * (mainLight.color * ndotl * mainLight.shadowAttenuation
                                     + SampleSH(nWS) * 0.5 + 0.15) * _Brightness;
                lit = MixFog(lit, IN.fogFactor);
                return half4(lit, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float  _Cutoff;
                float  _AlphaClip;
                float4 _ObjectColor;
            CBUFFER_END

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttr
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct ShadowVary
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float3 ApplyShadowBiasWS(float3 posWS, float3 normalWS, float3 lightDir)
            {
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

            #if UNITY_REVERSED_Z
                posCS.z = min(posCS.z, posCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
                posCS.z = max(posCS.z, posCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif

                OUT.positionCS = posCS;
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 ShadowFrag(ShadowVary IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                if (_AlphaClip > 0.5)
                {
                    float alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, TRANSFORM_TEX(IN.uv, _MainTex)).a * _ObjectColor.a;
                    clip(alpha - _Cutoff);
                }
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Lit"
}
