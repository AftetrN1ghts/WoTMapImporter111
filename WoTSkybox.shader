Shader "WoT/Skybox"
{
    // Reconstructs the WoT sky: a vertical sky-gradient dome with a panoramic
    // cloud layer on top. Driven as a normal Unity skybox (RenderSettings.skybox)
    // so it always follows the camera. Time-of-day is applied via _Tint/_Exposure
    // computed by the importer from the map's day_night_cycle keys.
    Properties
    {
        _Gradient   ("Sky Gradient (vertical)", 2D) = "white" {}
        _Clouds     ("Clouds (panoramic)", 2D) = "black" {}
        _CloudsMask ("Clouds Mask", 2D) = "white" {}
        _Tint       ("Sky Tint", Color) = (1,1,1,1)
        _Exposure   ("Exposure", Float) = 1.0
        _CloudOpacity ("Cloud Opacity", Range(0,1)) = 1.0
        _CloudScroll ("Cloud Scroll", Float) = 0.0
        _Rotation   ("Rotation (deg)", Range(0,360)) = 0
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _Gradient;
            sampler2D _Clouds;
            sampler2D _CloudsMask;
            half4 _Tint;
            half _Exposure;
            half _CloudOpacity;
            half _CloudScroll;
            half _Rotation;

            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 RotateY(float3 v, float deg)
            {
                float r = radians(deg);
                float s = sin(r), c = cos(r);
                return float3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z);
            }

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = RotateY(v.vertex.xyz, _Rotation);
                return o;
            }

            #define PI 3.14159265

            half4 frag (v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);

                // Vertical sky gradient: sample by elevation (horizon -> zenith).
                float gv = saturate(dir.y * 0.5 + 0.5);
                half3 sky = tex2D(_Gradient, float2(0.5, gv)).rgb;

                // Panoramic clouds on the upper hemisphere, faded out at the horizon.
                float az = atan2(dir.z, dir.x) * (0.5 / PI) + 0.5;
                float cu = az + _CloudScroll;
                float cv = saturate(acos(clamp(dir.y, -1.0, 1.0)) / PI); // 0 zenith .. 1 nadir
                half4 cl = tex2D(_Clouds, float2(cu, cv));
                half mask = tex2D(_CloudsMask, float2(cu, cv)).r;
                half horizonFade = saturate(dir.y * 3.0);
                half cloudAmt = saturate(cl.a * mask * horizonFade * _CloudOpacity);
                sky = lerp(sky, cl.rgb, cloudAmt);

                sky *= _Tint.rgb * _Exposure;
                return half4(sky, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
