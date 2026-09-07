//UNITY_SHADER_NO_UPGRADE
#ifndef CUSTOM_BUILDING_INCLUDED
#define CUSTOM_BUILDING_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Assets/Shaders/Noise.hlsl"

// ============================================================================
// Triplanar mapping
// ============================================================================

float3 GetTriplanarWeights(
    float3 normalWS,
    float blendSharpness)
{
    float3 weights = pow(
        abs(normalWS),
        max(blendSharpness, 0.0001)
    );

    return weights / max(
        weights.x + weights.y + weights.z,
        0.0001
    );
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
    float3 normalXTS = UnpackNormalScale(
        SAMPLE_TEXTURE2D(
            normalTexture.tex,
            normalTexture.samplerstate,
            uvX
        ),
        normalStrength
    );

    float3 normalYTS = UnpackNormalScale(
        SAMPLE_TEXTURE2D(
            normalTexture.tex,
            normalTexture.samplerstate,
            uvY
        ),
        normalStrength
    );

    float3 normalZTS = UnpackNormalScale(
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

    float3 normalXWS = float3(
         normalXTS.z * signX,
         normalXTS.y,
        -normalXTS.x * signX
    );

    float3 normalYWS = float3(
         normalYTS.x,
         normalYTS.z * signY,
        -normalYTS.y * signY
    );

    float3 normalZWS = float3(
         normalZTS.x * signZ,
         normalZTS.y,
         normalZTS.z * signZ
    );

    return normalize(
        normalXWS * weights.x +
        normalYWS * weights.y +
        normalZWS * weights.z
    );
}

// ============================================================================
// Procedural window helpers
// ============================================================================

float GetCenteredSquareMask(
    float2 localUV,
    float squareSize)
{
    float2 distanceFromCenter =
        abs(localUV);

    float halfSize =
        saturate(squareSize) * 0.5;

    float distanceToEdge =
        halfSize -
        max(
            distanceFromCenter.x,
            distanceFromCenter.y
        );

    float edgeWidth =
        max(
            fwidth(distanceToEdge),
            0.0001
        );

    return smoothstep(
        -edgeWidth,
         edgeWidth,
         distanceToEdge
    );
}


float PositiveModulo(
    float value,
    float modulus)
{
    return value - modulus * floor(value / modulus);
}

float GetWindowGroupMask(
    float2 gridUV,
    float patternOffset)
{
    float columnIndex = floor(gridUV.x);
    float patternIndex = PositiveModulo(
        columnIndex + patternOffset,
        3.0
    );

    // Two window columns followed by one empty column.
    return 1.0 - step(2.0, patternIndex);
}

// Deterministic hash used to assign one stable random value to each window
// cell. The input is derived from the fitted facade grid and a quantized
// world-space facade identifier, so the result remains fixed over time.
float HashWindowCell(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);

    return frac(
        (p.x + p.y) * p.z
    );
}

// ============================================================================
// Rain streaks
// ============================================================================

float GetRainStreakMask(
    float3 positionWS,
    float2 facadeGridUV,
    float buildingMinY,
    float buildingMaxY,
    float wallMask,
    float windowMask)
{
    const float streakFrequency = 2.25;
    const float streakThreshold = 0.58;
    const float streakSoftness = 0.10;

    const float minimumStreakLength = 1.25;
    const float maximumStreakLength = 4.25;
    const float topInset = 0.10;
    const float endFadeFraction = 0.35;

    const float breakupHorizontalFrequency = 0.65;
    const float breakupVerticalFrequency = 0.22;
    const float breakupStrength = 0.30;
    const float noiseSeed = 23.7;

    float buildingHeight = max(
        buildingMaxY - buildingMinY,
        0.001
    );

    float distanceBelowRoof = clamp(
        buildingMaxY - positionWS.y,
        0.0,
        buildingHeight
    );

    float facadeX = facadeGridUV.x;

    float columnNoise = IFC_FBM3D(
        float3(
            facadeX * streakFrequency,
            7.15,
            noiseSeed
        )
    );

    float columnMask = smoothstep(
        streakThreshold,
        streakThreshold + streakSoftness,
        columnNoise
    );

    float lengthFactor = saturate(
        (columnNoise - streakThreshold) /
        max(1.0 - streakThreshold, 0.001)
    );

    float localStreakLength = min(
        lerp(
            minimumStreakLength,
            maximumStreakLength,
            lengthFactor
        ),
        buildingHeight
    );

    float startMask = smoothstep(
        0.02,
        topInset,
        distanceBelowRoof
    );

    float fadeStart =
        localStreakLength * (1.0 - endFadeFraction);

    float endMask = 1.0 - smoothstep(
        fadeStart,
        localStreakLength,
        distanceBelowRoof
    );

    float breakupNoise = IFC_FBM3D(
        float3(
            facadeX * breakupHorizontalFrequency + 11.3,
            distanceBelowRoof * breakupVerticalFrequency,
            noiseSeed + 37.1
        )
    );

    float breakup = lerp(
        1.0 - breakupStrength,
        1.0,
        breakupNoise
    );

    return saturate(
        columnMask *
        startMask *
        endMask *
        breakup *
        wallMask *
        (1.0 - windowMask)
    );
}


