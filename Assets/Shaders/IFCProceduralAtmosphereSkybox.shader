Shader "Skybox/IFC Procedural Atmosphere"
{
    Properties
    {
        [Header(Sky Gradient)]
        [HDR] _ZenithColor("Zenith Color", Color) = (0.055, 0.11, 0.22, 1)
        [HDR] _HorizonColor("Sky Horizon Color", Color) = (0.38, 0.48, 0.58, 1)
        _SkyGradientPower("Sky Gradient Power", Range(0.15, 4.0)) = 0.72

        [Header(Lower Hemisphere)]
        [HDR] _GroundHorizonColor("Ground Horizon Color", Color) = (0.17, 0.20, 0.17, 1)
        [HDR] _GroundBottomColor("Ground Bottom Color", Color) = (0.035, 0.030, 0.026, 1)
        _GroundGradientPower("Ground Gradient Power", Range(0.15, 4.0)) = 0.85
        _HorizonBlend("Sky Ground Blend", Range(0.001, 0.25)) = 0.045

        [Header(Horizon Haze)]
        [HDR] _HorizonHazeColor("Horizon Haze Color", Color) = (0.34, 0.41, 0.45, 1)
        _HorizonHazeStrength("Horizon Haze Strength", Range(0, 1)) = 0.52
        _HorizonHazeSharpness("Horizon Haze Sharpness", Range(0.5, 40)) = 9.0

        [Header(Procedural Clouds)]
        [HDR] _CloudLightColor("Cloud Light Color", Color) = (0.58, 0.61, 0.64, 1)
        [HDR] _CloudShadowColor("Cloud Shadow Color", Color) = (0.10, 0.12, 0.15, 1)
        _CloudOpacity("Cloud Opacity", Range(0, 1)) = 0.78
        _CloudCoverage("Cloud Coverage", Range(0, 1)) = 0.54
        _CloudSoftness("Cloud Edge Softness", Range(0.01, 0.35)) = 0.10
        _CloudContrast("Cloud Contrast", Range(0.25, 4.0)) = 1.45
        _CloudScale("Cloud Scale", Range(0.05, 8.0)) = 1.15
        _CloudDetailScale("Cloud Detail Scale", Range(1.0, 5.0)) = 2.15
        _CloudBillowStrength("Cloud Billow Strength", Range(0, 1)) = 0.42

        [Header(Cloud fBM)]
        _CloudOctaves("fBM Octaves", Range(1, 6)) = 5
        _CloudLacunarity("fBM Lacunarity", Range(1.1, 3.5)) = 2.02
        _CloudGain("fBM Gain", Range(0.1, 0.9)) = 0.50
        _CloudFlowVariation("Octave Flow Variation", Range(0, 2)) = 0.42

        [Header(Cloud Animation)]
        _CloudWind("Cloud Wind XZ", Vector) = (1.0, 0.18, 0, 0)
        _CloudSpeed("Cloud Drift Speed", Range(0, 0.25)) = 0.018
        _CloudEvolution("Cloud Evolution", Range(0, 1.5)) = 0.20
        _CloudEvolutionSpeed("Cloud Evolution Speed", Range(0, 1.0)) = 0.085

        [Header(Domain Warping)]
        _WarpScale("Warp Scale", Range(0.05, 3.0)) = 0.62
        _WarpStrength("Warp Strength", Range(0, 4.0)) = 1.15
        _WarpSpeed("Warp Speed", Range(0, 0.5)) = 0.035

        [Header(Cloud Horizon)]
        _CloudProjectionFloor("Projection Floor", Range(0.02, 0.35)) = 0.09
        _CloudHorizonFadeStart("Cloud Horizon Fade Start", Range(0, 0.5)) = 0.025
        _CloudHorizonFadeEnd("Cloud Horizon Fade End", Range(0.02, 0.7)) = 0.16

        [Header(Sun)]
        _UseMainLight("Use URP Main Light", Range(0, 1)) = 1
        _ManualSunDirection("Manual Sun Direction", Vector) = (0.25, 0.55, 0.80, 0)
        [HDR] _SunColor("Sun Color", Color) = (1.0, 0.70, 0.42, 1)
        _SunDiskSize("Sun Disk Radius", Range(0.05, 8.0)) = 0.85
        _SunDiskSoftness("Sun Disk Softness", Range(0.01, 4.0)) = 0.30
        _SunDiskIntensity("Sun Disk Intensity", Range(0, 8)) = 1.65
        _SunGlowIntensity("Sun Glow Intensity", Range(0, 4)) = 0.42
        _SunGlowExponent("Sun Glow Tightness", Range(1, 512)) = 52

        [Header(Output)]
        _Exposure("Exposure", Range(0, 4)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            Name "IFC Procedural Atmosphere"

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 directionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _ZenithColor;
                half4 _HorizonColor;
                float _SkyGradientPower;

                half4 _GroundHorizonColor;
                half4 _GroundBottomColor;
                float _GroundGradientPower;
                float _HorizonBlend;

                half4 _HorizonHazeColor;
                float _HorizonHazeStrength;
                float _HorizonHazeSharpness;

                half4 _CloudLightColor;
                half4 _CloudShadowColor;
                float _CloudOpacity;
                float _CloudCoverage;
                float _CloudSoftness;
                float _CloudContrast;
                float _CloudScale;
                float _CloudDetailScale;
                float _CloudBillowStrength;

                float _CloudOctaves;
                float _CloudLacunarity;
                float _CloudGain;
                float _CloudFlowVariation;

                float4 _CloudWind;
                float _CloudSpeed;
                float _CloudEvolution;
                float _CloudEvolutionSpeed;

                float _WarpScale;
                float _WarpStrength;
                float _WarpSpeed;

                float _CloudProjectionFloor;
                float _CloudHorizonFadeStart;
                float _CloudHorizonFadeEnd;

                float _UseMainLight;
                float4 _ManualSunDirection;
                half4 _SunColor;
                float _SunDiskSize;
                float _SunDiskSoftness;
                float _SunDiskIntensity;
                float _SunGlowIntensity;
                float _SunGlowExponent;

                float _Exposure;
            CBUFFER_END

            static const int FBM_MAX_OCTAVES = 6;
            static const float DEG_TO_RAD = 0.017453292519943295;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.directionWS = TransformObjectToWorldDir(input.positionOS);
                return output;
            }

            float2 SafeNormalize2(float2 value)
            {
                float lengthSquared = dot(value, value);
                return lengthSquared > 0.000001
                    ? value * rsqrt(lengthSquared)
                    : float2(1.0, 0.0);
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 cell = floor(p);
                float2 localPosition = frac(p);
                float2 interpolation = localPosition * localPosition * (3.0 - 2.0 * localPosition);

                float bottomLeft = Hash21(cell);
                float bottomRight = Hash21(cell + float2(1.0, 0.0));
                float topLeft = Hash21(cell + float2(0.0, 1.0));
                float topRight = Hash21(cell + float2(1.0, 1.0));

                float bottom = lerp(bottomLeft, bottomRight, interpolation.x);
                float top = lerp(topLeft, topRight, interpolation.x);
                return lerp(bottom, top, interpolation.y);
            }

            float FlowFBM(float2 p, float timeValue, float speedMultiplier)
            {
                float accumulatedValue = 0.0;
                float accumulatedAmplitude = 0.0;
                float amplitude = 0.5;
                float2 windDirection = SafeNormalize2(_CloudWind.xy);

                const float2x2 octaveRotation = float2x2(
                     0.7986355, -0.6018150,
                     0.6018150,  0.7986355
                );

                [unroll]
                for (int octaveIndex = 0; octaveIndex < FBM_MAX_OCTAVES; octaveIndex++)
                {
                    float octaveEnabled = step((float)octaveIndex + 0.5, _CloudOctaves);
                    float octaveNumber = (float)octaveIndex;
                    float directionSign = 1.0 - 2.0 * (float)(octaveIndex % 2);
                    float flowMultiplier = 1.0 + octaveNumber * _CloudFlowVariation;

                    float2 directionalFlow =
                        windDirection
                        * timeValue
                        * _CloudSpeed
                        * speedMultiplier
                        * flowMultiplier
                        * directionSign;

                    float phase =
                        timeValue
                        * _CloudEvolutionSpeed
                        * (1.0 + octaveNumber * 0.19);

                    float2 evolvingFlow = float2(
                        sin(phase + octaveNumber * 1.73),
                        cos(phase * 0.83 + octaveNumber * 2.11)
                    ) * _CloudEvolution;

                    float octaveValue = ValueNoise(p + directionalFlow + evolvingFlow);

                    accumulatedValue += octaveValue * amplitude * octaveEnabled;
                    accumulatedAmplitude += amplitude * octaveEnabled;

                    p = mul(octaveRotation, p) * _CloudLacunarity + float2(17.13, 9.27);
                    amplitude *= _CloudGain;
                }

                return accumulatedValue / max(accumulatedAmplitude, 0.00001);
            }

            float2 CalculateDomainWarp(float2 p, float timeValue)
            {
                float2 windDirection = SafeNormalize2(_CloudWind.xy);
                float2 warpInput = p * _WarpScale;

                float2 warpDrift =
                    windDirection
                    * timeValue
                    * _WarpSpeed;

                float warpX = ValueNoise(warpInput + warpDrift);
                float warpY = ValueNoise(
                    warpInput
                    - warpDrift * 0.83
                    + float2(31.71, 17.93)
                );

                float2 warp = float2(warpX, warpY) * 2.0 - 1.0;
                return warp * _WarpStrength;
            }

            float2 EvaluateClouds(float3 directionWS)
            {
                if (directionWS.y <= 0.0)
                    return float2(0.0, 0.0);

                float cloudHorizonMask = smoothstep(
                    _CloudHorizonFadeStart,
                    max(_CloudHorizonFadeEnd, _CloudHorizonFadeStart + 0.001),
                    directionWS.y
                );

                float projectionY = max(directionWS.y, _CloudProjectionFloor);
                float2 projectedSky = directionWS.xz / projectionY;
                float2 p = projectedSky * _CloudScale;
                float timeValue = _Time.y;

                float2 domainWarp = CalculateDomainWarp(p, timeValue);

                float broadNoise = FlowFBM(
                    p + domainWarp,
                    timeValue,
                    1.0
                );

                float detailNoise = FlowFBM(
                    p * _CloudDetailScale
                    - domainWarp * 0.28
                    + float2(14.7, 8.3),
                    timeValue,
                    1.27
                );

                float billowNoise = 1.0 - abs(detailNoise * 2.0 - 1.0);
                float cloudShape = lerp(
                    broadNoise,
                    billowNoise,
                    _CloudBillowStrength
                );

                cloudShape = saturate(
                    (cloudShape - 0.5) * _CloudContrast + 0.5
                );

                float threshold = 1.0 - _CloudCoverage;
                float cloudDensity = smoothstep(
                    threshold - _CloudSoftness,
                    threshold + _CloudSoftness,
                    cloudShape
                );

                cloudDensity *= cloudHorizonMask * _CloudOpacity;
                return float2(cloudDensity, cloudShape);
            }

            float3 EvaluateBaseSky(float3 directionWS)
            {
                float skyHeight = pow(saturate(directionWS.y), _SkyGradientPower);
                float groundDepth = pow(saturate(-directionWS.y), _GroundGradientPower);

                float3 upperSky = lerp(
                    _HorizonColor.rgb,
                    _ZenithColor.rgb,
                    skyHeight
                );

                float3 lowerGround = lerp(
                    _GroundHorizonColor.rgb,
                    _GroundBottomColor.rgb,
                    groundDepth
                );

                float upperHemisphere = smoothstep(
                    -_HorizonBlend,
                    _HorizonBlend,
                    directionWS.y
                );

                float3 baseColor = lerp(
                    lowerGround,
                    upperSky,
                    upperHemisphere
                );

                float horizonHaze =
                    exp(-abs(directionWS.y) * _HorizonHazeSharpness)
                    * _HorizonHazeStrength;

                return lerp(
                    baseColor,
                    _HorizonHazeColor.rgb,
                    saturate(horizonHaze)
                );
            }

            float3 ResolveSunDirection()
            {
                Light mainLight = GetMainLight();
                float3 mainLightDirection = SafeNormalize(mainLight.direction);
                float3 manualDirection = SafeNormalize(_ManualSunDirection.xyz);

                return SafeNormalize(
                    lerp(
                        manualDirection,
                        mainLightDirection,
                        saturate(_UseMainLight)
                    )
                );
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 directionWS = SafeNormalize(input.directionWS);
                float3 color = EvaluateBaseSky(directionWS);

                float3 sunDirection = ResolveSunDirection();
                float sunDot = saturate(dot(directionWS, sunDirection));

                float diskInnerCos = cos(_SunDiskSize * DEG_TO_RAD);
                float diskOuterCos = cos(
                    (_SunDiskSize + _SunDiskSoftness) * DEG_TO_RAD
                );

                float sunDisk = smoothstep(
                    diskOuterCos,
                    diskInnerCos,
                    sunDot
                ) * _SunDiskIntensity;

                float sunGlow = pow(
                    sunDot,
                    max(_SunGlowExponent, 1.0)
                ) * _SunGlowIntensity;

                color += _SunColor.rgb * sunGlow;
                color = lerp(
                    color,
                    _SunColor.rgb * _SunDiskIntensity,
                    saturate(sunDisk)
                );

                float2 cloudData = EvaluateClouds(directionWS);
                float cloudDensity = cloudData.x;
                float cloudShape = cloudData.y;

                float cloudLighting = saturate(
                    0.18
                    + cloudShape * 0.72
                    + sunGlow * 0.25
                );

                float3 cloudColor = lerp(
                    _CloudShadowColor.rgb,
                    _CloudLightColor.rgb,
                    cloudLighting
                );

                color = lerp(color, cloudColor, cloudDensity);
                color *= _Exposure;

                return half4(color, 1.0);
            }

            ENDHLSL
        }
    }

    Fallback Off
}
