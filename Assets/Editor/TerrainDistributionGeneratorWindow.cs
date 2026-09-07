using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generates an RGB terrain distribution texture:
/// R = grass, G = dirt/dry ground, B = third layer/hardstanding.
///
/// Place this file inside an Editor folder. Place TerrainDistributionStamp.cs
/// outside the Editor folder so the stamp component is available to scenes.
/// </summary>
public sealed class TerrainDistributionGeneratorWindow : EditorWindow
{
    private static readonly int[] PreviewResolutions = { 128, 256, 512 };
    private static readonly string[] PreviewResolutionLabels = { "128", "256", "512" };
    private static readonly int[] FinalResolutions = { 512, 1024, 2048, 4096 };
    private static readonly string[] FinalResolutionLabels = { "512", "1024", "2048", "4096" };

    [SerializeField] private Terrain terrain;
    [SerializeField] private Material targetMaterial;
    [SerializeField] private string texturePropertyName = "_DistributionMap";
    [SerializeField] private string outputAssetPath = "Assets/Generated/TerrainDistribution.png";

    [SerializeField] private int previewResolution = 256;
    [SerializeField] private int finalResolution = 1024;
    [SerializeField] private int seed = 317;
    [SerializeField] private bool autoPreview;
    [SerializeField] private bool includeInactiveStamps;
    [SerializeField] private bool assignAfterBake = true;
    [SerializeField] private bool flipHorizontal;
    [SerializeField] private bool flipVertical;

    [Header("Base Distribution")]
    [SerializeField] private Vector3 baseWeights = new Vector3(0.76f, 0.22f, 0.02f);

    [Header("Developed Grass Zone")]
    [SerializeField] private bool useDevelopedZone = true;
    [SerializeField] private Vector2 developedCenter = new Vector2(0.36f, 0.52f);
    [SerializeField] private Vector2 developedRadius = new Vector2(0.34f, 0.47f);
    [SerializeField, Range(0.001f, 1.0f)] private float developedFalloff = 0.22f;
    [SerializeField, Range(0.0f, 3.0f)] private float developedGrassBoost = 1.1f;
    [SerializeField, Range(0.0f, 1.0f)] private float developedDirtSuppression = 0.35f;

    [Header("Directional Dry Region")]
    [SerializeField] private bool useDryRegion = true;
    [SerializeField] private Vector2 dryDirection = Vector2.right;
    [SerializeField, Range(-0.25f, 1.25f)] private float dryStart = 0.56f;
    [SerializeField, Range(0.001f, 1.0f)] private float dryTransition = 0.22f;
    [SerializeField, Range(0.0f, 4.0f)] private float dryStrength = 1.45f;
    [SerializeField, Range(0.0f, 1.0f)] private float dryGrassSuppression = 0.55f;
    [SerializeField, Range(0.1f, 20.0f)] private float dryBoundaryNoiseScale = 2.6f;
    [SerializeField, Range(0.0f, 0.5f)] private float dryBoundaryWarp = 0.12f;

    [Header("Procedural Breakup")]
    [SerializeField, Range(0.1f, 30.0f)] private float macroNoiseScale = 3.1f;
    [SerializeField, Range(1, 8)] private int macroOctaves = 5;
    [SerializeField, Range(1.1f, 4.0f)] private float macroLacunarity = 2.0f;
    [SerializeField, Range(0.1f, 0.9f)] private float macroGain = 0.5f;
    [SerializeField, Range(0.0f, 1.0f)] private float macroVariation = 0.32f;
    [SerializeField, Range(1.0f, 100.0f)] private float detailNoiseScale = 22.0f;
    [SerializeField, Range(0.0f, 0.5f)] private float detailVariation = 0.07f;

    [Header("Terrain Slope Bias")]
    [SerializeField] private bool useSlopeBias = true;
    [SerializeField, Range(0.0f, 90.0f)] private float slopeStartDegrees = 7.0f;
    [SerializeField, Range(0.0f, 90.0f)] private float slopeEndDegrees = 24.0f;
    [SerializeField, Range(0.0f, 3.0f)] private float slopeDirtStrength = 0.45f;

