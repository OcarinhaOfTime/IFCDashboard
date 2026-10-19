using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Scene-level time / weather coordinator for URP (Unity 6).
/// Attach to one scene GameObject. Effect material properties are configurable
/// because shader property reference names differ from Inspector labels.
/// Runs in Play Mode only (does not change material assets during edit-time).
/// </summary>
[DefaultExecutionOrder(500)]
[DisallowMultipleComponent]
public sealed class EnvironmentCycleController : MonoBehaviour
{
    [Header("INPUTS — 0..24h / normalized weather")]
    [Range(0f, 24f)] public float timeOfDay = 12f;
    [Range(0f, 1f)] public float cloudiness = 0.4f;
    [Range(0f, 1f)] public float rainIntensity;
    [Range(0f, 1f)] public float fogginess;

    [Header("REFERENCES — Lighting / sky")]
    public Light sun;
    [Tooltip("Optional secondary directional light. Its intensity fades from day to night; rotation and color are untouched.")]
    public Light fillLight;
    [Tooltip("Single existing Skybox/IFC Procedural Atmosphere material. A runtime clone is used; colors are blended directly.")]
    public Material skyboxMaterial;
    [Tooltip("Optional only: if a future skybox shader exposes a day/night blend, this parameter can drive it. Not needed for the existing shader.")]
    public bool driveSkyDaylightProperty = false;
    public string skyDaylightProperty = "_Daylight";

    [Header("SETTINGS — Procedural stars")]
    [Tooltip("The supplied v10 skybox shader exposes _StarsVisibility. Stars fade in as the sky becomes dark.")]
    public bool controlStars = true;
    public string starsVisibilityProperty = "_StarsVisibility";
    [Range(0f, 1f)] public float starsBeginBelowDaylight = 0.22f;
    [Range(0f, 1f)] public float starsFullyVisibleBelowDaylight = 0.015f;

    [Header("SETTINGS — Sky color property names")]
    [Tooltip("Reference names inferred from Inspector labels; verify in shader Properties block.")]
    public string zenithColorProperty = "_ZenithColor";
    public string skyHorizonColorProperty = "_HorizonColor";
    public string groundHorizonColorProperty = "_GroundHorizonColor";
    public string groundBottomColorProperty = "_GroundBottomColor";
    public string horizonHazeColorProperty = "_HorizonHazeColor";
    [Tooltip("Skybox shader reference for Horizon Haze Strength. The controller reduces this in daylight while preserving the source material's original value at night.")]
    public string horizonHazeStrengthProperty = "_HorizonHazeStrength";
    public string cloudLightColorProperty = "_CloudLightColor";
    public string cloudShadowColorProperty = "_CloudShadowColor";

    [Header("SETTINGS — Sky presets (HDR colors)")]
    [Tooltip("These presets blend continuously on the same skybox material; adjust to match your own daytime and nighttime material appearances.")]
    [ColorUsage(true, true)] public Color dayZenith = new Color(0.17f, 0.39f, 0.79f);
    [ColorUsage(true, true)] public Color nightZenith = new Color(0.004f, 0.009f, 0.024f);
    [ColorUsage(true, true)] public Color daySkyHorizon = new Color(0.29f, 0.57f, 0.88f);
    [ColorUsage(true, true)] public Color nightSkyHorizon = new Color(0.012f, 0.026f, 0.062f);
    [ColorUsage(true, true)] public Color dayGroundHorizon = new Color(0.27f, 0.40f, 0.59f);
    [ColorUsage(true, true)] public Color nightGroundHorizon = new Color(0.01f, 0.018f, 0.035f);
    [ColorUsage(true, true)] public Color dayGroundBottom = new Color(0.22f, 0.24f, 0.26f);
    [ColorUsage(true, true)] public Color nightGroundBottom = new Color(0.003f, 0.005f, 0.012f);
    [ColorUsage(true, true)] public Color dayHaze = new Color(0.28f, 0.51f, 0.79f);
    [ColorUsage(true, true)] public Color nightHaze = new Color(0.014f, 0.03f, 0.064f);
    [ColorUsage(true, true)] public Color dayCloudLight = new Color(0.88f, 0.91f, 0.97f);
    [ColorUsage(true, true)] public Color nightCloudLight = new Color(0.23f, 0.37f, 0.61f);
    [ColorUsage(true, true)] public Color dayCloudShadow = new Color(0.39f, 0.49f, 0.62f);
    [ColorUsage(true, true)] public Color nightCloudShadow = new Color(0.055f, 0.1f, 0.19f);
    [Tooltip("Warm band around sunrise/sunset, added predominantly to the horizon/haze.")]
    [ColorUsage(true, true)] public Color twilightTint = new Color(0.85f, 0.34f, 0.13f);
    [Range(0f, 1f)] public float twilightHorizonStrength = 0.22f;

    [Header("SETTINGS — Daytime sky clarity")]
    [Tooltip("Horizon haze strength at full daylight. Low values make the daytime sky more saturated blue; nighttime uses the original skybox material value.")]
    [Range(0f, 1f)] public float dayHazeStrength = 0.12f;

    [Header("REFERENCES — Rain")]
    [Tooltip("Assign the existing RainSystemController component here (without needing its type at compile time). Its serialized 'intensity' field or property will be updated via reflection.")]
    public MonoBehaviour rainSystemController;
    [Tooltip("Fallback only: direct ParticleSystem control if Rain System Controller is not assigned.")]
    public ParticleSystem rainParticles;
    public Material rainOnLensMaterial;

    [Header("REFERENCES — URP Renderer Features")]
    [Tooltip("Assign the PC_Renderer Universal Renderer Data asset, not the URP Pipeline Asset.")]
    public ScriptableRendererData rendererData;
    [Tooltip("Exact renderer feature name (as displayed in renderer Inspector).")]
    public string fogFeatureName = "ScreenSpaceHeightFogFeature";
    [Tooltip("Optional direct reference. If left blank the controller searches rendererData by feature name/type.")]
    public ScriptableRendererFeature fogFeatureReference;
    public string rainLensFeatureName = "Rain On Lens";
    [Tooltip("Optional: toggle these renderer features on/off based on weather. These assets may be shared by other cameras.")]
    public bool toggleRendererFeatures = true;

    [Header("REFERENCES — Building windows")]
    [Tooltip("The shared materials used for emissive building windows. Do not assign individual material instances.")]
    public Material[] buildingMaterials;