// ============================================================================
// Cubemap helpers
// ============================================================================
float3 RotateDirectionAroundY(
    float3 direction,
    float angleDegrees)
{
    float angle =
        radians(angleDegrees);

    float sine;
    float cosine;

    sincos(
        angle,
        sine,
        cosine
    );

    return float3(
        cosine * direction.x +
        sine * direction.z,

        direction.y,

       -sine * direction.x +
        cosine * direction.z
    );
}


float3 SampleCubemapLOD(
    UnityTextureCube cubemapTexture,
    float3 direction,
    float lod)
{
    return SAMPLE_TEXTURECUBE_LOD(
        cubemapTexture.tex,
        cubemapTexture.samplerstate,
        normalize(direction),
        lod
    ).rgb;
}

// ============================================================================
// Shader Graph custom function
// ============================================================================

void CustomBuilding_float(
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

    float4 WallTint,
    float4 RoofTint,
    float RoofStart,
    float RoofEnd,

    float GridCellWidth,
    float GridCellHeight,
    float GridPadding,
    float SquareSize,

    float BuildingMinY,
    float BuildingMaxY,

    float HorizontalOffset,
    float VerticalOffset,

    float BottomPadding,
    float TopPadding,
    float4 WindowEmission,
    float4 FacadeData,

    UnityTextureCube ExteriorCubemap,
    UnityTextureCube InteriorCubemap,
    float3 ViewDirectionWS,

    float NightFactor,
    float EmissionStrength,

    out float3 BaseColor,
    out float3 NormalWS,
    out float Smoothness,
    out float3 Emission,
    out float Metallic)
{
    // GridCellHeight, GridPadding, and Scalar remain in the signature so the
    // existing Shader Graph ports do not need to be rebuilt. The current
    // facade layout fits exactly four rows and uses SquareSize for window size.

    // ========================================================================
    // 1. Triplanar concrete inputs
    // ========================================================================

    GeometricNormalWS = normalize(GeometricNormalWS);

    float3 triplanarWeights = GetTriplanarWeights(
        GeometricNormalWS,
        BlendSharpness
    );

    float2 uvX;
    float2 uvY;
    float2 uvZ;

    GetTriplanarUVs(
        PositionWS * Tiling,
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
        triplanarWeights
    );

    float3 triplanarNormalWS = SampleTriplanarNormalWS(
        NormalTex,
        uvX,
        uvY,
        uvZ,
        triplanarWeights,
        GeometricNormalWS,
        NormalStrength
    );

    float roughness = SampleTriplanarColor(
        RoughnessTex,
        uvX,
        uvY,
        uvZ,
        triplanarWeights
    ).r;

    // Keep the concrete in a plausible rough range. Window materials override
    // this value later, after all concrete weathering has been applied.
    const float maximumConcreteSmoothness = 0.32;
    float surfaceSmoothness = min(
        saturate(1.0 - roughness),
        maximumConcreteSmoothness
    );

    // ========================================================================
    // 2. Roof and wall masks
    // ========================================================================

    float roofMask = smoothstep(
        RoofStart,
        RoofEnd,
        saturate(GeometricNormalWS.y)
    );

    float wallMask = 1.0 - smoothstep(
        0.10,
        0.25,
        abs(GeometricNormalWS.y)
    );

    float3 orientationTint = lerp(
        WallTint.rgb,
        RoofTint.rgb,
        roofMask
    ) * 5;

    // ========================================================================
    // 3. Fitted window grid from UV3 facade data
    //
    // FacadeData.x = horizontal distance along this facade
    // FacadeData.y = vertical distance above this facade's bottom
    // FacadeData.z = facade width
    // FacadeData.w = facade height
    // ========================================================================

    const float minimumExtent = 0.001;

    float facadeU = FacadeData.x;
    float facadeV = FacadeData.y;
    float facadeWidth = max(FacadeData.z, minimumExtent);
    float facadeHeight = max(FacadeData.w, minimumExtent);

    float validFacadeDataMask =
        step(minimumExtent, FacadeData.z) *
        step(minimumExtent, FacadeData.w);

    // Horizontal layout: fit complete cells between two side margins.
    const float desiredSidePadding = 0.75;
    const float minimumFacadeWidth = 2.40;
    const float minimumFittedCellWidth = 1.80;

    float sidePadding = min(
        desiredSidePadding,
        facadeWidth * 0.22
    );

    float maximumHorizontalOffset = max(
        sidePadding - 0.05,
        0.0
    );

    float safeHorizontalOffset = clamp(
        HorizontalOffset,
        -maximumHorizontalOffset,
        maximumHorizontalOffset
    );

    float leftPadding = sidePadding + safeHorizontalOffset;
    float rightPadding = sidePadding - safeHorizontalOffset;

    float usableFacadeWidth = max(
        facadeWidth - leftPadding - rightPadding,
        minimumExtent
    );

    float desiredCellWidth = max(GridCellWidth, 0.01);
    float horizontalCellCount = max(
        floor(usableFacadeWidth / desiredCellWidth + 0.5),
        1.0
    );

    float fittedCellWidth =
        usableFacadeWidth / horizontalCellCount;

    float horizontalGridU =
        (facadeU - leftPadding) / fittedCellWidth;

    float validHorizontalMask =
        step(minimumFacadeWidth, facadeWidth) *
        step(minimumFittedCellWidth, fittedCellWidth) *
        step(leftPadding, facadeU) *
        step(facadeU, facadeWidth - rightPadding);

    // Vertical layout: fit exactly four rows to each facade's own height.
    float effectiveBottomPadding = max(
        BottomPadding + VerticalOffset,
        0.0
    );

    float effectiveTopPadding = max(
        TopPadding - VerticalOffset,
        0.0
    );

    float usableFacadeHeight = max(
        facadeHeight - effectiveBottomPadding - effectiveTopPadding,
        minimumExtent
    );

    const float verticalCellCount = 4.0;
    float fittedCellHeight =
        usableFacadeHeight / verticalCellCount;

    float verticalGridV =
        (facadeV - effectiveBottomPadding) / fittedCellHeight;

    float validVerticalMask =
        step(effectiveBottomPadding, facadeV) *
        step(facadeV, facadeHeight - effectiveTopPadding);

    float2 gridUV = float2(
        horizontalGridU,
        verticalGridV
    );

    // cellUV is the reusable 0-1 canvas inside the current grid cell.
    // centeredCellUV is convenient for symmetric window shapes.
    float2 cellUV = frac(gridUV);
    float2 centeredCellUV = cellUV - 0.5;

    float groupMask = GetWindowGroupMask(
        gridUV,
        0.0
    );

    float windowLayoutMask =
        groupMask *
        validFacadeDataMask *
        validHorizontalMask *
        validVerticalMask *
        wallMask;

    // Nested window regions: opening -> reveal -> frame -> glass.
    float openingSize = SquareSize;
    const float revealThickness = 0.025;
    const float frameThickness = 0.045;

    float frameOuterSize = max(
        openingSize - 2.0 * revealThickness,
        0.01
    );

    float glassSize = max(
        frameOuterSize - 2.0 * frameThickness,
        0.01
    );

    float openingMask = GetCenteredSquareMask(
        centeredCellUV,
        openingSize
    ) * windowLayoutMask;

    float frameOuterMask = GetCenteredSquareMask(
        centeredCellUV,
        frameOuterSize
    ) * windowLayoutMask;

    float glassMask = GetCenteredSquareMask(
        centeredCellUV,
        glassSize
    ) * windowLayoutMask;

    float revealMask = saturate(
        openingMask - frameOuterMask
    );

    float frameMask = saturate(
        frameOuterMask - glassMask
    );

    // Weathering and construction joints treat the complete opening as a
    // protected region.
    float windowMask = openingMask;

    // Remap only the glass rectangle to its own 0-1 local canvas.
    float glassMinimum =
        0.5 - glassSize * 0.5;

    float2 glassUV = saturate(
        (cellUV - glassMinimum) / glassSize
    );

    // A slightly offset inner border provides a cheap recessed shadow.
    const float2 recessShadowOffset = float2(
        -0.020,
         0.020
    );

    float shiftedFrameOuterMask = GetCenteredSquareMask(
        centeredCellUV + recessShadowOffset,
        frameOuterSize
    ) * windowLayoutMask;

    float recessShadowMask = saturate(
        openingMask - shiftedFrameOuterMask
    );

    // ========================================================================
    // 4. Base concrete and macro variation
    // ========================================================================

    float3 concreteColor =
        triplanarColor *
        Tint.rgb *
        orientationTint;

    const float macroNoiseScale = .25;
    const float macroSmoothnessVariation = 0.04;
    const float3 cleanConcrete =
        float3(1.04, 1.02, 0.98);
    const float3 weatheredConcrete =
        float3(0.90, 0.92, 0.89)* 0.5;

    float macroNoise = IFC_FBM3D(
        PositionWS * macroNoiseScale
    );

    float macroSigned = macroNoise * 2.0 - 1.0;

    concreteColor *= lerp(
        weatheredConcrete,
        cleanConcrete,
        macroNoise
    );

    surfaceSmoothness = saturate(
        surfaceSmoothness +
        macroSigned * macroSmoothnessVariation
    );

    surfaceSmoothness = min(
        surfaceSmoothness,
        maximumConcreteSmoothness
    );

    // ========================================================================
    // 5. Weathering layers
    // ========================================================================

    // Ground-level dampness.
    const float baseWeatherScale = 0.35;
    const float baseWeatherHeight = 1.20;
    const float dampNoiseStart = 0.35;
    const float dampNoiseEnd = 0.70;
    const float dampColorStrength = 0.50;
    const float dampSmoothness = 0.35;
    const float3 dampTint = float3(0.85, 0.90, 0.95);

    float heightAboveBase =
        PositionWS.y - BuildingMinY;

    float baseBand = 1.0 - smoothstep(
        0.0,
        baseWeatherHeight,
        heightAboveBase
    );

    float baseNoise = IFC_FBM3D(
        PositionWS * baseWeatherScale
    );

    float dampMask =
        baseBand *
        wallMask *
        smoothstep(
            dampNoiseStart,
            dampNoiseEnd,
            baseNoise
        );

    concreteColor = lerp(
        concreteColor,
        concreteColor * dampTint * 0.75,
        dampMask * dampColorStrength
    );

    surfaceSmoothness = lerp(
        surfaceSmoothness,
        dampSmoothness,
        dampMask
    );

    // Rain streaks beneath the roofline.
    float rainStreakMask = GetRainStreakMask(
        PositionWS,
        gridUV,
        BuildingMinY,
        BuildingMaxY,
        wallMask,
        windowMask
    );

    const float3 rainStreakTint =
        float3(0.58, 0.63, 0.60);
    const float rainStreakColorStrength = 0.25;
    const float rainStreakSmoothnessReduction = 0.04;

    concreteColor = lerp(
        concreteColor,
        concreteColor * rainStreakTint,
        rainStreakMask * rainStreakColorStrength
    );

    surfaceSmoothness = saturate(
        surfaceSmoothness -
        rainStreakMask * rainStreakSmoothnessReduction
    );

    // Broad roof dirt patches. Dirt is kept rougher than clean concrete.
    float roofDirtNoise = IFC_FBM3D(
        float3(
            PositionWS.x * 0.10,
            18.7,
            PositionWS.z * 0.10
        )
    );

    float roofDirtMask = smoothstep(
        0.55,
        0.72,
        roofDirtNoise
    ) * roofMask;

    const float3 roofDirtTint =
        float3(0.52, 0.48, 0.38);
    const float roofDirtColorStrength = 0.80;
    const float roofDirtSmoothnessReduction = 0.04;

    concreteColor = lerp(
        concreteColor,
        concreteColor * roofDirtTint,
        roofDirtMask * roofDirtColorStrength
    );

    surfaceSmoothness = saturate(
        surfaceSmoothness -
        roofDirtMask * roofDirtSmoothnessReduction
    );

    // Horizontal construction joints aligned with the fitted rows.
    float floorPhase = frac(gridUV.y);
    float distanceToJoint = min(
        floorPhase,
        1.0 - floorPhase
    );

    const float jointWidth = 0.025;
    float jointSoftness = max(
        fwidth(gridUV.y),
        0.0001
    );

    float jointMask =
        (1.0 - smoothstep(
            jointWidth,
            jointWidth + jointSoftness,
            distanceToJoint
        )) *
        validFacadeDataMask *
        validVerticalMask *
        wallMask *
        (1.0 - windowMask);

    concreteColor *= lerp(
        1.0,
        0.82,
        jointMask
    );

    surfaceSmoothness = lerp(
        surfaceSmoothness,
        0.18,
        jointMask
    );

    // Subtle long-term directional exposure.
    float3 weatherDirection = normalize(
        float3(0.75, 0.0, 0.45)
    );

    float exposure = saturate(
        dot(GeometricNormalWS, weatherDirection) *
        0.5 + 0.5
    ) * wallMask;

    const float3 exposedWallTint =
        float3(0.91, 0.94, 0.93);

    concreteColor *= lerp(
        float3(1.0, 1.0, 1.0),
        exposedWallTint,
        exposure * 0.08
    );

    // ========================================================================
    // 6. Glass optical appearance
    // ========================================================================

    float3 glassNormalWS = GeometricNormalWS;
    float3 viewDirectionWS = normalize(ViewDirectionWS);

    float normalDotView = saturate(
        dot(glassNormalWS, viewDirectionWS)
    );

    // Exterior cubemap: reflected view direction and a smoothness-selected LOD.
    float3 exteriorDirectionWS = reflect(
        -viewDirectionWS,
        glassNormalWS
    );

    const float exteriorRotationDegrees = 0.0;
    exteriorDirectionWS = RotateDirectionAroundY(
        exteriorDirectionWS,
        exteriorRotationDegrees
    );

    const float revealSmoothness = 0.12;
    const float frameSmoothness = 0.60;
    const float glassSmoothness = 0.97;
    const float maximumReflectionLOD = 5.0;

    float exteriorLOD =
        (1.0 - glassSmoothness) *
        maximumReflectionLOD;

    float3 exteriorCubemapColor = SampleCubemapLOD(
        ExteriorCubemap,
        exteriorDirectionWS,
        exteriorLOD
    );

    // Interior cubemap: refract into the pane, then express the direction in
    // a facade-local coordinate frame so every facade gets a coherent room.
    const float3 worldUp = float3(0.0, 1.0, 0.0);

    float3 facadeRightWS = normalize(
        cross(worldUp, glassNormalWS) +
        float3(0.0001, 0.0, 0.0)
    );

    float3 facadeUpWS = normalize(
        cross(glassNormalWS, facadeRightWS)
    );

    float3 facadeInwardWS =
        -glassNormalWS;

    const float airToGlassRatio = 1.0 / 1.5;

    float3 transmittedDirectionWS = refract(
        -viewDirectionWS,
        glassNormalWS,
        airToGlassRatio
    );

    float3 interiorDirection = float3(
        dot(transmittedDirectionWS, facadeRightWS),
        dot(transmittedDirectionWS, facadeUpWS),
        dot(transmittedDirectionWS, facadeInwardWS)
    );

    const float interiorParallax = 0.10;
    interiorDirection.xy +=
        (glassUV - 0.5) *
        interiorParallax;

    interiorDirection = normalize(
        interiorDirection
    );

    const float interiorRotationDegrees = 0.0;
    interiorDirection = RotateDirectionAroundY(
        interiorDirection,
        interiorRotationDegrees
    );

    const float interiorLOD = 0.0;

    float3 interiorCubemapColor = SampleCubemapLOD(
        InteriorCubemap,
        interiorDirection,
        interiorLOD
    );

    // Reduce the projected-panorama look: desaturate, darken the midtones,
    // tint through absorption, and darken the pane near its frame.
    float interiorLuminance = dot(
        interiorCubemapColor,
        float3(0.2126, 0.7152, 0.0722)
    );

    interiorCubemapColor = lerp(
        interiorLuminance.xxx,
        interiorCubemapColor,
        0.55
    );

    interiorCubemapColor = pow(
        max(interiorCubemapColor, 0.0),
        1.35
    );

    float opticalThickness = lerp(
        1.0,
        2.5,
        1.0 - normalDotView
    );

    const float3 absorptionCoefficient = float3(
        0.32,
        0.12,
        0.06
    );

    interiorCubemapColor *= exp(
        -absorptionCoefficient *
        opticalThickness
    );

    float2 centeredGlassUV =
        abs(glassUV - 0.5) * 2.0;

    float distanceToGlassEdge =
        1.0 - max(
            centeredGlassUV.x,
            centeredGlassUV.y
        );

    float innerEdgeMask = smoothstep(
        0.0,
        0.16,
        distanceToGlassEdge
    );

    interiorCubemapColor *= lerp(
        0.68,
        1.0,
        innerEdgeMask
    );

    // Dielectric Fresnel: transmission dominates frontally; exterior
    // reflection grows toward grazing angles.
    const float glassF0 = 0.04;

    float fresnel = glassF0 +
        (1.0 - glassF0) *
        pow(1.0 - normalDotView, 5.0);

    float nightFactor = saturate(NightFactor);

    float exteriorStrength = lerp(
        0.40,
        0.18,
        nightFactor
    );

    float interiorStrength = lerp(
        0.42,
        0.65,
        nightFactor
    );

    float3 glassEnvironment =
        exteriorCubemapColor *
        exteriorStrength *
        fresnel +

        interiorCubemapColor *
        interiorStrength *
        (1.0 - fresnel);

    // ========================================================================
    // 7. Final surface outputs
    // ========================================================================

    const float3 revealColor = float3(
        0.018,
        0.022,
        0.024
    );

    const float3 recessShadowColor = float3(
        0.006,
        0.008,
        0.010
    );

    const float3 frameColor = float3(
        0.24,
        0.27,
        0.28
    );

    const float3 glassBottomColor = float3(
        0.006,
        0.014,
        0.018
    );

    const float3 glassTopColor = float3(
        0.012,
        0.026,
        0.032
    );

    float glassGradient = smoothstep(
        0.05,
        0.95,
        glassUV.y
    );

    float3 glassColor = lerp(
        glassBottomColor,
        glassTopColor,
        glassGradient
    );

    BaseColor = concreteColor;

    BaseColor = lerp(
        BaseColor,
        revealColor,
        revealMask
    );

    BaseColor = lerp(
        BaseColor,
        recessShadowColor,
        recessShadowMask
    );

    BaseColor = lerp(
        BaseColor,
        frameColor,
        frameMask
    );

    BaseColor = lerp(
        BaseColor,
        glassColor,
        glassMask
    );

    surfaceSmoothness = lerp(
        surfaceSmoothness,
        revealSmoothness,
        revealMask
    );

    surfaceSmoothness = lerp(
        surfaceSmoothness,
        frameSmoothness,
        frameMask
    );

    surfaceSmoothness = lerp(
        surfaceSmoothness,
        glassSmoothness,
        glassMask
    );

    Smoothness = surfaceSmoothness;

    NormalWS = normalize(
        lerp(
            triplanarNormalWS,
            GeometricNormalWS,
            windowMask
        )
    );

    // Keep the lit/unlit decision stable for every pixel in a window cell.
    // The first two seed components are the fitted grid-cell coordinates.
    // The third identifies the facade using its quantized world-space plane
    // position, orientation, dimensions, and building elevation.
    float2 windowCellIndex = floor(gridUV);

    float facadePlaneCoordinate = floor(
        dot(PositionWS, GeometricNormalWS) * 0.10
    );

    float3 quantizedFacadeNormal = floor(
        (GeometricNormalWS + 1.0) * 8.0
    );

    float facadeOrientationSeed = dot(
        quantizedFacadeNormal,
        float3(1.0, 17.0, 37.0)
    );

    float3 windowCellSeed = float3(
        windowCellIndex.x + facadeOrientationSeed,
        windowCellIndex.y + floor(BuildingMinY * 0.25),
        facadePlaneCoordinate +
        floor(facadeWidth * 0.25) * 13.0 +
        floor(facadeHeight * 0.25) * 29.0
    );

    float windowRandomValue = HashWindowCell(
        windowCellSeed
    );

    const float windowLitRate = 0.60;

    // Approximately windowLitRate of valid window cells receive emission.
    float windowLitMask = step(
        windowRandomValue,
        windowLitRate
    );

    float3 interiorLight =
        WindowEmission.rgb *
        EmissionStrength *
        nightFactor *
        windowLitMask;

    Emission =
        (glassEnvironment + interiorLight) *
        glassMask;

    // Concrete and glass are dielectrics. Keep this at zero unless the frame
    // is later redesigned as exposed metal.
    Metallic = 0.0;
}

#endif
