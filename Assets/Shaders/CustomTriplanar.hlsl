//UNITY_SHADER_NO_UPGRADE

#ifndef CUSTOM_CONCRETE_INCLUDED
#define CUSTOM_CONCRETE_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

float3 GetTriplanarWeights(
    float3 normalWS,
    float blendSharpness)
{
    // We care about alignment with each axis, not whether the
    // surface faces the positive or negative direction.
    float3 weights = abs(normalWS);

    // Higher values produce sharper transitions between projections.
    weights = pow(
        weights,
        max(blendSharpness, 0.0001));

    // Normalize so x + y + z = 1.
    float weightSum =
        weights.x +
        weights.y +
        weights.z;

    return weights / max(weightSum, 0.0001);
}

void GetTriplanarUVs(
    float3 positionWS,
    float3 geometricNormalWS,
    out float2 uvX,
    out float2 uvY,
    out float2 uvZ)
{
    float signX = geometricNormalWS.x >= 0.0 ? 1.0 : -1.0;
    float signY = geometricNormalWS.y >= 0.0 ? 1.0 : -1.0;
    float signZ = geometricNormalWS.z >= 0.0 ? 1.0 : -1.0;

    // X projection: YZ plane.
    uvX = float2(
        -positionWS.z * signX,
         positionWS.y
    );

    // Y projection: XZ plane.
    uvY = float2(
         positionWS.x,
        -positionWS.z * signY
    );

    // Z projection: XY plane.
    uvZ = float2(
         positionWS.x * signZ,
         positionWS.y
    );
}

float3 SampleTriplanarColor(
    UnityTexture2D textureInput,
    float2 uvX,
    float2 uvY,
    float2 uvZ,
    float3 weights)
{
    float3 sampleX = SAMPLE_TEXTURE2D(
        textureInput.tex,
        textureInput.samplerstate,
        uvX
    ).rgb;

    float3 sampleY = SAMPLE_TEXTURE2D(
        textureInput.tex,
        textureInput.samplerstate,
        uvY
    ).rgb;

    float3 sampleZ = SAMPLE_TEXTURE2D(
        textureInput.tex,
        textureInput.samplerstate,
        uvZ
    ).rgb;

    return
        sampleX * weights.x +
        sampleY * weights.y +
        sampleZ * weights.z;
}

float3 SampleTriplanarNormalWS(
    UnityTexture2D normalTexture,
    float2 uvX,
    float2 uvY,
    float2 uvZ,
    float3 weights,
    float3 geometricNormalWS,
    float normalStrength)
{
    // Each result is initially a tangent-space normal.
    float3 normalX_TS = UnpackNormalScale(
        SAMPLE_TEXTURE2D(
            normalTexture.tex,
            normalTexture.samplerstate,
            uvX
        ),
        normalStrength
    );

    float3 normalY_TS = UnpackNormalScale(
        SAMPLE_TEXTURE2D(
            normalTexture.tex,
            normalTexture.samplerstate,
            uvY
        ),
        normalStrength
    );

    float3 normalZ_TS = UnpackNormalScale(
        SAMPLE_TEXTURE2D(
            normalTexture.tex,
            normalTexture.samplerstate,
            uvZ
        ),
        normalStrength
    );

    float signX = geometricNormalWS.x >= 0.0 ? 1.0 : -1.0;
    float signY = geometricNormalWS.y >= 0.0 ? 1.0 : -1.0;
    float signZ = geometricNormalWS.z >= 0.0 ? 1.0 : -1.0;

    // X projection: tangent basis lies on the YZ plane.
    float3 normalX_WS = float3(
         normalX_TS.z * signX,
         normalX_TS.y,
        -normalX_TS.x * signX
    );

    // Y projection: tangent basis lies on the XZ plane.
    float3 normalY_WS = float3(
         normalY_TS.x,
         normalY_TS.z * signY,
        -normalY_TS.y * signY
    );

    // Z projection: tangent basis lies on the XY plane.
    float3 normalZ_WS = float3(
         normalZ_TS.x * signZ,
         normalZ_TS.y,
         normalZ_TS.z * signZ
    );

    return normalize(
        normalX_WS * weights.x +
        normalY_WS * weights.y +
        normalZ_WS * weights.z
    );
}

void CustomTriplanar_float(
    UnityTexture2D BaseTex,
    UnityTexture2D NormalTex,
    UnityTexture2D RoughnessTex,

    float3 PositionWS,
    float3 GeometricNormalWS,

    float4 Tint,
    float Tiling,
    float BlendSharpness,
    float NormalStrength,
    float Scalar,

    out float3 BaseColor,
    out float3 NormalWS,
    out float Smoothness)
{

    GeometricNormalWS = normalize(GeometricNormalWS);

    float3 weights = GetTriplanarWeights(
        GeometricNormalWS,
        BlendSharpness
    );

    float3 scaledPositionWS =
        PositionWS * Tiling;

    float2 uvX;
    float2 uvY;
    float2 uvZ;

    GetTriplanarUVs(
        scaledPositionWS,
        GeometricNormalWS,
        uvX,
        uvY,
        uvZ
    );

    float3 triplanarColor = SampleTriplanarColor(
        BaseTex,
        uvX,
        uvY,
        uvZ,
        weights
    );

    BaseColor =
        Tint.rgb * triplanarColor;

    NormalWS = SampleTriplanarNormalWS(
        NormalTex,
        uvX,
        uvY,
        uvZ,
        weights,
        GeometricNormalWS,
        NormalStrength
    );

    
    float roughness = SampleTriplanarColor(
        RoughnessTex,
        uvX,
        uvY,
        uvZ,
        weights
    ).r;

    Smoothness = saturate(1.0 - roughness);
}

#endif