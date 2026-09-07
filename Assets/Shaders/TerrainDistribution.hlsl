#ifndef TERRAIN_DISTRIBUTION_INCLUDED
#define TERRAIN_DISTRIBUTION_INCLUDED

void TerrainDistribution_float(
    UnityTexture2D TerrainMap,
    float2 TerrainUV,

    out float GrassWeight,
    out float DryGrassWeight,
    out float SoilWeight,

    out float DryGrassBlend,
    out float SoilBlend)
{
    // The control map covers the complete terrain once.
    float2 uv =
        saturate(TerrainUV);

    // R = grass
    // G = dry grass
    // B = soil
    float3 weights =
        SAMPLE_TEXTURE2D(
            TerrainMap.tex,
            TerrainMap.samplerstate,
            uv
        ).rgb;

    // Prevent negative values from filtering or compression.
    weights =
        max(
            weights,
            0.0.xxx
        );

    // Filtering and compression can make R + G + B deviate
    // slightly from one, so normalize the sampled weights.
    float weightSum =
        max(
            weights.r +
            weights.g +
            weights.b,
            0.0001
        );

    weights /=
        weightSum;

    GrassWeight =
        weights.r;

    DryGrassWeight =
        weights.g;

    SoilWeight =
        weights.b;


    // Blend grass and dry grass while preserving their
    // relative proportions.
    float vegetationWeight =
        GrassWeight +
        DryGrassWeight;

    DryGrassBlend =
        DryGrassWeight /
        max(
            vegetationWeight,
            0.0001
        );

    // Because the weights are normalized, this is the correct
    // mask for blending soil over the vegetation result.
    SoilBlend =
        SoilWeight;
}

#endif