    [Header("Perimeter Wear")]
    [SerializeField] private bool useEdgeWear;
    [SerializeField, Range(0.001f, 0.5f)] private float edgeWearWidth = 0.055f;
    [SerializeField, Range(0.0f, 3.0f)] private float edgeDirtStrength = 0.3f;

    private Texture2D previewTexture;
    private Vector2 scrollPosition;
    private bool previewQueued;

    [MenuItem("Tools/IFC Dashboard/Terrain Distribution Generator")]
    private static void OpenWindow()
    {
        TerrainDistributionGeneratorWindow window = GetWindow<TerrainDistributionGeneratorWindow>();
        window.titleContent = new GUIContent("Terrain Distribution");
        window.minSize = new Vector2(420.0f, 620.0f);
        window.Show();
    }

    private void OnEnable()
    {
        if (terrain == null)
            terrain = Terrain.activeTerrain;

        if (targetMaterial == null && terrain != null)
            targetMaterial = terrain.materialTemplate;
    }

    private void OnDisable()
    {
        DestroyPreviewTexture();
    }

    private void OnGUI()
    {
        EditorGUI.BeginChangeCheck();
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        DrawSourceAndOutputSection();
        DrawBaseSection();
        DrawDevelopedZoneSection();
        DrawDryRegionSection();
        DrawNoiseSection();
        DrawSlopeSection();
        DrawEdgeSection();
        DrawStampSection();
        DrawPreviewSection();

        EditorGUILayout.EndScrollView();
        bool changed = EditorGUI.EndChangeCheck();

        if (changed && autoPreview && terrain != null)
            QueuePreview();
    }

    private void DrawSourceAndOutputSection()
    {
        EditorGUILayout.LabelField("Source and Output", EditorStyles.boldLabel);

        terrain = (Terrain)EditorGUILayout.ObjectField("Terrain", terrain, typeof(Terrain), true);
        targetMaterial = (Material)EditorGUILayout.ObjectField("Target Material", targetMaterial, typeof(Material), false);
        texturePropertyName = EditorGUILayout.TextField("Texture Property", texturePropertyName);
        outputAssetPath = EditorGUILayout.TextField("Output Asset Path", outputAssetPath);

        previewResolution = EditorGUILayout.IntPopup(
            "Preview Resolution",
            previewResolution,
            PreviewResolutionLabels,
            PreviewResolutions
        );

        finalResolution = EditorGUILayout.IntPopup(
            "Final Resolution",
            finalResolution,
            FinalResolutionLabels,
            FinalResolutions
        );

        seed = EditorGUILayout.IntField("Seed", seed);
        autoPreview = EditorGUILayout.Toggle("Auto Preview", autoPreview);
        assignAfterBake = EditorGUILayout.Toggle("Assign After Bake", assignAfterBake);
        includeInactiveStamps = EditorGUILayout.Toggle("Include Inactive Stamps", includeInactiveStamps);

        using (new EditorGUILayout.HorizontalScope())
        {
            flipHorizontal = EditorGUILayout.ToggleLeft("Flip Horizontal", flipHorizontal);
            flipVertical = EditorGUILayout.ToggleLeft("Flip Vertical", flipVertical);
        }

        EditorGUILayout.HelpBox(
            "The generated texture stores normalized weights as R = grass, G = dirt/dry ground, and B = the third layer. " +
            "Set Texture Property to the reference used by your terrain material.",
            MessageType.Info
        );

        EditorGUILayout.Space();
    }

    private void DrawBaseSection()
    {
        EditorGUILayout.LabelField("Base Distribution", EditorStyles.boldLabel);
        baseWeights = EditorGUILayout.Vector3Field("RGB Base Weights", baseWeights);
        EditorGUILayout.Space();
    }

