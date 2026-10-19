using UnityEngine;

/// <summary>
/// Timelapse demo for EnvironmentCycleController (including v11).
/// Drives time of day and smooth, repeatable cloud variation. Rainy days grow
/// overcast before rainfall starts, remain cloudy during rain, then clear.
/// One day lasts secondsPerDay seconds. No changes to the environment controller required.
/// </summary>
public sealed class EnvironmentTimelapse : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private EnvironmentCycleController environment;

    [Header("Timelapse")]
    [Min(0.1f)] public float secondsPerDay = 30f;
    [Range(0f, 24f)] public float startingHour = 8f;
    public bool playOnStart = true;
    public bool useUnscaledTime = false;

    [Header("Clouds - smooth natural variation")]
    public bool animateClouds = true;
    [Range(0f, 1f)] public float minimumCloudiness = 0.08f;
    [Range(0f, 1f)] public float maximumDryCloudiness = 0.60f;
    [Min(0.01f)] public float cloudChangesPerDay = 1.2f;
    [Range(0f, 1f)] public float cloudDetailStrength = 0.20f;
    public int cloudSeed = 31;

    [Header("Clouds - rain coupling")]
    [Range(0f, 1f)] public float rainyCloudiness = 0.95f;
    [Range(0f, 0.5f)] public float cloudsLeadRainDayFraction = 0.12f;
    [Range(0f, 0.5f)] public float cloudsLingerDayFraction = 0.10f;

    [Header("Rain")]
    public bool animateRain = true;
    [Min(1)] public int rainEveryDays = 3;
    [Range(0f, 1f)] public float rainIntensity = 0.85f;

    [Header("Fog")]
    public bool animateFog = true;
    [Min(1)] public int fogEveryDays = 4;
    [Range(0f, 1f)] public float fogIntensity = 0.65f;

    [Header("Weather Timing (fraction of event day)")]
    [Range(0f, 1f)] public float weatherStart = 0.20f;
    [Range(0f, 1f)] public float weatherEnd = 0.85f;
    [Range(0.001f, 0.25f)] public float fadeFraction = 0.10f;

    [Header("Performance")]
    [Range(0f, 0.1f)] public float cloudUpdateThreshold = 0.01f;

    [Header("Status (read-only during play)")]
    [SerializeField] private bool playing;
    [SerializeField] private int completedDays;
    [SerializeField] private float currentHour;

    private double elapsedDays;
    private float lastRain = float.NaN;
    private float lastFog = float.NaN;
    private float lastClouds = float.NaN;

    private void Awake()
    {
        if (environment == null)
            environment = GetComponent<EnvironmentCycleController>();
    }

    private void Start()
    {
        Restart();
        playing = playOnStart;
    }

    private void Update()
    {
        if (!playing || environment == null) return;
        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        elapsedDays += dt / Mathf.Max(0.1f, secondsPerDay);
        ApplyState();
    }

    [ContextMenu("Restart Timelapse")]
    public void Restart()
    {
        elapsedDays = Mathf.Repeat(startingHour, 24f) / 24.0;
        lastRain = lastFog = lastClouds = float.NaN;
        ApplyState();
    }

    public void Play() => playing = true;
    public void Pause() => playing = false;
    public void TogglePlay() => playing = !playing;

    private void ApplyState()
    {
        if (environment == null) return;

        int day = (int)System.Math.Floor(elapsedDays);
        float fraction = (float)(elapsedDays - day);
        completedDays = day;
        currentHour = fraction * 24f;

        float rainEnvelope = WeatherEnvelope(fraction);
        bool rainyDay = animateRain && (day + 1) % Mathf.Max(1, rainEveryDays) == 0;
        float rain = rainyDay ? rainIntensity * rainEnvelope : 0f;
        float fog = animateFog && (day + 1) % Mathf.Max(1, fogEveryDays) == 0
            ? fogIntensity * rainEnvelope : 0f;

        if (animateClouds)
        {
            // Continuous (not per-day random): no popping at midnight.
            float t = (float)elapsedDays * Mathf.Max(0.01f, cloudChangesPerDay);
            float seedOffset = cloudSeed * 0.13739f + 100f;
            float primary = Mathf.PerlinNoise(t + seedOffset, 0.37f);
            float detail = Mathf.PerlinNoise(t * 3.1f + seedOffset, 4.73f);
            float noise = Mathf.Lerp(primary, detail, cloudDetailStrength);

            float lo = Mathf.Clamp01(minimumCloudiness);
            float hi = Mathf.Max(lo, Mathf.Clamp01(maximumDryCloudiness));
            float clouds = Mathf.Lerp(lo, hi, noise);

            if (rainyDay)
            {
                // Overcast builds before rain and dissipates afterward.
                float start = Mathf.Min(weatherStart, weatherEnd);
                float end = Mathf.Max(weatherStart, weatherEnd);
                float cloudStart = Mathf.Max(0f, start - cloudsLeadRainDayFraction);
                float cloudEnd = Mathf.Min(1f, end + cloudsLingerDayFraction);
                float ramp = Mathf.Min(Mathf.Max(0.001f, fadeFraction), (cloudEnd - cloudStart) * 0.5f);
                float coverage = Envelope(fraction, cloudStart, cloudEnd, ramp);
                // Full rain envelope always means at least rainyCloudiness.
                coverage = Mathf.Max(coverage, rainEnvelope);
                clouds = Mathf.Lerp(clouds, Mathf.Max(clouds, rainyCloudiness), coverage);
            }

            // Keep transitions flowing smoothly, avoid needless material updates.
            if (float.IsNaN(lastClouds) || Mathf.Abs(clouds - lastClouds) >= cloudUpdateThreshold)
            {
                environment.SetCloudiness(clouds);
                lastClouds = clouds;
            }
        }

        // Time updates each frame to animate the solar trajectory smoothly.
        environment.SetTimeOfDay(currentHour);

        if (float.IsNaN(lastRain) || Mathf.Abs(rain - lastRain) > 0.005f ||
            (rain == 0f && lastRain != 0f))
        {
            environment.SetRainIntensity(rain);
            lastRain = rain;
        }
        if (float.IsNaN(lastFog) || Mathf.Abs(fog - lastFog) > 0.005f ||
            (fog == 0f && lastFog != 0f))
        {
            environment.SetFogginess(fog);
            lastFog = fog;
        }
    }

    private float WeatherEnvelope(float fraction)
    {
        float start = Mathf.Min(weatherStart, weatherEnd);
        float end = Mathf.Max(weatherStart, weatherEnd);
        float ramp = Mathf.Min(Mathf.Max(0.001f, fadeFraction), (end - start) * 0.5f);
        return Envelope(fraction, start, end, ramp);
    }

    private static float Envelope(float phase, float start, float end, float ramp)
    {
        if (phase <= start || phase >= end) return 0f;
        float fadeIn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((phase - start) / ramp));
        float fadeOut = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((end - phase) / ramp));
        return Mathf.Min(fadeIn, fadeOut);
    }
}