    [Header("SETTINGS — Day / night")]
    [Range(0f, 12f)] public float sunriseHour = 6f;
    [Range(12f, 24f)] public float sunsetHour = 18f;
    [Range(0f, 4f)] public float twilightHours = 1f;
    public float daySunIntensity = 2f;
    public float nightSunIntensity = 0f;
    [Header("SETTINGS — Fill light")]
    [Min(0f)] public float dayFillIntensity = 0.8f;
    [Min(0f)] public float nightFillIntensity = 0.4f;
    [Range(0f, 1f)] public float overcastFillDimming = 0f;
    [Range(-90f, 90f)] public float highestSunElevation = 65f;
    [Range(-90f, 90f)] public float lowestSunElevation = -20f;
    public enum SunMotionMode { FixedAzimuth, Orbital }

    [Header("SETTINGS — Sun motion / artistic orientation")]
    [Tooltip("Fixed Azimuth keeps sunlight facing the preferred direction. Orbital sweeps from east to west during daylight. Both operate in the Sun LOCAL space, preserving the parent lighting rotation.")]
    public SunMotionMode sunMotionMode = SunMotionMode.FixedAzimuth;
    [Tooltip("LOCAL azimuth offset (degrees) relative to the lighting parent. The parent Y rotation remains an independent artistic adjustment.")]
    [Range(-180f, 180f)] public float sunAzimuth = -30f;
    [Tooltip("Total east-to-west azimuth sweep between sunrise and sunset in Orbital mode. 180 gives a horizon-to-horizon sweep.")]
    [Range(0f, 360f)] public float orbitalAzimuthSweep = 180f;
    [Range(0f, 1f)] public float overcastSunDimming = 0.5f;
    [Tooltip("Use a color/intensity appropriate for an HDR Shader Graph Color property.")]
    [ColorUsage(true, true)] public Color windowsNightEmission = new Color(0.5f, 0.9f, 0.85f, 1f) * 2f;
    [ColorUsage(true, true)] public Color windowsDayEmission = Color.black;

    [Header("SETTINGS — Cloud shader")]
    public string cloudCoverageProperty = "_CloudCoverage";
    public string cloudOpacityProperty = "_CloudOpacity";
    [Range(0f, 1f)] public float clearSkyCoverage = 0.05f;
    [Range(0f, 1f)] public float overcastCoverage = 0.95f;
    [Range(0f, 1f)] public float clearSkyOpacity = 0.52f;
    [Range(0f, 1f)] public float overcastOpacity = 0.98f;

    [Header("SETTINGS — Rain / lens")]
    [Tooltip("Existing rain controller's field/property name; screenshot indicates 'Intensity'.")]
    public string rainControllerIntensityMember = "intensity";
    [Range(0f, 1f)] public float particlesBeginAt = 0.02f;
    [Range(0f, 1f)] public float lensBeginAt = 0.1f;
    [Range(0f, 1f)] public float lensFullyVisibleAt = 0.8f;
    public string lensRainAmountProperty = "_RainAmount";

    [Header("SETTINGS — Rain-on-lens transitions (real seconds)")]
    [Tooltip("Time rain must continue above Lens Begin At before new water appears on the camera. Uses unscaled real seconds, even during a fast timelapse.")]
    [Min(0f)] public float lensOnsetDelaySeconds = 0.6f;
    [Tooltip("Approximate time to become fully wet after the delay. Also smooths changes in rain intensity.")]
    [Min(0f)] public float lensFadeInSeconds = 1.2f;
    [Tooltip("How long existing droplets remain unchanged after rain stops before they begin to dry.")]
    [Min(0f)] public float lensDryingHoldSeconds = 0.5f;
    [Tooltip("Approximate drying time when rain stops. The effect stays enabled while fading out.")]
    [Min(0f)] public float lensFadeOutSeconds = 2.5f;

    [Tooltip("Only when Rain System Controller is not assigned.")]
    public float fallbackParticlesPerSecond = 2000f;

    [Header("SETTINGS — Fog Renderer Feature")]
    [Range(0f, 1f)] public float fogBeginsAt = 0.01f;
    [Tooltip("Internal 'Effect Enabled' setting, distinct from the renderer feature's top-level active checkbox.")]
    public string fogEffectEnabledMember = "effectEnabled";
    public string fogMaximumOpacityMember = "maximumOpacity";
    [Tooltip("Preserves Fog Density and all other fog composition settings on PC_Renderer; scales only Maximum Opacity.")]
    [Range(0f, 1f)] public float fogMaximumOpacityAtOne = 1f;
    [Tooltip("Only enable if your custom fog feature caches settings in Create(). Usually leave OFF because Create() can recreate resources.")]
    public bool recreateFogFeatureOnChange = false;

    [Header("SETTINGS — Building shader")]
    [Tooltip("Shader property Reference, not display name. Check your Shader Graph Blackboard.")]
    public string windowEmissionProperty = "_WindowEmission";
    [Tooltip("Actual Shader Graph numeric Reference. Try EmissionStrength or _EmissionStrength; the controller checks both.")]
    public string emissionStrengthProperty = "EmissionStrength";
    [Min(0f)] public float dayEmissionStrength = 0f;
    [Min(0f)] public float nightEmissionStrength = 2f;
    [Tooltip("Leave disabled when EmissionStrength alone is responsible for the window effect.")]
    public bool alsoDriveWindowEmissionColor = false;
    [Header("SETTINGS — Window switching schedule")]
    [Tooltip("Lights turn on after sunset plus twilightHours, plus this offset in hours.")]
    [Range(-4f, 4f)] public float windowsEveningOffsetHours = 0f;
    [Tooltip("Lights turn off after sunrise plus twilightHours, plus this offset in hours.")]
    [Range(-4f, 4f)] public float windowsMorningOffsetHours = 0f;
    [Tooltip("Width of each sharp on/off transition in minutes; use 0 for an immediate switch.")]
    [Range(0f, 60f)] public float windowSwitchMinutes = 10f;

    [Header("DEBUG — Inspector Live Preview")]
    [Tooltip("When enabled, changes in the Inspector during Play Mode trigger a deferred ApplyEnvironment call. Disable after debugging. Runtime setter methods continue to work.")]
    public bool debugApplyOnValidate = true;

