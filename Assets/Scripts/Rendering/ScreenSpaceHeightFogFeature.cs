using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public sealed class ScreenSpaceHeightFogFeature : ScriptableRendererFeature
{
    public enum DebugView
    {
        FinalImage = 0,
        FogFactor = 1,
        HeightDensity = 2,
        Noise = 3
    }

    [Serializable]
    public sealed class Settings
    {
        [Header("General")]

        [Tooltip("Enables or disables the screen-space smoke effect.")]
        public bool effectEnabled = true;

        [Tooltip("Displays the effect in Unity's Scene view.")]
        public bool showInSceneView = true;

        [Tooltip("Point in the URP pipeline at which the effect is applied.")]
        public RenderPassEvent injectionPoint =
            RenderPassEvent.BeforeRenderingPostProcessing;

        [Tooltip(
            "Assign Hidden/IFC/ScreenSpaceHeightFog. " +
            "The feature also attempts to find it automatically."
        )]
        public Shader shader;

        [Header("Fog Composition")]

        [ColorUsage(false, true)]
        public Color fogColor =
            new Color(0.24f, 0.28f, 0.29f, 1.0f);

        [Tooltip("Overall optical density integrated along the view ray.")]
        [Min(0.0f)]
        public float fogDensity = 0.018f;

        [Tooltip("Distance from the camera at which integration begins.")]
        [Min(0.0f)]
        public float startDistance = 5.0f;

        [Tooltip("Maximum distance integrated by the fog raymarch.")]
        [Min(1.0f)]
        public float maxDistance = 550.0f;

        [Tooltip("Maximum amount composited over the camera image.")]
        [Range(0.0f, 1.0f)]
        public float maximumOpacity = 0.50f;

        [Header("Height Layer")]

        [Tooltip(
            "Absolute world-space Y coordinate at which the fog has full " +
            "height density. Tune this to the terrain elevation."
        )]
        public float baseHeight = 5.0f;

        [Tooltip(
            "How rapidly density decreases above Base Height. " +
            "Higher values create a thinner ground layer."
        )]
        [Min(0.0001f)]
        public float heightFalloff = 0.09f;

        [Header("Procedural fBM")]

        [Tooltip(
            "World-space frequency of the main formations. " +
            "Lower values create broader smoke masses."
        )]
        [Min(0.000001f)]
        public float noiseScale = 0.008f;

        [Tooltip(
            "Blends between continuous fog and threshold-shaped smoke."
        )]
        [Range(0.0f, 1.0f)]
        public float noiseStrength = 0.90f;

        [Tooltip("Number of value-noise layers used by the fBM field.")]
        [Range(1, 5)]
        public int fbmOctaves = 4;

        [Tooltip("Frequency multiplier between consecutive fBM octaves.")]
        [Range(1.1f, 3.5f)]
        public float fbmLacunarity = 2.0f;

        [Tooltip("Amplitude multiplier between consecutive fBM octaves.")]
        [Range(0.1f, 0.9f)]
        public float fbmGain = 0.5f;

        [Tooltip("Separates dense smoke from thin regions.")]
        [Range(0.25f, 4.0f)]
        public float noiseContrast = 1.55f;

        [Tooltip(
            "How strongly world height shifts the XZ noise projection. " +
            "This gives the nominally 2D field volumetric variation."
        )]
        public Vector2 verticalShear =
            new Vector2(0.35f, -0.22f);

        [Header("Animation")]

        [Tooltip("World-space XZ direction in which the smoke drifts.")]
        public Vector2 noiseDirection =
            new Vector2(1.0f, 0.20f);

        [Tooltip(
            "Directional speed in procedural-noise coordinates per second."
        )]
        [Min(0.0f)]
        public float noiseSpeed = 0.0015f;

        [Tooltip(
            "Magnitude of slow internal deformation for each fBM octave."
        )]
        [Range(0.0f, 1.5f)]
        public float evolutionStrength = 0.35f;

        [Tooltip("Speed of the nonlinear internal deformation.")]
        [Range(0.0f, 1.0f)]
        public float evolutionSpeed = 0.16f;

        [Tooltip(
            "How differently consecutive fBM octaves move. " +
            "Higher values create more visible internal flow."
        )]
        [Range(0.0f, 2.0f)]
        public float flowVariation = 0.70f;

        [Header("Smoke Shaping")]

        [Tooltip(
            "Threshold used to form smoke pockets. " +
            "Higher values produce less overall smoke."
        )]
        [Range(0.0f, 1.0f)]
        public float smokeCoverage = 0.52f;

        [Tooltip("Softness of the boundaries between clear and dense areas.")]
        [Range(0.01f, 0.35f)]
        public float smokeSoftness = 0.09f;

        [Tooltip(
            "Minimum background fog retained between smoke formations."
        )]
        [Range(0.0f, 1.0f)]
        public float smokeFloor = 0.03f;

        [Tooltip("Density multiplier inside the strongest smoke pockets.")]
        [Range(0.5f, 3.0f)]
        public float smokePeakDensity = 1.60f;

        [Header("Domain Warping")]

        [Tooltip("Frequency of the low-frequency field that bends the smoke.")]
        [Range(0.05f, 2.0f)]
        public float warpScale = 0.65f;

        [Tooltip("How strongly the procedural coordinate space is distorted.")]
        [Range(0.0f, 3.0f)]
        public float warpStrength = 1.35f;

        [Tooltip("Speed at which the domain-warping field moves.")]
        [Range(0.0f, 0.2f)]
        public float warpSpeed = 0.04f;

        [Tooltip(
            "Blends the broad fBM field with rounded cloud-like detail."
        )]
        [Range(0.0f, 1.0f)]
        public float billowStrength = 0.50f;

        [Header("Horizon")]

        [Tooltip("Strength of the atmospheric band near the horizon.")]
        [Range(0.0f, 1.0f)]
        public float horizonStrength = 0.08f;

        [Tooltip(
            "Controls how narrow the horizon band is. " +
            "Higher values create a thinner band."
        )]
        [Min(0.1f)]
        public float horizonFalloff = 11.0f;

        [Header("Debug")]
        public DebugView debugView = DebugView.FinalImage;
    }

    [SerializeField]
    private Settings settings = new Settings();

    private Material fogMaterial;
    private FogPass fogPass;

    private static readonly int FogColorId =
        Shader.PropertyToID("_FogColor");

    private static readonly int FogDensityId =
        Shader.PropertyToID("_FogDensity");

    private static readonly int FogStartDistanceId =
        Shader.PropertyToID("_FogStartDistance");

    private static readonly int FogMaxDistanceId =
        Shader.PropertyToID("_FogMaxDistance");

    private static readonly int MaximumOpacityId =
        Shader.PropertyToID("_MaxOpacity");

    private static readonly int FogBaseHeightId =
        Shader.PropertyToID("_FogBaseHeight");

    private static readonly int HeightFalloffId =
        Shader.PropertyToID("_HeightFalloff");

    private static readonly int NoiseScaleId =
        Shader.PropertyToID("_NoiseScale");

    private static readonly int NoiseStrengthId =
        Shader.PropertyToID("_NoiseStrength");

    private static readonly int NoiseVelocityId =
        Shader.PropertyToID("_NoiseVelocity");

    private static readonly int VerticalShearId =
        Shader.PropertyToID("_VerticalShear");

    private static readonly int FBMOctavesId =
        Shader.PropertyToID("_FBMOctaves");

    private static readonly int FBMLacunarityId =
        Shader.PropertyToID("_FBMLacunarity");

    private static readonly int FBMGainId =
        Shader.PropertyToID("_FBMGain");

    private static readonly int NoiseContrastId =
        Shader.PropertyToID("_NoiseContrast");

    private static readonly int EvolutionStrengthId =
        Shader.PropertyToID("_EvolutionStrength");

    private static readonly int EvolutionSpeedId =
        Shader.PropertyToID("_EvolutionSpeed");

    private static readonly int FlowVariationId =
        Shader.PropertyToID("_FlowVariation");

    private static readonly int SmokeCoverageId =
        Shader.PropertyToID("_SmokeCoverage");

    private static readonly int SmokeSoftnessId =
        Shader.PropertyToID("_SmokeSoftness");

    private static readonly int SmokeFloorId =
        Shader.PropertyToID("_SmokeFloor");

    private static readonly int SmokePeakDensityId =
        Shader.PropertyToID("_SmokePeakDensity");

    private static readonly int WarpScaleId =
        Shader.PropertyToID("_WarpScale");

    private static readonly int WarpStrengthId =
        Shader.PropertyToID("_WarpStrength");

    private static readonly int WarpSpeedId =
        Shader.PropertyToID("_WarpSpeed");

    private static readonly int BillowStrengthId =
        Shader.PropertyToID("_BillowStrength");

    private static readonly int HorizonStrengthId =
        Shader.PropertyToID("_HorizonStrength");

    private static readonly int HorizonFalloffId =
        Shader.PropertyToID("_HorizonFalloff");

    private static readonly int DebugModeId =
        Shader.PropertyToID("_DebugMode");

    public override void Create()
    {
        CoreUtils.Destroy(fogMaterial);

        fogMaterial = null;
        fogPass = null;

        Shader selectedShader = settings.shader;

        if (selectedShader == null)
        {
            selectedShader = Shader.Find(
                "Hidden/IFC/ScreenSpaceHeightFog"
            );
        }

        if (selectedShader == null)
        {
            Debug.LogError(
                "ScreenSpaceHeightFogFeature could not find shader " +
                "'Hidden/IFC/ScreenSpaceHeightFog'. Assign it manually " +
                "in the Renderer Feature inspector."
            );

            return;
        }

        fogMaterial =
            CoreUtils.CreateEngineMaterial(selectedShader);

        fogPass = new FogPass(
            fogMaterial,
            settings.injectionPoint
        );
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        if (!ShouldRender(renderingData.cameraData))
            return;

        if (fogMaterial == null || fogPass == null)
            return;

        UpdateMaterialProperties();

        fogPass.renderPassEvent = settings.injectionPoint;

        fogPass.ConfigureInput(
            ScriptableRenderPassInput.Depth
        );

        renderer.EnqueuePass(fogPass);
    }

    private bool ShouldRender(CameraData cameraData)
    {
        if (!settings.effectEnabled)
            return false;

        Camera camera = cameraData.camera;

        if (camera == null)
            return false;

        CameraType cameraType = camera.cameraType;

        if (cameraType == CameraType.Preview ||
            cameraType == CameraType.Reflection)
        {
            return false;
        }

        if (cameraType == CameraType.SceneView &&
            !settings.showInSceneView)
        {
            return false;
        }

        if (cameraData.renderType == CameraRenderType.Overlay)
            return false;

        return true;
    }

    private void UpdateMaterialProperties()
    {
        if (fogMaterial == null)
            return;

        float safeStartDistance =
            Mathf.Max(settings.startDistance, 0.0f);

        float safeMaximumDistance = Mathf.Max(
            settings.maxDistance,
            safeStartDistance + 0.01f
        );

        Vector2 direction = settings.noiseDirection;

        if (direction.sqrMagnitude > 0.000001f)
            direction.Normalize();
        else
            direction = Vector2.zero;

        Vector2 noiseVelocity =
            direction * Mathf.Max(settings.noiseSpeed, 0.0f);

        fogMaterial.SetColor(
            FogColorId,
            settings.fogColor
        );

        fogMaterial.SetFloat(
            FogDensityId,
            Mathf.Max(settings.fogDensity, 0.0f)
        );

        fogMaterial.SetFloat(
            FogStartDistanceId,
            safeStartDistance
        );

        fogMaterial.SetFloat(
            FogMaxDistanceId,
            safeMaximumDistance
        );

        fogMaterial.SetFloat(
            MaximumOpacityId,
            Mathf.Clamp01(settings.maximumOpacity)
        );

        fogMaterial.SetFloat(
            FogBaseHeightId,
            settings.baseHeight
        );

        fogMaterial.SetFloat(
            HeightFalloffId,
            Mathf.Max(settings.heightFalloff, 0.0001f)
        );

        fogMaterial.SetFloat(
            NoiseScaleId,
            Mathf.Max(settings.noiseScale, 0.000001f)
        );

        fogMaterial.SetFloat(
            NoiseStrengthId,
            Mathf.Clamp01(settings.noiseStrength)
        );

        fogMaterial.SetVector(
            NoiseVelocityId,
            new Vector4(
                noiseVelocity.x,
                noiseVelocity.y,
                0.0f,
                0.0f
            )
        );

        fogMaterial.SetVector(
            VerticalShearId,
            new Vector4(
                settings.verticalShear.x,
                settings.verticalShear.y,
                0.0f,
                0.0f
            )
        );

        fogMaterial.SetFloat(
            FBMOctavesId,
            Mathf.Clamp(settings.fbmOctaves, 1, 5)
        );

        fogMaterial.SetFloat(
            FBMLacunarityId,
            Mathf.Clamp(settings.fbmLacunarity, 1.1f, 3.5f)
        );

        fogMaterial.SetFloat(
            FBMGainId,
            Mathf.Clamp(settings.fbmGain, 0.1f, 0.9f)
        );

        fogMaterial.SetFloat(
            NoiseContrastId,
            Mathf.Clamp(settings.noiseContrast, 0.25f, 4.0f)
        );

        fogMaterial.SetFloat(
            EvolutionStrengthId,
            Mathf.Clamp(settings.evolutionStrength, 0.0f, 1.5f)
        );

        fogMaterial.SetFloat(
            EvolutionSpeedId,
            Mathf.Clamp01(settings.evolutionSpeed)
        );

        fogMaterial.SetFloat(
            FlowVariationId,
            Mathf.Clamp(settings.flowVariation, 0.0f, 2.0f)
        );

        fogMaterial.SetFloat(
            SmokeCoverageId,
            Mathf.Clamp01(settings.smokeCoverage)
        );

        fogMaterial.SetFloat(
            SmokeSoftnessId,
            Mathf.Clamp(settings.smokeSoftness, 0.01f, 0.35f)
        );

        fogMaterial.SetFloat(
            SmokeFloorId,
            Mathf.Clamp01(settings.smokeFloor)
        );

        fogMaterial.SetFloat(
            SmokePeakDensityId,
            Mathf.Clamp(settings.smokePeakDensity, 0.5f, 3.0f)
        );

        fogMaterial.SetFloat(
            WarpScaleId,
            Mathf.Clamp(settings.warpScale, 0.05f, 2.0f)
        );

        fogMaterial.SetFloat(
            WarpStrengthId,
            Mathf.Clamp(settings.warpStrength, 0.0f, 3.0f)
        );

        fogMaterial.SetFloat(
            WarpSpeedId,
            Mathf.Clamp(settings.warpSpeed, 0.0f, 0.2f)
        );

        fogMaterial.SetFloat(
            BillowStrengthId,
            Mathf.Clamp01(settings.billowStrength)
        );

        fogMaterial.SetFloat(
            HorizonStrengthId,
            Mathf.Clamp01(settings.horizonStrength)
        );

        fogMaterial.SetFloat(
            HorizonFalloffId,
            Mathf.Max(settings.horizonFalloff, 0.1f)
        );

        fogMaterial.SetFloat(
            DebugModeId,
            (float)settings.debugView
        );
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(fogMaterial);

        fogMaterial = null;
        fogPass = null;
    }

    private sealed class FogPass : ScriptableRenderPass
    {
        private const string PassName =
            "Screen Space Height Fog";

        private readonly Material material;

        public FogPass(
            Material material,
            RenderPassEvent injectionPoint)
        {
            this.material = material;
            renderPassEvent = injectionPoint;
            requiresIntermediateTexture = true;
        }

        public override void RecordRenderGraph(
            RenderGraph renderGraph,
            ContextContainer frameData)
        {
            if (material == null)
                return;

            UniversalResourceData resourceData =
                frameData.Get<UniversalResourceData>();

            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source =
                resourceData.activeColorTexture;

            if (!source.IsValid())
                return;

            TextureDesc destinationDescription =
                source.GetDescriptor(renderGraph);

            destinationDescription.name =
                "_ScreenSpaceHeightFogColor";

            destinationDescription.clearBuffer = false;
            destinationDescription.depthBufferBits = 0;

            TextureHandle destination =
                renderGraph.CreateTexture(
                    destinationDescription
                );

            if (!destination.IsValid())
                return;

            var blitParameters =
                new RenderGraphUtils.BlitMaterialParameters(
                    source,
                    destination,
                    material,
                    0
                );

            using (
                var builder = renderGraph.AddBlitPass(
                    blitParameters,
                    PassName,
                    returnBuilder: true
                )
            )
            {
                TextureHandle depthTexture =
                    resourceData.cameraDepthTexture;

                if (depthTexture.IsValid())
                {
                    builder.UseTexture(
                        depthTexture,
                        AccessFlags.Read
                    );
                }
            }

            resourceData.cameraColor = destination;
        }
    }
}
