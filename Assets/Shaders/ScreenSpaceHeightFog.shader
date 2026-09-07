Shader "Hidden/IFC/ScreenSpaceHeightFog"
{
    Properties
    {
        _FogColor("Fog Color", Color) = (0.24, 0.28, 0.29, 1)

        _FogDensity("Fog Density", Float) = 0.018
        _FogStartDistance("Fog Start Distance", Float) = 5.0
        _FogMaxDistance("Fog Max Distance", Float) = 550.0
        _MaxOpacity("Maximum Opacity", Range(0, 1)) = 0.50

        _FogBaseHeight("Fog Base Height", Float) = 5.0
        _HeightFalloff("Height Falloff", Float) = 0.09

        _NoiseScale("Noise Scale", Float) = 0.008
        _NoiseStrength("Smoke Shaping Strength", Range(0, 1)) = 0.90
        _NoiseVelocity("Noise Velocity", Vector) = (0.0015, 0.0003, 0, 0)
        _VerticalShear("Vertical Noise Shear", Vector) = (0.35, -0.22, 0, 0)

        _FBMOctaves("fBM Octaves", Range(1, 5)) = 4
        _FBMLacunarity("fBM Lacunarity", Range(1.1, 3.5)) = 2.0
        _FBMGain("fBM Gain", Range(0.1, 0.9)) = 0.5
        _NoiseContrast("Noise Contrast", Range(0.25, 4.0)) = 1.55

        _EvolutionStrength("Evolution Strength", Range(0, 1.5)) = 0.35
        _EvolutionSpeed("Evolution Speed", Range(0, 1)) = 0.16
        _FlowVariation("Octave Flow Variation", Range(0, 2)) = 0.70

        _SmokeCoverage("Smoke Coverage Threshold", Range(0, 1)) = 0.52
        _SmokeSoftness("Smoke Edge Softness", Range(0.01, 0.35)) = 0.09
        _SmokeFloor("Background Fog Floor", Range(0, 1)) = 0.03
        _SmokePeakDensity("Smoke Peak Density", Range(0.5, 3.0)) = 1.60

        _WarpScale("Domain Warp Scale", Range(0.05, 2.0)) = 0.65
        _WarpStrength("Domain Warp Strength", Range(0, 3.0)) = 1.35
        _WarpSpeed("Domain Warp Speed", Range(0, 0.2)) = 0.04
        _BillowStrength("Billow Strength", Range(0, 1)) = 0.50

        _HorizonStrength("Horizon Strength", Range(0, 1)) = 0.08
        _HorizonFalloff("Horizon Falloff", Float) = 11.0

        _DebugMode("Debug Mode", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "Screen Space Height Fog"

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _FogColor;

                float _FogDensity;
                float _FogStartDistance;
                float _FogMaxDistance;
                float _MaxOpacity;

                float _FogBaseHeight;
                float _HeightFalloff;

                float _NoiseScale;
                float _NoiseStrength;
                float4 _NoiseVelocity;
                float4 _VerticalShear;

                float _FBMOctaves;
                float _FBMLacunarity;
                float _FBMGain;
                float _NoiseContrast;

                float _EvolutionStrength;
                float _EvolutionSpeed;
                float _FlowVariation;

                float _SmokeCoverage;
                float _SmokeSoftness;
                float _SmokeFloor;
                float _SmokePeakDensity;

                float _WarpScale;
                float _WarpStrength;
                float _WarpSpeed;
                float _BillowStrength;

                float _HorizonStrength;
                float _HorizonFalloff;

                float _DebugMode;
            CBUFFER_END

            /*
             * The important change in this version is not simply adding more
             * samples. The samples are restricted to the part of the view ray
             * that actually intersects the height-fog layer, then distributed
             * non-uniformly toward the visible surface. This makes top-down
             * views much more stable while preserving the same Base Height and
             * Height Falloff behaviour.
             */
            static const int FOG_SAMPLE_COUNT = 5;
            static const int FBM_MAX_OCTAVES = 5;

            /*
             * Height density below this value is ignored when locating the
             * useful fog interval. The exponential height function itself is
             * unchanged; this only prevents samples being wasted far above it.
             */
            static const float HEIGHT_DENSITY_CUTOFF = 0.015;

            float DeviceDepthFromRawDepth(float rawDepth)
            {
                #if UNITY_REVERSED_Z
                    return rawDepth;
                #else
                    return lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
                #endif
            }

            float3 ReconstructWorldPosition(float2 uv, float rawDepth)
            {
                float deviceDepth = DeviceDepthFromRawDepth(rawDepth);

                return ComputeWorldSpacePosition(
                    uv,
                    deviceDepth,
                    UNITY_MATRIX_I_VP
                );
            }

            bool IsSkyDepth(float rawDepth)
            {
                #if UNITY_REVERSED_Z
                    return rawDepth <= 0.00001;
                #else
                    return rawDepth >= 0.99999;
                #endif
            }

            void CalculateViewRay(
                float2 uv,
                out float3 rayOriginWS,
                out float3 rayDirectionWS)
            {
                #if UNITY_REVERSED_Z
                    const float nearRawDepth = 1.0;
                    const float farRawDepth = 0.0001;
                #else
                    const float nearRawDepth = 0.0;
                    const float farRawDepth = 0.9999;
                #endif

                float3 nearPositionWS =
                    ReconstructWorldPosition(uv, nearRawDepth);

                float3 farPositionWS =
                    ReconstructWorldPosition(uv, farRawDepth);

                rayDirectionWS = SafeNormalize(
                    farPositionWS - nearPositionWS
                );

                rayOriginWS =
                    unity_OrthoParams.w > 0.5
                    ? nearPositionWS
                    : GetCameraPositionWS();
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 position)
            {
                float2 cell = floor(position);
                float2 localPosition = frac(position);

                float bottomLeft = Hash21(cell);
                float bottomRight = Hash21(cell + float2(1.0, 0.0));
                float topLeft = Hash21(cell + float2(0.0, 1.0));
                float topRight = Hash21(cell + float2(1.0, 1.0));

                float2 smoothPosition =
                    localPosition
                    * localPosition
                    * (3.0 - 2.0 * localPosition);

                float bottom = lerp(
                    bottomLeft,
                    bottomRight,
                    smoothPosition.x
                );

                float top = lerp(
                    topLeft,
                    topRight,
                    smoothPosition.x
                );

                return lerp(bottom, top, smoothPosition.y);
            }

            float FlowFBM(float2 position, float timeValue)
            {
                float accumulatedValue = 0.0;
                float accumulatedAmplitude = 0.0;
                float amplitude = 0.5;

                const float2x2 octaveRotation =
                    float2x2(
                         0.7986355, -0.6018150,
                         0.6018150,  0.7986355
                    );

                [loop]
                for (
                    int octaveIndex = 0;
                    octaveIndex < FBM_MAX_OCTAVES;
                    octaveIndex++)
                {
                    if ((float)octaveIndex >= _FBMOctaves)
                        break;

                    float octaveNumber = (float)octaveIndex;

                    float2 octaveDirection = float2(
                        cos(1.73 * octaveNumber + 0.35),
                        sin(1.37 * octaveNumber + 1.10)
                    );

                    float baseSpeed = length(_NoiseVelocity.xy);

                    float2 octaveFlow =
                        (
                            _NoiseVelocity.xy
                            + octaveDirection
                            * baseSpeed
                            * _FlowVariation
                        )
                        * timeValue
                        * (1.0 + octaveNumber * 0.32);

                    float evolutionPhase =
                        timeValue
                        * _EvolutionSpeed
                        * (1.0 + octaveNumber * 0.11);

                    float2 evolutionOffset =
                        float2(
                            sin(evolutionPhase + octaveNumber * 1.91),
                            cos(evolutionPhase * 0.83 + octaveNumber * 2.27)
                        )
                        * (
                            _EvolutionStrength
                            / (1.0 + octaveNumber * 0.45)
                        );

                    float octaveValue = ValueNoise(
                        position + octaveFlow + evolutionOffset
                    );

                    accumulatedValue += octaveValue * amplitude;
                    accumulatedAmplitude += amplitude;

                    position =
                        mul(octaveRotation, position)
                        * _FBMLacunarity
                        + float2(17.13, 9.27);

                    amplitude *= _FBMGain;
                }

                return accumulatedValue
                    / max(accumulatedAmplitude, 0.00001);
            }

            float2 CalculateDomainWarp(float2 position, float timeValue)
            {
                float2 warpPosition = position * _WarpScale;

                float2 primaryDrift =
                    float2(0.73, -0.41)
                    * timeValue
                    * _WarpSpeed;

                float2 secondaryDrift =
                    float2(-0.29, 0.67)
                    * timeValue
                    * (_WarpSpeed * 0.73);

                float warpX = ValueNoise(
                    warpPosition + primaryDrift
                );

                float warpY = ValueNoise(
                    warpPosition
                    + secondaryDrift
                    + float2(31.71, 17.93)
                );

                return (
                    float2(warpX, warpY) * 2.0 - 1.0
                ) * _WarpStrength;
            }

            /*
             * The height model is intentionally unchanged from the original
             * shader. Base Height and Height Falloff therefore keep exactly
             * the same artistic meaning and visual placement.
             */
            float HeightDensity(float3 positionWS)
            {
                float heightAboveBase = max(
                    positionWS.y - _FogBaseHeight,
                    0.0
                );

                return exp(
                    -heightAboveBase * _HeightFalloff
                );
            }

            float SampleFogNoise(float3 positionWS)
            {
                float2 projectedPosition =
                    positionWS.xz
                    + positionWS.y * _VerticalShear.xy;

                float2 noisePosition =
                    projectedPosition * _NoiseScale;

                float timeValue = _Time.y;

                float2 domainWarp = CalculateDomainWarp(
                    noisePosition,
                    timeValue
                );

                float broadNoise = FlowFBM(
                    noisePosition + domainWarp,
                    timeValue
                );

                float detailPhase =
                    timeValue * (_EvolutionSpeed * 1.65 + 0.05);

                float2 detailMotion = float2(
                    sin(detailPhase),
                    cos(detailPhase * 0.79)
                ) * 0.45;

                float detailNoise = ValueNoise(
                    noisePosition * 3.15
                    - domainWarp * 0.35
                    + detailMotion
                    + float2(14.7, 8.3)
                );

                float billowNoise =
                    1.0 - abs(detailNoise * 2.0 - 1.0);

                float billowedField = saturate(
                    broadNoise * 0.72
                    + billowNoise * 0.48
                );

                float noiseValue = lerp(
                    broadNoise,
                    billowedField,
                    _BillowStrength
                );

                noiseValue = saturate(
                    (noiseValue - 0.5)
                    * _NoiseContrast
                    + 0.5
                );

                return noiseValue;
            }

            float SmokeDensityMultiplier(float3 positionWS)
            {
                float noiseValue = SampleFogNoise(positionWS);

                float lowerThreshold =
                    _SmokeCoverage - _SmokeSoftness;

                float upperThreshold =
                    _SmokeCoverage + _SmokeSoftness;

                float smokeShape = smoothstep(
                    lowerThreshold,
                    upperThreshold,
                    noiseValue
                );

                float internalDetail = lerp(
                    0.72,
                    1.0,
                    noiseValue
                );

                float shapedDensity = lerp(
                    _SmokeFloor,
                    _SmokePeakDensity * internalDetail,
                    smokeShape
                );

                return lerp(
                    1.0,
                    shapedDensity,
                    _NoiseStrength
                );
            }

            float CalculateFogTopHeight()
            {
                float safeFalloff = max(
                    _HeightFalloff,
                    0.0001
                );

                return _FogBaseHeight
                    - log(HEIGHT_DENSITY_CUTOFF)
                    / safeFalloff;
            }

            /*
             * Finds the segment of the camera ray where the exponential
             * height fog is relevant. The visible surface remains the final
             * endpoint even when it is farther than Fog Max Distance.
             *
             * This is the key correction for far zooms and top-down views:
             * the old version distributed four samples across hundreds of
             * metres and often missed the last few metres containing the fog.
             */
            bool CalculateFogInterval(
                float3 rayOriginWS,
                float3 rayDirectionWS,
                float surfaceDistance,
                out float intervalStart,
                out float intervalEnd)
            {
                intervalEnd = max(surfaceDistance, 0.0);

                /*
                 * Fog Max Distance limits the amount of ray considered behind
                 * the visible surface; it never truncates the surface itself.
                 */
                intervalStart = max(
                    0.0,
                    intervalEnd - max(_FogMaxDistance, 0.01)
                );

                float fogTopHeight = CalculateFogTopHeight();
                float verticalDirection = rayDirectionWS.y;

                if (verticalDirection < -0.0001)
                {
                    /*
                     * Downward-looking ray. If the camera begins above the
                     * useful layer, move the interval start to the height at
                     * which the ray enters that layer.
                     */
                    if (rayOriginWS.y > fogTopHeight)
                    {
                        float entryDistance =
                            (fogTopHeight - rayOriginWS.y)
                            / verticalDirection;

                        if (entryDistance >= intervalEnd)
                            return false;

                        intervalStart = max(
                            intervalStart,
                            max(entryDistance, 0.0)
                        );
                    }
                }
                else if (verticalDirection > 0.0001)
                {
                    /*
                     * Upward-looking ray. Clamp the endpoint to the point at
                     * which the ray exits the useful layer.
                     */
                    if (rayOriginWS.y >= fogTopHeight)
                        return false;

                    float exitDistance =
                        (fogTopHeight - rayOriginWS.y)
                        / verticalDirection;

                    intervalEnd = min(
                        intervalEnd,
                        max(exitDistance, 0.0)
                    );
                }
                else
                {
                    /*
                     * Nearly horizontal ray entirely above the useful layer.
                     */
                    if (rayOriginWS.y >= fogTopHeight)
                        return false;
                }

                return intervalEnd - intervalStart > 0.0001;
            }

            /*
             * Fog Start Distance is treated as a soft characteristic distance
             * rather than a hard cut. Consequently, close views retain some
             * fog instead of suddenly losing the effect.
             */
            float CalculateNearDistanceFade(float surfaceDistance)
            {
                float fadeScale = max(
                    _FogStartDistance,
                    0.001
                );

                return 1.0 - exp(
                    -surfaceDistance / fadeScale
                );
            }

            float IntegrateFog(
                float3 rayOriginWS,
                float3 rayDirectionWS,
                float surfaceDistance)
            {
                float intervalStart;
                float intervalEnd;

                if (
                    !CalculateFogInterval(
                        rayOriginWS,
                        rayDirectionWS,
                        surfaceDistance,
                        intervalStart,
                        intervalEnd
                    )
                )
                {
                    return 0.0;
                }

                float travelDistance =
                    intervalEnd - intervalStart;

                float integratedDensity = 0.0;

                /*
                 * Quadratic stratification allocates progressively smaller
                 * integration segments near the visible surface, where a
                 * downward ray encounters the densest part of the layer.
                 * Segment lengths are preserved, so this remains a weighted
                 * numerical integral rather than an arbitrary visual sample.
                 */
                [unroll]
                for (
                    int sampleIndex = 0;
                    sampleIndex < FOG_SAMPLE_COUNT;
                    sampleIndex++)
                {
                    float normalizedStart =
                        (float)sampleIndex
                        / (float)FOG_SAMPLE_COUNT;

                    float normalizedEnd =
                        ((float)sampleIndex + 1.0)
                        / (float)FOG_SAMPLE_COUNT;

                    float warpedStart =
                        1.0
                        - (1.0 - normalizedStart)
                        * (1.0 - normalizedStart);

                    float warpedEnd =
                        1.0
                        - (1.0 - normalizedEnd)
                        * (1.0 - normalizedEnd);

                    float warpedMidpoint =
                        (warpedStart + warpedEnd) * 0.5;

                    float sampleDistance = lerp(
                        intervalStart,
                        intervalEnd,
                        warpedMidpoint
                    );

                    float segmentLength =
                        travelDistance
                        * (warpedEnd - warpedStart);

                    float3 samplePositionWS =
                        rayOriginWS
                        + rayDirectionWS * sampleDistance;

                    float localDensity =
                        HeightDensity(samplePositionWS)
                        * SmokeDensityMultiplier(samplePositionWS);

                    integratedDensity +=
                        localDensity * segmentLength;
                }

                /*
                 * A modest visual compensation reduces the natural disparity
                 * between long grazing paths and short top-down paths. It does
                 * not change the fog's height or move it onto rooftops.
                 */
                float topDownAmount = saturate(
                    abs(rayDirectionWS.y)
                );

                float viewAngleCompensation = lerp(
                    1.0,
                    1.45,
                    topDownAmount * topDownAmount
                );

                integratedDensity *= viewAngleCompensation;

                float fogAmount =
                    1.0 - exp(
                        -integratedDensity * _FogDensity
                    );

                return fogAmount
                    * CalculateNearDistanceFade(surfaceDistance);
            }

            float CalculateHorizonFog(float3 rayDirectionWS)
            {
                float horizonBand = exp(
                    -abs(rayDirectionWS.y)
                    * _HorizonFalloff
                );

                return saturate(
                    horizonBand * _HorizonStrength
                );
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv =
                    UnityStereoTransformScreenSpaceTex(
                        input.texcoord
                    );

                half4 sourceColor =
                    SAMPLE_TEXTURE2D_X_LOD(
                        _BlitTexture,
                        sampler_LinearClamp,
                        uv,
                        0.0
                    );

                float rawDepth = SampleSceneDepth(uv);

                float3 rayOriginWS;
                float3 rayDirectionWS;

                CalculateViewRay(
                    uv,
                    rayOriginWS,
                    rayDirectionWS
                );

                bool isSky = IsSkyDepth(rawDepth);

                float fogAmount = 0.0;
                float debugHeightDensity = 0.0;
                float debugNoise = 0.5;

                if (isSky)
                {
                    fogAmount = CalculateHorizonFog(
                        rayDirectionWS
                    );
                }
                else
                {
                    float3 surfacePositionWS =
                        ReconstructWorldPosition(
                            uv,
                            rawDepth
                        );

                    float surfaceDistance = length(
                        surfacePositionWS - rayOriginWS
                    );

                    float volumeFog = IntegrateFog(
                        rayOriginWS,
                        rayDirectionWS,
                        surfaceDistance
                    );

                    float distanceRange = max(
                        _FogMaxDistance,
                        0.0001
                    );

                    float distanceFactor = saturate(
                        surfaceDistance / distanceRange
                    );

                    float horizonFog =
                        CalculateHorizonFog(rayDirectionWS)
                        * distanceFactor;

                    fogAmount =
                        1.0
                        - (1.0 - volumeFog)
                        * (1.0 - horizonFog);

                    debugHeightDensity =
                        HeightDensity(surfacePositionWS);

                    debugNoise =
                        SampleFogNoise(surfacePositionWS);
                }

                fogAmount = min(
                    saturate(fogAmount),
                    _MaxOpacity
                );

                if (_DebugMode > 0.5 && _DebugMode < 1.5)
                {
                    return half4(fogAmount.xxx, 1.0);
                }

                if (_DebugMode > 1.5 && _DebugMode < 2.5)
                {
                    return half4(debugHeightDensity.xxx, 1.0);
                }

                if (_DebugMode > 2.5 && _DebugMode < 3.5)
                {
                    return half4(debugNoise.xxx, 1.0);
                }

                half3 finalColor = lerp(
                    sourceColor.rgb,
                    _FogColor.rgb,
                    fogAmount
                );

                return half4(
                    finalColor,
                    sourceColor.a
                );
            }

            ENDHLSL
        }
    }

    Fallback Off
}