    [Header("SETTINGS — Update")]
    public bool continuouslyUpdate = true;
    [Range(0.02f, 1f)] public float updateInterval = 0.1f;


    /// <summary>
    /// Existing Unity components retain their serialized Inspector values after
    /// C# field initializers change. Run this context-menu action once to adopt
    /// the new appearance without losing object/material references or timings.
    /// </summary>
    /// <summary>
    /// Applies daytime-only color/clarity defaults to existing scene components.
    /// Does not reset moonlit colors, fill light, weather, bindings, or solar motion.
    /// </summary>
    [ContextMenu("Apply Preset — Clear Blue Day (v13)")]
    public void ApplyClearBlueDayPreset()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
            UnityEditor.Undo.RecordObject(this, "Apply Clear Blue Day Preset");
#endif
        dayZenith = new Color(0.17f, 0.39f, 0.79f);
        daySkyHorizon = new Color(0.29f, 0.57f, 0.88f);
        dayGroundHorizon = new Color(0.27f, 0.40f, 0.59f);
        dayHaze = new Color(0.28f, 0.51f, 0.79f);
        dayHazeStrength = 0.12f;
        horizonHazeStrengthProperty = "_HorizonHazeStrength";
        skyHorizonColorProperty = "_HorizonColor";

        if (Application.isPlaying) ApplyEnvironment();
#if UNITY_EDITOR
        else UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    [ContextMenu("Apply Preset — Moonlit Blue Clouds (v12)")]
    public void ApplyMoonlitCloudPreset()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
            UnityEditor.Undo.RecordObject(this, "Apply Moonlit Cloud Preset");
#endif
        nightZenith = new Color(0.004f, 0.009f, 0.024f);
        nightSkyHorizon = new Color(0.012f, 0.026f, 0.062f);
        nightGroundHorizon = new Color(0.01f, 0.018f, 0.035f);
        nightGroundBottom = new Color(0.003f, 0.005f, 0.012f);
        nightHaze = new Color(0.014f, 0.03f, 0.064f);
        nightCloudLight = new Color(0.23f, 0.37f, 0.61f);
        nightCloudShadow = new Color(0.055f, 0.1f, 0.19f);
        dayZenith = new Color(0.33f, 0.55f, 0.82f);
        daySkyHorizon = new Color(0.69f, 0.79f, 0.91f);
        dayCloudLight = new Color(0.88f, 0.91f, 0.97f);
        dayCloudShadow = new Color(0.39f, 0.49f, 0.62f);
        dayFillIntensity = 0.8f;
        nightFillIntensity = 0.4f;
        overcastFillDimming = 0f;
        clearSkyOpacity = 0.52f;
        overcastOpacity = 0.98f;
        twilightHorizonStrength = 0.22f;
        // The actual procedural atmosphere shader uses _HorizonColor.
        skyHorizonColorProperty = "_HorizonColor";
        if (Application.isPlaying) ApplyEnvironment();
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }

    private float nextUpdateTime;
    private readonly Dictionary<Material, Color> originalWindowEmissions = new Dictionary<Material, Color>();
    private readonly Dictionary<Material, float> originalEmissionStrengths = new Dictionary<Material, float>();
    private readonly Dictionary<Material, string> resolvedEmissionProperties = new Dictionary<Material, string>();
    private bool fogMissingWarned;
    private Material previousSkybox;
    private Material runtimeSkybox;
    private float sourceSkyboxHazeStrength = 0.52f;
    private bool sourceSkyboxHasHazeStrength;
    private bool missingDaylightPropertyWarned;
    private readonly HashSet<string> missingSkyProperties = new HashSet<string>();
    private float previousFillIntensity;
    private bool previousFillEnabled;
    private float previousSunIntensity;
    private Quaternion previousSunLocalRotation;
    private ScriptableRendererFeature fogFeature;
    private ScriptableRendererFeature lensFeature;
    private bool previousFogFeatureEnabled;
    private bool previousLensFeatureEnabled;
    private bool featuresCaptured;
    private bool startupCaptured;
    private FieldInfo rainIntensityField;
    private PropertyInfo rainIntensityProperty;
    private Type cachedRainType;
    private MethodInfo rainApplySettingsMethod;
    private bool missingRainApplySettingsWarned;
    private float lastAppliedRainIntensity = float.NaN;

    // Lens wetting/drying is intentionally independent of ApplyEnvironment's
    // frequency: an event-driven environment still needs animation each frame.
    private float currentLensRainAmount;
    private float requestedLensRainAmount;
    private float lensAmountVelocity;
    private float lensWetWeatherElapsed;
    private float lensDryWeatherElapsed;
    private float lensHeldAmount;
    private bool lensRainIsActive;
    private bool originalLensAmountCaptured;
    private float originalLensAmount;
#if UNITY_EDITOR
    private bool editorApplyQueued;
#endif
    private bool missingRainMemberWarned;
    private bool missingWindowPropertyWarned;
    private bool fogBindingWarned;
    private readonly List<FogMemberSnapshot> fogOriginalValues = new List<FogMemberSnapshot>();
    private bool fogOriginalValuesCaptured;
    private bool fogLastActive;


    private void Awake()
    {
        CacheInitialState();
        ApplyEnvironment();
    }

    private void OnEnable()
    {
        if (Application.isPlaying)
        {
            CacheInitialState();
            ApplyEnvironment();
        }
    }

    // OnValidate executes when Inspector fields change. Unity can call it while
    // serializing, so defer scene/material/renderer changes to the Editor update.
    private void OnValidate()
    {
        timeOfDay = Mathf.Clamp(timeOfDay, 0f, 24f);
        cloudiness = Mathf.Clamp01(cloudiness);
        rainIntensity = Mathf.Clamp01(rainIntensity);
        fogginess = Mathf.Clamp01(fogginess);

#if UNITY_EDITOR
        // OnValidate can run on a serialization thread. Only schedule work here;
        // Unity object changes happen in the delayed callback on the main thread.
        if (!debugApplyOnValidate || !Application.isPlaying || !isActiveAndEnabled)
        {
            if (editorApplyQueued)
            {
                UnityEditor.EditorApplication.delayCall -= ApplyValidatedChanges;
                editorApplyQueued = false;
            }
            return;
        }

        if (editorApplyQueued) return;
        editorApplyQueued = true;
        UnityEditor.EditorApplication.delayCall += ApplyValidatedChanges;
#endif
    }

