#ifndef TERRAIN_DISTRIBUTION_BLEND_INCLUDED
#define TERRAIN_DISTRIBUTION_BLEND_INCLUDED


void TerrainDistributionBlend_float(
    float4x4 GrassMaterial,
    float4x4 DryGrassMaterial,
    float4x4 SoilMaterial,

    UnityTexture2D MainTex,
    float2 TerrainUV,

    out float4x4 BlendedMaterial,
    out float3 DistributionWeights)
{
    // ------------------------------------------------------------
    // 1. Sample the control map once across the complete terrain
    // ------------------------------------------------------------

    float2 uv =
        saturate(TerrainUV);

    // R = grass
    // G = dry grass
    // B = soil
    float3 weights =
        SAMPLE_TEXTURE2D(
            MainTex.tex,
            MainTex.samplerstate,
            uv
        ).rgb;


    // ------------------------------------------------------------
    // 2. Clean and normalize the weights
    // ------------------------------------------------------------

    weights =
        max(
            weights,
            0.0.xxx
        );

    //weights = float3(0, 0, 1);
    weights.r *= .25;

    float weightSum =
        max(
            weights.r +
            weights.g +
            weights.b,
            0.0001
        );

    weights /=
        weightSum;

    DistributionWeights =
        weights;


    // ------------------------------------------------------------
    // 3. Blend the complete packed terrain materials
    // ------------------------------------------------------------

    BlendedMaterial =
        GrassMaterial *
        weights.r +

        DryGrassMaterial *
        weights.g +

        SoilMaterial *
        weights.b;
}


#endif