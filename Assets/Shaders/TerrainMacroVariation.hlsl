#ifndef TERRAIN_MACRO_VARIATION_INCLUDED
#define TERRAIN_MACRO_VARIATION_INCLUDED

#include "Assets/Shaders/Noise.hlsl"

void TerrainMacroVariation_float(
    float3 PositionWS,
    float2 TerrainMapUV,
    float3 BaseColor,
    float Smoothness,
    UnityTexture2D TerrainMapDist,

    out float3 VariedColor,
    out float VariedSmoothness,
    out float MacroMask)
{
    // Broad regions, approximately hundreds of metres in scale.
    const float broadFrequency =
        0.006;

    // Secondary shapes prevent the broad noise from looking too uniform.
    const float secondaryFrequency =
        0.015;

    float3 horizontalPosition =
        float3(
            PositionWS.x,
            0.0,
            PositionWS.z
        );

    float broadNoise =
        IFC_FBM3D(
            horizontalPosition *
            broadFrequency + 666
        );

    float secondaryNoise =
        IFC_FBM3D(
            horizontalPosition *
            secondaryFrequency +
            float3(
                31.7,
                0.0,
                18.4
            )
        );

    // Use these lines only if IFC_FBM3D returns approximately -1 to +1.
    // broadNoise = broadNoise * 0.5 + 0.5;
    // secondaryNoise = secondaryNoise * 0.5 + 0.5;

    MacroMask =
        saturate(
            broadNoise * 0.75 +
            secondaryNoise * 0.25
        );

    MacroMask =
        smoothstep(
            0.18,
            0.82,
            MacroMask
        );

    const float3 dryTint =
        float3(
            0.45, 0.48, 0.32
        );

    const float3 lushTint =
        float3(
            0.12, 0.53, 0.22
        );

    float3 macroTint =
        lerp(
            dryTint,
            lushTint,
            MacroMask
        );

    const float colorStrength =
        0.12;


    // Sampling terrain stuff
    float2 distributionUV =
        saturate(TerrainMapUV);

    float3 distributionWeights =
        SAMPLE_TEXTURE2D(
            TerrainMapDist.tex,
            TerrainMapDist.samplerstate,
            distributionUV
        ).rgb;


    distributionWeights =
        max(
            distributionWeights,
            0.0.xxx
        );

    float totalWeight =
        max(
            dot(
                distributionWeights,
                1.0.xxx
            ),
            0.0001
        );

    distributionWeights /=
        totalWeight;

    float grassWeight =
        distributionWeights.r;

    float dryGrassWeight =
        distributionWeights.g;

    float soilWeight =
        distributionWeights.b;

    VariedColor =
        BaseColor * macroTint * .75;
        // lerp(
        //     1.0.xxx,
        //     macroTint,
        //     colorStrength
        // );

    const float smoothnessStrength =
        0.035;

    VariedSmoothness =
        saturate(
            Smoothness +
            (
                MacroMask -
                0.5
            ) *
            smoothnessStrength
        );

    //VariedColor = grassWeight;
}

#endif