#if UNITY_EDITOR
    private void ApplyValidatedChanges()
    {
        editorApplyQueued = false;
        if (this == null || !debugApplyOnValidate || !Application.isPlaying || !isActiveAndEnabled) return;
        // No editor-only code is shipped to WebGL builds.
        ApplyEnvironment();
    }
#endif

    private void Update()
    {
        if (!Application.isPlaying) return;

        if (continuouslyUpdate && Time.unscaledTime >= nextUpdateTime)
        {
            nextUpdateTime = Time.unscaledTime + Mathf.Max(0.02f, updateInterval);
            ApplyEnvironment();
        }

        // This must run every frame, including when continuouslyUpdate is OFF.
        // The timelapse also calls ApplyEnvironment through public setters;
        // doing lens animation only here avoids applying deltaTime repeatedly.
        AdvanceLensTransition(Time.unscaledDeltaTime);
    }

    /// <summary>Also callable from UI sliders or weather automation.</summary>
    public void SetTimeOfDay(float value) { timeOfDay = Mathf.Repeat(value, 24f); ApplyEnvironment(); }
    public void SetCloudiness(float value) { cloudiness = Mathf.Clamp01(value); ApplyEnvironment(); }
    public void SetRainIntensity(float value) { rainIntensity = Mathf.Clamp01(value); ApplyEnvironment(); }
    public void SetFogginess(float value) { fogginess = Mathf.Clamp01(value); ApplyEnvironment(); }

    [ContextMenu("Apply Environment Now")]
    public void ApplyEnvironment()
    {
        if (!Application.isPlaying) return;
        // Smooth daylight envelope that wraps at midnight (no sudden discontinuity).
        float hour = Mathf.Repeat(timeOfDay, 24f);
        float day = ComputeDaylight(hour);
        UpdateSun(hour, day);
        UpdateFill(day);
        UpdateSky(day);
        UpdateBuildings(hour);
        UpdateRain();
        UpdateFog();
    }

    public float ComputeDaylight(float hour)
    {
        hour = Mathf.Repeat(hour, 24f);
        // Periodic sin/cos solar phase: at noon = 1; at midnight = -1.
        float midpoint = (sunriseHour + sunsetHour) * 0.5f;
        float halfDay = Mathf.Max(0.1f, (sunsetHour - sunriseHour) * 0.5f);
        // Sunrise/sunset are controlled independently; smoothstep gives a clean transition.
        float ramp = Mathf.Max(0.01f, twilightHours);
        float rise = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(sunriseHour - ramp, sunriseHour + ramp, hour));
        float set = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(sunsetHour - ramp, sunsetHour + ramp, hour));
        float bell = Mathf.Max(0f, Mathf.Cos((hour - midpoint) * Mathf.PI / (2f * halfDay)));
        return Mathf.Clamp01(rise * set * (0.3f + 0.7f * bell));
    }

    private void UpdateSun(float hour, float daylight)
    {
        if (sun == null) return;

        float elevation;
        float azimuth = sunAzimuth;

        if (sunMotionMode == SunMotionMode.Orbital)
        {
            // Daytime: 0° at sunrise, peak elevation at noon, 0° at sunset.
            // Nighttime: below the horizon, reaching the lowest elevation at
            // the midpoint of the night. Both branches are continuous.
            float daylightDuration = Mathf.Max(0.01f, sunsetHour - sunriseHour);
            if (hour >= sunriseHour && hour <= sunsetHour)
            {
                float progress = Mathf.Clamp01((hour - sunriseHour) / daylightDuration);
                elevation = highestSunElevation * Mathf.Sin(Mathf.PI * progress);
                azimuth += (progress - 0.5f) * orbitalAzimuthSweep;
            }
            else
            {
                float nightDuration = Mathf.Max(0.01f, 24f - daylightDuration);
                float hoursAfterSunset = hour > sunsetHour
                    ? hour - sunsetHour
                    : 24f - sunsetHour + hour;
                float progress = Mathf.Clamp01(hoursAfterSunset / nightDuration);
                elevation = Mathf.Min(0f, lowestSunElevation) * Mathf.Sin(Mathf.PI * progress);
                // Continue the azimuth sweep across the night so the motion
                // loops without a jump at midnight or sunrise.
                azimuth += 0.5f * orbitalAzimuthSweep + progress *
                           (360f - orbitalAzimuthSweep);
            }
        }
        else
        {
            // Original elevation cycle, now in LOCAL space. The orientation
            // of the 'lighting' parent is never overwritten by this script.
            float midday = (sunriseHour + sunsetHour) * 0.5f;
            float solarCycle = Mathf.Cos((hour - midday) * (2f * Mathf.PI / 24f));
            elevation = Mathf.Lerp(lowestSunElevation, highestSunElevation,
                0.5f + 0.5f * solarCycle);
        }

        // A Unity directional light emits rays along its +forward direction.
        // Positive X elevation therefore points the light downward.
        sun.transform.localRotation = Quaternion.Euler(elevation, azimuth, 0f);
        sun.intensity = Mathf.Lerp(nightSunIntensity, daySunIntensity, daylight) *
                        (1f - cloudiness * overcastSunDimming);
    }

    private void UpdateFill(float daylight)
    {
        if (fillLight == null) return;
        float dimming = 1f - Mathf.Clamp01(cloudiness) * overcastFillDimming;
        fillLight.intensity = Mathf.Lerp(nightFillIntensity, dayFillIntensity, daylight) * dimming;
    }

    private void UpdateSky(float daylight)
    {
        if (runtimeSkybox == null) return;

        if (driveSkyDaylightProperty)
        {
            if (runtimeSkybox.HasProperty(skyDaylightProperty))
                runtimeSkybox.SetFloat(skyDaylightProperty, daylight);
            else if (!missingDaylightPropertyWarned)
            {
                Debug.LogWarning($"Skybox does not define '{skyDaylightProperty}'. Turn off Drive Sky Daylight Property when using the existing procedural skybox.", this);
                missingDaylightPropertyWarned = true;
            }
        }

        if (controlStars && !string.IsNullOrWhiteSpace(starsVisibilityProperty) &&
            runtimeSkybox.HasProperty(starsVisibilityProperty))
        {
            // Zero in daylight; one after dusk. Smooth inversion of the daylight factor.
            float fadeSpan = Mathf.Max(0.0001f,
                starsBeginBelowDaylight - starsFullyVisibleBelowDaylight);
            float starFactor = Mathf.Clamp01((starsBeginBelowDaylight - daylight) / fadeSpan);
            starFactor = starFactor * starFactor * (3f - 2f * starFactor);
            runtimeSkybox.SetFloat(starsVisibilityProperty, starFactor);
        }

        // The same material stays bound throughout the cycle. No shader/material swap.
        float twilight = TwilightWeight(Mathf.Repeat(timeOfDay, 24f));
        Color warmedHorizon = Color.Lerp(nightSkyHorizon, daySkyHorizon, daylight);
        Color warmedHaze = Color.Lerp(nightHaze, dayHaze, daylight);
        warmedHorizon = Color.Lerp(warmedHorizon, twilightTint, twilight * twilightHorizonStrength);
        warmedHaze = Color.Lerp(warmedHaze, twilightTint, twilight * twilightHorizonStrength * 0.7f);

        SetSkyColor(zenithColorProperty, Color.Lerp(nightZenith, dayZenith, daylight));
        SetSkyColor(skyHorizonColorProperty, warmedHorizon);
        SetSkyColor(groundHorizonColorProperty, Color.Lerp(nightGroundHorizon, dayGroundHorizon, daylight));
        SetSkyColor(groundBottomColorProperty, Color.Lerp(nightGroundBottom, dayGroundBottom, daylight));
        SetSkyColor(horizonHazeColorProperty, warmedHaze);
        // The shader mixes horizon haze into the sky colors. A strong haze setting
        // washes the blue horizon toward gray/white even with Cloudiness = 0.
        // Blend from the material's original night value to a low daytime value.
        if (sourceSkyboxHasHazeStrength)
            runtimeSkybox.SetFloat(horizonHazeStrengthProperty,
                Mathf.Lerp(sourceSkyboxHazeStrength, dayHazeStrength, daylight));
        SetSkyColor(cloudLightColorProperty, Color.Lerp(nightCloudLight, dayCloudLight, daylight));
        SetSkyColor(cloudShadowColorProperty, Color.Lerp(nightCloudShadow, dayCloudShadow, daylight));

        ApplyFloatIfPresent(runtimeSkybox, cloudCoverageProperty,
            Mathf.Lerp(clearSkyCoverage, overcastCoverage, Mathf.Clamp01(cloudiness)));
        ApplyFloatIfPresent(runtimeSkybox, cloudOpacityProperty,
            Mathf.Lerp(clearSkyOpacity, overcastOpacity, Mathf.Clamp01(cloudiness)));
    }

    private void SetSkyColor(string property, Color value)
    {
        if (string.IsNullOrWhiteSpace(property)) return;
        if (runtimeSkybox.HasProperty(property)) runtimeSkybox.SetColor(property, value);
        else if (missingSkyProperties.Add(property))
            Debug.LogWarning($"Skybox shader '{runtimeSkybox.shader.name}' has no property '{property}'. Inspector display names may differ from shader references.", this);
    }

    private float TwilightWeight(float hour)
    {
        float width = Mathf.Max(0.05f, twilightHours * 1.5f);
        float rise = HourDistance(hour, sunriseHour);
        float set = HourDistance(hour, sunsetHour);
        float distance = Mathf.Min(rise, set);
        float t = 1f - Mathf.Clamp01(distance / width);
        return t * t * (3f - 2f * t);
    }

    private static float HourDistance(float a, float b)
    {
        float d = Mathf.Abs(Mathf.Repeat(a - b + 12f, 24f) - 12f);
        return d;
    }

    private string ResolveEmissionStrength(Material material)
    {
        if (material == null) return null;
        if (resolvedEmissionProperties.TryGetValue(material, out string cached)) return cached;
        string prop = null;
        if (!string.IsNullOrEmpty(emissionStrengthProperty) && material.HasProperty(emissionStrengthProperty))
            prop = emissionStrengthProperty;
        else if (material.HasProperty("EmissionStrength")) prop = "EmissionStrength";
        else if (material.HasProperty("_EmissionStrength")) prop = "_EmissionStrength";
        resolvedEmissionProperties[material] = prop;
        return prop;
    }

    // Independent of sky/sun brightness: building lights behave like a timed switch.
    // Twilight is considered finished at sunsetHour + twilightHours (and similarly
    // for the morning). This intentionally keeps emission exactly zero during day.
    private float ComputeWindowOnFactor(float hour)
    {
        const float hoursPerDay = 24f;
        hour = Mathf.Repeat(hour, hoursPerDay);
        float on = Mathf.Repeat(sunsetHour + twilightHours + windowsEveningOffsetHours, hoursPerDay);
        float off = Mathf.Repeat(sunriseHour + twilightHours + windowsMorningOffsetHours, hoursPerDay);
        float nightLength = Mathf.Repeat(off - on, hoursPerDay);
        if (nightLength < 0.001f) return 0f;

        float elapsed = Mathf.Repeat(hour - on, hoursPerDay);
        float halfWidth = Mathf.Min(windowSwitchMinutes / 120f, nightLength * 0.45f);
        if (halfWidth <= 0.00001f) return elapsed < nightLength ? 1f : 0f;

        // Center the turn-on ramp at 'on'; the turn-off ramp at 'off'.
        // Work in an unwrapped interval so transitions also work across midnight.
        float signedFromOn = Mathf.Repeat(hour - on + halfWidth, hoursPerDay) - halfWidth;
        float turnOn = Mathf.SmoothStep(0f, 1f,
            Mathf.Clamp01((signedFromOn + halfWidth) / (2f * halfWidth)));
        float turnOff = 1f - Mathf.SmoothStep(0f, 1f,
            Mathf.Clamp01((signedFromOn - nightLength + halfWidth) / (2f * halfWidth)));
        return Mathf.Clamp01(turnOn * turnOff);
    }

    private void UpdateBuildings(float hour)
    {
        if (buildingMaterials == null) return;
        float glow = ComputeWindowOnFactor(hour);
        float strength = Mathf.Lerp(dayEmissionStrength, nightEmissionStrength, glow);
        foreach (Material material in buildingMaterials)
        {
            if (material == null) continue;
            string emissionProperty = ResolveEmissionStrength(material);
            if (emissionProperty != null)
            {
                if (!originalEmissionStrengths.ContainsKey(material))
                    originalEmissionStrengths[material] = material.GetFloat(emissionProperty);
                material.SetFloat(emissionProperty, strength);
            }
            else if (!missingWindowPropertyWarned)
            {
                Debug.LogWarning($"EnvironmentCycleController: no EmissionStrength float found on '{material.name}'. Check Shader Graph Reference, material assignment, and that this is the material actually used by buildings.", this);
                missingWindowPropertyWarned = true;
            }
            if (alsoDriveWindowEmissionColor && !string.IsNullOrEmpty(windowEmissionProperty) && material.HasProperty(windowEmissionProperty))
            {
                if (!originalWindowEmissions.ContainsKey(material))
                    originalWindowEmissions[material] = material.GetColor(windowEmissionProperty);
                material.SetColor(windowEmissionProperty, Color.Lerp(windowsDayEmission, windowsNightEmission, glow));
            }
        }
    }

    private void UpdateRain()
    {
        float amount = Mathf.Clamp01(rainIntensity);
        float activeParticleAmount = Mathf.InverseLerp(particlesBeginAt, 1f, amount);
        if (rainSystemController != null)
        {
            SetControllerIntensity(activeParticleAmount);
        }
        else if (rainParticles != null)
        {
            var emission = rainParticles.emission;
            emission.rateOverTimeMultiplier = activeParticleAmount * fallbackParticlesPerSecond;
            if (activeParticleAmount > 0f && !rainParticles.isPlaying) rainParticles.Play(true);
            if (activeParticleAmount <= 0f && rainParticles.isPlaying) rainParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
        // Rain particles respond immediately. Lens water is delayed and eased
        // independently, so brief showers need not wet the camera instantly.
        UpdateLensRainTarget(amount);
    }

    private void UpdateLensRainTarget(float rainAmount)
    {
        bool isRainingForLens = rainAmount > lensBeginAt;
        requestedLensRainAmount = isRainingForLens
            ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                lensBeginAt, Mathf.Max(lensBeginAt + 0.001f, lensFullyVisibleAt), rainAmount))
            : 0f;

        if (isRainingForLens == lensRainIsActive) return;

        lensRainIsActive = isRainingForLens;
        if (isRainingForLens)
        {
            lensWetWeatherElapsed = 0f;
            // If a shower restarts while the glass is still wet, keep the water
            // visible during the new onset delay rather than wiping it instantly.
            lensHeldAmount = currentLensRainAmount;
        }
        else
        {
            lensDryWeatherElapsed = 0f;
            lensHeldAmount = currentLensRainAmount;
        }
    }

    private void AdvanceLensTransition(float deltaTime)
    {
        if (!startupCaptured) return;

        deltaTime = Mathf.Max(0f, deltaTime);
        float target;
        if (lensRainIsActive)
        {
            lensWetWeatherElapsed += deltaTime;
            target = lensWetWeatherElapsed < Mathf.Max(0f, lensOnsetDelaySeconds)
                ? lensHeldAmount
                : requestedLensRainAmount;
        }
        else
        {
            lensDryWeatherElapsed += deltaTime;
            target = lensDryWeatherElapsed < Mathf.Max(0f, lensDryingHoldSeconds)
                ? lensHeldAmount
                : 0f;
        }

        // Critically damped interpolation avoids a visible jump even if the
        // weather target changes during a transition. Fade times are approximate
        // 95%-settling durations; no easing parameters are applied to the shader.
        float duration = target > currentLensRainAmount ? lensFadeInSeconds : lensFadeOutSeconds;
        if (duration <= 0f)
        {
            currentLensRainAmount = target;
            lensAmountVelocity = 0f;
        }
        else if (deltaTime > 0f)
        {
            float smoothTime = Mathf.Max(0.01f, duration / 4.75f);
            currentLensRainAmount = Mathf.SmoothDamp(
                currentLensRainAmount, target, ref lensAmountVelocity,
                smoothTime, Mathf.Infinity, deltaTime);
        }

        if (Mathf.Abs(currentLensRainAmount - target) < 0.0005f &&
            Mathf.Abs(lensAmountVelocity) < 0.005f)
        {
            currentLensRainAmount = target;
            lensAmountVelocity = 0f;
        }

        currentLensRainAmount = Mathf.Clamp01(currentLensRainAmount);
        ApplyFloatIfPresent(rainOnLensMaterial, lensRainAmountProperty, currentLensRainAmount);

        // Disable only after the *fade-out* finishes, not when rain stops.
        if (toggleRendererFeatures && lensFeature != null)
        {
            bool shouldEnable = currentLensRainAmount > 0.0005f;
            if (lensFeature.isActive != shouldEnable)
                lensFeature.SetActive(shouldEnable);
        }
    }

    private void UpdateFog()
    {
        if (fogFeature == null) return;
        float input = Mathf.Clamp01(fogginess);
        bool enabled = input > fogBeginsAt;
        float scaled = enabled ? Mathf.InverseLerp(fogBeginsAt, 1f, input) : 0f;
        if (!fogOriginalValuesCaptured)
        {
            CaptureFogMembers();
            fogOriginalValuesCaptured = true;
        }

        bool changed = false;
        // Set internal settings as well as the feature's top-level active state.
        changed |= SetFogMember(fogEffectEnabledMember, enabled);
        changed |= SetFogMember(fogMaximumOpacityMember,
            fogMaximumOpacityAtOne * scaled);

        if (toggleRendererFeatures && fogFeature.isActive != enabled)
        {
            fogFeature.SetActive(enabled);
            changed = true;
        }
        // Many custom URP features read settings each frame. If your feature
        // caches them only in Create(), you may have to modify the feature to
        // upload settings in AddRenderPasses/RecordRenderGraph instead.
        if (changed && recreateFogFeatureOnChange && enabled)
            fogFeature.Create();
    }

    // Compatible with Unity's C# runtime; avoids inaccessible framework/Unity
    // ReferenceEqualityComparer types and compares containers by identity.
    private sealed class ObjectReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ObjectReferenceComparer Instance = new ObjectReferenceComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class FogMemberSnapshot
    {
        public object owner;
        public FieldInfo field;
        public PropertyInfo property;
        public object value;
        public void Restore()
        {
            try
            {
                if (field != null) field.SetValue(owner, value);
                else if (property != null && property.CanWrite) property.SetValue(owner, value);
            }
            catch (Exception) { /* another component may have been destroyed */ }
        }
    }

    private static string NormalizeMember(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var result = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) result.Append(char.ToLowerInvariant(c));
        return result.ToString().TrimStart('_');
    }

    private bool TryFindFogMember(string memberName, out FogMemberSnapshot found)
    {
        found = null;
        if (fogFeature == null || string.IsNullOrWhiteSpace(memberName)) return false;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string wanted = NormalizeMember(memberName);
        var queue = new Queue<(object item, int depth)>();
        var seen = new HashSet<object>(ObjectReferenceComparer.Instance);
        queue.Enqueue((fogFeature, 0));
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            object obj = node.item;
            if (obj == null || !seen.Add(obj)) continue;
            Type type = obj.GetType();
            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsStatic) continue;
                if (NormalizeMember(field.Name) == wanted &&
                    (field.FieldType == typeof(bool) || field.FieldType == typeof(float)))
                {
                    found = new FogMemberSnapshot { owner=obj, field=field, value=field.GetValue(obj) };
                    return true;
                }
            }
            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0 || !property.CanRead || !property.CanWrite) continue;
                if (NormalizeMember(property.Name) == wanted &&
                    (property.PropertyType == typeof(bool) || property.PropertyType == typeof(float)))
                {
                    found = new FogMemberSnapshot { owner=obj, property=property, value=property.GetValue(obj) };
                    return true;
                }
            }
            if (node.depth >= 3) continue;
            // Only traverse serialized custom settings containers, not arbitrary
            // Unity engine internals. This covers e.g. feature.settings.general,
            // feature.settings.fogComposition and feature.settings.heightLayer.
            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsStatic || field.FieldType.IsPrimitive || field.FieldType.IsEnum ||
                    field.FieldType == typeof(string) ||
                    typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)) continue;
                if (!field.IsPublic && !Attribute.IsDefined(field, typeof(SerializeField))) continue;
                object nested = field.GetValue(obj);
                if (nested != null && !field.FieldType.IsArray && !typeof(System.Collections.IEnumerable).IsAssignableFrom(field.FieldType))
                    queue.Enqueue((nested, node.depth + 1));
            }
        }
        return false;
    }

    private void CaptureFogMembers()
    {
        fogOriginalValues.Clear();
        foreach (string memberName in new [] {fogEffectEnabledMember, fogMaximumOpacityMember})
        {
            if (TryFindFogMember(memberName, out FogMemberSnapshot snapshot))
                fogOriginalValues.Add(snapshot);
            else if (!fogBindingWarned)
            {
                Debug.LogWarning($"EnvironmentCycleController: cannot find fog feature setting '{memberName}' on {fogFeature.GetType().Name}. " +
                    "Check ScreenSpaceHeightFogFeature.cs for its serialized field names. Other settings will still be driven where found.", this);
                fogBindingWarned = true;
            }
        }
    }

    private bool SetFogMember(string memberName, object value)
    {
        // Cached member owners must be re-found only once; reflection writes
        // into the live settings object. No material instance is assumed.
        FogMemberSnapshot snapshot = fogOriginalValues.Find(
            item => NormalizeMember(item.field != null ? item.field.Name : item.property.Name) == NormalizeMember(memberName));
        if (snapshot == null) return false;
        try
        {
            object old = snapshot.field != null ? snapshot.field.GetValue(snapshot.owner) : snapshot.property.GetValue(snapshot.owner);
            object converted = value is bool ? value : Convert.ToSingle(value);
            if (Equals(old, converted)) return false;
            if (snapshot.field != null) snapshot.field.SetValue(snapshot.owner, converted);
            else snapshot.property.SetValue(snapshot.owner, converted);
            return true;
        }
        catch (Exception error)
        {
            if (!fogBindingWarned)
            {
                Debug.LogWarning("EnvironmentCycleController: could not write fog setting: " + error.Message, this);
                fogBindingWarned = true;
            }
            return false;
        }
    }

    private void SetControllerIntensity(float value)
    {
        Type t = rainSystemController.GetType();
        if (t != cachedRainType)
        {
            cachedRainType = t;
            lastAppliedRainIntensity = float.NaN;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            rainIntensityField = t.GetField(rainControllerIntensityMember, flags);
            rainIntensityProperty = t.GetProperty(rainControllerIntensityMember, flags);
            rainApplySettingsMethod = t.GetMethod("ApplySettings", flags, null, Type.EmptyTypes, null);
        }

        // Avoid reconfiguring the ParticleSystem every update tick when weather
        // has not changed. Other effect inputs can still update as usual.
        if (Mathf.Approximately(lastAppliedRainIntensity, value)) return;

        try
        {
            if (rainIntensityField != null && rainIntensityField.FieldType == typeof(float))
                rainIntensityField.SetValue(rainSystemController, value);
            else if (rainIntensityProperty != null && rainIntensityProperty.PropertyType == typeof(float) && rainIntensityProperty.CanWrite)
                rainIntensityProperty.SetValue(rainSystemController, value);
            else
            {
                if (!missingRainMemberWarned)
                {
                    Debug.LogWarning($"EnvironmentCycleController: cannot find float '{rainControllerIntensityMember}' on {t.Name}.", this);
                    missingRainMemberWarned = true;
                }
                return;
            }

            // ApplySettings transfers the controller values into ParticleSystem
            // modules (emission rate, speed, etc.). Writing intensity alone does not.
            if (rainApplySettingsMethod != null)
                rainApplySettingsMethod.Invoke(rainSystemController, null);
            else if (!missingRainApplySettingsWarned)
            {
                Debug.LogWarning($"EnvironmentCycleController: {t.Name} has no parameterless ApplySettings() method.", this);
                missingRainApplySettingsWarned = true;
            }
            lastAppliedRainIntensity = value;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("EnvironmentCycleController: failed to apply rain settings: " +
                (ex.InnerException != null ? ex.InnerException.Message : ex.Message), this);
        }
    }

    private void CacheInitialState()
    {
        if (startupCaptured) return;
        previousSkybox = RenderSettings.skybox;
        if (skyboxMaterial != null)
        {
            runtimeSkybox = new Material(skyboxMaterial)
            {
                name = skyboxMaterial.name + " (Runtime Day-Night)"
            };
            // Preserve the source skybox haze for nighttime. Only daytime haze
            // is overridden; existing night appearance remains untouched.
            sourceSkyboxHasHazeStrength = !string.IsNullOrEmpty(horizonHazeStrengthProperty) &&
                runtimeSkybox.HasProperty(horizonHazeStrengthProperty);
            if (sourceSkyboxHasHazeStrength)
                sourceSkyboxHazeStrength = runtimeSkybox.GetFloat(horizonHazeStrengthProperty);
            RenderSettings.skybox = runtimeSkybox;
        }
        if (sun != null) { previousSunIntensity = sun.intensity; previousSunLocalRotation = sun.transform.localRotation; }
        if (fillLight != null) { previousFillIntensity = fillLight.intensity; previousFillEnabled = fillLight.enabled; }
        if (buildingMaterials != null)
            foreach (Material material in buildingMaterials)
            {
                if (material == null) continue;
                string property = ResolveEmissionStrength(material);
                if (property != null && !originalEmissionStrengths.ContainsKey(material))
                    originalEmissionStrengths.Add(material, material.GetFloat(property));
                if (alsoDriveWindowEmissionColor && material.HasProperty(windowEmissionProperty) && !originalWindowEmissions.ContainsKey(material))
                    originalWindowEmissions.Add(material, material.GetColor(windowEmissionProperty));
            }
        FindFeatures();

        // The Full Screen Pass uses a referenced material rather than a runtime
        // clone; capture/restore its initial value to avoid leaving Play Mode
        // with rain permanently applied to the asset.
        originalLensAmountCaptured = rainOnLensMaterial != null &&
            !string.IsNullOrEmpty(lensRainAmountProperty) &&
            rainOnLensMaterial.HasProperty(lensRainAmountProperty);
        if (originalLensAmountCaptured)
            originalLensAmount = rainOnLensMaterial.GetFloat(lensRainAmountProperty);
        currentLensRainAmount = 0f;
        requestedLensRainAmount = 0f;
        lensAmountVelocity = 0f;
        lensWetWeatherElapsed = 0f;
        lensDryWeatherElapsed = 0f;
        lensHeldAmount = 0f;
        lensRainIsActive = false;
        ApplyFloatIfPresent(rainOnLensMaterial, lensRainAmountProperty, 0f);
        if (toggleRendererFeatures && lensFeature != null)
            lensFeature.SetActive(false);

        startupCaptured = true;
    }

    private static string NormalizedFeatureName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var chars = new System.Text.StringBuilder(name.Length);
        foreach (char c in name) if (char.IsLetterOrDigit(c)) chars.Append(char.ToLowerInvariant(c));
        return chars.ToString();
    }

    private static bool FeatureMatches(ScriptableRendererFeature feature, string configuredName)
    {
        if (feature == null || string.IsNullOrWhiteSpace(configuredName)) return false;
        string target = NormalizedFeatureName(configuredName);
        return NormalizedFeatureName(feature.name) == target ||
               NormalizedFeatureName(feature.GetType().Name) == target;
    }

    private void FindFeatures()
    {
        fogFeature = fogFeatureReference;
        lensFeature = null;
        if (rendererData != null)
        {
            foreach (ScriptableRendererFeature feature in rendererData.rendererFeatures)
            {
                if (feature == null) continue;
                if (fogFeature == null && FeatureMatches(feature, fogFeatureName)) fogFeature = feature;
                if (FeatureMatches(feature, rainLensFeatureName)) lensFeature = feature;
            }
        }
        if (toggleRendererFeatures && fogFeature == null && !fogMissingWarned)
        {
            Debug.LogWarning($"EnvironmentCycleController: fog feature '{fogFeatureName}' was not found. Assign PC_Renderer (Renderer Data) and/or drag the fog feature into Fog Feature Reference. Found features: " +
                             (rendererData == null ? "<rendererData unassigned>" : string.Join(", ", GetFeatureNames(rendererData))), this);
            fogMissingWarned = true;
        }
        if (fogFeature != null) previousFogFeatureEnabled = fogFeature.isActive;
        if (lensFeature != null) previousLensFeatureEnabled = lensFeature.isActive;
        featuresCaptured = true;
    }

    private static List<string> GetFeatureNames(ScriptableRendererData data)
    {
        var names = new List<string>();
        foreach (var f in data.rendererFeatures) if (f != null) names.Add(f.name + " (" + f.GetType().Name + ")");
        return names;
    }

    private static void ApplyFloatIfPresent(Material material, string property, float value)
    {
        if (material != null && !string.IsNullOrEmpty(property) && material.HasProperty(property))
            material.SetFloat(property, value);
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        if (editorApplyQueued)
        {
            UnityEditor.EditorApplication.delayCall -= ApplyValidatedChanges;
            editorApplyQueued = false;
        }
#endif
        lastAppliedRainIntensity = float.NaN;
        if (!Application.isPlaying || !startupCaptured) return;
        if (runtimeSkybox != null)
        {
            RenderSettings.skybox = previousSkybox;
            Destroy(runtimeSkybox);
            runtimeSkybox = null;
        }
        if (sun != null) { sun.intensity = previousSunIntensity; sun.transform.localRotation = previousSunLocalRotation; }
        if (fillLight != null) { fillLight.intensity = previousFillIntensity; fillLight.enabled = previousFillEnabled; }
        foreach (var pair in originalWindowEmissions)
            if (pair.Key != null && pair.Key.HasProperty(windowEmissionProperty)) pair.Key.SetColor(windowEmissionProperty, pair.Value);
        foreach (var pair in originalEmissionStrengths)
        {
            if (pair.Key == null) continue;
            string property = ResolveEmissionStrength(pair.Key);
            if (property != null) pair.Key.SetFloat(property, pair.Value);
        }
        originalWindowEmissions.Clear();
        originalEmissionStrengths.Clear();
        resolvedEmissionProperties.Clear();
        foreach (FogMemberSnapshot snapshot in fogOriginalValues) snapshot.Restore();
        fogOriginalValues.Clear();
        fogOriginalValuesCaptured = false;
        if (originalLensAmountCaptured)
            ApplyFloatIfPresent(rainOnLensMaterial, lensRainAmountProperty, originalLensAmount);
        originalLensAmountCaptured = false;
        lensRainIsActive = false;
        lensAmountVelocity = 0f;
        if (featuresCaptured)
        {
            if (fogFeature != null) fogFeature.SetActive(previousFogFeatureEnabled);
            if (lensFeature != null) lensFeature.SetActive(previousLensFeatureEnabled);
        }
        startupCaptured = false;
    }
}
