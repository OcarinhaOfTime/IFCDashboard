using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using IFCDashboard.Weather;

namespace IFCDashboard.Weather.Editor
{
    public sealed class RainSystemGeneratorWindow : EditorWindow
    {
        private const string DefaultOutputFolder =
            "Assets/IFCDashboard/Weather/Rain";

        [SerializeField] private Transform parent;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private string systemName = "WeatherSystem";
        [SerializeField] private string outputFolder = DefaultOutputFolder;
        [SerializeField] private bool saveAsPrefab = true;

        [SerializeField, Range(0f, 1f)]
        private float initialIntensity = 0.7f;

        [SerializeField, Min(0f)]
        private float maximumEmissionRate = 1200f;

        [SerializeField, Min(1)]
        private int maximumParticles = 2200;

        [SerializeField, Min(0.1f)]
        private float fallSpeed = 34f;

        [SerializeField]
        private Vector2 wind = new Vector2(2.5f, 0.5f);

        [SerializeField]
        private Vector2 coverage = new Vector2(70f, 70f);

        [SerializeField, Min(0.1f)]
        private float emitterHeight = 35f;

        [SerializeField, Min(0.1f)]
        private float emissionThickness = 4f;

        [SerializeField]
        private float referencePlaneHeight;

        [SerializeField, Min(0.001f)]
        private float minimumDropWidth = 0.055f;

        [SerializeField, Min(0.001f)]
        private float maximumDropWidth = 0.095f;

        [SerializeField, Min(0f)]
        private float stretchLength = 3.5f;

        [SerializeField, Min(0f)]
        private float velocityStretch = 0.06f;

        [SerializeField]
        private Color minimumDropColor =
            new Color(0.70f, 0.82f, 1.00f, 0.28f);

        [SerializeField]
        private Color maximumDropColor =
            new Color(0.90f, 0.96f, 1.00f, 0.55f);

        private Vector2 scrollPosition;

        [MenuItem("Tools/IFC Dashboard/Weather/Rain System Generator")]
        public static void OpenWindow()
        {
            RainSystemGeneratorWindow window =
                GetWindow<RainSystemGeneratorWindow>();

            window.titleContent = new GUIContent("Rain Generator");
            window.minSize = new Vector2(390f, 620f);
            window.Show();
        }

