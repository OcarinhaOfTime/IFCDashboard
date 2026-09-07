using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class ScreenSpaceGodRaysController : MonoBehaviour
{
    [Header("References")]

    [Tooltip("The camera that receives the god-ray effect.")]
    [SerializeField]
    private Camera targetCamera;

    [Tooltip("The directional light representing the sun. " +
             "When empty, RenderSettings.sun is used.")]
    [SerializeField]
    private Light sun;

    [Tooltip("Material used by the URP Full Screen Pass Renderer Feature.")]
    [SerializeField]
    private Material godRaysMaterial;

    [Header("Appearance")]

    [ColorUsage(showAlpha: false, hdr: true)]
    [SerializeField]
    private Color rayColor = new Color(1.0f, 0.82f, 0.55f, 1.0f);

    [SerializeField, Range(0.0f, 4.0f)]
    private float intensity = 1.0f;

    [SerializeField, Range(0.8f, 0.999f)]
    private float decay = 0.96f;

    [SerializeField, Range(0.1f, 1.5f)]
    private float density = 0.9f;

    [SerializeField, Range(0.001f, 0.2f)]
    private float sampleWeight = 0.055f;

    [SerializeField, Range(0.01f, 0.6f)]
    private float sunRadius = 0.22f;

    [Header("Visibility")]

    [Tooltip("The rays begin fading when the sun approaches the screen edge.")]
    [SerializeField, Range(0.0f, 0.99f)]
    private float edgeFadeStart = 0.72f;

    private static readonly int SunScreenPositionId =
        Shader.PropertyToID("_SunScreenPosition");

    private static readonly int CameraAspectId =
        Shader.PropertyToID("_CameraAspect");

    private static readonly int RayColorId =
        Shader.PropertyToID("_RayColor");

    private static readonly int IntensityId =
        Shader.PropertyToID("_Intensity");

    private static readonly int DecayId =
        Shader.PropertyToID("_Decay");

    private static readonly int DensityId =
        Shader.PropertyToID("_Density");

    private static readonly int WeightId =
        Shader.PropertyToID("_Weight");

    private static readonly int SunRadiusId =
        Shader.PropertyToID("_SunRadius");

    private void Reset()
    {
        targetCamera = GetComponent<Camera>();

        if (targetCamera == null)
            targetCamera = Camera.main;

        sun = RenderSettings.sun;
    }

    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        DisableRays();
    }

    private void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
    }

    private void OnValidate()
    {
        if (targetCamera == null)
            targetCamera = GetComponent<Camera>();
    }

    private void OnBeginCameraRendering(
    ScriptableRenderContext context,
    Camera renderingCamera)
    {
        if (godRaysMaterial == null)
            return;

        // Do not modify the material for Scene View, preview,
        // reflection, or other cameras.
        if (renderingCamera != targetCamera)
            return;

        UpdateMaterial(renderingCamera);
    }

    private void UpdateMaterial(Camera camera)
    {
        Light activeSun = sun != null ? sun : RenderSettings.sun;

        if (activeSun == null ||
            activeSun.type != LightType.Directional ||
            !activeSun.isActiveAndEnabled ||
            activeSun.intensity <= 0.0001f)
        {
            DisableRays();
            return;
        }

        /*
         * A directional light points in the direction its rays travel.
         * The apparent sun is therefore in the opposite direction.
         */
        Vector3 directionTowardSun = -activeSun.transform.forward;

        float cameraFacing =
            Vector3.Dot(camera.transform.forward, directionTowardSun);

        if (cameraFacing <= 0.0f)
        {
            DisableRays();
            return;
        }

        float projectionDistance =
            Mathf.Max(camera.farClipPlane * 0.9f, 1000.0f);

        Vector3 virtualSunPosition =
            camera.transform.position +
            directionTowardSun * projectionDistance;

        Vector3 viewportPosition =
            camera.WorldToViewportPoint(virtualSunPosition);

        if (viewportPosition.z <= 0.0f)
        {
            DisableRays();
            return;
        }

        /*
         * Convert the viewport coordinates to a centered [-1, 1] range.
         * maxAxisDistance is:
         *
         * 0 at screen center
         * 1 at a screen edge
         * >1 outside the screen
         */
        float centeredX = Mathf.Abs(viewportPosition.x * 2.0f - 1.0f);
        float centeredY = Mathf.Abs(viewportPosition.y * 2.0f - 1.0f);

        float maxAxisDistance = Mathf.Max(centeredX, centeredY);

        float edgeFade = 1.0f - Mathf.SmoothStep(
            edgeFadeStart,
            1.0f,
            maxAxisDistance
        );

        float visibility = edgeFade * Mathf.Clamp01(cameraFacing * 4.0f);

        godRaysMaterial.SetVector(
            SunScreenPositionId,
            new Vector4(
                viewportPosition.x,
                viewportPosition.y,
                0.0f,
                visibility
            )
        );

        godRaysMaterial.SetFloat(CameraAspectId, camera.aspect);
        godRaysMaterial.SetColor(RayColorId, rayColor);
        godRaysMaterial.SetFloat(IntensityId, intensity);
        godRaysMaterial.SetFloat(DecayId, decay);
        godRaysMaterial.SetFloat(DensityId, density);
        godRaysMaterial.SetFloat(WeightId, sampleWeight);
        godRaysMaterial.SetFloat(SunRadiusId, sunRadius);
    }

    private void DisableRays()
    {
        if (godRaysMaterial == null)
            return;

        godRaysMaterial.SetVector(
            SunScreenPositionId,
            Vector4.zero
        );
    }

    public void SetIntensity(float value)
    {
        intensity = Mathf.Max(0.0f, value);
    }
}