#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using RoofFacet = RoofFacetDetector.Facet;
using RoofTriangle = RoofFacetDetector.Triangle;
using Triangle2D = RoofFacetDetector.Triangle2D;
using FacetProjection = RoofFacetDetector.Projection;
using OrientationMode = RoofFacetDetector.OrientationMode;

public sealed class SolarPanelRoofGeneratorV4Window : EditorWindow
{
    private enum LayoutMode { SingleGrid, RepeatedGroups }
    private enum GridMode { AutoFit, Manual }

    private struct PanelBounds
    {
        public bool valid;
        public Bounds bounds;
    }

    private struct Placement
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 u, v, normal;
    }

    private sealed class GroupPlacement
    {
        public int facetIndex;
        public int groupRow;
        public int groupColumn;
        public Vector3 center;
        public Quaternion rotation;
        public Vector3 u, v, normal;
        public float width, length;
        public readonly List<Placement> panels = new List<Placement>();
    }

    private sealed class FacetPreviewCache
    {
        public FacetProjection projection;
        public readonly List<GroupPlacement> groups =
            new List<GroupPlacement>();
        public int panelCount;
        public Vector3[] boundaryLines = Array.Empty<Vector3>();
        public Vector3[] groupLines = Array.Empty<Vector3>();
        public Vector3[] panelLines = Array.Empty<Vector3>();
    }

    [SerializeField] private GameObject siteRoot;
    [SerializeField] private GameObject buildingRoot;
    [SerializeField] private GameObject panelPrefab;

    [SerializeField] private string solarPanelsRootName = "roof_panels";
    [SerializeField] private string buildingFolderName = "";
    [SerializeField] private string groupNamePrefix = "group";
    [SerializeField] private string panelNamePrefix = "panel";
    [SerializeField] private bool replaceExisting = true;

    [SerializeField, Range(0f, 0.95f)] private float minimumRelativeHeight = 0.55f;
    [SerializeField, Min(0.1f)] private float roofSearchDepth = 6f;
    [SerializeField, Range(1f, 80f)] private float maximumRoofSlope = 55f;
    [SerializeField, Range(0.1f, 20f)] private float normalMergeTolerance = 4f;
    [SerializeField, Min(0.001f)] private float planeMergeTolerance = 0.18f;
    [SerializeField, Min(0.001f)] private float minimumTriangleArea = 0.02f;
    [SerializeField, Min(0.01f)] private float minimumFacetArea = 2f;

    [SerializeField] private LayoutMode layoutMode = LayoutMode.RepeatedGroups;

    [SerializeField] private GridMode gridMode = GridMode.AutoFit;
    [SerializeField, Min(1)] private int manualColumns = 8;
    [SerializeField, Min(1)] private int manualRows = 2;

    [SerializeField, Min(1)] private int panelsPerGroupColumns = 4;
    [SerializeField, Min(1)] private int panelsPerGroupRows = 2;
    [SerializeField] private GridMode groupCountMode = GridMode.AutoFit;
    [SerializeField, Min(1)] private int manualGroupColumns = 2;
    [SerializeField, Min(1)] private int manualGroupRows = 1;
    [SerializeField, Min(0)] private int maximumGroupsPerFacet = 0;
    [SerializeField, Min(0f)] private float groupColumnSpacing = 1.5f;
    [SerializeField, Min(0f)] private float groupRowSpacing = 1.5f;
    [SerializeField] private bool requireCompleteGroups = true;
    [SerializeField, Min(0)] private int maximumPanelsPerFacet = 0;
    [SerializeField, Min(0f)] private float edgePadding = 0.3f;
    [SerializeField, Min(0f)] private float columnSpacing = 0.12f;
    [SerializeField, Min(0f)] private float rowSpacing = 0.12f;
    [SerializeField] private Vector2 gridOffset;

    [SerializeField] private OrientationMode orientationMode =
        OrientationMode.AutoBestFit;

    [SerializeField, Range(-180f, 180f)]
    private float gridRotation;

    [SerializeField]
    private bool snapPrincipalAxisToBoundary = true;

    [SerializeField, Range(0f, 45f)]
    private float boundarySnapTolerance = 14f;

    [SerializeField, Range(0f, 0.5f)]
    private float pcaAmbiguityThreshold = 0.08f;

    [SerializeField, Range(2, 5)] private int footprintSamples = 3;

    [SerializeField] private bool usePrefabDimensions = true;
    [SerializeField, Min(0.01f)] private float panelWidth = 2f;
    [SerializeField, Min(0.01f)] private float panelLength = 2f;
    [SerializeField, Min(0f)] private float pivotToBottom = 0.25f;
    [SerializeField, Min(0f)] private float roofClearance = 0.08f;
    [SerializeField] private Vector3 prefabRotationOffset;

    [SerializeField] private bool showFacets = true;
    [SerializeField] private bool showLabels = true;
    [SerializeField] private bool showGroupBoundsPreview = true;
    [SerializeField] private bool showGroupLabels;
    [SerializeField] private bool showPanelPreview;

    [SerializeField, Range(50, 5000)]
    private int maximumPreviewPanels = 800;

    private readonly List<RoofFacet> facets = new();
    private readonly List<FacetPreviewCache> previewCache = new();
    private Vector2 scroll;
    private Bounds buildingBounds;
    private int scannedTriangles;
    private int acceptedTriangles;
    private string status = "Assign a building and run roof detection.";
    private string previewStatus = "Preview has not been built.";
    private bool previewDirty = true;
    private bool previewRebuildScheduled;

    [MenuItem("Tools/IFC Dashboard/Solar Panel Roof Generator V4 (Shared Detector)")]
    public static void Open()
    {
        var window = GetWindow<SolarPanelRoofGeneratorV4Window>();
        window.titleContent = new GUIContent("Solar Roof Generator V4");
        window.minSize = new Vector2(440f, 620f);
        window.Show();
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += DrawScenePreview;
        Undo.undoRedoPerformed += OnExternalEditorChange;
        EditorApplication.hierarchyChanged += OnExternalEditorChange;
        autoRepaintOnSceneChange = false;
        SchedulePreviewRebuild();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= DrawScenePreview;
        Undo.undoRedoPerformed -= OnExternalEditorChange;
        EditorApplication.hierarchyChanged -= OnExternalEditorChange;
        EditorApplication.delayCall -= RebuildPreviewCacheDelayed;
    }

    private void OnExternalEditorChange()
    {
        if (buildingRoot != null && facets.Count > 0)
            SchedulePreviewRebuild();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Version 3 adds geometry-based roof orientation, automatic 90-degree packing selection, and a cached Scene-view preview. The gizmos no longer rebuild the full layout every repaint.",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();

        DrawReferences();
        EditorGUILayout.Space(8);
        DrawDetection();
        EditorGUILayout.Space(8);
        DrawFacets();
        EditorGUILayout.Space(8);
        DrawGrid();
        EditorGUILayout.Space(8);
        DrawOutput();

        if (EditorGUI.EndChangeCheck())
            SchedulePreviewRebuild();

        EditorGUILayout.Space(12);
        DrawActions();

        EditorGUILayout.EndScrollView();
    }

    private void DrawReferences()
    {
        EditorGUILayout.LabelField("1. References", EditorStyles.boldLabel);
        siteRoot = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Site Root", "Normally cib_site. Optional if it can be found in the building's ancestors."),
            siteRoot, typeof(GameObject), true);

        GameObject previous = buildingRoot;
        buildingRoot = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Building", "Root GameObject containing the imported building MeshFilters."),
            buildingRoot, typeof(GameObject), true);

        if (previous != buildingRoot)
        {
            facets.Clear();
            previewCache.Clear();
            previewDirty = true;

            if (buildingRoot != null)
            {
                buildingFolderName = buildingRoot.name;
                siteRoot ??= FindLikelySiteRoot(buildingRoot.transform);
            }
        }

        panelPrefab = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Solar Panel Prefab", "Local X = width, local Y = surface normal, local Z = length."),
            panelPrefab, typeof(GameObject), false);

        using (new EditorGUI.DisabledScope(panelPrefab == null))
        {
            if (GUILayout.Button("Read Panel Dimensions From Prefab"))
                ReadPrefabDimensions();
        }
    }

    private void DrawDetection()
    {
        EditorGUILayout.LabelField("2. Roof Detection", EditorStyles.boldLabel);
        minimumRelativeHeight = EditorGUILayout.Slider(
            new GUIContent("Minimum Relative Height", "Rejects triangles below this fraction of the building height."),
            minimumRelativeHeight, 0f, 0.95f);
        roofSearchDepth = Mathf.Max(0.1f, EditorGUILayout.FloatField(
            new GUIContent("Roof Search Depth", "Only searches this far below the building's highest point."), roofSearchDepth));
        maximumRoofSlope = EditorGUILayout.Slider("Maximum Roof Slope", maximumRoofSlope, 1f, 80f);
        normalMergeTolerance = EditorGUILayout.Slider("Normal Merge Tolerance", normalMergeTolerance, 0.1f, 20f);
        planeMergeTolerance = Mathf.Max(0.001f, EditorGUILayout.FloatField("Plane Merge Tolerance", planeMergeTolerance));
        minimumTriangleArea = Mathf.Max(0.001f, EditorGUILayout.FloatField("Minimum Triangle Area", minimumTriangleArea));
        minimumFacetArea = Mathf.Max(0.01f, EditorGUILayout.FloatField("Minimum Facet Area", minimumFacetArea));
        showFacets = EditorGUILayout.Toggle("Show Detected Facets", showFacets);
        showLabels = EditorGUILayout.Toggle("Show Facet Labels", showLabels);

        using (new EditorGUI.DisabledScope(buildingRoot == null))
        {
            if (GUILayout.Button("Detect Roof Facets", GUILayout.Height(30)))
                DetectRoofs();
        }

        EditorGUILayout.HelpBox(status, facets.Count > 0 ? MessageType.None : MessageType.Warning);
    }

    private void DrawFacets()
    {
        EditorGUILayout.LabelField("3. Detected Roof Facets", EditorStyles.boldLabel);
        if (facets.Count == 0)
        {
            EditorGUILayout.LabelField("No detected facets.", EditorStyles.miniLabel);
            return;
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Enable All")) { foreach (var f in facets) f.enabled = true; SceneView.RepaintAll(); }
        if (GUILayout.Button("Disable All")) { foreach (var f in facets) f.enabled = false; SceneView.RepaintAll(); }
        EditorGUILayout.EndHorizontal();

        for (int i = 0; i < facets.Count; i++)
        {
            RoofFacet facet = facets[i];
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            facet.enabled = EditorGUILayout.Toggle(facet.enabled, GUILayout.Width(18));
            EditorGUILayout.LabelField($"Facet {i:00}", EditorStyles.boldLabel, GUILayout.Width(72));
            EditorGUILayout.LabelField($"Area {facet.area:F1} m²", GUILayout.Width(105));
            EditorGUILayout.LabelField($"Slope {facet.Slope:F1}°");
            EditorGUILayout.EndHorizontal();
            FacetPreviewCache cached =
                i < previewCache.Count ? previewCache[i] : null;

            string estimateText = previewDirty
                ? "Preview updating…"
                : cached == null
                    ? "No preview"
                    : $"{cached.groups.Count} groups, {cached.panelCount} panels";

            string orientationText = cached == null
                ? $"PCA confidence {facet.orientationConfidence:F2}"
                : cached.projection.orientationSummary;

            EditorGUILayout.LabelField(
                $"Height {facet.minY:F2}–{facet.maxY:F2} | " +
                $"Normal ({facet.normal.x:F2}, {facet.normal.y:F2}, {facet.normal.z:F2}) | " +
                $"{orientationText} | {estimateText}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }
    }

    private void DrawGrid()
    {
        EditorGUILayout.LabelField("4. Layout, Groups, and Panel Transform", EditorStyles.boldLabel);

        layoutMode = (LayoutMode)EditorGUILayout.EnumPopup(
            new GUIContent(
                "Layout Mode",
                "Single Grid preserves the Version 1 behavior. Repeated Groups creates separate islands of panel grids across each roof facet."),
            layoutMode);

        EditorGUILayout.Space(3);

        if (layoutMode == LayoutMode.SingleGrid)
        {
            EditorGUILayout.LabelField("Single Grid", EditorStyles.miniBoldLabel);
            gridMode = (GridMode)EditorGUILayout.EnumPopup("Grid Count Mode", gridMode);
            if (gridMode == GridMode.Manual)
            {
                manualColumns = Mathf.Max(1, EditorGUILayout.IntField("Columns Per Facet", manualColumns));
                manualRows = Mathf.Max(1, EditorGUILayout.IntField("Rows Per Facet", manualRows));
            }

            maximumPanelsPerFacet = Mathf.Max(0, EditorGUILayout.IntField(
                new GUIContent("Maximum Panels Per Facet", "Zero means unlimited."), maximumPanelsPerFacet));
        }
        else
        {
            EditorGUILayout.LabelField("Panels Inside Each Group", EditorStyles.miniBoldLabel);
            panelsPerGroupColumns = Mathf.Max(1, EditorGUILayout.IntField("Panel Columns", panelsPerGroupColumns));
            panelsPerGroupRows = Mathf.Max(1, EditorGUILayout.IntField("Panel Rows", panelsPerGroupRows));

            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField("Group Distribution Across Roof", EditorStyles.miniBoldLabel);
            groupCountMode = (GridMode)EditorGUILayout.EnumPopup("Group Count Mode", groupCountMode);
            if (groupCountMode == GridMode.Manual)
            {
                manualGroupColumns = Mathf.Max(1, EditorGUILayout.IntField("Group Columns", manualGroupColumns));
                manualGroupRows = Mathf.Max(1, EditorGUILayout.IntField("Group Rows", manualGroupRows));
            }

            maximumGroupsPerFacet = Mathf.Max(0, EditorGUILayout.IntField(
                new GUIContent("Maximum Groups Per Facet", "Zero means unlimited."), maximumGroupsPerFacet));
            groupColumnSpacing = Mathf.Max(0f, EditorGUILayout.FloatField("Horizontal Isle Gap", groupColumnSpacing));
            groupRowSpacing = Mathf.Max(0f, EditorGUILayout.FloatField("Vertical Isle Gap", groupRowSpacing));
            requireCompleteGroups = EditorGUILayout.Toggle(
                new GUIContent(
                    "Require Complete Groups",
                    "When enabled, an island is rejected unless every panel in its row-by-column pattern fits on the roof."),
                requireCompleteGroups);
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Roof Orientation", EditorStyles.miniBoldLabel);
        orientationMode = (OrientationMode)EditorGUILayout.EnumPopup(
            new GUIContent(
                "Orientation Mode",
                "Auto Best Fit uses the roof's geometric principal axis and tests both perpendicular directions, choosing the one that accepts the most panels."),
            orientationMode);

        if (orientationMode == OrientationMode.AutoBestFit ||
            orientationMode == OrientationMode.PrincipalAxis)
        {
            snapPrincipalAxisToBoundary = EditorGUILayout.Toggle(
                new GUIContent(
                    "Snap PCA To Roof Edge",
                    "Snaps the detected principal axis to the longest boundary edge when both directions are sufficiently close."),
                snapPrincipalAxisToBoundary);

            if (snapPrincipalAxisToBoundary)
            {
                boundarySnapTolerance = EditorGUILayout.Slider(
                    "Boundary Snap Tolerance",
                    boundarySnapTolerance,
                    0f,
                    45f);
            }

            pcaAmbiguityThreshold = EditorGUILayout.Slider(
                new GUIContent(
                    "PCA Ambiguity Threshold",
                    "When the roof is nearly square and PCA has no strong direction, the longest boundary edge is used instead."),
                pcaAmbiguityThreshold,
                0f,
                0.5f);
        }

        gridRotation = EditorGUILayout.Slider(
            new GUIContent(
                "Rotation Offset",
                "Manual angle added after automatic orientation. Auto Best Fit also tests the perpendicular orientation."),
            gridRotation,
            -180f,
            180f);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Shared Layout Controls", EditorStyles.miniBoldLabel);
        edgePadding = Mathf.Max(0, EditorGUILayout.FloatField("Roof Edge Padding", edgePadding));
        columnSpacing = Mathf.Max(0, EditorGUILayout.FloatField("Panel Column Spacing", columnSpacing));
        rowSpacing = Mathf.Max(0, EditorGUILayout.FloatField("Panel Row Spacing", rowSpacing));
        gridOffset = EditorGUILayout.Vector2Field("Layout Offset", gridOffset);
        footprintSamples = EditorGUILayout.IntSlider(
            new GUIContent("Footprint Fit Samples", "Higher values more conservatively reject panels crossing roof boundaries."),
            footprintSamples, 2, 5);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Panel Prefab Transform", EditorStyles.miniBoldLabel);
        usePrefabDimensions = EditorGUILayout.Toggle("Use Prefab Dimensions", usePrefabDimensions);
        using (new EditorGUI.DisabledScope(usePrefabDimensions))
        {
            panelWidth = Mathf.Max(0.01f, EditorGUILayout.FloatField("Panel Width (Local X)", panelWidth));
            panelLength = Mathf.Max(0.01f, EditorGUILayout.FloatField("Panel Length (Local Z)", panelLength));
            pivotToBottom = Mathf.Max(0, EditorGUILayout.FloatField("Pivot-To-Bottom Offset", pivotToBottom));
        }
        roofClearance = Mathf.Max(0, EditorGUILayout.FloatField("Roof Clearance", roofClearance));
        prefabRotationOffset = EditorGUILayout.Vector3Field("Prefab Rotation Offset", prefabRotationOffset);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Preview", EditorStyles.miniBoldLabel);
        showGroupBoundsPreview = EditorGUILayout.Toggle("Show Group Bounds", showGroupBoundsPreview);
        showGroupLabels = EditorGUILayout.Toggle("Show Group Labels", showGroupLabels);
        showPanelPreview = EditorGUILayout.Toggle("Show Candidate Panels", showPanelPreview);
        maximumPreviewPanels = EditorGUILayout.IntSlider(
            new GUIContent(
                "Maximum Preview Panels",
                "Limits only the Scene-view lines. Counts and generated panels are not limited."),
            maximumPreviewPanels,
            50,
            5000);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(previewStatus, EditorStyles.miniLabel);
        if (GUILayout.Button("Refresh Preview", GUILayout.Width(120f)))
            RebuildPreviewCacheNow();
        EditorGUILayout.EndHorizontal();
    }

    private void DrawOutput()
    {
        EditorGUILayout.LabelField("5. Output Hierarchy", EditorStyles.boldLabel);
        solarPanelsRootName = EditorGUILayout.TextField("Roof Panels Root", solarPanelsRootName);
        buildingFolderName = EditorGUILayout.TextField("Building Folder", buildingFolderName);
        groupNamePrefix = EditorGUILayout.TextField("Group Name Prefix", groupNamePrefix);
        panelNamePrefix = EditorGUILayout.TextField("Panel Name Prefix", panelNamePrefix);
        replaceExisting = EditorGUILayout.Toggle("Replace Existing Groups", replaceExisting);
        EditorGUILayout.HelpBox(
            $"<site>/{solarPanelsRootName}/{(string.IsNullOrWhiteSpace(buildingFolderName) ? "<building>" : buildingFolderName)}/" +
            $"{groupNamePrefix}_000/{panelNamePrefix}_0000",
            MessageType.None);
    }

    private void DrawActions()
    {
        bool canGenerate = buildingRoot != null && panelPrefab != null && facets.Count > 0;
        using (new EditorGUI.DisabledScope(!canGenerate))
        {
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.55f, 0.85f, 0.55f);
            if (GUILayout.Button("Generate Solar Panels", GUILayout.Height(38))) GeneratePanels();
            GUI.backgroundColor = previous;
        }

        using (new EditorGUI.DisabledScope(buildingRoot == null))
        {
            if (GUILayout.Button("Clear Generated Panels For Building")) ClearGeneratedPanels();
        }
    }

    private void DetectRoofs()
    {
        facets.Clear();
        previewCache.Clear();

        RoofFacetDetector.DetectionSettings detectionSettings =
            new RoofFacetDetector.DetectionSettings
            {
                minimumRelativeHeight = minimumRelativeHeight,
                roofSearchDepth = roofSearchDepth,
                maximumRoofSlope = maximumRoofSlope,
                normalMergeTolerance = normalMergeTolerance,
                planeMergeTolerance = planeMergeTolerance,
                minimumTriangleArea = minimumTriangleArea,
                minimumFacetArea = minimumFacetArea,
                splitDisconnectedFacets = true
            };

        RoofFacetDetector.DetectionResult result =
            RoofFacetDetector.Detect(
                buildingRoot,
                detectionSettings
            );

        buildingBounds = result.buildingBounds;
        scannedTriangles = result.scannedTriangles;
        acceptedTriangles = result.acceptedTriangles;
        status = result.status;
        facets.AddRange(result.facets);

        if (!result.succeeded)
        {
            previewStatus = "No roof preview is available.";
            SceneView.RepaintAll();
            Repaint();
            return;
        }

        RebuildPreviewCacheNow();
        LogRoofProperties();
        SceneView.RepaintAll();
        Repaint();
    }

    private void LogRoofProperties()
    {
        Debug.Log(
            $"[Solar Roof Generator V4] Building '{buildingRoot.name}'\nBounds: center {buildingBounds.center}, size {buildingBounds.size}\n" +
            $"Scanned {scannedTriangles:N0}; accepted {acceptedTriangles:N0}; facets {facets.Count}", buildingRoot);

        for (int i = 0; i < facets.Count; i++)
        {
            RoofFacet f = facets[i];
            FacetProjection p = ResolveFacetProjection(f, i);
            Vector2 dimensions = p.max - p.min;
            Debug.Log(
                $"[Solar Roof Generator V4] Facet {i:00}\nArea {f.area:F2} m²\nSlope {f.Slope:F2}°\nNormal {f.normal}\n" +
                $"Height {f.minY:F2}–{f.maxY:F2}\nProjected bounds {dimensions.x:F2} × {dimensions.y:F2}\n" +
                $"Estimated groups/panels: {GetFacetEstimateText(f, i)}", buildingRoot);
        }
    }

    private string GetFacetEstimateText(RoofFacet facet, int facetIndex)
    {
        GetLayoutCounts(facet, facetIndex, out int groupCount, out int panelCount);
        return $"{groupCount}/{panelCount}";
    }

    private void GetLayoutCounts(RoofFacet facet, int facetIndex, out int groupCount, out int panelCount)
    {
        groupCount = BuildGroups(facet, facetIndex, null, out panelCount);
    }

    private int BuildGroups(
        RoofFacet facet,
        int facetIndex,
        List<GroupPlacement> output,
        out int totalPanelCount)
    {
        totalPanelCount = 0;
        if (facet == null || facet.triangles.Count == 0) return 0;

        FacetProjection projection = ResolveFacetProjection(facet, facetIndex);
        return BuildGroupsForProjection(
            projection,
            facetIndex,
            output,
            out totalPanelCount);
    }

    private int BuildGroupsForProjection(
        FacetProjection projection,
        int facetIndex,
        List<GroupPlacement> output,
        out int totalPanelCount)
    {
        return layoutMode == LayoutMode.RepeatedGroups
            ? BuildRepeatedGroups(
                projection,
                facetIndex,
                output,
                out totalPanelCount)
            : BuildSingleGridGroup(
                projection,
                facetIndex,
                output,
                out totalPanelCount);
    }

    private int BuildSingleGridGroup(
        FacetProjection projection,
        int facetIndex,
        List<GroupPlacement> output,
        out int totalPanelCount)
    {
        totalPanelCount = 0;
        float width = Mathf.Max(panelWidth, 0.01f);
        float length = Mathf.Max(panelLength, 0.01f);
        float availableW = projection.max.x - projection.min.x - edgePadding * 2f;
        float availableL = projection.max.y - projection.min.y - edgePadding * 2f;
        if (availableW < width || availableL < length) return 0;

        int columns = gridMode == GridMode.AutoFit
            ? Mathf.FloorToInt((availableW + columnSpacing) / (width + columnSpacing))
            : Mathf.Max(1, manualColumns);
        int rows = gridMode == GridMode.AutoFit
            ? Mathf.FloorToInt((availableL + rowSpacing) / (length + rowSpacing))
            : Mathf.Max(1, manualRows);
        if (columns <= 0 || rows <= 0) return 0;

        float usedW = columns * width + Mathf.Max(0, columns - 1) * columnSpacing;
        float usedL = rows * length + Mathf.Max(0, rows - 1) * rowSpacing;
        float centerU = (projection.min.x + projection.max.x) * 0.5f + gridOffset.x;
        float centerV = (projection.min.y + projection.max.y) * 0.5f + gridOffset.y;

        GroupPlacement group = CreateGroupPlacement(
            projection, facetIndex, 0, 0, centerU, centerV, usedW, usedL);

        float firstU = centerU - usedW * 0.5f + width * 0.5f;
        float firstV = centerV - usedL * 0.5f + length * 0.5f;

        for (int row = 0; row < rows; row++)
        {
            float v = firstV + row * (length + rowSpacing);
            for (int column = 0; column < columns; column++)
            {
                if (maximumPanelsPerFacet > 0 && group.panels.Count >= maximumPanelsPerFacet) break;
                float u = firstU + column * (width + columnSpacing);
                if (!FootprintFits(projection, new Vector2(u, v), width, length)) continue;
                group.panels.Add(CreatePanelPlacement(projection, u, v));
            }
        }

        totalPanelCount = group.panels.Count;
        if (group.panels.Count == 0) return 0;
        output?.Add(group);
        return 1;
    }

    private int BuildRepeatedGroups(
        FacetProjection projection,
        int facetIndex,
        List<GroupPlacement> output,
        out int totalPanelCount)
    {
        totalPanelCount = 0;
        float width = Mathf.Max(panelWidth, 0.01f);
        float length = Mathf.Max(panelLength, 0.01f);
        int panelColumns = Mathf.Max(1, panelsPerGroupColumns);
        int panelRows = Mathf.Max(1, panelsPerGroupRows);

        float groupWidth = panelColumns * width + Mathf.Max(0, panelColumns - 1) * columnSpacing;
        float groupLength = panelRows * length + Mathf.Max(0, panelRows - 1) * rowSpacing;
        float availableW = projection.max.x - projection.min.x - edgePadding * 2f;
        float availableL = projection.max.y - projection.min.y - edgePadding * 2f;
        if (availableW < groupWidth || availableL < groupLength) return 0;

        int groupColumns = groupCountMode == GridMode.AutoFit
            ? Mathf.FloorToInt((availableW + groupColumnSpacing) / (groupWidth + groupColumnSpacing))
            : Mathf.Max(1, manualGroupColumns);
        int groupRows = groupCountMode == GridMode.AutoFit
            ? Mathf.FloorToInt((availableL + groupRowSpacing) / (groupLength + groupRowSpacing))
            : Mathf.Max(1, manualGroupRows);
        if (groupColumns <= 0 || groupRows <= 0) return 0;

        float usedW = groupColumns * groupWidth + Mathf.Max(0, groupColumns - 1) * groupColumnSpacing;
        float usedL = groupRows * groupLength + Mathf.Max(0, groupRows - 1) * groupRowSpacing;
        float layoutCenterU = (projection.min.x + projection.max.x) * 0.5f + gridOffset.x;
        float layoutCenterV = (projection.min.y + projection.max.y) * 0.5f + gridOffset.y;
        float firstGroupU = layoutCenterU - usedW * 0.5f + groupWidth * 0.5f;
        float firstGroupV = layoutCenterV - usedL * 0.5f + groupLength * 0.5f;

        int acceptedGroups = 0;
        int expectedPanels = panelColumns * panelRows;

        for (int groupRow = 0; groupRow < groupRows; groupRow++)
        {
            float groupCenterV = firstGroupV + groupRow * (groupLength + groupRowSpacing);
            for (int groupColumn = 0; groupColumn < groupColumns; groupColumn++)
            {
                if (maximumGroupsPerFacet > 0 && acceptedGroups >= maximumGroupsPerFacet)
                    return acceptedGroups;

                float groupCenterU = firstGroupU + groupColumn * (groupWidth + groupColumnSpacing);
                GroupPlacement group = CreateGroupPlacement(
                    projection, facetIndex, groupRow, groupColumn,
                    groupCenterU, groupCenterV, groupWidth, groupLength);

                float firstPanelU = groupCenterU - groupWidth * 0.5f + width * 0.5f;
                float firstPanelV = groupCenterV - groupLength * 0.5f + length * 0.5f;

                for (int panelRow = 0; panelRow < panelRows; panelRow++)
                {
                    float v = firstPanelV + panelRow * (length + rowSpacing);
                    for (int panelColumn = 0; panelColumn < panelColumns; panelColumn++)
                    {
                        float u = firstPanelU + panelColumn * (width + columnSpacing);
                        if (!FootprintFits(projection, new Vector2(u, v), width, length)) continue;
                        group.panels.Add(CreatePanelPlacement(projection, u, v));
                    }
                }

                bool complete = group.panels.Count == expectedPanels;
                if (group.panels.Count == 0 || (requireCompleteGroups && !complete)) continue;

                acceptedGroups++;
                totalPanelCount += group.panels.Count;
                output?.Add(group);
            }
        }

        return acceptedGroups;
    }

    private GroupPlacement CreateGroupPlacement(
        FacetProjection projection,
        int facetIndex,
        int groupRow,
        int groupColumn,
        float centerU,
        float centerV,
        float width,
        float length)
    {
        Vector3 roofCenter = projection.origin + projection.u * centerU + projection.v * centerV;
        Quaternion rotation = Quaternion.LookRotation(projection.v, projection.normal) * Quaternion.Euler(prefabRotationOffset);
        return new GroupPlacement
        {
            facetIndex = facetIndex,
            groupRow = groupRow,
            groupColumn = groupColumn,
            center = roofCenter + projection.normal * (roofClearance + pivotToBottom),
            rotation = rotation,
            u = projection.u,
            v = projection.v,
            normal = projection.normal,
            width = width,
            length = length
        };
    }

    private Placement CreatePanelPlacement(FacetProjection projection, float u, float v)
    {
        Vector3 roofPoint = projection.origin + projection.u * u + projection.v * v;
        Quaternion rotation = Quaternion.LookRotation(projection.v, projection.normal) * Quaternion.Euler(prefabRotationOffset);
        return new Placement
        {
            position = roofPoint + projection.normal * (roofClearance + pivotToBottom),
            rotation = rotation,
            u = projection.u,
            v = projection.v,
            normal = projection.normal
        };
    }

    private FacetProjection ResolveFacetProjection(
        RoofFacet facet,
        int facetIndex)
    {
        RoofFacetDetector.OrientationSettings orientationSettings =
            new RoofFacetDetector.OrientationSettings
            {
                snapPrincipalAxisToBoundary =
                    snapPrincipalAxisToBoundary,
                boundarySnapTolerance = boundarySnapTolerance,
                pcaAmbiguityThreshold = pcaAmbiguityThreshold
            };

        return RoofFacetDetector.ResolveProjection(
            facet,
            buildingRoot != null
                ? buildingRoot.transform
                : null,
            orientationMode,
            gridRotation,
            orientationSettings,
            projection =>
            {
                int groups = BuildGroupsForProjection(
                    projection,
                    facetIndex,
                    null,
                    out int panels
                );

                return new RoofFacetDetector.ProjectionScore(
                    panels,
                    groups
                );
            }
        );
    }

    private void SchedulePreviewRebuild()
    {
        previewDirty = true;
        previewStatus = "Preview update queued…";

        if (previewRebuildScheduled)
            return;

        previewRebuildScheduled = true;
        EditorApplication.delayCall += RebuildPreviewCacheDelayed;
    }

    private void RebuildPreviewCacheDelayed()
    {
        EditorApplication.delayCall -= RebuildPreviewCacheDelayed;
        previewRebuildScheduled = false;

        if (this == null)
            return;

        RebuildPreviewCacheNow();
    }

    private void RebuildPreviewCacheNow()
    {
        previewCache.Clear();

        if (buildingRoot == null || facets.Count == 0)
        {
            previewDirty = false;
            previewStatus = "No detected roof facets.";
            Repaint();
            SceneView.RepaintAll();
            return;
        }

        int totalGroups = 0;
        int totalPanels = 0;

        for (int facetIndex = 0; facetIndex < facets.Count; facetIndex++)
        {
            RoofFacet facet = facets[facetIndex];
            FacetPreviewCache cache = new();
            cache.projection = ResolveFacetProjection(
                facet,
                facetIndex);

            BuildGroupsForProjection(
                cache.projection,
                facetIndex,
                cache.groups,
                out cache.panelCount);

            cache.boundaryLines = BuildOffsetBoundaryLines(facet);
            cache.groupLines = BuildGroupPreviewLines(cache.groups);
            cache.panelLines = BuildPanelPreviewLines(
                cache.groups,
                maximumPreviewPanels);

            previewCache.Add(cache);
            totalGroups += cache.groups.Count;
            totalPanels += cache.panelCount;
        }

        previewDirty = false;
        previewStatus =
            $"Cached {totalGroups} groups and {totalPanels} panels.";

        Repaint();
        SceneView.RepaintAll();
    }

    private static Vector3[] BuildOffsetBoundaryLines(
        RoofFacet facet)
    {
        Vector3[] lines = new Vector3[
            facet.boundaryLineSegments.Count];

        Vector3 offset = facet.normal * 0.025f;

        for (int index = 0;
             index < facet.boundaryLineSegments.Count;
             index++)
        {
            lines[index] =
                facet.boundaryLineSegments[index] + offset;
        }

        return lines;
    }

    private static Vector3[] BuildGroupPreviewLines(
        List<GroupPlacement> groups)
    {
        List<Vector3> lines = new(groups.Count * 8);

        foreach (GroupPlacement group in groups)
        {
            AddRectangleLines(
                lines,
                group.center,
                group.u,
                group.v,
                group.width,
                group.length);
        }

        return lines.ToArray();
    }

    private Vector3[] BuildPanelPreviewLines(
        List<GroupPlacement> groups,
        int maximumPanels)
    {
        List<Vector3> lines = new(
            Mathf.Min(maximumPanels, 1000) * 8);

        int drawnPanels = 0;

        foreach (GroupPlacement group in groups)
        {
            foreach (Placement placement in group.panels)
            {
                if (drawnPanels >= maximumPanels)
                    return lines.ToArray();

                AddRectangleLines(
                    lines,
                    placement.position,
                    placement.u,
                    placement.v,
                    panelWidth,
                    panelLength);

                drawnPanels++;
            }
        }

        return lines.ToArray();
    }

    private static void AddRectangleLines(
        List<Vector3> lines,
        Vector3 center,
        Vector3 axisU,
        Vector3 axisV,
        float width,
        float length)
    {
        Vector3 u = axisU * width * 0.5f;
        Vector3 v = axisV * length * 0.5f;

        Vector3 bottomLeft = center - u - v;
        Vector3 bottomRight = center + u - v;
        Vector3 topRight = center + u + v;
        Vector3 topLeft = center - u + v;

        lines.Add(bottomLeft);
        lines.Add(bottomRight);
        lines.Add(bottomRight);
        lines.Add(topRight);
        lines.Add(topRight);
        lines.Add(topLeft);
        lines.Add(topLeft);
        lines.Add(bottomLeft);
    }

    private bool FootprintFits(
        FacetProjection projection,
        Vector2 center,
        float width,
        float length)
    {
        return RoofFacetDetector.FootprintFits(
            projection,
            center,
            width,
            length,
            footprintSamples
        );
    }

    private void GeneratePanels()
    {
        if (usePrefabDimensions && !ApplyPrefabDimensions())
            return;

        RebuildPreviewCacheNow();

        Transform buildingParent = GetOrCreateOutputFolder();
        if (buildingParent == null) return;

        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Generate Grouped Solar Panels");
        if (replaceExisting) DestroyChildren(buildingParent);

        var generatedGroups = new List<GroupPlacement>();

        for (int facetIndex = 0;
             facetIndex < facets.Count && facetIndex < previewCache.Count;
             facetIndex++)
        {
            if (!facets[facetIndex].enabled)
                continue;

            generatedGroups.AddRange(
                previewCache[facetIndex].groups);
        }

        int nextGroupIndex = replaceExisting ? 0 : GetNextGroupIndex(buildingParent);
        int createdGroups = 0;
        int createdPanels = 0;

        foreach (GroupPlacement groupPlacement in generatedGroups)
        {
            string groupName = $"{SafePrefix(groupNamePrefix, "group")}_{nextGroupIndex + createdGroups:000}";
            GameObject groupObject = new(groupName);
            Undo.RegisterCreatedObjectUndo(groupObject, "Create Solar Panel Group");
            Undo.SetTransformParent(groupObject.transform, buildingParent, "Parent Solar Panel Group");
            groupObject.transform.SetPositionAndRotation(groupPlacement.center, groupPlacement.rotation);
            groupObject.transform.localScale = Vector3.one;

            int panelIndex = 0;
            foreach (Placement placement in groupPlacement.panels)
            {
                GameObject instance = InstantiatePanel();
                if (instance == null) continue;
                Undo.RegisterCreatedObjectUndo(instance, "Create Solar Panel");
                instance.name = $"{SafePrefix(panelNamePrefix, "panel")}_{panelIndex:0000}";
                Undo.SetTransformParent(instance.transform, groupObject.transform, "Parent Solar Panel");
                Undo.RegisterCompleteObjectUndo(instance.transform, "Position Solar Panel");
                instance.transform.SetPositionAndRotation(placement.position, placement.rotation);
                panelIndex++;
                createdPanels++;
            }

            createdGroups++;
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(buildingRoot.scene);
        Selection.activeTransform = buildingParent;
        Debug.Log(
            $"[Solar Roof Generator V4] Generated {createdGroups} groups and {createdPanels} panels under '{GetPath(buildingParent)}'.",
            buildingParent);
    }

    private int GetNextGroupIndex(Transform buildingParent)
    {
        int next = 0;
        string prefix = SafePrefix(groupNamePrefix, "group") + "_";
        for (int i = 0; i < buildingParent.childCount; i++)
        {
            string childName = buildingParent.GetChild(i).name;
            if (!childName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(childName.Substring(prefix.Length), out int parsed))
                next = Mathf.Max(next, parsed + 1);
        }
        return next;
    }

    private static string SafePrefix(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private GameObject InstantiatePanel()
    {
        if (PrefabUtility.IsPartOfPrefabAsset(panelPrefab))
            return PrefabUtility.InstantiatePrefab(panelPrefab, buildingRoot.scene) as GameObject;
        return Instantiate(panelPrefab);
    }

    private bool ApplyPrefabDimensions()
    {
        PanelBounds info = CalculatePanelBounds(panelPrefab);
        if (!info.valid)
        {
            Debug.LogError("Could not determine panel Prefab bounds. Disable Use Prefab Dimensions and enter them manually.", panelPrefab);
            return false;
        }
        panelWidth = info.bounds.size.x;
        panelLength = info.bounds.size.z;
        pivotToBottom = -info.bounds.min.y;
        return true;
    }

    private void ReadPrefabDimensions()
    {
        if (ApplyPrefabDimensions())
        {
            Debug.Log(
                $"[Solar Roof Generator V4] Panel size: " +
                $"{panelWidth:F3} × {panelLength:F3}; " +
                $"pivot-to-bottom {pivotToBottom:F3}.",
                panelPrefab);

            SchedulePreviewRebuild();
        }
    }

    private static PanelBounds CalculatePanelBounds(GameObject prefab)
    {
        if (prefab == null) return default;
        bool found = false;
        Bounds result = default;
        Transform root = prefab.transform;
        Matrix4x4 rootScale = Matrix4x4.Scale(root.localScale);

        foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null) continue;
            Matrix4x4 childToRoot = rootScale * root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            Bounds b = RoofFacetDetector.TransformBounds(filter.sharedMesh.bounds, childToRoot);
            if (!found) { result = b; found = true; } else result.Encapsulate(b);
        }
        foreach (SkinnedMeshRenderer renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer.sharedMesh == null) continue;
            Matrix4x4 childToRoot = rootScale * root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            Bounds b = RoofFacetDetector.TransformBounds(renderer.localBounds, childToRoot);
            if (!found) { result = b; found = true; } else result.Encapsulate(b);
        }
        return new PanelBounds { valid = found, bounds = result };
    }

    private bool TryGetBuildingBounds(out Bounds result)
    {
        return RoofFacetDetector.TryGetBuildingBounds(
            buildingRoot,
            out result
        );
    }

    private Transform GetOrCreateOutputFolder()
    {
        Transform site = siteRoot != null ? siteRoot.transform : FindLikelySiteRoot(buildingRoot.transform)?.transform;
        if (site == null) { Debug.LogError("Could not resolve Site Root.", buildingRoot); return null; }
        Transform solarRoot = FindOrCreateChild(site, solarPanelsRootName);
        string folder = string.IsNullOrWhiteSpace(buildingFolderName) ? buildingRoot.name : buildingFolderName.Trim();
        return FindOrCreateChild(solarRoot, folder);
    }

    private Transform FindOutputFolder()
    {
        if (buildingRoot == null) return null;
        Transform site = siteRoot != null ? siteRoot.transform : FindLikelySiteRoot(buildingRoot.transform)?.transform;
        if (site == null) return null;
        Transform solar = FindChild(site, solarPanelsRootName);
        if (solar == null) return null;
        string folder = string.IsNullOrWhiteSpace(buildingFolderName) ? buildingRoot.name : buildingFolderName.Trim();
        return FindChild(solar, folder);
    }

    private void ClearGeneratedPanels()
    {
        Transform folder = FindOutputFolder();
        if (folder == null) { Debug.Log("No generated panel folder found.", buildingRoot); return; }
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Clear Generated Solar Panels");
        DestroyChildren(folder);
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(buildingRoot.scene);
    }

    private static void DestroyChildren(Transform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--) Undo.DestroyObjectImmediate(parent.GetChild(i).gameObject);
    }

    private static Transform FindOrCreateChild(Transform parent, string name)
    {
        string safe = string.IsNullOrWhiteSpace(name) ? "Generated" : name.Trim();
        Transform existing = FindChild(parent, safe);
        if (existing != null) return existing;
        GameObject child = new(safe);
        Undo.RegisterCreatedObjectUndo(child, $"Create {safe}");
        Undo.SetTransformParent(child.transform, parent, $"Parent {safe}");
        child.transform.localPosition = Vector3.zero;
        child.transform.localRotation = Quaternion.identity;
        child.transform.localScale = Vector3.one;
        return child.transform;
    }

    private static Transform FindChild(Transform parent, string name)
    {
        if (parent == null || string.IsNullOrWhiteSpace(name)) return null;
        for (int i = 0; i < parent.childCount; i++)
            if (string.Equals(parent.GetChild(i).name, name.Trim(), StringComparison.Ordinal)) return parent.GetChild(i);
        return null;
    }

    private static GameObject FindLikelySiteRoot(Transform building)
    {
        if (building == null) return null;
        Transform current = building;
        Transform highest = building;
        while (current != null)
        {
            highest = current;
            if (current.name.IndexOf("cib_site", StringComparison.OrdinalIgnoreCase) >= 0) return current.gameObject;
            current = current.parent;
        }
        return highest.gameObject;
    }

    private void DrawScenePreview(SceneView sceneView)
    {
        if (buildingRoot == null || facets.Count == 0)
            return;

        if (previewDirty && !previewRebuildScheduled)
            SchedulePreviewRebuild();

        if (Event.current.type != EventType.Repaint)
            return;

        int count = Mathf.Min(facets.Count, previewCache.Count);

        for (int facetIndex = 0; facetIndex < count; facetIndex++)
        {
            RoofFacet facet = facets[facetIndex];

            if (!facet.enabled)
                continue;

            FacetPreviewCache cache = previewCache[facetIndex];
            Color color = Color.HSVToRGB(
                (facetIndex * 0.173f) % 1f,
                0.75f,
                1f);

            if (showFacets && cache.boundaryLines.Length > 0)
            {
                Handles.color = new Color(
                    color.r,
                    color.g,
                    color.b,
                    0.78f);

                Handles.DrawLines(cache.boundaryLines);

                Handles.ArrowHandleCap(
                    0,
                    facet.center,
                    Quaternion.LookRotation(facet.normal),
                    HandleUtility.GetHandleSize(facet.center) * 0.65f,
                    EventType.Repaint);
            }

            if (showLabels)
            {
                Handles.color = color;
                Handles.Label(
                    facet.center + facet.normal * 0.25f,
                    $"Roof {facetIndex:00}\n" +
                    $"{facet.area:F1} m² | {facet.Slope:F1}°\n" +
                    $"{cache.projection.orientationSummary}\n" +
                    $"{cache.groups.Count} groups | " +
                    $"{cache.panelCount} panels");
            }

            if (showGroupBoundsPreview &&
                cache.groupLines.Length > 0)
            {
                Handles.color = new Color(
                    1f,
                    0.72f,
                    0.12f,
                    0.95f);

                Handles.DrawLines(cache.groupLines);
            }

            if (showGroupLabels)
            {
                Handles.color = new Color(
                    1f,
                    0.72f,
                    0.12f,
                    1f);

                for (int groupIndex = 0;
                     groupIndex < cache.groups.Count;
                     groupIndex++)
                {
                    GroupPlacement group = cache.groups[groupIndex];
                    Handles.Label(
                        group.center + group.normal * 0.15f,
                        $"G{groupIndex:00}");
                }
            }

            if (showPanelPreview &&
                cache.panelLines.Length > 0)
            {
                Handles.color = new Color(
                    color.r,
                    color.g,
                    color.b,
                    0.85f);

                Handles.DrawLines(cache.panelLines);
            }
        }
    }

    private static string GetPath(Transform transform)
    {
        string path = transform.name;
        for (Transform p = transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}

#endif