    private void DrawDevelopedZoneSection()
    {
        useDevelopedZone = EditorGUILayout.BeginToggleGroup("Developed Grass Zone", useDevelopedZone);
        developedCenter = EditorGUILayout.Vector2Field("Center (UV)", developedCenter);
        developedRadius = EditorGUILayout.Vector2Field("Radius (UV)", developedRadius);
        developedFalloff = EditorGUILayout.Slider("Falloff", developedFalloff, 0.001f, 1.0f);
        developedGrassBoost = EditorGUILayout.Slider("Grass Boost", developedGrassBoost, 0.0f, 3.0f);
        developedDirtSuppression = EditorGUILayout.Slider("Dirt Suppression", developedDirtSuppression, 0.0f, 1.0f);
        EditorGUILayout.EndToggleGroup();
        EditorGUILayout.Space();
    }

    private void DrawDryRegionSection()
    {
        useDryRegion = EditorGUILayout.BeginToggleGroup("Directional Dry Region", useDryRegion);
        dryDirection = EditorGUILayout.Vector2Field("Direction", dryDirection);
        dryStart = EditorGUILayout.Slider("Start", dryStart, -0.25f, 1.25f);
        dryTransition = EditorGUILayout.Slider("Transition Width", dryTransition, 0.001f, 1.0f);
        dryStrength = EditorGUILayout.Slider("Dirt Strength", dryStrength, 0.0f, 4.0f);
        dryGrassSuppression = EditorGUILayout.Slider("Grass Suppression", dryGrassSuppression, 0.0f, 1.0f);
        dryBoundaryNoiseScale = EditorGUILayout.Slider("Boundary Noise Scale", dryBoundaryNoiseScale, 0.1f, 20.0f);
        dryBoundaryWarp = EditorGUILayout.Slider("Boundary Warp", dryBoundaryWarp, 0.0f, 0.5f);
        EditorGUILayout.EndToggleGroup();
        EditorGUILayout.Space();
    }

    private void DrawNoiseSection()
    {
        EditorGUILayout.LabelField("Procedural Breakup", EditorStyles.boldLabel);
        macroNoiseScale = EditorGUILayout.Slider("Macro Scale", macroNoiseScale, 0.1f, 30.0f);
        macroOctaves = EditorGUILayout.IntSlider("Macro Octaves", macroOctaves, 1, 8);
        macroLacunarity = EditorGUILayout.Slider("Lacunarity", macroLacunarity, 1.1f, 4.0f);
        macroGain = EditorGUILayout.Slider("Gain", macroGain, 0.1f, 0.9f);
        macroVariation = EditorGUILayout.Slider("Macro Variation", macroVariation, 0.0f, 1.0f);
        detailNoiseScale = EditorGUILayout.Slider("Detail Scale", detailNoiseScale, 1.0f, 100.0f);
        detailVariation = EditorGUILayout.Slider("Detail Variation", detailVariation, 0.0f, 0.5f);
        EditorGUILayout.Space();
    }

    private void DrawSlopeSection()
    {
        useSlopeBias = EditorGUILayout.BeginToggleGroup("Terrain Slope Bias", useSlopeBias);
        slopeStartDegrees = EditorGUILayout.Slider("Slope Start", slopeStartDegrees, 0.0f, 90.0f);
        slopeEndDegrees = EditorGUILayout.Slider("Slope End", slopeEndDegrees, 0.0f, 90.0f);
        slopeDirtStrength = EditorGUILayout.Slider("Dirt Strength", slopeDirtStrength, 0.0f, 3.0f);
        EditorGUILayout.EndToggleGroup();
        EditorGUILayout.Space();
    }

    private void DrawEdgeSection()
    {
        useEdgeWear = EditorGUILayout.BeginToggleGroup("Perimeter Wear", useEdgeWear);
        edgeWearWidth = EditorGUILayout.Slider("Width", edgeWearWidth, 0.001f, 0.5f);
        edgeDirtStrength = EditorGUILayout.Slider("Dirt Strength", edgeDirtStrength, 0.0f, 3.0f);
        EditorGUILayout.EndToggleGroup();
        EditorGUILayout.Space();
    }

