Shader "WoT/TerrainChunkBaked"
{
    Properties
    {
        _MainTex    ("Chunk Albedo", 2D)   = "white" {}
        _Brightness ("Brightness",  Float) = 1
        [Enum(None,0,Rotate 90 CW,1,Rotate 180,2,Rotate 270 CW,3)]
        _UVRotation ("Baked UV Rotation", Float) = 0
        [Toggle] _UVFlipX ("Flip U", Float) = 0
        [Toggle] _UVFlipY ("Flip V", Float) = 0

        // ВАЖНО: этот normal texture создаётся кодом как обычный RGBA asset,
        // поэтому НЕ используем Unity UnpackNormal/импортный NormalMap тип.
        _NormalMap      ("Baked Height Normal RGB", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Float) = 3.0

        // По умолчанию realtime shadows на terrain выключены, потому что большие
        // mesh chunks часто дают странные полосы/пятна self-shadowing в URP.
        // Если нужны тени от объектов на землю — можно поднять до 1 в материале.
        [Range(0,1)] _ReceiveShadows ("Receive Realtime Shadows", Float) = 0
        [Range(0,1)] _ShadowStrength ("Shadow Strength", Float) = 0.65
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _SHADOW_CASCADES_BLEND
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);   SAMPLER(sampler_MainTex);
            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _NormalMap_ST;
                float  _Brightness;
                float  _UVRotation;
                float  _UVFlipX;
                float  _UVFlipY;
                float  _NormalStrength;
                float  _ReceiveShadows;
                float  _ShadowStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 uv          : TEXCOORD2;
                float3 tangentWS   : TEXCOORD3;
                float3 bitangentWS : TEXCOORD4;
                float  fogFactor   : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs nrmInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS  = posInputs.positionWS;
                OUT.normalWS    = normalize(nrmInputs.normalWS);
                OUT.tangentWS   = normalize(nrmInputs.tangentWS);
                OUT.bitangentWS = normalize(nrmInputs.bitangentWS);
                OUT.uv          = IN.uv;
                OUT.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            float2 RotateBakedUV(float2 uv)
            {
                if      (_UVRotation > 0.5 && _UVRotation < 1.5) uv = float2(1.0 - uv.y, uv.x);
                else if (_UVRotation >= 1.5 && _UVRotation < 2.5) uv = float2(1.0 - uv.x, 1.0 - uv.y);
                else if (_UVRotation >= 2.5)                     uv = float2(uv.y, 1.0 - uv.x);
                if (_UVFlipX > 0.5) uv.x = 1.0 - uv.x;
                if (_UVFlipY > 0.5) uv.y = 1.0 - uv.y;
                return uv;
            }

            float3 DecodeGeneratedNormal(float4 packed)
            {
                // TerrainMeshBuilder пишет RGB так:
                // R = tangent X, G = tangent Y/bitangent, B = tangent Z/up.
                // Это обычный RGB normal, НЕ DXT5nm, поэтому UnpackNormalScale здесь
                // давал почти плоскую/неверную нормаль.
                float3 n = packed.rgb * 2.0 - 1.0;
                n.xy *= max(_NormalStrength, 0.0);
                n.z = max(n.z, 0.035);
                return normalize(n);
            }

            float3 ResolveNormalWS(Varyings IN, float2 bakedUv)
            {
                float4 s = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, TRANSFORM_TEX(bakedUv, _NormalMap));
                float3 nTS = DecodeGeneratedNormal(s);

                float3 t = normalize(IN.tangentWS);
                float3 b = normalize(IN.bitangentWS);
                float3 n = normalize(IN.normalWS);

                // Защита от редких нулевых tangents после RecalculateTangents().
                if (dot(t, t) < 1e-4 || dot(b, b) < 1e-4)
                    return n;

                return normalize(t * nTS.x + b * nTS.y + n * nTS.z);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float2 bakedUv = RotateBakedUV(IN.uv);
                float3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, TRANSFORM_TEX(bakedUv, _MainTex)).rgb;
                float3 nWS = ResolveNormalWS(IN, bakedUv);

                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = ComputeScreenPos(TransformWorldToHClip(IN.positionWS));
                #elif defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    float4 shadowCoord = float4(0,0,0,0);
                #endif

                Light mainLight = GetMainLight(shadowCoord);
                float ndotl = saturate(dot(nWS, mainLight.direction));

                // Мягкое освещение: нормали видны, но без чёрных провалов на склонах.
                float diffuse = ndotl * 0.85 + 0.15;
                float shadow = lerp(1.0, lerp(1.0, mainLight.shadowAttenuation, _ShadowStrength), saturate(_ReceiveShadows));
                float3 ambient = SampleSH(nWS) * 0.55 + 0.18;
                float3 lit = albedo * (mainLight.color * diffuse * shadow + ambient) * _Brightness;

                lit = MixFog(lit, IN.fogFactor);
                return half4(lit, 1);
            }
            ENDHLSL
        }

        // ShadowCaster pass намеренно отсутствует: terrain mesh chunks больше не
        // бросают собственные realtime shadows, из-за которых появлялись странные
        // пятна/полосы. При этом ForwardLit всё ещё может принимать тени, если
        // поднять _ReceiveShadows выше 0 в материале.
    }

    Fallback Off
}