        private void OnEnable()
        {
            if (targetCamera == null)
            {
                targetCamera = Camera.main;

                if (targetCamera == null)
                {
                    targetCamera =
                        UnityEngine.Object.FindFirstObjectByType<Camera>();
                }
            }
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            EditorGUILayout.LabelField(
                "IFC Dashboard Rain System",
                EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Creates a camera-following world-space Particle System, a " +
                "procedural streak texture, and a URP particle material. " +
                "The defaults avoid collisions, trails, shadows, lighting, " +
                "and soft-particle depth sampling for a WebGL-friendly first version.",
                MessageType.Info);

            EditorGUILayout.Space(6f);
            DrawSceneSection();
            EditorGUILayout.Space(8f);
            DrawRainSection();
            EditorGUILayout.Space(8f);
            DrawAppearanceSection();
            EditorGUILayout.Space(8f);
            DrawOutputSection();
            EditorGUILayout.Space(12f);

            using (new EditorGUI.DisabledScope(!CanGenerate()))
            {
                if (GUILayout.Button(
                        "Generate Rain System",
                        GUILayout.Height(38f)))
                {
                    GenerateRainSystem();
                }
            }

            if (!CanGenerate())
            {
                EditorGUILayout.HelpBox(
                    "The system name and output folder must not be empty. " +
                    "The output folder must be inside Assets.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(
                "Set Reference Plane Height to the approximate terrain/site Y " +
                "coordinate. During play, the emitter follows the center of the " +
                "camera view projected onto that horizontal plane.",
                MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private void DrawSceneSection()
        {
            EditorGUILayout.LabelField("Scene", EditorStyles.boldLabel);

            parent = (Transform)EditorGUILayout.ObjectField(
                "Optional Parent",
                parent,
                typeof(Transform),
                true);

            targetCamera = (Camera)EditorGUILayout.ObjectField(
                "Target Camera",
                targetCamera,
                typeof(Camera),
                true);

            referencePlaneHeight = EditorGUILayout.FloatField(
                new GUIContent(
                    "Reference Plane Height",
                    "Approximate terrain/site world-space Y."),
                referencePlaneHeight);

            emitterHeight = EditorGUILayout.FloatField(
                new GUIContent(
                    "Emitter Height",
                    "Height above the reference plane."),
                emitterHeight);

            coverage = EditorGUILayout.Vector2Field(
                new GUIContent(
                    "Coverage X/Z",
                    "Width and depth of the rain box in world units."),
                coverage);

            emissionThickness = EditorGUILayout.FloatField(
                new GUIContent(
                    "Emission Thickness",
                    "Vertical thickness of the box emitter."),
                emissionThickness);
        }

        private void DrawRainSection()
        {
            EditorGUILayout.LabelField("Rain", EditorStyles.boldLabel);

            initialIntensity = EditorGUILayout.Slider(
                "Initial Intensity",
                initialIntensity,
                0f,
                1f);

            maximumEmissionRate = EditorGUILayout.FloatField(
                new GUIContent(
                    "Full-Intensity Rate",
                    "Particles emitted per second at intensity 1."),
                maximumEmissionRate);

            maximumParticles = EditorGUILayout.IntField(
                "Maximum Particles",
                maximumParticles);

            fallSpeed = EditorGUILayout.FloatField(
                "Fall Speed",
                fallSpeed);

            wind = EditorGUILayout.Vector2Field(
                new GUIContent(
                    "Wind X/Z",
                    "Horizontal world-space velocity."),
                wind);
        }

        private void DrawAppearanceSection()
        {
            EditorGUILayout.LabelField("Appearance", EditorStyles.boldLabel);

            minimumDropWidth = EditorGUILayout.FloatField(
                "Minimum Width",
                minimumDropWidth);

            maximumDropWidth = EditorGUILayout.FloatField(
                "Maximum Width",
                maximumDropWidth);

            stretchLength = EditorGUILayout.FloatField(
                "Base Stretch",
                stretchLength);

            velocityStretch = EditorGUILayout.FloatField(
                "Velocity Stretch",
                velocityStretch);

            minimumDropColor = EditorGUILayout.ColorField(
                "Minimum Color",
                minimumDropColor);

            maximumDropColor = EditorGUILayout.ColorField(
                "Maximum Color",
                maximumDropColor);
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            systemName = EditorGUILayout.TextField(
                "System Name",
                systemName);

            outputFolder = EditorGUILayout.TextField(
                "Asset Folder",
                outputFolder);

            saveAsPrefab = EditorGUILayout.Toggle(
                "Save and Connect Prefab",
                saveAsPrefab);
        }

        private bool CanGenerate()
        {
            return !string.IsNullOrWhiteSpace(systemName)
                && !string.IsNullOrWhiteSpace(outputFolder)
                && NormalizeAssetPath(outputFolder).StartsWith(
                    "Assets",
                    StringComparison.Ordinal);
        }

        private void GenerateRainSystem()
        {
            ClampValues();

            string normalizedFolder = NormalizeAssetPath(outputFolder);
            EnsureAssetFolder(normalizedFolder);

            Texture2D streakTexture =
                CreateOrUpdateStreakTexture(normalizedFolder);

            Material rainMaterial =
                CreateOrUpdateRainMaterial(
                    normalizedFolder,
                    streakTexture);

            GameObject root = new GameObject(systemName.Trim());
            Undo.RegisterCreatedObjectUndo(root, "Create Rain System");

            if (parent != null)
            {
                Undo.SetTransformParent(
                    root.transform,
                    parent,
                    "Parent Rain System");

                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one;
            }

            GameObject streakObject = new GameObject("RainStreaks");
            Undo.RegisterCreatedObjectUndo(
                streakObject,
                "Create Rain Streaks");

            Undo.SetTransformParent(
                streakObject.transform,
                root.transform,
                "Parent Rain Streaks");

            streakObject.transform.localPosition = Vector3.zero;
            streakObject.transform.localRotation = Quaternion.identity;
            streakObject.transform.localScale = Vector3.one;

            ParticleSystem particleSystem =
                Undo.AddComponent<ParticleSystem>(streakObject);

            ConfigureParticleSystem(particleSystem, rainMaterial);

            RainSystemController controller =
                Undo.AddComponent<RainSystemController>(root);

            // Prefab assets cannot retain references to scene Cameras.
            // Configure the asset without one, then apply the selected Camera
            // as an instance override after the prefab is connected.
            Camera setupCamera = saveAsPrefab ? null : targetCamera;

            controller.ConfigureGeneratedSystem(
                particleSystem,
                setupCamera,
                initialIntensity,
                maximumEmissionRate,
                maximumParticles,
                coverage,
                emissionThickness,
                emitterHeight,
                referencePlaneHeight,
                fallSpeed,
                wind,
                stretchLength,
                velocityStretch);

            if (saveAsPrefab)
            {
                string prefabFolder = normalizedFolder + "/Prefabs";
                EnsureAssetFolder(prefabFolder);

                string prefabPath =
                    prefabFolder + "/" +
                    MakeSafeFileName(systemName.Trim()) +
                    ".prefab";

                GameObject connectedInstance =
                    PrefabUtility.SaveAsPrefabAssetAndConnect(
                        root,
                        prefabPath,
                        InteractionMode.UserAction);

                if (connectedInstance == null)
                {
                    Debug.LogError(
                        "The Rain System was created in the scene, but Unity " +
                        "could not save or connect the prefab.");
                }
                else
                {
                    controller =
                        connectedInstance.GetComponent<RainSystemController>();

                    if (controller != null && targetCamera != null)
                    {
                        controller.SetTargetCamera(targetCamera);

                        PrefabUtility.RecordPrefabInstancePropertyModifications(
                            controller);

                        EditorUtility.SetDirty(controller);
                    }

                    root = connectedInstance;
                }
            }
            else if (targetCamera != null)
            {
                controller.SetTargetCamera(targetCamera);
                EditorUtility.SetDirty(controller);
            }

            if (root.transform.parent != null &&
                root.transform.parent.lossyScale != Vector3.one)
            {
                Debug.LogWarning(
                    "The selected parent has a non-unit world scale. " +
                    "For predictable particle coverage, place the rain system " +
                    "under an unscaled parent.",
                    root);
            }

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);

            if (root.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(root.scene);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                "Generated the IFC Dashboard rain system. " +
                "Enter Play Mode and tune RainSystemController on " +
                root.name + ".",
                root);
        }

        private void ConfigureParticleSystem(
            ParticleSystem particleSystem,
            Material material)
        {
            particleSystem.Stop(
                true,
                ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = particleSystem.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.duration = 1.5f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            main.maxParticles = maximumParticles;
            main.startSpeed = 0f;
            main.gravityModifier = 0f;
            main.startSize3D = false;
            main.startSize = new ParticleSystem.MinMaxCurve(
                minimumDropWidth,
                maximumDropWidth);
            main.startColor = new ParticleSystem.MinMaxGradient(
                minimumDropColor,
                maximumDropColor);
            main.cullingMode = ParticleSystemCullingMode.Automatic;
            main.stopAction = ParticleSystemStopAction.None;

            float nominalLifetime = Mathf.Max(
                0.25f,
                (emitterHeight + 10f) / Mathf.Max(0.1f, fallSpeed));

            main.startLifetime = new ParticleSystem.MinMaxCurve(
                nominalLifetime * 0.85f,
                nominalLifetime * 1.15f);

            ParticleSystem.EmissionModule emission =
                particleSystem.emission;

            emission.enabled = true;
            emission.rateOverTime =
                maximumEmissionRate * initialIntensity;

            ParticleSystem.ShapeModule shape = particleSystem.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(
                coverage.x,
                emissionThickness,
                coverage.y);

            ParticleSystem.VelocityOverLifetimeModule velocity =
                particleSystem.velocityOverLifetime;

            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = RandomizedConstant(wind.x, 0.25f);
            velocity.y = RandomizedConstant(-fallSpeed, 0.12f);
            velocity.z = RandomizedConstant(wind.y, 0.25f);

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime =
                particleSystem.colorOverLifetime;

            colorOverLifetime.enabled = true;
            colorOverLifetime.color =
                new ParticleSystem.MinMaxGradient(CreateLifetimeGradient());

            ParticleSystem.CollisionModule collision =
                particleSystem.collision;

            collision.enabled = false;

            ParticleSystem.TrailModule trails = particleSystem.trails;
            trails.enabled = false;

            ParticleSystem.NoiseModule noise = particleSystem.noise;
            noise.enabled = false;

            ParticleSystem.LightsModule lights = particleSystem.lights;
            lights.enabled = false;

            ParticleSystemRenderer renderer =
                particleSystem.GetComponent<ParticleSystemRenderer>();

            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.lengthScale = stretchLength;
            renderer.velocityScale = velocityStretch;
            renderer.cameraVelocityScale = 0f;
            renderer.sortMode = ParticleSystemSortMode.None;
            renderer.material = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode =
                MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;

            EditorUtility.SetDirty(particleSystem);
            EditorUtility.SetDirty(renderer);
        }

        private Texture2D CreateOrUpdateStreakTexture(string folder)
        {
            const int width = 64;
            const int height = 256;

            string path = folder + "/RainStreakTexture.asset";
            Texture2D texture =
                AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            if (texture != null &&
                (texture.width != width || texture.height != height))
            {
                AssetDatabase.DeleteAsset(path);
                texture = null;
            }

            if (texture == null)
            {
                texture = new Texture2D(
                    width,
                    height,
                    TextureFormat.RGBA32,
                    true,
                    true)
                {
                    name = "RainStreakTexture",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 1
                };

                AssetDatabase.CreateAsset(texture, path);
            }

            Color[] pixels = new Color[width * height];

            for (int y = 0; y < height; y++)
            {
                float v = (y + 0.5f) / height;

                float verticalFade =
                    SmoothStep(0f, 0.10f, v) *
                    (1f - SmoothStep(0.76f, 1f, v));

                float centerWidth =
                    Mathf.Lerp(
                        0.32f,
                        0.82f,
                        Mathf.Sin(v * Mathf.PI));

                for (int x = 0; x < width; x++)
                {
                    float normalizedX =
                        Mathf.Abs(((x + 0.5f) / width) * 2f - 1f);

                    float horizontal =
                        Mathf.Clamp01(
                            1f - normalizedX / centerWidth);

                    horizontal =
                        horizontal * horizontal *
                        (3f - 2f * horizontal);

                    float alpha =
                        Mathf.Clamp01(horizontal * verticalFade);

                    pixels[y * width + x] =
                        new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(true, false);
            EditorUtility.SetDirty(texture);

            return texture;
        }

        private Material CreateOrUpdateRainMaterial(
            string folder,
            Texture2D streakTexture)
        {
            string path = folder + "/RainStreaks_URP.mat";
            Material material =
                AssetDatabase.LoadAssetAtPath<Material>(path);

            Shader shader = Shader.Find(
                "Universal Render Pipeline/Particles/Unlit");

            if (shader == null)
            {
                shader = Shader.Find("Particles/Standard Unlit");
            }

            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Unity could not find the URP Particles/Unlit shader or " +
                    "the built-in Standard Unlit particle fallback.");
            }

            if (material == null)
            {
                material = new Material(shader)
                {
                    name = "RainStreaks_URP"
                };

                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }

            SetTextureIfPresent(material, "_BaseMap", streakTexture);
            SetTextureIfPresent(material, "_MainTex", streakTexture);
            SetColorIfPresent(material, "_BaseColor", Color.white);
            SetColorIfPresent(material, "_Color", Color.white);

            SetFloatIfPresent(material, "_Surface", 1f);
            SetFloatIfPresent(material, "_Blend", 0f);
            SetFloatIfPresent(material, "_ZWrite", 0f);
            SetFloatIfPresent(material, "_AlphaClip", 0f);
            SetFloatIfPresent(
                material,
                "_SrcBlend",
                (float)BlendMode.SrcAlpha);
            SetFloatIfPresent(
                material,
                "_DstBlend",
                (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, "_Cull", (float)CullMode.Off);
            SetFloatIfPresent(material, "_SoftParticlesEnabled", 0f);
            SetFloatIfPresent(material, "_CameraFadingEnabled", 0f);

            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)RenderQueue.Transparent;
            material.enableInstancing = true;

            EditorUtility.SetDirty(material);
            return material;
        }

        private void ClampValues()
        {
            initialIntensity = Mathf.Clamp01(initialIntensity);
            maximumEmissionRate = Mathf.Max(0f, maximumEmissionRate);
            maximumParticles = Mathf.Max(1, maximumParticles);
            fallSpeed = Mathf.Max(0.1f, fallSpeed);
            coverage.x = Mathf.Max(0.1f, coverage.x);
            coverage.y = Mathf.Max(0.1f, coverage.y);
            emitterHeight = Mathf.Max(0.1f, emitterHeight);
            emissionThickness = Mathf.Max(0.1f, emissionThickness);
            minimumDropWidth = Mathf.Max(0.001f, minimumDropWidth);
            maximumDropWidth = Mathf.Max(
                minimumDropWidth,
                maximumDropWidth);
            stretchLength = Mathf.Max(0f, stretchLength);
            velocityStretch = Mathf.Max(0f, velocityStretch);
        }

        private static Gradient CreateLifetimeGradient()
        {
            Gradient gradient = new Gradient();

            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.08f),
                    new GradientAlphaKey(0.82f, 0.78f),
                    new GradientAlphaKey(0f, 1f)
                });

            return gradient;
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

        private static float SmoothStep(
            float minimum,
            float maximum,
            float value)
        {
            float t = Mathf.Clamp01(
                (value - minimum) /
                Mathf.Max(0.00001f, maximum - minimum));

            return t * t * (3f - 2f * t);
        }

        private static void SetTextureIfPresent(
            Material material,
            string property,
            Texture texture)
        {
            if (material.HasProperty(property))
            {
                material.SetTexture(property, texture);
            }
        }

        private static void SetColorIfPresent(
            Material material,
            string property,
            Color color)
        {
            if (material.HasProperty(property))
            {
                material.SetColor(property, color);
            }
        }

        private static void SetFloatIfPresent(
            Material material,
            string property,
            float value)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, value);
            }
        }

        private static string NormalizeAssetPath(string path)
        {
            return path
                .Replace('\\', '/')
                .Trim()
                .TrimEnd('/');
        }

        private static string MakeSafeFileName(string value)
        {
            foreach (char invalidCharacter in
                     System.IO.Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidCharacter, '_');
            }

            return string.IsNullOrWhiteSpace(value)
                ? "WeatherSystem"
                : value;
        }

        private static void EnsureAssetFolder(string folder)
        {
            string normalized = NormalizeAssetPath(folder);

            if (!normalized.StartsWith(
                    "Assets",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Generated assets must be placed inside Assets.");
            }

            string[] parts = normalized.Split('/');
            string current = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];

                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }
    }
}
