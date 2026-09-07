using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public sealed class IfcTerrainPipelineWindow : EditorWindow
{
    private static readonly int[] SupportedResolutions =
    {
        129,
        257,
        513,
        1025,
        2049
    };

    private static readonly string[] SupportedResolutionLabels =
    {
        "129",
        "257",
        "513",
        "1025",
        "2049"
    };

    private enum TerrainHeightSource
    {
        Repaired,
        Smoothed,
        ClearanceAdjusted,
        BestAvailable
    }

    private enum ClearanceHeightSource
    {
        Repaired,
        Smoothed
    }

    [Header("Source")]
    [SerializeField]
    private GameObject sourceObject;

    [SerializeField]
    private Transform outputParent;

    [Header("Heightmap")]
    [SerializeField]
    private IfcTerrainHeightmapAsset heightmapData;

    [Header("Sampling")]
    [SerializeField]
    private int heightmapResolution = 513;

    [SerializeField]
    private bool centerGridOnSourceOrigin = true;

    [SerializeField]
    private float horizontalPadding = 0f;

    [SerializeField]
    private float rayTopPadding = 10f;

    [SerializeField]
    private float rayBottomPadding = 10f;

    [Header("Smoothing")]
    [SerializeField]
    private int smoothingIterations = 1;

    [SerializeField]
    [Range(0f, 1f)]
    private float smoothingStrength = 0.20f;

    [SerializeField]
    private bool useDiagonalNeighbours = true;

    [Header("Clearance")]
    [SerializeField]
    private ClearanceHeightSource clearanceHeightSource =
        ClearanceHeightSource.Smoothed;

    [SerializeField]
    private List<GameObject> clearanceTargets =
        new List<GameObject>();

    [SerializeField]
    [Min(0f)]
    private float clearanceOffset = 0.05f;

    [SerializeField]
    [Min(0f)]
    private float maximumClearanceLowering = 5f;

    [SerializeField]
    [Min(0.01f)]
    private float clearanceRayPadding = 1f;

    [SerializeField]
    [Min(0f)]
    private float clearanceFeatherDistance = 3f;

    [SerializeField]
    [Range(0, 12)]
    private int clearanceFeatherIterations = 3;

    [SerializeField]
    [Range(0f, 1f)]
    private float clearanceFeatherStrength = 0.25f;

    [SerializeField]
    private bool clearanceUsesDiagonalNeighbours = true;

    [Header("Terrain Creation")]
    [SerializeField]
    private TerrainHeightSource terrainHeightSource =
        TerrainHeightSource.BestAvailable;

    [SerializeField]
    private float terrainBottomPadding = 2f;

    [SerializeField]
    private float terrainTopPadding = 2f;

    [SerializeField]
    private bool createExteriorHoles = true;

    [Header("Output")]
    [SerializeField]
    private string outputFolder =
        "Assets/Generated/Terrain";

    [SerializeField]
    private string generatedBaseName =
        "Generated_IFC_Terrain";

    [MenuItem("Tools/IFC Terrain/Heightmap Pipeline")]
    private static void OpenWindow()
    {
        IfcTerrainPipelineWindow window =
            GetWindow<IfcTerrainPipelineWindow>(
                "IFC Terrain Pipeline");

        window.minSize = new Vector2(500f, 930f);

        if (Selection.activeGameObject != null)
        {
            window.TryAssignSelectedSource(
                Selection.activeGameObject);
        }
    }

    private void OnEnable()
    {
        if (sourceObject == null &&
            Selection.activeGameObject != null)
        {
            TryAssignSelectedSource(
                Selection.activeGameObject);
        }
    }

    private void OnSelectionChange()
    {
        // Once a source has been chosen, selecting prospective clearance
        // targets must not replace it automatically.
        if (sourceObject == null &&
            Selection.activeGameObject != null)
        {
            TryAssignSelectedSource(
                Selection.activeGameObject);
        }

        Repaint();
    }

    private void TryAssignSelectedSource(
        GameObject candidate)
    {
        if (candidate == null ||
            candidate.GetComponent<MeshFilter>() == null)
        {
            return;
        }

        sourceObject = candidate;

        if (outputParent == null)
        {
            outputParent = candidate.transform.parent;
        }

        if (string.IsNullOrWhiteSpace(generatedBaseName) ||
            generatedBaseName == "Generated_IFC_Terrain")
        {
            generatedBaseName =
                $"{candidate.name}_Heightmap";
        }
    }

    private void OnGUI()
    {
        DrawSourceSection();

        EditorGUILayout.Space(10f);

        DrawSamplingSection();

        EditorGUILayout.Space(14f);

        DrawRepairSection();

        EditorGUILayout.Space(14f);

        DrawSmoothingSection();

        EditorGUILayout.Space(14f);

        DrawClearanceSection();

        EditorGUILayout.Space(14f);

        DrawTerrainCreationSection();
    }

    private void DrawSourceSection()
    {
        EditorGUILayout.LabelField(
            "Source",
            EditorStyles.boldLabel);

        sourceObject =
            (GameObject)EditorGUILayout.ObjectField(
                "Source Mesh Object",
                sourceObject,
                typeof(GameObject),
                true);

        outputParent =
            (Transform)EditorGUILayout.ObjectField(
                "Generated Terrain Parent",
                outputParent,
                typeof(Transform),
                true);

        heightmapData =
            (IfcTerrainHeightmapAsset)
            EditorGUILayout.ObjectField(
                "Heightmap Data",
                heightmapData,
                typeof(IfcTerrainHeightmapAsset),
                false);

        if (heightmapData != null)
        {
            EditorGUILayout.HelpBox(
                BuildHeightmapSummary(heightmapData),
                MessageType.Info);
        }
    }

    private void DrawSamplingSection()
    {
        EditorGUILayout.LabelField(
            "1. Sample Mesh",
            EditorStyles.boldLabel);

        heightmapResolution =
            EditorGUILayout.IntPopup(
                "Resolution",
                heightmapResolution,
                SupportedResolutionLabels,
                SupportedResolutions);

        centerGridOnSourceOrigin =
            EditorGUILayout.Toggle(
                "Centre Grid on Source Origin",
                centerGridOnSourceOrigin);

        horizontalPadding =
            Mathf.Max(
                0f,
                EditorGUILayout.FloatField(
                    "Horizontal Padding",
                    horizontalPadding));

        rayTopPadding =
            Mathf.Max(
                0.01f,
                EditorGUILayout.FloatField(
                    "Ray Top Padding",
                    rayTopPadding));

        rayBottomPadding =
            Mathf.Max(
                0.01f,
                EditorGUILayout.FloatField(
                    "Ray Bottom Padding",
                    rayBottomPadding));

        outputFolder =
            EditorGUILayout.TextField(
                "Output Folder",
                outputFolder);

        generatedBaseName =
            EditorGUILayout.TextField(
                "Base Name",
                generatedBaseName);

        EditorGUILayout.HelpBox(
            "This operation only raycasts the source mesh. It stores " +
            "the raw heights and validity mask without repairing holes.",
            MessageType.None);

        using (new EditorGUI.DisabledScope(
                   sourceObject == null))
        {
            if (GUILayout.Button(
                    "1. Sample Mesh to Heightmap",
                    GUILayout.Height(34f)))
            {
                SampleMesh();
            }
        }
    }

    private void DrawRepairSection()
    {
        EditorGUILayout.LabelField(
            "2. Repair Internal Holes",
            EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "Invalid cells connected to the rectangular heightmap " +
            "boundary are classified as exterior. Enclosed invalid " +
            "cells are filled by repeatedly averaging their resolved " +
            "north, south, east and west neighbours.",
            MessageType.None);

        bool canRepair =
            heightmapData != null &&
            heightmapData.HasRawData;

        using (new EditorGUI.DisabledScope(!canRepair))
        {
            if (GUILayout.Button(
                    "2. Flood Fill and Interpolate",
                    GUILayout.Height(34f)))
            {
                RepairHeightmap();
            }
        }
    }

    private void DrawSmoothingSection()
    {
        EditorGUILayout.LabelField(
            "3. Smooth Heightmap",
            EditorStyles.boldLabel);

        smoothingIterations =
            EditorGUILayout.IntSlider(
                "Iterations",
                smoothingIterations,
                1,
                10);

        smoothingStrength =
            EditorGUILayout.Slider(
                "Strength",
                smoothingStrength,
                0f,
                1f);

        useDiagonalNeighbours =
            EditorGUILayout.Toggle(
                "Use Diagonal Neighbours",
                useDiagonalNeighbours);

        EditorGUILayout.HelpBox(
            "Smoothing is applied only inside the repaired terrain " +
            "footprint. Exterior cells are ignored, and each iteration " +
            "uses a separate buffer so the result does not depend on " +
            "traversal order.",
            MessageType.None);

        bool canSmooth =
            heightmapData != null &&
            heightmapData.HasRepairedData;

        using (new EditorGUI.DisabledScope(!canSmooth))
        {
            if (GUILayout.Button(
                    "3. Smooth Repaired Heightmap",
                    GUILayout.Height(34f)))
            {
                SmoothHeightmap();
            }
        }
    }

    private void DrawClearanceSection()
    {
        EditorGUILayout.LabelField(
            "4. Apply Surface Clearance",
            EditorStyles.boldLabel);

        clearanceHeightSource =
            (ClearanceHeightSource)EditorGUILayout.EnumPopup(
                "Height Source",
                clearanceHeightSource);

        DrawClearanceTargetList();

        clearanceOffset =
            Mathf.Max(
                0f,
                EditorGUILayout.FloatField(
                    "Surface Clearance",
                    clearanceOffset));

        maximumClearanceLowering =
            Mathf.Max(
                0f,
                EditorGUILayout.FloatField(
                    "Maximum Lowering",
                    maximumClearanceLowering));

        clearanceRayPadding =
            Mathf.Max(
                0.01f,
                EditorGUILayout.FloatField(
                    "Ray Padding",
                    clearanceRayPadding));

        clearanceFeatherDistance =
            Mathf.Max(
                0f,
                EditorGUILayout.FloatField(
                    "Feather Distance",
                    clearanceFeatherDistance));

        clearanceFeatherIterations =
            EditorGUILayout.IntSlider(
                "Feather Iterations",
                clearanceFeatherIterations,
                0,
                12);

        clearanceFeatherStrength =
            EditorGUILayout.Slider(
                "Feather Strength",
                clearanceFeatherStrength,
                0f,
                1f);

        clearanceUsesDiagonalNeighbours =
            EditorGUILayout.Toggle(
                "Use Diagonal Neighbours",
                clearanceUsesDiagonalNeighbours);

        int usableTargetCount =
            CountUsableClearanceTargets();

        if (usableTargetCount == 0)
        {
            EditorGUILayout.HelpBox(
                "No clearance targets are assigned. Running this step " +
                "clears any stored clearance result, and terrain creation " +
                "will fall back to the smoothed or repaired heightmap.",
                MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "Each target is sampled from above. Terrain is lowered only " +
                "where it sits above a target surface, then a bounded " +
                "transition is feathered around the corrected cells.",
                MessageType.None);
        }

        bool canApplyClearance =
            IsClearanceInputAvailable();

        if (!canApplyClearance && heightmapData != null)
        {
            string message =
                clearanceHeightSource == ClearanceHeightSource.Smoothed
                    ? "Run Step 3 or select Repaired as the clearance input."
                    : "Run Step 2 before applying clearance.";

            EditorGUILayout.HelpBox(
                message,
                MessageType.Warning);
        }

        using (new EditorGUI.DisabledScope(
                   !canApplyClearance))
        {
            string buttonLabel =
                usableTargetCount == 0
                    ? "4. Skip Clearance and Clear Stored Result"
                    : "4. Apply Clearance Targets";

            if (GUILayout.Button(
                    buttonLabel,
                    GUILayout.Height(36f)))
            {
                ApplyClearanceTargets();
            }
        }
    }

    private void DrawClearanceTargetList()
    {
        clearanceTargets ??=
            new List<GameObject>();

        EditorGUILayout.Space(3f);
        EditorGUILayout.LabelField(
            "Clearance Targets",
            EditorStyles.miniBoldLabel);

        int requestedCount =
            Mathf.Max(
                0,
                EditorGUILayout.IntField(
                    "Target Count",
                    clearanceTargets.Count));

        while (clearanceTargets.Count < requestedCount)
        {
            clearanceTargets.Add(null);
        }

        while (clearanceTargets.Count > requestedCount)
        {
            clearanceTargets.RemoveAt(
                clearanceTargets.Count - 1);
        }

        for (int index = 0;
             index < clearanceTargets.Count;
             index++)
        {
            EditorGUILayout.BeginHorizontal();

            clearanceTargets[index] =
                (GameObject)EditorGUILayout.ObjectField(
                    $"Element {index}",
                    clearanceTargets[index],
                    typeof(GameObject),
                    true);

            if (GUILayout.Button(
                    "−",
                    GUILayout.Width(28f)))
            {
                clearanceTargets.RemoveAt(index);
                index--;
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Add Selected"))
        {
            foreach (GameObject selected
                     in Selection.gameObjects)
            {
                if (selected != null &&
                    !clearanceTargets.Contains(selected))
                {
                    clearanceTargets.Add(selected);
                }
            }
        }

        if (GUILayout.Button("Add Empty Slot"))
        {
            clearanceTargets.Add(null);
        }

        if (GUILayout.Button("Clear List"))
        {
            clearanceTargets.Clear();
        }

        EditorGUILayout.EndHorizontal();
    }

    private void DrawTerrainCreationSection()
    {
        EditorGUILayout.LabelField(
            "5. Create Unity Terrain",
            EditorStyles.boldLabel);

        terrainHeightSource =
            (TerrainHeightSource)EditorGUILayout.EnumPopup(
                "Height Source",
                terrainHeightSource);

        terrainBottomPadding =
            Mathf.Max(
                0f,
                EditorGUILayout.FloatField(
                    "Bottom Padding",
                    terrainBottomPadding));

        terrainTopPadding =
            Mathf.Max(
                0f,
                EditorGUILayout.FloatField(
                    "Top Padding",
                    terrainTopPadding));

        createExteriorHoles =
            EditorGUILayout.Toggle(
                "Create Exterior Holes",
                createExteriorHoles);

        bool selectedSourceAvailable =
            TryResolveTerrainHeightSource(
                out _,
                out string resolvedSuffix,
                out TerrainHeightSource resolvedSource);

        if (!selectedSourceAvailable &&
            heightmapData != null)
        {
            EditorGUILayout.HelpBox(
                BuildMissingTerrainSourceMessage(),
                MessageType.Warning);
        }
        else if (selectedSourceAvailable)
        {
            string sourceDescription =
                terrainHeightSource == TerrainHeightSource.BestAvailable
                    ? $"Best Available currently resolves to {resolvedSource}."
                    : $"Terrain will use {resolvedSuffix} heights.";

            EditorGUILayout.HelpBox(
                sourceDescription + " The original IFC mesh is not " +
                "modified or disabled.",
                MessageType.None);
        }

        using (new EditorGUI.DisabledScope(
                   !selectedSourceAvailable))
        {
            if (GUILayout.Button(
                    "5. Create TerrainData and GameObject",
                    GUILayout.Height(38f)))
            {
                CreateUnityTerrain();
            }
        }
    }

    private bool IsClearanceInputAvailable()
    {
        if (heightmapData == null)
        {
            return false;
        }

        return clearanceHeightSource switch
        {
            ClearanceHeightSource.Repaired =>
                heightmapData.HasRepairedData,

            ClearanceHeightSource.Smoothed =>
                heightmapData.HasSmoothedData,

            _ => false
        };
    }

    private int CountUsableClearanceTargets()
    {
        if (clearanceTargets == null)
        {
            return 0;
        }

        HashSet<GameObject> uniqueTargets =
            new HashSet<GameObject>();

        foreach (GameObject target in clearanceTargets)
        {
            if (target == null)
            {
                continue;
            }

            uniqueTargets.Add(target);
        }

        return uniqueTargets.Count;
    }

    private bool TryResolveTerrainHeightSource(
        out float[] heights,
        out string suffix,
        out TerrainHeightSource resolvedSource)
    {
        heights = null;
        suffix = string.Empty;
        resolvedSource = terrainHeightSource;

        if (heightmapData == null)
        {
            return false;
        }

        switch (terrainHeightSource)
        {
            case TerrainHeightSource.Repaired:
                if (!heightmapData.HasRepairedData)
                {
                    return false;
                }

                heights = heightmapData.repairedHeights;
                suffix = "Repaired";
                resolvedSource = TerrainHeightSource.Repaired;
                return true;

            case TerrainHeightSource.Smoothed:
                if (!heightmapData.HasSmoothedData)
                {
                    return false;
                }

                heights = heightmapData.smoothedHeights;
                suffix = "Smoothed";
                resolvedSource = TerrainHeightSource.Smoothed;
                return true;

            case TerrainHeightSource.ClearanceAdjusted:
                if (!heightmapData.HasClearanceData)
                {
                    return false;
                }

                heights = heightmapData.clearanceHeights;
                suffix = "ClearanceAdjusted";
                resolvedSource = TerrainHeightSource.ClearanceAdjusted;
                return true;

            case TerrainHeightSource.BestAvailable:
                if (heightmapData.HasClearanceData)
                {
                    heights = heightmapData.clearanceHeights;
                    suffix = "ClearanceAdjusted";
                    resolvedSource = TerrainHeightSource.ClearanceAdjusted;
                    return true;
                }

                if (heightmapData.HasSmoothedData)
                {
                    heights = heightmapData.smoothedHeights;
                    suffix = "Smoothed";
                    resolvedSource = TerrainHeightSource.Smoothed;
                    return true;
                }

                if (heightmapData.HasRepairedData)
                {
                    heights = heightmapData.repairedHeights;
                    suffix = "Repaired";
                    resolvedSource = TerrainHeightSource.Repaired;
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private string BuildMissingTerrainSourceMessage()
    {
        return terrainHeightSource switch
        {
            TerrainHeightSource.Repaired =>
                "The repaired heightmap has not been generated yet.",

            TerrainHeightSource.Smoothed =>
                "The smoothed heightmap has not been generated yet. " +
                "Run Step 3 or select Repaired.",

            TerrainHeightSource.ClearanceAdjusted =>
                "The clearance-adjusted heightmap has not been generated. " +
                "Assign targets and run Step 4, or select another source.",

            TerrainHeightSource.BestAvailable =>
                "No repaired, smoothed, or clearance-adjusted heightmap " +
                "is available yet.",

            _ => "The selected heightmap source is unavailable."
        };
    }

    private static string BuildHeightmapSummary(
        IfcTerrainHeightmapAsset data)
    {
        string status;

        if (data.HasClearanceData)
        {
            status =
                $"Clearance adjusted ({data.clearanceTargetCount} target(s), " +
                $"{data.clearanceAdjustedSampleCount:N0} adjusted samples)";
        }
        else if (data.HasSmoothedData)
        {
            status =
                $"Smoothed ({data.smoothingIterations} iteration(s), " +
                $"strength {data.smoothingStrength:F2})";
        }
        else if (data.HasRepairedData)
        {
            status = "Repaired";
        }
        else if (data.HasRawData)
        {
            status = "Sampled";
        }
        else
        {
            status = "Incomplete";
        }

        float validPercentage =
            data.totalSampleCount > 0
                ? 100f *
                  data.validSampleCount /
                  data.totalSampleCount
                : 0f;

        return
            $"Resolution: {data.resolution} × {data.resolution}\n" +
            $"Sample spacing: " +
            $"{data.sampleSpacingX:F3} × " +
            $"{data.sampleSpacingZ:F3} world units\n" +
            $"Valid samples: {validPercentage:F1}%\n" +
            $"Elevation: {data.minimumSampledWorldY:F3} to " +
            $"{data.maximumSampledWorldY:F3}\n" +
            $"Status: {status}";
    }

    // ---------------------------------------------------------------------
    // Stage 1: Sampling
    // ---------------------------------------------------------------------

    private void SampleMesh()
    {
        if (!TryValidateSource(
                out MeshFilter meshFilter,
                out MeshRenderer meshRenderer))
        {
            return;
        }

        EnsureAssetFolderExists(outputFolder);

        MeshCollider collider =
            sourceObject.GetComponent<MeshCollider>();

        bool addedTemporaryCollider = false;

        bool previousColliderEnabled = false;
        bool previousColliderConvex = false;
        Mesh previousColliderMesh = null;

        bool previousBackfaceSetting =
            Physics.queriesHitBackfaces;

        try
        {
            if (collider == null)
            {
                collider =
                    sourceObject.AddComponent<MeshCollider>();

                addedTemporaryCollider = true;
            }
            else
            {
                previousColliderEnabled = collider.enabled;
                previousColliderConvex = collider.convex;
                previousColliderMesh = collider.sharedMesh;
            }

            collider.enabled = true;
            collider.convex = false;

            collider.sharedMesh = null;
            collider.sharedMesh = meshFilter.sharedMesh;

            /*
             * IFC tessellation can contain inconsistent triangle winding.
             * Allowing backface queries makes the sampling less sensitive
             * to that source defect.
             */
            Physics.queriesHitBackfaces = true;
            Physics.SyncTransforms();

            Bounds sourceBounds = meshRenderer.bounds;

            CalculateSamplingBounds(
                sourceBounds,
                sourceObject.transform.position,
                out float gridMinX,
                out float gridMaxX,
                out float gridMinZ,
                out float gridMaxZ);

            float rayTopY =
                sourceBounds.max.y + rayTopPadding;

            float rayBottomY =
                sourceBounds.min.y - rayBottomPadding;

            float rayLength =
                rayTopY - rayBottomY;

            int resolution = heightmapResolution;
            int totalCells = resolution * resolution;

            float[] rawHeights =
                new float[totalCells];

            byte[] validMask =
                new byte[totalCells];

            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;

            int validCount = 0;

            for (int z = 0; z < resolution; z++)
            {
                float progress =
                    z / (float)(resolution - 1);

                if (EditorUtility.DisplayCancelableProgressBar(
                        "Sampling IFC terrain",
                        $"Row {z + 1} of {resolution}",
                        progress))
                {
                    throw new OperationCanceledException();
                }

                float normalizedZ =
                    z / (float)(resolution - 1);

                float worldZ =
                    Mathf.Lerp(
                        gridMinZ,
                        gridMaxZ,
                        normalizedZ);

                for (int x = 0; x < resolution; x++)
                {
                    float normalizedX =
                        x / (float)(resolution - 1);

                    float worldX =
                        Mathf.Lerp(
                            gridMinX,
                            gridMaxX,
                            normalizedX);

                    Ray ray = new Ray(
                        new Vector3(
                            worldX,
                            rayTopY,
                            worldZ),
                        Vector3.down);

                    int index =
                        z * resolution + x;

                    if (!collider.Raycast(
                            ray,
                            out RaycastHit hit,
                            rayLength))
                    {
                        continue;
                    }

                    rawHeights[index] = hit.point.y;
                    validMask[index] = 1;

                    validCount++;

                    minY =
                        Mathf.Min(minY, hit.point.y);

                    maxY =
                        Mathf.Max(maxY, hit.point.y);
                }
            }

            if (validCount == 0)
            {
                throw new InvalidOperationException(
                    "No rays hit the source mesh. Check that the " +
                    "selected object is the actual terrain mesh.");
            }

            string safeBaseName =
                SanitizeFileName(
                    string.IsNullOrWhiteSpace(generatedBaseName)
                        ? $"{sourceObject.name}_Heightmap"
                        : generatedBaseName);

            string assetPath =
                AssetDatabase.GenerateUniqueAssetPath(
                    $"{NormalizeAssetFolder(outputFolder)}/" +
                    $"{safeBaseName}_Data.asset");

            IfcTerrainHeightmapAsset data =
                CreateInstance<IfcTerrainHeightmapAsset>();

            PopulateSampleMetadata(
                data,
                safeBaseName,
                meshFilter,
                sourceBounds,
                gridMinX,
                gridMaxX,
                gridMinZ,
                gridMaxZ,
                rayTopY,
                rayBottomY,
                rawHeights,
                validMask,
                validCount,
                minY,
                maxY);

            AssetDatabase.CreateAsset(
                data,
                assetPath);

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            WriteRawPreviews(data);

            AssetDatabase.Refresh();

            heightmapData = data;

            Selection.activeObject = data;
            EditorGUIUtility.PingObject(data);

            Debug.Log(
                $"Sampled IFC terrain into '{assetPath}'.\n" +
                $"Valid samples: {validCount:N0} / " +
                $"{totalCells:N0}\n" +
                $"Sample spacing: " +
                $"{data.sampleSpacingX:F3} × " +
                $"{data.sampleSpacingZ:F3}\n" +
                $"Elevation range: {minY:F3} to {maxY:F3}",
                data);
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning(
                "Terrain sampling was cancelled.");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            Physics.queriesHitBackfaces =
                previousBackfaceSetting;

            if (collider != null)
            {
                if (addedTemporaryCollider)
                {
                    UnityEngine.Object.DestroyImmediate(collider);
                }
                else
                {
                    collider.sharedMesh = null;
                    collider.sharedMesh = previousColliderMesh;
                    collider.convex = previousColliderConvex;
                    collider.enabled = previousColliderEnabled;
                }
            }

            EditorUtility.ClearProgressBar();
        }
    }

    private void CalculateSamplingBounds(
        Bounds sourceBounds,
        Vector3 sourceOrigin,
        out float minX,
        out float maxX,
        out float minZ,
        out float maxZ)
    {
        if (!centerGridOnSourceOrigin)
        {
            minX =
                sourceBounds.min.x - horizontalPadding;

            maxX =
                sourceBounds.max.x + horizontalPadding;

            minZ =
                sourceBounds.min.z - horizontalPadding;

            maxZ =
                sourceBounds.max.z + horizontalPadding;

            return;
        }

        float halfWidth =
            Mathf.Max(
                Mathf.Abs(
                    sourceBounds.min.x -
                    sourceOrigin.x),
                Mathf.Abs(
                    sourceBounds.max.x -
                    sourceOrigin.x));

        float halfDepth =
            Mathf.Max(
                Mathf.Abs(
                    sourceBounds.min.z -
                    sourceOrigin.z),
                Mathf.Abs(
                    sourceBounds.max.z -
                    sourceOrigin.z));

        halfWidth += horizontalPadding;
        halfDepth += horizontalPadding;

        minX = sourceOrigin.x - halfWidth;
        maxX = sourceOrigin.x + halfWidth;

        minZ = sourceOrigin.z - halfDepth;
        maxZ = sourceOrigin.z + halfDepth;
    }

    private void PopulateSampleMetadata(
        IfcTerrainHeightmapAsset data,
        string safeBaseName,
        MeshFilter meshFilter,
        Bounds sourceBounds,
        float gridMinX,
        float gridMaxX,
        float gridMinZ,
        float gridMaxZ,
        float rayTopY,
        float rayBottomY,
        float[] rawHeights,
        byte[] validMask,
        int validCount,
        float minY,
        float maxY)
    {
        string meshAssetPath =
            AssetDatabase.GetAssetPath(
                meshFilter.sharedMesh);

        data.datasetName = safeBaseName;
        data.generatedUtc =
            DateTime.UtcNow.ToString("O");

        data.sourceObjectName =
            sourceObject.name;

        data.sourceHierarchyPath =
            GetHierarchyPath(
                sourceObject.transform);

        data.sourceScenePath =
            sourceObject.scene.path;

        data.sourceMeshAssetPath =
            meshAssetPath;

        data.sourceMeshGuid =
            string.IsNullOrWhiteSpace(meshAssetPath)
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(
                    meshAssetPath);

        data.sourceOriginWorld =
            sourceObject.transform.position;

        data.sourcePositionWorld =
            sourceObject.transform.position;

        data.sourceRotationWorld =
            sourceObject.transform.rotation;

        data.sourceLossyScale =
            sourceObject.transform.lossyScale;

        data.sourceWorldBounds =
            sourceBounds;

        data.resolution =
            heightmapResolution;

        data.gridMinX = gridMinX;
        data.gridMaxX = gridMaxX;
        data.gridMinZ = gridMinZ;
        data.gridMaxZ = gridMaxZ;

        data.sampleSpacingX =
            (gridMaxX - gridMinX) /
            (heightmapResolution - 1f);

        data.sampleSpacingZ =
            (gridMaxZ - gridMinZ) /
            (heightmapResolution - 1f);

        data.rayTopY = rayTopY;
        data.rayBottomY = rayBottomY;

        data.centeredOnSourceOrigin =
            centerGridOnSourceOrigin;

        data.horizontalPadding =
            horizontalPadding;

        data.rayTopPadding =
            rayTopPadding;

        data.rayBottomPadding =
            rayBottomPadding;

        data.validSampleCount =
            validCount;

        data.totalSampleCount =
            heightmapResolution *
            heightmapResolution;

        data.minimumSampledWorldY = minY;
        data.maximumSampledWorldY = maxY;

        data.hasRepair = false;
        data.exteriorSampleCount = 0;
        data.interpolatedSampleCount = 0;

        data.hasSmoothing = false;
        data.smoothingIterations = 0;
        data.smoothingStrength = 0f;
        data.smoothingUsesDiagonals = false;

        data.rawHeights = rawHeights;
        data.validMask = validMask;

        data.repairedHeights = null;
        data.exteriorMask = null;
        data.interpolatedMask = null;
        data.smoothedHeights = null;

        ResetClearanceData(data);
    }

    // ---------------------------------------------------------------------
    // Stage 2: Flood-fill classification and interpolation
    // ---------------------------------------------------------------------

    private void RepairHeightmap()
    {
        if (heightmapData == null ||
            !heightmapData.HasRawData)
        {
            EditorUtility.DisplayDialog(
                "Missing Heightmap",
                "Select a valid sampled heightmap asset.",
                "OK");

            return;
        }

        try
        {
            int resolution =
                heightmapData.resolution;

            byte[] exteriorMask =
                FindExteriorInvalidSamples(
                    heightmapData.validMask,
                    resolution);

            float[] repairedHeights =
                (float[])heightmapData
                    .rawHeights.Clone();

            byte[] interpolatedMask =
                new byte[
                    heightmapData.CellCount];

            int interpolatedCount =
                InterpolateInternalHoles(
                    repairedHeights,
                    heightmapData.validMask,
                    exteriorMask,
                    interpolatedMask,
                    resolution);

            int exteriorCount = 0;

            for (int index = 0;
                 index < exteriorMask.Length;
                 index++)
            {
                if (exteriorMask[index] != 0)
                {
                    exteriorCount++;
                }
            }

            heightmapData.repairedHeights =
                repairedHeights;

            heightmapData.exteriorMask =
                exteriorMask;

            heightmapData.interpolatedMask =
                interpolatedMask;

            heightmapData.exteriorSampleCount =
                exteriorCount;

            heightmapData.interpolatedSampleCount =
                interpolatedCount;

            heightmapData.hasRepair = true;

            // Repairing invalidates any previous smoothing result.
            heightmapData.hasSmoothing = false;
            heightmapData.smoothingIterations = 0;
            heightmapData.smoothingStrength = 0f;
            heightmapData.smoothingUsesDiagonals = false;
            heightmapData.smoothedHeights = null;

            ResetClearanceData(heightmapData);

            EditorUtility.SetDirty(heightmapData);
            AssetDatabase.SaveAssets();

            WriteRepairPreviews(heightmapData);

            AssetDatabase.Refresh();

            Debug.Log(
                $"Repaired '{heightmapData.name}'.\n" +
                $"Exterior samples: {exteriorCount:N0}\n" +
                $"Interpolated internal samples: " +
                $"{interpolatedCount:N0}",
                heightmapData);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>
    /// Flood-fills invalid cells beginning at the rectangular grid border.
    ///
    /// A returned value of 1 means the invalid sample is connected to the
    /// outside of the heightmap.
    /// </summary>
    private static byte[] FindExteriorInvalidSamples(
        byte[] validMask,
        int resolution)
    {
        byte[] exteriorMask =
            new byte[validMask.Length];

        Queue<int> queue =
            new Queue<int>();

        for (int x = 0; x < resolution; x++)
        {
            TryEnqueueExterior(
                x,
                0,
                resolution,
                validMask,
                exteriorMask,
                queue);

            TryEnqueueExterior(
                x,
                resolution - 1,
                resolution,
                validMask,
                exteriorMask,
                queue);
        }

        for (int z = 0; z < resolution; z++)
        {
            TryEnqueueExterior(
                0,
                z,
                resolution,
                validMask,
                exteriorMask,
                queue);

            TryEnqueueExterior(
                resolution - 1,
                z,
                resolution,
                validMask,
                exteriorMask,
                queue);
        }

        int[] deltaX = { -1, 1, 0, 0 };
        int[] deltaZ = { 0, 0, -1, 1 };

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();

            int x = index % resolution;
            int z = index / resolution;

            for (int direction = 0;
                 direction < 4;
                 direction++)
            {
                TryEnqueueExterior(
                    x + deltaX[direction],
                    z + deltaZ[direction],
                    resolution,
                    validMask,
                    exteriorMask,
                    queue);
            }
        }

        return exteriorMask;
    }

    private static void TryEnqueueExterior(
        int x,
        int z,
        int resolution,
        byte[] validMask,
        byte[] exteriorMask,
        Queue<int> queue)
    {
        if (x < 0 ||
            x >= resolution ||
            z < 0 ||
            z >= resolution)
        {
            return;
        }

        int index =
            z * resolution + x;

        if (validMask[index] != 0 ||
            exteriorMask[index] != 0)
        {
            return;
        }

        exteriorMask[index] = 1;
        queue.Enqueue(index);
    }

    /// <summary>
    /// Repeatedly expands from resolved terrain samples into enclosed
    /// missing regions.
    ///
    /// New cells within an iteration use only neighbours that were resolved
    /// before that iteration, avoiding dependence on traversal order.
    /// </summary>
    private static int InterpolateInternalHoles(
        float[] heights,
        byte[] validMask,
        byte[] exteriorMask,
        byte[] interpolatedMask,
        int resolution)
    {
        bool[] resolved =
            new bool[heights.Length];

        int remaining = 0;

        for (int index = 0;
             index < heights.Length;
             index++)
        {
            if (validMask[index] != 0)
            {
                resolved[index] = true;
                continue;
            }

            if (exteriorMask[index] == 0)
            {
                remaining++;
            }
        }

        List<int> stagedIndices =
            new List<int>();

        List<float> stagedValues =
            new List<float>();

        int initialRemaining = remaining;
        int iteration = 0;

        while (remaining > 0)
        {
            iteration++;

            EditorUtility.DisplayProgressBar(
                "Interpolating terrain holes",
                $"Iteration {iteration}, " +
                $"{remaining:N0} samples remaining",
                1f -
                remaining /
                (float)Mathf.Max(1, initialRemaining));

            stagedIndices.Clear();
            stagedValues.Clear();

            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int index =
                        z * resolution + x;

                    if (resolved[index] ||
                        exteriorMask[index] != 0)
                    {
                        continue;
                    }

                    float sum = 0f;
                    int count = 0;

                    AccumulateResolvedNeighbour(
                        x - 1,
                        z,
                        resolution,
                        heights,
                        resolved,
                        ref sum,
                        ref count);

                    AccumulateResolvedNeighbour(
                        x + 1,
                        z,
                        resolution,
                        heights,
                        resolved,
                        ref sum,
                        ref count);

                    AccumulateResolvedNeighbour(
                        x,
                        z - 1,
                        resolution,
                        heights,
                        resolved,
                        ref sum,
                        ref count);

                    AccumulateResolvedNeighbour(
                        x,
                        z + 1,
                        resolution,
                        heights,
                        resolved,
                        ref sum,
                        ref count);

                    if (count == 0)
                    {
                        continue;
                    }

                    stagedIndices.Add(index);
                    stagedValues.Add(sum / count);
                }
            }

            if (stagedIndices.Count == 0)
            {
                throw new InvalidOperationException(
                    "Some enclosed heightmap holes could not be " +
                    "reached from valid terrain samples.");
            }

            for (int index = 0;
                 index < stagedIndices.Count;
                 index++)
            {
                int targetIndex =
                    stagedIndices[index];

                heights[targetIndex] =
                    stagedValues[index];

                resolved[targetIndex] = true;
                interpolatedMask[targetIndex] = 1;
            }

            remaining -= stagedIndices.Count;
        }

        return initialRemaining;
    }

    private static void AccumulateResolvedNeighbour(
        int x,
        int z,
        int resolution,
        float[] heights,
        bool[] resolved,
        ref float sum,
        ref int count)
    {
        if (x < 0 ||
            x >= resolution ||
            z < 0 ||
            z >= resolution)
        {
            return;
        }

        int index =
            z * resolution + x;

        if (!resolved[index])
        {
            return;
        }

        sum += heights[index];
        count++;
    }

    // ---------------------------------------------------------------------
    // Stage 3: Smoothing
    // ---------------------------------------------------------------------

    private void SmoothHeightmap()
    {
        if (heightmapData == null ||
            !heightmapData.HasRepairedData)
        {
            EditorUtility.DisplayDialog(
                "Missing Repaired Heightmap",
                "Run the repair stage before smoothing.",
                "OK");

            return;
        }

        smoothingIterations =
            Mathf.Max(1, smoothingIterations);

        smoothingStrength =
            Mathf.Clamp01(smoothingStrength);

        try
        {
            float[] smoothed =
                SmoothInteriorHeights(
                    heightmapData.repairedHeights,
                    heightmapData.exteriorMask,
                    heightmapData.resolution,
                    smoothingIterations,
                    smoothingStrength,
                    useDiagonalNeighbours);

            heightmapData.smoothedHeights = smoothed;
            heightmapData.hasSmoothing = true;
            heightmapData.smoothingIterations =
                smoothingIterations;
            heightmapData.smoothingStrength =
                smoothingStrength;
            heightmapData.smoothingUsesDiagonals =
                useDiagonalNeighbours;

            ResetClearanceData(heightmapData);

            EditorUtility.SetDirty(heightmapData);
            AssetDatabase.SaveAssets();

            WriteSmoothingPreview(heightmapData);

            AssetDatabase.Refresh();

            Debug.Log(
                $"Smoothed '{heightmapData.name}'.\n" +
                $"Iterations: {smoothingIterations}\n" +
                $"Strength: {smoothingStrength:F2}\n" +
                $"Neighbourhood: " +
                $"{(useDiagonalNeighbours ? "8-neighbour" : "4-neighbour")}",
                heightmapData);
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning(
                "Heightmap smoothing was cancelled.");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static float[] SmoothInteriorHeights(
        float[] sourceHeights,
        byte[] exteriorMask,
        int resolution,
        int iterations,
        float strength,
        bool includeDiagonals)
    {
        float[] current =
            (float[])sourceHeights.Clone();

        float[] next =
            new float[sourceHeights.Length];

        int[] cardinalDeltaX = { -1, 1, 0, 0 };
        int[] cardinalDeltaZ = { 0, 0, -1, 1 };

        int[] diagonalDeltaX = { -1, 1, -1, 1 };
        int[] diagonalDeltaZ = { -1, -1, 1, 1 };

        for (int iteration = 0;
             iteration < iterations;
             iteration++)
        {
            if (EditorUtility.DisplayCancelableProgressBar(
                    "Smoothing repaired heightmap",
                    $"Iteration {iteration + 1} of {iterations}",
                    iteration / (float)iterations))
            {
                throw new OperationCanceledException();
            }

            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int index =
                        z * resolution + x;

                    if (exteriorMask[index] != 0)
                    {
                        next[index] = current[index];
                        continue;
                    }

                    float sum = 0f;
                    int count = 0;

                    AccumulateInteriorNeighbour(
                        current,
                        exteriorMask,
                        resolution,
                        x,
                        z,
                        cardinalDeltaX,
                        cardinalDeltaZ,
                        ref sum,
                        ref count);

                    if (includeDiagonals)
                    {
                        AccumulateInteriorNeighbour(
                            current,
                            exteriorMask,
                            resolution,
                            x,
                            z,
                            diagonalDeltaX,
                            diagonalDeltaZ,
                            ref sum,
                            ref count);
                    }

                    if (count == 0 || strength <= 0f)
                    {
                        next[index] = current[index];
                        continue;
                    }

                    float neighbourAverage =
                        sum / count;

                    next[index] =
                        Mathf.Lerp(
                            current[index],
                            neighbourAverage,
                            strength);
                }
            }

            float[] swap = current;
            current = next;
            next = swap;
        }

        return current;
    }

    private static void AccumulateInteriorNeighbour(
        float[] heights,
        byte[] exteriorMask,
        int resolution,
        int centreX,
        int centreZ,
        int[] deltaX,
        int[] deltaZ,
        ref float sum,
        ref int count)
    {
        for (int direction = 0;
             direction < deltaX.Length;
             direction++)
        {
            int x = centreX + deltaX[direction];
            int z = centreZ + deltaZ[direction];

            if (x < 0 ||
                x >= resolution ||
                z < 0 ||
                z >= resolution)
            {
                continue;
            }

            int index =
                z * resolution + x;

            if (exteriorMask[index] != 0)
            {
                continue;
            }

            sum += heights[index];
            count++;
        }
    }

    // ---------------------------------------------------------------------
    // Stage 4: Surface clearance
    // ---------------------------------------------------------------------

    private void ApplyClearanceTargets()
    {
        if (!IsClearanceInputAvailable())
        {
            EditorUtility.DisplayDialog(
                "Missing Clearance Input",
                "Generate the selected repaired or smoothed heightmap " +
                "before applying surface clearance.",
                "OK");

            return;
        }

        List<GameObject> targets =
            GetUniqueClearanceTargets();

        if (targets.Count == 0)
        {
            ResetClearanceData(heightmapData);
            EditorUtility.SetDirty(heightmapData);
            AssetDatabase.SaveAssets();

            Debug.Log(
                $"Skipped clearance for '{heightmapData.name}' because " +
                "no targets were assigned. Best Available terrain " +
                "creation will fall back to smoothed or repaired heights.",
                heightmapData);

            return;
        }

        float[] sourceHeights =
            clearanceHeightSource == ClearanceHeightSource.Smoothed
                ? heightmapData.smoothedHeights
                : heightmapData.repairedHeights;

        int resolution =
            heightmapData.resolution;

        float[] surfaceCaps =
            new float[heightmapData.CellCount];

        for (int index = 0;
             index < surfaceCaps.Length;
             index++)
        {
            surfaceCaps[index] =
                float.NegativeInfinity;
        }

        HashSet<MeshFilter> processedMeshFilters =
            new HashSet<MeshFilter>();

        List<string> targetPaths =
            new List<string>();

        bool previousBackfaceSetting =
            Physics.queriesHitBackfaces;

        int processedMeshCount = 0;
        int rayHitCount = 0;
        int skippedInactiveMeshCount = 0;

        try
        {
            Physics.queriesHitBackfaces = true;

            for (int targetIndex = 0;
                 targetIndex < targets.Count;
                 targetIndex++)
            {
                GameObject target =
                    targets[targetIndex];

                targetPaths.Add(
                    GetHierarchyPath(target.transform));

                MeshFilter[] meshFilters =
                    target.GetComponentsInChildren<MeshFilter>(true);

                for (int meshIndex = 0;
                     meshIndex < meshFilters.Length;
                     meshIndex++)
                {
                    MeshFilter meshFilter =
                        meshFilters[meshIndex];

                    if (meshFilter == null ||
                        meshFilter.sharedMesh == null ||
                        !processedMeshFilters.Add(meshFilter))
                    {
                        continue;
                    }

                    if (!meshFilter.gameObject.activeInHierarchy)
                    {
                        skippedInactiveMeshCount++;
                        continue;
                    }

                    float progress =
                        (targetIndex +
                         meshIndex /
                         (float)Mathf.Max(1, meshFilters.Length)) /
                        targets.Count;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Applying surface clearance",
                            $"Sampling {target.name}",
                            progress))
                    {
                        throw new OperationCanceledException();
                    }

                    TemporaryMeshColliderState colliderState =
                        PrepareTemporaryMeshCollider(meshFilter);

                    try
                    {
                        Physics.SyncTransforms();

                        rayHitCount +=
                            SampleClearanceCollider(
                                colliderState.collider,
                                sourceHeights,
                                surfaceCaps,
                                heightmapData.exteriorMask,
                                heightmapData,
                                clearanceOffset,
                                maximumClearanceLowering,
                                clearanceRayPadding);

                        processedMeshCount++;
                    }
                    finally
                    {
                        colliderState.Restore();
                    }
                }
            }

            float[] hardClearanceHeights =
                (float[])sourceHeights.Clone();

            byte[] coreMask =
                new byte[heightmapData.CellCount];

            int coreSampleCount = 0;

            for (int index = 0;
                 index < surfaceCaps.Length;
                 index++)
            {
                if (heightmapData.exteriorMask[index] != 0 ||
                    float.IsNegativeInfinity(surfaceCaps[index]))
                {
                    continue;
                }

                float loweredHeight =
                    Mathf.Min(
                        sourceHeights[index],
                        surfaceCaps[index]);

                if (loweredHeight >=
                    sourceHeights[index] - 0.0001f)
                {
                    continue;
                }

                hardClearanceHeights[index] =
                    loweredHeight;

                coreMask[index] = 1;
                coreSampleCount++;
            }

            byte[] featherRegion =
                BuildClearanceFeatherRegion(
                    coreMask,
                    heightmapData.exteriorMask,
                    resolution,
                    heightmapData.sampleSpacingX,
                    heightmapData.sampleSpacingZ,
                    clearanceFeatherDistance);

            float[] clearanceHeights =
                FeatherClearanceTransition(
                    sourceHeights,
                    hardClearanceHeights,
                    coreMask,
                    featherRegion,
                    heightmapData.exteriorMask,
                    resolution,
                    clearanceFeatherIterations,
                    clearanceFeatherStrength,
                    clearanceUsesDiagonalNeighbours);

            byte[] clearanceMask =
                new byte[heightmapData.CellCount];

            float[] clearanceDelta =
                new float[heightmapData.CellCount];

            int adjustedSampleCount = 0;
            float maximumAppliedLowering = 0f;

            for (int index = 0;
                 index < clearanceHeights.Length;
                 index++)
            {
                if (heightmapData.exteriorMask[index] != 0)
                {
                    continue;
                }

                float delta =
                    Mathf.Max(
                        0f,
                        sourceHeights[index] -
                        clearanceHeights[index]);

                clearanceDelta[index] = delta;

                if (delta <= 0.0001f)
                {
                    continue;
                }

                clearanceMask[index] = 1;
                adjustedSampleCount++;
                maximumAppliedLowering =
                    Mathf.Max(
                        maximumAppliedLowering,
                        delta);
            }

            heightmapData.formatVersion =
                Mathf.Max(
                    heightmapData.formatVersion,
                    3);

            heightmapData.hasClearance = true;
            heightmapData.clearanceInputWasSmoothed =
                clearanceHeightSource ==
                ClearanceHeightSource.Smoothed;
            heightmapData.clearanceTargetCount =
                targets.Count;
            heightmapData.clearanceProcessedMeshCount =
                processedMeshCount;
            heightmapData.clearanceTargetHitSampleCount =
                coreSampleCount;
            heightmapData.clearanceAdjustedSampleCount =
                adjustedSampleCount;
            heightmapData.clearanceOffset =
                clearanceOffset;
            heightmapData.maximumClearanceLowering =
                maximumClearanceLowering;
            heightmapData.clearanceFeatherDistance =
                clearanceFeatherDistance;
            heightmapData.clearanceFeatherIterations =
                clearanceFeatherIterations;
            heightmapData.clearanceFeatherStrength =
                clearanceFeatherStrength;
            heightmapData.clearanceUsesDiagonals =
                clearanceUsesDiagonalNeighbours;
            heightmapData.maximumAppliedClearanceLowering =
                maximumAppliedLowering;
            heightmapData.clearanceTargetHierarchyPaths =
                targetPaths.ToArray();
            heightmapData.clearanceHeights =
                clearanceHeights;
            heightmapData.clearanceMask =
                clearanceMask;
            heightmapData.clearanceDelta =
                clearanceDelta;

            EditorUtility.SetDirty(heightmapData);
            AssetDatabase.SaveAssets();

            WriteClearancePreviews(heightmapData);
            AssetDatabase.Refresh();

            Debug.Log(
                $"Applied surface clearance to '{heightmapData.name}'.\n" +
                $"Targets: {targets.Count}\n" +
                $"Processed meshes: {processedMeshCount}\n" +
                $"Direct target samples: {coreSampleCount:N0}\n" +
                $"Adjusted samples after feathering: " +
                $"{adjustedSampleCount:N0}\n" +
                $"Maximum applied lowering: " +
                $"{maximumAppliedLowering:F3}\n" +
                $"Collider ray hits considered: {rayHitCount:N0}\n" +
                $"Inactive target meshes skipped: " +
                $"{skippedInactiveMeshCount:N0}",
                heightmapData);
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning(
                "Surface-clearance generation was cancelled.");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            Physics.queriesHitBackfaces =
                previousBackfaceSetting;

            EditorUtility.ClearProgressBar();
        }
    }

    private List<GameObject> GetUniqueClearanceTargets()
    {
        List<GameObject> result =
            new List<GameObject>();

        if (clearanceTargets == null)
        {
            return result;
        }

        HashSet<GameObject> uniqueTargets =
            new HashSet<GameObject>();

        foreach (GameObject target in clearanceTargets)
        {
            if (target == null ||
                !uniqueTargets.Add(target))
            {
                continue;
            }

            result.Add(target);
        }

        return result;
    }

    private static TemporaryMeshColliderState
        PrepareTemporaryMeshCollider(
            MeshFilter meshFilter)
    {
        GameObject owner =
            meshFilter.gameObject;

        MeshCollider collider =
            owner.GetComponent<MeshCollider>();

        bool addedCollider =
            collider == null;

        if (addedCollider)
        {
            collider =
                owner.AddComponent<MeshCollider>();
        }

        TemporaryMeshColliderState state =
            new TemporaryMeshColliderState(
                collider,
                addedCollider);

        collider.enabled = true;
        collider.convex = false;
        collider.sharedMesh = null;
        collider.sharedMesh =
            meshFilter.sharedMesh;

        return state;
    }

    private static int SampleClearanceCollider(
        MeshCollider collider,
        float[] sourceHeights,
        float[] surfaceCaps,
        byte[] exteriorMask,
        IfcTerrainHeightmapAsset data,
        float clearance,
        float maximumLowering,
        float rayPadding)
    {
        Bounds bounds =
            collider.bounds;

        if (bounds.size.sqrMagnitude <= 0f ||
            bounds.max.x < data.gridMinX ||
            bounds.min.x > data.gridMaxX ||
            bounds.max.z < data.gridMinZ ||
            bounds.min.z > data.gridMaxZ)
        {
            return 0;
        }

        int resolution =
            data.resolution;

        int minimumX =
            WorldCoordinateToMinimumGridIndex(
                bounds.min.x,
                data.gridMinX,
                data.sampleSpacingX,
                resolution);

        int maximumX =
            WorldCoordinateToMaximumGridIndex(
                bounds.max.x,
                data.gridMinX,
                data.sampleSpacingX,
                resolution);

        int minimumZ =
            WorldCoordinateToMinimumGridIndex(
                bounds.min.z,
                data.gridMinZ,
                data.sampleSpacingZ,
                resolution);

        int maximumZ =
            WorldCoordinateToMaximumGridIndex(
                bounds.max.z,
                data.gridMinZ,
                data.sampleSpacingZ,
                resolution);

        float rayTopY =
            bounds.max.y + rayPadding;

        float rayDistance =
            Mathf.Max(
                0.01f,
                bounds.size.y +
                rayPadding * 2f);

        int hitCount = 0;

        for (int z = minimumZ;
             z <= maximumZ;
             z++)
        {
            float worldZ =
                data.gridMinZ +
                z * data.sampleSpacingZ;

            for (int x = minimumX;
                 x <= maximumX;
                 x++)
            {
                int index =
                    z * resolution + x;

                if (exteriorMask[index] != 0)
                {
                    continue;
                }

                float worldX =
                    data.gridMinX +
                    x * data.sampleSpacingX;

                Ray ray =
                    new Ray(
                        new Vector3(
                            worldX,
                            rayTopY,
                            worldZ),
                        Vector3.down);

                if (!collider.Raycast(
                        ray,
                        out RaycastHit hit,
                        rayDistance))
                {
                    continue;
                }

                float proposedCap =
                    hit.point.y - clearance;

                float lowering =
                    sourceHeights[index] -
                    proposedCap;

                if (lowering <= 0.0001f ||
                    lowering > maximumLowering)
                {
                    continue;
                }

                // When target surfaces overlap, retain the highest valid
                // surface below the terrain. This avoids choosing an
                // underside or a lower duplicate.
                surfaceCaps[index] =
                    float.IsNegativeInfinity(
                        surfaceCaps[index])
                        ? proposedCap
                        : Mathf.Max(
                            surfaceCaps[index],
                            proposedCap);

                hitCount++;
            }
        }

        return hitCount;
    }

    private static int WorldCoordinateToMinimumGridIndex(
        float worldCoordinate,
        float gridMinimum,
        float sampleSpacing,
        int resolution)
    {
        return Mathf.Clamp(
            Mathf.FloorToInt(
                (worldCoordinate - gridMinimum) /
                Mathf.Max(0.000001f, sampleSpacing)) - 1,
            0,
            resolution - 1);
    }

    private static int WorldCoordinateToMaximumGridIndex(
        float worldCoordinate,
        float gridMinimum,
        float sampleSpacing,
        int resolution)
    {
        return Mathf.Clamp(
            Mathf.CeilToInt(
                (worldCoordinate - gridMinimum) /
                Mathf.Max(0.000001f, sampleSpacing)) + 1,
            0,
            resolution - 1);
    }

    private static byte[] BuildClearanceFeatherRegion(
        byte[] coreMask,
        byte[] exteriorMask,
        int resolution,
        float spacingX,
        float spacingZ,
        float featherDistance)
    {
        byte[] region =
            (byte[])coreMask.Clone();

        if (featherDistance <= 0f)
        {
            return region;
        }

        int radiusX =
            Mathf.CeilToInt(
                featherDistance /
                Mathf.Max(0.000001f, spacingX));

        int radiusZ =
            Mathf.CeilToInt(
                featherDistance /
                Mathf.Max(0.000001f, spacingZ));

        List<Vector2Int> offsets =
            new List<Vector2Int>();

        float distanceSquared =
            featherDistance * featherDistance;

        for (int offsetZ = -radiusZ;
             offsetZ <= radiusZ;
             offsetZ++)
        {
            for (int offsetX = -radiusX;
                 offsetX <= radiusX;
                 offsetX++)
            {
                float worldOffsetX =
                    offsetX * spacingX;

                float worldOffsetZ =
                    offsetZ * spacingZ;

                if (worldOffsetX * worldOffsetX +
                    worldOffsetZ * worldOffsetZ <=
                    distanceSquared)
                {
                    offsets.Add(
                        new Vector2Int(
                            offsetX,
                            offsetZ));
                }
            }
        }

        for (int z = 0;
             z < resolution;
             z++)
        {
            for (int x = 0;
                 x < resolution;
                 x++)
            {
                int index =
                    z * resolution + x;

                if (coreMask[index] == 0)
                {
                    continue;
                }

                foreach (Vector2Int offset in offsets)
                {
                    int targetX =
                        x + offset.x;

                    int targetZ =
                        z + offset.y;

                    if (targetX < 0 ||
                        targetX >= resolution ||
                        targetZ < 0 ||
                        targetZ >= resolution)
                    {
                        continue;
                    }

                    int targetIndex =
                        targetZ * resolution +
                        targetX;

                    if (exteriorMask[targetIndex] == 0)
                    {
                        region[targetIndex] = 1;
                    }
                }
            }
        }

        return region;
    }

    private static float[] FeatherClearanceTransition(
        float[] sourceHeights,
        float[] hardClearanceHeights,
        byte[] coreMask,
        byte[] featherRegion,
        byte[] exteriorMask,
        int resolution,
        int iterations,
        float strength,
        bool includeDiagonals)
    {
        float[] current =
            (float[])hardClearanceHeights.Clone();

        if (iterations <= 0 ||
            strength <= 0f)
        {
            return current;
        }

        strength =
            Mathf.Clamp01(strength);

        float[] next =
            new float[current.Length];

        int[] cardinalDeltaX = { -1, 1, 0, 0 };
        int[] cardinalDeltaZ = { 0, 0, -1, 1 };

        int[] diagonalDeltaX = { -1, 1, -1, 1 };
        int[] diagonalDeltaZ = { -1, -1, 1, 1 };

        for (int iteration = 0;
             iteration < iterations;
             iteration++)
        {
            EditorUtility.DisplayProgressBar(
                "Feathering clearance transition",
                $"Iteration {iteration + 1} of {iterations}",
                iteration / (float)iterations);

            Array.Copy(
                current,
                next,
                current.Length);

            for (int z = 0;
                 z < resolution;
                 z++)
            {
                for (int x = 0;
                     x < resolution;
                     x++)
                {
                    int index =
                        z * resolution + x;

                    if (exteriorMask[index] != 0 ||
                        featherRegion[index] == 0)
                    {
                        continue;
                    }

                    if (coreMask[index] != 0)
                    {
                        next[index] =
                            hardClearanceHeights[index];
                        continue;
                    }

                    float sum = 0f;
                    int count = 0;

                    AccumulateInteriorNeighbour(
                        current,
                        exteriorMask,
                        resolution,
                        x,
                        z,
                        cardinalDeltaX,
                        cardinalDeltaZ,
                        ref sum,
                        ref count);

                    if (includeDiagonals)
                    {
                        AccumulateInteriorNeighbour(
                            current,
                            exteriorMask,
                            resolution,
                            x,
                            z,
                            diagonalDeltaX,
                            diagonalDeltaZ,
                            ref sum,
                            ref count);
                    }

                    if (count == 0)
                    {
                        continue;
                    }

                    float candidate =
                        Mathf.Lerp(
                            current[index],
                            sum / count,
                            strength);

                    // The clearance stage may only lower terrain. It must
                    // never raise a sample above the selected input map.
                    next[index] =
                        Mathf.Min(
                            sourceHeights[index],
                            candidate);
                }
            }

            // Reapply exact target caps after every smoothing pass.
            for (int index = 0;
                 index < coreMask.Length;
                 index++)
            {
                if (coreMask[index] != 0)
                {
                    next[index] =
                        hardClearanceHeights[index];
                }
            }

            float[] swap = current;
            current = next;
            next = swap;
        }

        return current;
    }

    private static void ResetClearanceData(
        IfcTerrainHeightmapAsset data)
    {
        data.hasClearance = false;
        data.clearanceInputWasSmoothed = false;
        data.clearanceTargetCount = 0;
        data.clearanceProcessedMeshCount = 0;
        data.clearanceTargetHitSampleCount = 0;
        data.clearanceAdjustedSampleCount = 0;
        data.clearanceOffset = 0f;
        data.maximumClearanceLowering = 0f;
        data.clearanceFeatherDistance = 0f;
        data.clearanceFeatherIterations = 0;
        data.clearanceFeatherStrength = 0f;
        data.clearanceUsesDiagonals = false;
        data.maximumAppliedClearanceLowering = 0f;
        data.clearanceTargetHierarchyPaths = null;
        data.clearanceHeights = null;
        data.clearanceMask = null;
        data.clearanceDelta = null;
    }

    private sealed class TemporaryMeshColliderState
    {
        public readonly MeshCollider collider;

        private readonly bool addedCollider;
        private readonly bool previousEnabled;
        private readonly bool previousConvex;
        private readonly Mesh previousSharedMesh;

        public TemporaryMeshColliderState(
            MeshCollider collider,
            bool addedCollider)
        {
            this.collider = collider;
            this.addedCollider = addedCollider;

            previousEnabled = collider.enabled;
            previousConvex = collider.convex;
            previousSharedMesh = collider.sharedMesh;
        }

        public void Restore()
        {
            if (collider == null)
            {
                return;
            }

            if (addedCollider)
            {
                UnityEngine.Object.DestroyImmediate(collider);
                return;
            }

            collider.sharedMesh = null;
            collider.sharedMesh = previousSharedMesh;
            collider.convex = previousConvex;
            collider.enabled = previousEnabled;
        }
    }

    // ---------------------------------------------------------------------
    // Stage 5: Unity Terrain creation
    // ---------------------------------------------------------------------

    private void CreateUnityTerrain()
    {
        if (!TryResolveTerrainHeightSource(
                out float[] selectedHeights,
                out string sourceSuffix,
                out TerrainHeightSource resolvedSource))
        {
            EditorUtility.DisplayDialog(
                "Missing Heightmap Source",
                BuildMissingTerrainSourceMessage(),
                "OK");

            return;
        }

        EnsureAssetFolderExists(outputFolder);

        try
        {
            int resolution =
                heightmapData.resolution;

            float[] terrainWorldHeights =
                FillExteriorSamplesForTerrain(
                    selectedHeights,
                    heightmapData.exteriorMask,
                    resolution);

            FindInteriorHeightRange(
                terrainWorldHeights,
                heightmapData.exteriorMask,
                out float minimumY,
                out float maximumY);

            float terrainBaseY =
                minimumY - terrainBottomPadding;

            float terrainVerticalSize =
                Mathf.Max(
                    1f,
                    maximumY -
                    minimumY +
                    terrainBottomPadding +
                    terrainTopPadding);

            float[,] normalizedHeights =
                new float[
                    resolution,
                    resolution];

            for (int z = 0;
                 z < resolution;
                 z++)
            {
                for (int x = 0;
                     x < resolution;
                     x++)
                {
                    int index =
                        z * resolution + x;

                    normalizedHeights[z, x] =
                        Mathf.Clamp01(
                            (terrainWorldHeights[index] -
                             terrainBaseY) /
                            terrainVerticalSize);
                }
            }

            TerrainData terrainData =
                new TerrainData();

            terrainData.heightmapResolution =
                resolution;

            terrainData.size =
                new Vector3(
                    heightmapData.gridMaxX -
                    heightmapData.gridMinX,
                    terrainVerticalSize,
                    heightmapData.gridMaxZ -
                    heightmapData.gridMinZ);

            terrainData.name =
                $"{heightmapData.datasetName}_" +
                $"{sourceSuffix}_TerrainData";

            terrainData.SetHeights(
                0,
                0,
                normalizedHeights);

            if (createExteriorHoles)
            {
                bool[,] surfaceMask =
                    BuildTerrainSurfaceMask(
                        heightmapData.exteriorMask,
                        resolution,
                        terrainData.holesResolution);

                terrainData.SetHoles(
                    0,
                    0,
                    surfaceMask);
            }

            string safeName =
                SanitizeFileName(
                    $"{heightmapData.datasetName}_" +
                    $"{sourceSuffix}_Terrain");

            string terrainDataPath =
                AssetDatabase.GenerateUniqueAssetPath(
                    $"{NormalizeAssetFolder(outputFolder)}/" +
                    $"{safeName}_Data.asset");

            AssetDatabase.CreateAsset(
                terrainData,
                terrainDataPath);

            GameObject terrainObject =
                Terrain.CreateTerrainGameObject(
                    terrainData);

            terrainObject.name = safeName;

            terrainObject.transform.position =
                new Vector3(
                    heightmapData.gridMinX,
                    terrainBaseY,
                    heightmapData.gridMinZ);

            terrainObject.transform.rotation =
                Quaternion.identity;

            terrainObject.transform.localScale =
                Vector3.one;

            if (outputParent != null)
            {
                terrainObject.transform.SetParent(
                    outputParent,
                    true);
            }

            Terrain terrain =
                terrainObject.GetComponent<Terrain>();

            if (terrain != null)
            {
                // Disabled because the Linux OpenGL editor's Terrain brush
                // preview can report shader-instancing errors on hover.
                terrain.drawInstanced = false;
            }

            IfcGeneratedTerrainReference reference =
                terrainObject.AddComponent<
                    IfcGeneratedTerrainReference>();

            reference.heightmapData =
                heightmapData;

            reference.sourceTerrainObject =
                sourceObject;

            EditorUtility.SetDirty(terrainData);
            EditorUtility.SetDirty(terrainObject);

            EditorSceneManager.MarkSceneDirty(
                terrainObject.scene);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeGameObject =
                terrainObject;

            EditorGUIUtility.PingObject(
                terrainData);

            Debug.Log(
                $"Created Unity Terrain '{terrainObject.name}'.\n" +
                $"Requested source: {terrainHeightSource}\n" +
                $"Resolved source: {resolvedSource}\n" +
                $"TerrainData: {terrainDataPath}\n" +
                $"Position: {terrainObject.transform.position}\n" +
                $"Size: {terrainData.size}",
                terrainObject);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>
    /// Unity requires height values even beneath cells that will be hidden
    /// as holes. Exterior samples receive the height of their nearest
    /// resolved neighbour.
    /// </summary>
    private static float[] FillExteriorSamplesForTerrain(
        float[] repairedHeights,
        byte[] exteriorMask,
        int resolution)
    {
        float[] result =
            (float[])repairedHeights.Clone();

        bool[] assigned =
            new bool[result.Length];

        Queue<int> queue =
            new Queue<int>();

        for (int index = 0;
             index < result.Length;
             index++)
        {
            if (exteriorMask[index] != 0)
            {
                continue;
            }

            assigned[index] = true;
            queue.Enqueue(index);
        }

        if (queue.Count == 0)
        {
            throw new InvalidOperationException(
                "The heightmap contains no interior terrain samples.");
        }

        int[] deltaX = { -1, 1, 0, 0 };
        int[] deltaZ = { 0, 0, -1, 1 };

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();

            int x = index % resolution;
            int z = index / resolution;

            for (int direction = 0;
                 direction < 4;
                 direction++)
            {
                int neighbourX =
                    x + deltaX[direction];

                int neighbourZ =
                    z + deltaZ[direction];

                if (neighbourX < 0 ||
                    neighbourX >= resolution ||
                    neighbourZ < 0 ||
                    neighbourZ >= resolution)
                {
                    continue;
                }

                int neighbourIndex =
                    neighbourZ *
                    resolution +
                    neighbourX;

                if (assigned[neighbourIndex])
                {
                    continue;
                }

                result[neighbourIndex] =
                    result[index];

                assigned[neighbourIndex] = true;

                queue.Enqueue(neighbourIndex);
            }
        }

        return result;
    }

    private static void FindInteriorHeightRange(
        float[] heights,
        byte[] exteriorMask,
        out float minimumY,
        out float maximumY)
    {
        minimumY = float.PositiveInfinity;
        maximumY = float.NegativeInfinity;

        for (int index = 0;
             index < heights.Length;
             index++)
        {
            if (exteriorMask[index] != 0)
            {
                continue;
            }

            minimumY =
                Mathf.Min(
                    minimumY,
                    heights[index]);

            maximumY =
                Mathf.Max(
                    maximumY,
                    heights[index]);
        }

        if (float.IsInfinity(minimumY) ||
            float.IsInfinity(maximumY))
        {
            throw new InvalidOperationException(
                "Unable to determine the repaired terrain height range.");
        }
    }

    /// <summary>
    /// Unity uses true for visible surface and false for a Terrain hole.
    ///
    /// A Terrain cell becomes a hole only when all four corresponding
    /// heightmap corners are exterior. This avoids cutting too aggressively
    /// along the footprint boundary.
    /// </summary>
    private static bool[,] BuildTerrainSurfaceMask(
        byte[] exteriorMask,
        int heightResolution,
        int holesResolution)
    {
        bool[,] surfaceMask =
            new bool[
                holesResolution,
                holesResolution];

        for (int holeZ = 0;
             holeZ < holesResolution;
             holeZ++)
        {
            float normalizedZ0 =
                holeZ /
                (float)holesResolution;

            float normalizedZ1 =
                (holeZ + 1f) /
                holesResolution;

            int z0 =
                Mathf.Clamp(
                    Mathf.RoundToInt(
                        normalizedZ0 *
                        (heightResolution - 1)),
                    0,
                    heightResolution - 1);

            int z1 =
                Mathf.Clamp(
                    Mathf.RoundToInt(
                        normalizedZ1 *
                        (heightResolution - 1)),
                    0,
                    heightResolution - 1);

            for (int holeX = 0;
                 holeX < holesResolution;
                 holeX++)
            {
                float normalizedX0 =
                    holeX /
                    (float)holesResolution;

                float normalizedX1 =
                    (holeX + 1f) /
                    holesResolution;

                int x0 =
                    Mathf.Clamp(
                        Mathf.RoundToInt(
                            normalizedX0 *
                            (heightResolution - 1)),
                        0,
                        heightResolution - 1);

                int x1 =
                    Mathf.Clamp(
                        Mathf.RoundToInt(
                            normalizedX1 *
                            (heightResolution - 1)),
                        0,
                        heightResolution - 1);

                bool fullyExterior =
                    IsExterior(
                        exteriorMask,
                        heightResolution,
                        x0,
                        z0) &&
                    IsExterior(
                        exteriorMask,
                        heightResolution,
                        x1,
                        z0) &&
                    IsExterior(
                        exteriorMask,
                        heightResolution,
                        x0,
                        z1) &&
                    IsExterior(
                        exteriorMask,
                        heightResolution,
                        x1,
                        z1);

                surfaceMask[holeZ, holeX] =
                    !fullyExterior;
            }
        }

        return surfaceMask;
    }

    private static bool IsExterior(
        byte[] exteriorMask,
        int resolution,
        int x,
        int z)
    {
        return exteriorMask[
                   z * resolution + x] != 0;
    }

    // ---------------------------------------------------------------------
    // Diagnostic previews
    // ---------------------------------------------------------------------

    private static void WriteRawPreviews(
        IfcTerrainHeightmapAsset data)
    {
        string assetPath =
            AssetDatabase.GetAssetPath(data);

        string directory =
            NormalizeAssetFolder(
                Path.GetDirectoryName(assetPath));

        string baseName =
            Path.GetFileNameWithoutExtension(
                assetPath);

        WriteHeightPreview(
            $"{directory}/{baseName}_RawHeight.png",
            data.rawHeights,
            data.validMask,
            data.resolution,
            data.minimumSampledWorldY,
            data.maximumSampledWorldY);

        WriteMaskPreview(
            $"{directory}/{baseName}_Validity.png",
            data.validMask,
            data.resolution);
    }

    private static void WriteRepairPreviews(
        IfcTerrainHeightmapAsset data)
    {
        string assetPath =
            AssetDatabase.GetAssetPath(data);

        string directory =
            NormalizeAssetFolder(
                Path.GetDirectoryName(assetPath));

        string baseName =
            Path.GetFileNameWithoutExtension(
                assetPath);

        byte[] interiorMask =
            BuildInteriorMask(data);

        WriteHeightPreview(
            $"{directory}/{baseName}_RepairedHeight.png",
            data.repairedHeights,
            interiorMask,
            data.resolution,
            data.minimumSampledWorldY,
            data.maximumSampledWorldY);

        WriteMaskPreview(
            $"{directory}/{baseName}_ExteriorMask.png",
            data.exteriorMask,
            data.resolution);

        WriteMaskPreview(
            $"{directory}/{baseName}_InterpolatedMask.png",
            data.interpolatedMask,
            data.resolution);
    }

    private static void WriteSmoothingPreview(
        IfcTerrainHeightmapAsset data)
    {
        string assetPath =
            AssetDatabase.GetAssetPath(data);

        string directory =
            NormalizeAssetFolder(
                Path.GetDirectoryName(assetPath));

        string baseName =
            Path.GetFileNameWithoutExtension(
                assetPath);

        byte[] interiorMask =
            BuildInteriorMask(data);

        WriteHeightPreview(
            $"{directory}/{baseName}_SmoothedHeight.png",
            data.smoothedHeights,
            interiorMask,
            data.resolution,
            data.minimumSampledWorldY,
            data.maximumSampledWorldY);
    }

    private static void WriteClearancePreviews(
        IfcTerrainHeightmapAsset data)
    {
        string assetPath =
            AssetDatabase.GetAssetPath(data);

        string directory =
            NormalizeAssetFolder(
                Path.GetDirectoryName(assetPath));

        string baseName =
            Path.GetFileNameWithoutExtension(
                assetPath);

        byte[] interiorMask =
            BuildInteriorMask(data);

        FindInteriorHeightRange(
            data.clearanceHeights,
            data.exteriorMask,
            out float minimumY,
            out float maximumY);

        WriteHeightPreview(
            $"{directory}/{baseName}_ClearanceHeight.png",
            data.clearanceHeights,
            interiorMask,
            data.resolution,
            minimumY,
            maximumY);

        WriteMaskPreview(
            $"{directory}/{baseName}_ClearanceMask.png",
            data.clearanceMask,
            data.resolution);

        WriteHeightPreview(
            $"{directory}/{baseName}_ClearanceDelta.png",
            data.clearanceDelta,
            data.clearanceMask,
            data.resolution,
            0f,
            Mathf.Max(
                0.0001f,
                data.maximumAppliedClearanceLowering));
    }

    private static byte[] BuildInteriorMask(
        IfcTerrainHeightmapAsset data)
    {
        byte[] interiorMask =
            new byte[data.CellCount];

        for (int index = 0;
             index < interiorMask.Length;
             index++)
        {
            interiorMask[index] =
                data.exteriorMask[index] == 0
                    ? (byte)1
                    : (byte)0;
        }

        return interiorMask;
    }

    private static void WriteHeightPreview(
        string assetPath,
        float[] heights,
        byte[] visibleMask,
        int resolution,
        float minimumY,
        float maximumY)
    {
        Texture2D texture =
            new Texture2D(
                resolution,
                resolution,
                TextureFormat.RGBA32,
                false,
                true);

        Color32[] pixels =
            new Color32[
                resolution * resolution];

        float range =
            Mathf.Max(
                0.0001f,
                maximumY - minimumY);

        for (int index = 0;
             index < pixels.Length;
             index++)
        {
            if (visibleMask[index] == 0)
            {
                pixels[index] =
                    new Color32(
                        0,
                        0,
                        0,
                        255);

                continue;
            }

            float normalized =
                Mathf.Clamp01(
                    (heights[index] - minimumY) /
                    range);

            byte value =
                (byte)Mathf.RoundToInt(
                    normalized * 255f);

            pixels[index] =
                new Color32(
                    value,
                    value,
                    value,
                    255);
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);

        WriteTextureAsset(
            assetPath,
            texture);

        DestroyImmediate(texture);
    }

    private static void WriteMaskPreview(
        string assetPath,
        byte[] mask,
        int resolution)
    {
        Texture2D texture =
            new Texture2D(
                resolution,
                resolution,
                TextureFormat.RGBA32,
                false,
                true);

        Color32[] pixels =
            new Color32[
                resolution * resolution];

        for (int index = 0;
             index < pixels.Length;
             index++)
        {
            byte value =
                mask[index] != 0
                    ? (byte)255
                    : (byte)0;

            pixels[index] =
                new Color32(
                    value,
                    value,
                    value,
                    255);
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);

        WriteTextureAsset(
            assetPath,
            texture);

        DestroyImmediate(texture);
    }

    private static void WriteTextureAsset(
        string assetPath,
        Texture2D texture)
    {
        string absolutePath =
            Path.GetFullPath(
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    assetPath));

        File.WriteAllBytes(
            absolutePath,
            texture.EncodeToPNG());

        AssetDatabase.ImportAsset(
            assetPath,
            ImportAssetOptions.ForceUpdate);
    }

    // ---------------------------------------------------------------------
    // Validation and utility methods
    // ---------------------------------------------------------------------

    private bool TryValidateSource(
        out MeshFilter meshFilter,
        out MeshRenderer meshRenderer)
    {
        meshFilter = null;
        meshRenderer = null;

        if (sourceObject == null)
        {
            EditorUtility.DisplayDialog(
                "Missing Source",
                "Select the IFC terrain mesh object.",
                "OK");

            return false;
        }

        if (!sourceObject.activeInHierarchy)
        {
            EditorUtility.DisplayDialog(
                "Inactive Source",
                "The source object must be active while it is sampled.",
                "OK");

            return false;
        }

        meshFilter =
            sourceObject.GetComponent<MeshFilter>();

        meshRenderer =
            sourceObject.GetComponent<MeshRenderer>();

        if (meshFilter == null ||
            meshFilter.sharedMesh == null ||
            meshRenderer == null)
        {
            EditorUtility.DisplayDialog(
                "Invalid Source",
                "The selected object must contain a MeshFilter, a " +
                "MeshRenderer and a valid shared mesh.",
                "OK");

            return false;
        }

        if (string.IsNullOrWhiteSpace(outputFolder) ||
            !NormalizeAssetFolder(outputFolder)
                .StartsWith("Assets"))
        {
            EditorUtility.DisplayDialog(
                "Invalid Output Folder",
                "The output folder must be inside Assets.",
                "OK");

            return false;
        }

        return true;
    }

    private static string GetHierarchyPath(
        Transform target)
    {
        string path = target.name;
        Transform current = target.parent;

        while (current != null)
        {
            path = $"{current.name}/{path}";
            current = current.parent;
        }

        return path;
    }

    private static void EnsureAssetFolderExists(
        string requestedFolder)
    {
        string normalized =
            NormalizeAssetFolder(
                requestedFolder);

        string[] parts =
            normalized.Split('/');

        if (parts.Length == 0 ||
            parts[0] != "Assets")
        {
            throw new ArgumentException(
                "The generated output folder must be inside Assets.");
        }

        string current = "Assets";

        for (int index = 1;
             index < parts.Length;
             index++)
        {
            string next =
                $"{current}/{parts[index]}";

            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(
                    current,
                    parts[index]);
            }

            current = next;
        }
    }

    private static string NormalizeAssetFolder(
        string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "Assets";
        }

        return folder
            .Replace("\\", "/")
            .TrimEnd('/');
    }

    private static string SanitizeFileName(
        string proposedName)
    {
        string result =
            string.IsNullOrWhiteSpace(proposedName)
                ? "GeneratedTerrain"
                : proposedName.Trim();

        foreach (char invalidCharacter
                 in Path.GetInvalidFileNameChars())
        {
            result =
                result.Replace(
                    invalidCharacter,
                    '_');
        }

        return result
            .Replace('/', '_')
            .Replace('\\', '_');
    }
}
