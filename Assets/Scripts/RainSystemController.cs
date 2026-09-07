using UnityEngine;

namespace IFCDashboard.Weather
{
    /// <summary>
    /// Controls a camera-following, world-space rain particle system.
    ///
    /// The emitter follows the point at the center of the camera view projected
    /// onto a horizontal plane. This is better suited to an orbiting/isometric
    /// dashboard camera than following the camera's X/Z position directly.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RainSystemController : MonoBehaviour
    {
        public enum FollowMode
        {
            ViewCenterOnHorizontalPlane,
            CameraXZ
        }

        [Header("References")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private ParticleSystem rainParticles;

        [Header("Rain")]
        [SerializeField, Range(0f, 1f)] private float intensity = 0.7f;
        [SerializeField, Min(0f)] private float maximumEmissionRate = 1200f;
        [SerializeField, Min(1)] private int maximumParticles = 2200;
        [SerializeField, Min(0.1f)] private float fallSpeed = 34f;
        [SerializeField, Range(0f, 0.5f)] private float fallSpeedVariation = 0.12f;
        [SerializeField] private Vector2 wind = new Vector2(2.5f, 0.5f);
        [SerializeField, Range(0f, 1f)] private float windVariation = 0.25f;

        [Header("Coverage")]
        [SerializeField] private FollowMode followMode = FollowMode.ViewCenterOnHorizontalPlane;
        [SerializeField] private bool followCamera = true;
        [SerializeField] private Vector2 coverage = new Vector2(70f, 70f);
        [SerializeField, Min(0.1f)] private float emissionThickness = 4f;
        [SerializeField, Min(0.1f)] private float emitterHeight = 35f;
        [SerializeField] private float referencePlaneHeight;
        [SerializeField, Min(0f)] private float followSharpness = 18f;

        [Header("Rendering")]
        [SerializeField, Min(0f)] private float stretchLength = 3.5f;
        [SerializeField, Min(0f)] private float velocityStretch = 0.06f;

        [Header("Playback")]
        [SerializeField] private bool playOnEnable = true;

        private Camera resolvedCamera;
        private bool firstFollow = true;
        private bool settingsDirty = true;

        public float Intensity => intensity;
        public Vector2 Wind => wind;

        private void Awake()
        {
            ResolveReferences();

            if (followCamera)
            {
                UpdateEmitterPosition();
            }

            ApplySettings();
        }

        private void OnEnable()
        {
            ResolveReferences();
            settingsDirty = true;

            if (followCamera)
            {
                UpdateEmitterPosition();
            }

            if (!Application.isPlaying || !playOnEnable || rainParticles == null)
            {
                return;
            }

            ApplySettings();

            if (intensity > 0f && !rainParticles.isPlaying)
            {
                rainParticles.Play(true);
            }
        }

        private void OnValidate()
        {
            intensity = Mathf.Clamp01(intensity);
            maximumEmissionRate = Mathf.Max(0f, maximumEmissionRate);
            maximumParticles = Mathf.Max(1, maximumParticles);
            fallSpeed = Mathf.Max(0.1f, fallSpeed);
            coverage.x = Mathf.Max(0.1f, coverage.x);
            coverage.y = Mathf.Max(0.1f, coverage.y);
            emissionThickness = Mathf.Max(0.1f, emissionThickness);
            emitterHeight = Mathf.Max(0.1f, emitterHeight);
            stretchLength = Mathf.Max(0f, stretchLength);
            velocityStretch = Mathf.Max(0f, velocityStretch);

            settingsDirty = true;

            if (!Application.isPlaying)
            {
                ResolveReferences();
                ApplySettings();
            }
        }

        private void LateUpdate()
        {
            ResolveReferences();

            if (settingsDirty)
            {
                ApplySettings();
            }

            if (followCamera)
            {
                UpdateEmitterPosition();
            }
        }

        /// <summary>
        /// Called by the Editor generator. It is also safe to call from another setup tool.
        /// </summary>
        public void ConfigureGeneratedSystem(
            ParticleSystem particles,
            Camera camera,
            float initialIntensity,
            float maxEmissionRate,
            int maxParticleCount,
            Vector2 areaCoverage,
            float boxThickness,
            float heightAboveReference,
            float horizontalReferenceHeight,
            float verticalFallSpeed,
            Vector2 horizontalWind,
            float rendererStretchLength,
            float rendererVelocityStretch)
        {
            rainParticles = particles;
            targetCamera = camera;
            resolvedCamera = camera;

            intensity = Mathf.Clamp01(initialIntensity);
            maximumEmissionRate = Mathf.Max(0f, maxEmissionRate);
            maximumParticles = Mathf.Max(1, maxParticleCount);
            coverage = new Vector2(
                Mathf.Max(0.1f, areaCoverage.x),
                Mathf.Max(0.1f, areaCoverage.y));
            emissionThickness = Mathf.Max(0.1f, boxThickness);
            emitterHeight = Mathf.Max(0.1f, heightAboveReference);
            referencePlaneHeight = horizontalReferenceHeight;
            fallSpeed = Mathf.Max(0.1f, verticalFallSpeed);
            wind = horizontalWind;
            stretchLength = Mathf.Max(0f, rendererStretchLength);
            velocityStretch = Mathf.Max(0f, rendererVelocityStretch);

            firstFollow = true;
            settingsDirty = true;
            ResolveReferences();
            ApplySettings();
        }

        public void SetTargetCamera(Camera camera)
        {
            targetCamera = camera;
            resolvedCamera = camera;
            firstFollow = true;

            if (followCamera)
            {
                UpdateEmitterPosition();
            }
        }

        public void SetIntensity(float value)
        {
            intensity = Mathf.Clamp01(value);
            settingsDirty = true;

            if (rainParticles == null || !Application.isPlaying)
            {
                return;
            }

            if (intensity > 0f && !rainParticles.isPlaying)
            {
                rainParticles.Play(true);
            }
        }

        public void SetWind(Vector2 value)
        {
            wind = value;
            settingsDirty = true;
        }

        public void SetReferencePlaneHeight(float value)
        {
            referencePlaneHeight = value;
            firstFollow = true;
        }

        public void StartRain(float targetIntensity = 1f)
        {
            SetIntensity(targetIntensity);

            if (rainParticles != null && !rainParticles.isPlaying)
            {
                rainParticles.Play(true);
            }
        }

        public void StopRain(bool clearExistingParticles = false)
        {
            intensity = 0f;
            settingsDirty = true;
            ApplySettings();

            if (rainParticles == null)
            {
                return;
            }

            rainParticles.Stop(
                true,
                clearExistingParticles
                    ? ParticleSystemStopBehavior.StopEmittingAndClear
                    : ParticleSystemStopBehavior.StopEmitting);
        }

        [ContextMenu("Apply Rain Settings")]
        public void ApplySettings()
        {
            settingsDirty = false;

            if (rainParticles == null)
            {
                return;
            }

            ParticleSystem.MainModule main = rainParticles.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            main.maxParticles = maximumParticles;

            // Keep particles alive long enough to fall slightly below the
            // reference plane, preventing visible disappearance above roofs.
            float nominalLifetime = Mathf.Max(
                0.25f,
                (emitterHeight + 10f) / Mathf.Max(0.1f, fallSpeed));

            main.startLifetime = new ParticleSystem.MinMaxCurve(
                nominalLifetime * 0.85f,
                nominalLifetime * 1.15f);

            ParticleSystem.EmissionModule emission = rainParticles.emission;
            emission.enabled = true;
            emission.rateOverTime = maximumEmissionRate * intensity;

            ParticleSystem.ShapeModule shape = rainParticles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(
                Mathf.Max(0.1f, coverage.x),
                Mathf.Max(0.1f, emissionThickness),
                Mathf.Max(0.1f, coverage.y));

            ParticleSystem.VelocityOverLifetimeModule velocity =
                rainParticles.velocityOverLifetime;

            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = RandomizedConstant(wind.x, windVariation);
            velocity.y = RandomizedConstant(-fallSpeed, fallSpeedVariation);
            velocity.z = RandomizedConstant(wind.y, windVariation);

            ParticleSystemRenderer particleRenderer =
                rainParticles.GetComponent<ParticleSystemRenderer>();

            if (particleRenderer != null)
            {
                particleRenderer.lengthScale = stretchLength;
                particleRenderer.velocityScale = velocityStretch;
                particleRenderer.cameraVelocityScale = 0f;
            }
        }

        private void ResolveReferences()
        {
            if (rainParticles == null)
            {
                rainParticles = GetComponentInChildren<ParticleSystem>(true);
            }

            if (targetCamera != null)
            {
                resolvedCamera = targetCamera;
                return;
            }

            if (resolvedCamera == null || !resolvedCamera.isActiveAndEnabled)
            {
                resolvedCamera = Camera.main;
            }
        }

        private void UpdateEmitterPosition()
        {
            if (resolvedCamera == null)
            {
                return;
            }

            Vector3 anchor = GetFollowAnchor(resolvedCamera);
            Vector3 desiredPosition = new Vector3(
                anchor.x,
                referencePlaneHeight + emitterHeight,
                anchor.z);

            Quaternion desiredRotation = Quaternion.identity;

            if (!Application.isPlaying || firstFollow || followSharpness <= 0f)
            {
                transform.SetPositionAndRotation(desiredPosition, desiredRotation);
                firstFollow = false;
                return;
            }

            float interpolation =
                1f - Mathf.Exp(-followSharpness * Time.unscaledDeltaTime);

            transform.position = Vector3.Lerp(
                transform.position,
                desiredPosition,
                interpolation);

            transform.rotation = desiredRotation;
        }
        private Vector3 GetFollowAnchor(Camera cameraToFollow)
        {
            if (followMode == FollowMode.CameraXZ)
            {
                Vector3 cameraPosition = cameraToFollow.transform.position;
                return new Vector3(
                    cameraPosition.x,
                    referencePlaneHeight,
                    cameraPosition.z);
            }

            Ray viewRay = cameraToFollow.ViewportPointToRay(
                new Vector3(0.5f, 0.5f, 0f));

            Plane referencePlane = new Plane(
                Vector3.up,
                new Vector3(0f, referencePlaneHeight, 0f));

            if (referencePlane.Raycast(viewRay, out float enter))
            {
                return viewRay.GetPoint(enter);
            }

            Vector3 fallbackPosition = cameraToFollow.transform.position;
            return new Vector3(
                fallbackPosition.x,
                referencePlaneHeight,
                fallbackPosition.z);
        }

        private static ParticleSystem.MinMaxCurve RandomizedConstant(
            float value,
            float variation)
        {
            float clampedVariation = Mathf.Clamp01(variation);
            float first = value * (1f - clampedVariation);
            float second = value * (1f + clampedVariation);

            return new ParticleSystem.MinMaxCurve(
                Mathf.Min(first, second),
                Mathf.Max(first, second));
        }
    }
}
