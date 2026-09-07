#ifndef IFC_NOISE_INCLUDED
#define IFC_NOISE_INCLUDED

// ------------------------------------------------------------
// Hash
// ------------------------------------------------------------

// Maps a 3D position to one deterministic pseudorandom value
// in the range [0, 1].
float IFC_Hash31(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);

    return frac((p.x + p.y) * p.z);
}


// ------------------------------------------------------------
// 3D value noise
// ------------------------------------------------------------

float IFC_ValueNoise3D(float3 p)
{
    float3 cell = floor(p);
    float3 localPosition = frac(p);

    // Cubic Hermite interpolation:
    // smooth derivative at cell boundaries.
    float3 blend =
        localPosition *
        localPosition *
        (3.0 - 2.0 * localPosition);

    // Eight corners of the current 3D lattice cell.
    float n000 = IFC_Hash31(cell + float3(0, 0, 0));
    float n100 = IFC_Hash31(cell + float3(1, 0, 0));
    float n010 = IFC_Hash31(cell + float3(0, 1, 0));
    float n110 = IFC_Hash31(cell + float3(1, 1, 0));

    float n001 = IFC_Hash31(cell + float3(0, 0, 1));
    float n101 = IFC_Hash31(cell + float3(1, 0, 1));
    float n011 = IFC_Hash31(cell + float3(0, 1, 1));
    float n111 = IFC_Hash31(cell + float3(1, 1, 1));

    // Interpolate along X.
    float nx00 = lerp(n000, n100, blend.x);
    float nx10 = lerp(n010, n110, blend.x);
    float nx01 = lerp(n001, n101, blend.x);
    float nx11 = lerp(n011, n111, blend.x);

    // Interpolate along Y.
    float nxy0 = lerp(nx00, nx10, blend.y);
    float nxy1 = lerp(nx01, nx11, blend.y);

    // Interpolate along Z.
    return lerp(nxy0, nxy1, blend.z);
}


// ------------------------------------------------------------
// Fractal Brownian motion
// ------------------------------------------------------------

float IFC_FBM3D(float3 p)
{
    float value = 0.0;
    float amplitude = 0.5;
    float normalization = 0.0;

    [unroll]
    for (int octave = 0; octave < 3; octave++)
    {
        value +=
            IFC_ValueNoise3D(p) *
            amplitude;

        normalization += amplitude;

        // Slightly above 2 prevents extremely obvious octave
        // alignment. The translation further decorrelates them.
        p =
            p * 2.03 +
            float3(17.1, 9.2, 13.7);

        amplitude *= 0.5;
    }

    return value / normalization;
}

#endif