    private void DrawStampSection()
    {
        EditorGUILayout.LabelField("Scene Stamps", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "Use stamps for paths, hardstanding, developed grass zones, worn fence corridors, and local corrections. " +
            "The stamp's transform X/Z scale controls its world-space size.",
            MessageType.None
        );

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Create Stamp"))
                CreateStamp();

            if (GUILayout.Button("Select Stamps"))
                SelectAllStamps();
        }

        EditorGUILayout.Space();
    }

    private void DrawPreviewSection()
    {
        EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate Preview", GUILayout.Height(30.0f)))
                GeneratePreview();

            if (GUILayout.Button("Bake PNG", GUILayout.Height(30.0f)))
                BakePng();
        }

        if (previewTexture != null)
        {
            Rect previewRect = GUILayoutUtility.GetAspectRect(1.0f, GUILayout.MaxHeight(480.0f));
            EditorGUI.DrawPreviewTexture(previewRect, previewTexture, null, ScaleMode.ScaleToFit);
        }
    }

    private void QueuePreview()
    {
        if (previewQueued)
            return;

        previewQueued = true;
        EditorApplication.delayCall += () =>
        {
            previewQueued = false;
            if (this != null && terrain != null)
                GeneratePreview();
        };
    }

    private void GeneratePreview()
    {
        if (!ValidateTerrain())
            return;

        Texture2D generated = BuildTexture(previewResolution, false);
        if (generated == null)
            return;

        DestroyPreviewTexture();
        previewTexture = generated;
        previewTexture.hideFlags = HideFlags.HideAndDontSave;
        Repaint();
    }

    private void BakePng()
    {
        if (!ValidateTerrain())
            return;

        if (!ValidateOutputPath())
            return;

        Texture2D generated = null;

        try
        {
            generated = BuildTexture(finalResolution, true);
            if (generated == null)
                return;

            byte[] pngData = generated.EncodeToPNG();
            string fullPath = Path.GetFullPath(outputAssetPath);
            string directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(fullPath, pngData);
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            ConfigureImportedTexture(outputAssetPath);

            Texture2D textureAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(outputAssetPath);

            if (assignAfterBake)
                AssignTextureToMaterial(textureAsset);

            Selection.activeObject = textureAsset;
            EditorGUIUtility.PingObject(textureAsset);

            Debug.Log($"Generated terrain distribution map: {outputAssetPath}", textureAsset);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog(
                "Terrain Distribution Generator",
                $"Generation failed:\n{exception.Message}",
                "OK"
            );
        }
        finally
        {
            if (generated != null)
                DestroyImmediate(generated);

            EditorUtility.ClearProgressBar();
        }
    }

    private Texture2D BuildTexture(int resolution, bool showProgress)
    {
        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainPosition = terrain.transform.position;
        Vector3 terrainSize = terrainData.size;

        TerrainDistributionStamp[] stamps = FindStamps();
        Array.Sort(stamps, (left, right) => left.order.CompareTo(right.order));

        Color32[] pixels = new Color32[resolution * resolution];
        Vector3 normalizedBase = NormalizeWeights(baseWeights);

        float safeSlopeEnd = Mathf.Max(slopeEndDegrees, slopeStartDegrees + 0.001f);
        Vector2 safeDryDirection = dryDirection.sqrMagnitude > 0.000001f
            ? dryDirection.normalized
            : Vector2.right;

        for (int y = 0; y < resolution; y++)
        {
            if (showProgress && (y & 15) == 0)
            {
                EditorUtility.DisplayProgressBar(
                    "Generating Terrain Distribution",
                    $"Row {y + 1} of {resolution}",
                    y / (float)resolution
                );
            }

            float outputV = (y + 0.5f) / resolution;
            float sampleV = flipVertical ? 1.0f - outputV : outputV;

            for (int x = 0; x < resolution; x++)
            {
                float outputU = (x + 0.5f) / resolution;
                float sampleU = flipHorizontal ? 1.0f - outputU : outputU;
                Vector2 uv = new Vector2(sampleU, sampleV);

                Vector3 weights = normalizedBase;

                ApplyNoiseVariation(ref weights, uv);
                ApplyDevelopedZone(ref weights, uv);
                ApplyDryRegion(ref weights, uv, safeDryDirection);

                if (useSlopeBias)
                {
                    float steepness = terrainData.GetSteepness(uv.x, uv.y);
                    float slopeMask = SmoothStep(slopeStartDegrees, safeSlopeEnd, steepness);
                    weights.y += slopeMask * slopeDirtStrength;
                    weights.x *= 1.0f - slopeMask * 0.35f;
                }

                if (useEdgeWear)
                    ApplyEdgeWear(ref weights, uv);

                if (stamps.Length > 0)
                {
                    Vector3 worldPosition = new Vector3(
                        terrainPosition.x + uv.x * terrainSize.x,
                        terrainPosition.y,
                        terrainPosition.z + uv.y * terrainSize.z
                    );

                    for (int stampIndex = 0; stampIndex < stamps.Length; stampIndex++)
                        ApplyStamp(ref weights, stamps[stampIndex], worldPosition);
                }

                weights = NormalizeWeights(weights);

                pixels[y * resolution + x] = new Color32(
                    FloatToByte(weights.x),
                    FloatToByte(weights.y),
                    FloatToByte(weights.z),
                    255
                );
            }
        }

        Texture2D texture = new Texture2D(
            resolution,
            resolution,
            TextureFormat.RGBA32,
            false,
            true
        )
        {
            name = "TerrainDistribution_Generated",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private void ApplyNoiseVariation(ref Vector3 weights, Vector2 uv)
    {
        float macro = Fbm(
            uv * macroNoiseScale,
            seed,
            macroOctaves,
            macroLacunarity,
            macroGain
        );

        float detail = ValueNoise(uv * detailNoiseScale, seed + 7919);
        float macroCentered = macro * 2.0f - 1.0f;
        float detailCentered = detail * 2.0f - 1.0f;

        weights.x *= Mathf.Max(0.0f, 1.0f + macroCentered * macroVariation + detailCentered * detailVariation);
        weights.y *= Mathf.Max(0.0f, 1.0f - macroCentered * macroVariation * 0.75f - detailCentered * detailVariation * 0.35f);
        weights.z *= Mathf.Max(0.0f, 1.0f + detailCentered * detailVariation * 0.25f);
    }

    private void ApplyDevelopedZone(ref Vector3 weights, Vector2 uv)
    {
        if (!useDevelopedZone)
            return;

        float radiusX = Mathf.Max(Mathf.Abs(developedRadius.x), 0.0001f);
        float radiusY = Mathf.Max(Mathf.Abs(developedRadius.y), 0.0001f);
        float dx = (uv.x - developedCenter.x) / radiusX;
        float dy = (uv.y - developedCenter.y) / radiusY;
        float distance = Mathf.Sqrt(dx * dx + dy * dy);
        float inner = Mathf.Clamp01(1.0f - developedFalloff);
        float mask = 1.0f - SmoothStep(inner, 1.0f, distance);

        weights.x += mask * developedGrassBoost;
        weights.y *= 1.0f - mask * developedDirtSuppression;
    }

    private void ApplyDryRegion(ref Vector3 weights, Vector2 uv, Vector2 direction)
    {
        if (!useDryRegion)
            return;

        float directionalCoordinate = Vector2.Dot(uv - Vector2.one * 0.5f, direction) + 0.5f;
        float boundaryNoise = Fbm(
            uv * dryBoundaryNoiseScale,
            seed + 1291,
            Mathf.Min(macroOctaves, 5),
            macroLacunarity,
            macroGain
        );

        float warpedStart = dryStart + (boundaryNoise - 0.5f) * dryBoundaryWarp;
        float halfTransition = Mathf.Max(dryTransition * 0.5f, 0.0001f);
        float dryMask = SmoothStep(
            warpedStart - halfTransition,
            warpedStart + halfTransition,
            directionalCoordinate
        );

        weights.y += dryMask * dryStrength;
        weights.x *= 1.0f - dryMask * dryGrassSuppression;
    }

    private void ApplyEdgeWear(ref Vector3 weights, Vector2 uv)
    {
        float edgeDistance = Mathf.Min(
            Mathf.Min(uv.x, 1.0f - uv.x),
            Mathf.Min(uv.y, 1.0f - uv.y)
        );

        float mask = 1.0f - SmoothStep(0.0f, edgeWearWidth, edgeDistance);
        weights.y += mask * edgeDirtStrength;
        weights.x *= 1.0f - mask * 0.35f;
    }

    private static void ApplyStamp(
        ref Vector3 weights,
        TerrainDistributionStamp stamp,
        Vector3 worldPosition)
    {
        if (stamp == null || !stamp.contributes)
            return;

        float influence = stamp.EvaluateInfluence(worldPosition);
        if (influence <= 0.0f)
            return;

        float amount = influence * stamp.strength;
        int channelIndex = (int)stamp.channel;

        if (stamp.blendMode == TerrainDistributionStamp.StampBlendMode.Replace)
        {
            float replaceAmount = Mathf.Clamp01(amount);
            weights *= 1.0f - replaceAmount;
            weights[channelIndex] += replaceAmount;
        }
        else
        {
            weights[channelIndex] += amount;
        }
    }

    private TerrainDistributionStamp[] FindStamps()
    {
        FindObjectsInactive inactiveMode = includeInactiveStamps
            ? FindObjectsInactive.Include
            : FindObjectsInactive.Exclude;

        return UnityEngine.Object.FindObjectsByType<TerrainDistributionStamp>(
            inactiveMode,
            FindObjectsSortMode.None
        );
    }

    private void CreateStamp()
    {
        GameObject stampObject = new GameObject("Terrain Distribution Stamp");
        Undo.RegisterCreatedObjectUndo(stampObject, "Create Terrain Distribution Stamp");

        TerrainDistributionStamp stamp = stampObject.AddComponent<TerrainDistributionStamp>();
        stamp.shape = TerrainDistributionStamp.StampShape.Ellipse;
        stamp.channel = TerrainDistributionStamp.DistributionChannel.DirtGreen;
        stamp.strength = 1.0f;
        stamp.falloff = 0.2f;

        if (terrain != null && terrain.terrainData != null)
        {
            Vector3 terrainSize = terrain.terrainData.size;
            Vector3 terrainPosition = terrain.transform.position;
            stampObject.transform.position = terrainPosition + new Vector3(
                terrainSize.x * 0.5f,
                0.0f,
                terrainSize.z * 0.5f
            );
            stampObject.transform.localScale = new Vector3(
                terrainSize.x * 0.15f,
                1.0f,
                terrainSize.z * 0.08f
            );
        }
        else
        {
            stampObject.transform.localScale = new Vector3(50.0f, 1.0f, 25.0f);
        }

        Selection.activeGameObject = stampObject;
        SceneView.lastActiveSceneView?.FrameSelected();
    }

    private void SelectAllStamps()
    {
        TerrainDistributionStamp[] stamps = FindStamps();
        UnityEngine.Object[] objects = new UnityEngine.Object[stamps.Length];

        for (int index = 0; index < stamps.Length; index++)
            objects[index] = stamps[index].gameObject;

        Selection.objects = objects;
    }

    private void ConfigureImportedTexture(string assetPath)
    {
        TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null)
            return;

        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = false;
        importer.alphaSource = TextureImporterAlphaSource.None;
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
    }

    private void AssignTextureToMaterial(Texture2D textureAsset)
    {
        if (textureAsset == null || targetMaterial == null)
            return;

        if (string.IsNullOrWhiteSpace(texturePropertyName))
        {
            Debug.LogWarning("No terrain material texture-property name was provided.", targetMaterial);
            return;
        }

        if (!targetMaterial.HasProperty(texturePropertyName))
        {
            Debug.LogWarning(
                $"Material '{targetMaterial.name}' does not contain texture property '{texturePropertyName}'. " +
                "The PNG was generated but not assigned.",
                targetMaterial
            );
            return;
        }

        Undo.RecordObject(targetMaterial, "Assign Terrain Distribution Map");
        targetMaterial.SetTexture(texturePropertyName, textureAsset);
        EditorUtility.SetDirty(targetMaterial);
        AssetDatabase.SaveAssets();
    }

    private bool ValidateTerrain()
    {
        if (terrain == null || terrain.terrainData == null)
        {
            EditorUtility.DisplayDialog(
                "Terrain Distribution Generator",
                "Assign a valid Unity Terrain before generating the map.",
                "OK"
            );
            return false;
        }

        return true;
    }

    private bool ValidateOutputPath()
    {
        outputAssetPath = outputAssetPath.Replace('\\', '/').Trim();

        if (!outputAssetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
            !outputAssetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            EditorUtility.DisplayDialog(
                "Invalid Output Path",
                "Output Asset Path must be a PNG path inside the Assets folder, for example:\n" +
                "Assets/Generated/TerrainDistribution.png",
                "OK"
            );
            return false;
        }

        return true;
    }

    private void DestroyPreviewTexture()
    {
        if (previewTexture == null)
            return;

        DestroyImmediate(previewTexture);
        previewTexture = null;
    }

    private static Vector3 NormalizeWeights(Vector3 weights)
    {
        weights.x = Mathf.Max(weights.x, 0.0f);
        weights.y = Mathf.Max(weights.y, 0.0f);
        weights.z = Mathf.Max(weights.z, 0.0f);

        float sum = weights.x + weights.y + weights.z;
        if (sum <= 0.000001f)
            return new Vector3(1.0f, 0.0f, 0.0f);

        return weights / sum;
    }

    private static byte FloatToByte(float value)
    {
        return (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255.0f);
    }

    private static float Fbm(
        Vector2 position,
        int noiseSeed,
        int octaves,
        float lacunarity,
        float gain)
    {
        float value = 0.0f;
        float amplitude = 0.5f;
        float amplitudeSum = 0.0f;
        Vector2 p = position;

        int safeOctaves = Mathf.Clamp(octaves, 1, 8);

        for (int octave = 0; octave < safeOctaves; octave++)
        {
            value += ValueNoise(p, noiseSeed + octave * 1013) * amplitude;
            amplitudeSum += amplitude;

            // Rotation between octaves reduces obvious axis alignment.
            p = new Vector2(
                p.x * 0.80f - p.y * 0.60f,
                p.x * 0.60f + p.y * 0.80f
            ) * lacunarity + new Vector2(17.13f, 9.27f);

            amplitude *= gain;
        }

        return value / Mathf.Max(amplitudeSum, 0.000001f);
    }

    private static float ValueNoise(Vector2 position, int noiseSeed)
    {
        int x0 = Mathf.FloorToInt(position.x);
        int y0 = Mathf.FloorToInt(position.y);
        int x1 = x0 + 1;
        int y1 = y0 + 1;

        float tx = position.x - x0;
        float ty = position.y - y0;
        float sx = tx * tx * (3.0f - 2.0f * tx);
        float sy = ty * ty * (3.0f - 2.0f * ty);

        float a = Hash01(x0, y0, noiseSeed);
        float b = Hash01(x1, y0, noiseSeed);
        float c = Hash01(x0, y1, noiseSeed);
        float d = Hash01(x1, y1, noiseSeed);

        float bottom = Mathf.Lerp(a, b, sx);
        float top = Mathf.Lerp(c, d, sx);
        return Mathf.Lerp(bottom, top, sy);
    }

    private static float Hash01(int x, int y, int noiseSeed)
    {
        unchecked
        {
            uint hash = (uint)noiseSeed;
            hash ^= (uint)x * 0x9E3779B9u;
            hash = (hash << 17) | (hash >> 15);
            hash ^= (uint)y * 0x85EBCA6Bu;
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            hash *= 0x846CA68Bu;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFFu) / 16777215.0f;
        }
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (Mathf.Abs(edge1 - edge0) < 0.000001f)
            return value < edge0 ? 0.0f : 1.0f;

        float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
        return t * t * (3.0f - 2.0f * t);
    }
}
