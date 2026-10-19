using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Non-destructive editor workflow for finding and simplifying expensive static context geometry.
///
/// Suggested location:
///     Assets/Editor/ContextOptimizationTool.cs
///
/// Workflow:
///     1. Analyze a hierarchy root.
///     2. Mark dense objects as candidates.
///     3. Process checked candidates, search results, or all meshes below a selected hierarchy root.
///     4. Place optimized replacements beside originals with algorithm suffixes.
///     5. A/B toggle originals and optimizations under a chosen root.
///     6. Remove disabled objects in a controlled cleanup, and retain JSON audit logs.
///
/// Notes:
///     - Mesh replacement currently supports MeshFilter + MeshRenderer objects.
///     - SkinnedMeshRenderers are analyzed but intentionally not simplified.
///     - "Delete" is destructive after the scene is saved/reopened. Prefer Disable while iterating.
///     - Geometry processing exposes the actual algorithm families used:
///         * Constrained QEM Edge Collapse
///         * Normal-Aware Spatial Vertex Clustering
///         * PCA Primitive Decomposition
///     - PCA Primitive Decomposition is intended for structural BIM/IFC context such as rails,
///       posts, MUGA equipment, frames and panel-like assemblies where visual abstraction is
///       preferable to faithful surface decimation.
///     - This tool is aimed at distant/static BIM/IFC context geometry rather than hero assets.
/// </summary>
public class ContextOptimizationTool : EditorWindow
{
    private const string ToolVersion = "9.0.0";
    private const string GeneratedBaseFolder = "Assets/Generated/ContextOptimization";

    // ============================================================
    // ANALYSIS SETTINGS
    // ============================================================

    [SerializeField] private GameObject analysisRoot;
    [SerializeField] private bool searchInactiveObjects = true;
    [SerializeField] private bool onlyActiveInHierarchy = true;
    [SerializeField] private bool onlyEnabledRenderers = true;

    [SerializeField] private CandidateMetric candidateMetric = CandidateMetric.Triangles;
    [SerializeField] private int minimumTriangles = 1000;
    [SerializeField] private int minimumVertices = 1500;

    // Optional semantic search. This is useful for repeated IFC families such as
    // BarbedWireCoil where each instance is modest but the aggregate cost is huge.
    [SerializeField] private string searchText = "";
    [SerializeField] private SearchMatchMode searchMatchMode = SearchMatchMode.Contains;
    [SerializeField] private bool searchCaseSensitive = false;
    [SerializeField] private bool searchGameObjectNames = true;
    [SerializeField] private bool searchMeshNames = true;
    [SerializeField] private bool searchMaterialNames = false;
    [SerializeField] private bool searchMatchesBypassDensityThreshold = true;

    // When enabled and Search is non-empty, matching objects become the primary
    // optimization set: they always bypass per-object density thresholds, are
    // sorted ahead of non-matches, are checked automatically after Analyze,
    // and Layer 2 switches to Current Search Results.
    [SerializeField] private bool prioritizeSearchMatches = false;

    // Aggregate-family detection catches repeated low-cost objects whose total
    // cost is significant even when no single instance crosses the density threshold.
    [SerializeField] private bool flagAggregateGroups = true;
    [SerializeField] private long minimumAggregateTriangles = 100000;
    [SerializeField] private int minimumAggregateInstances = 250;

    [SerializeField] private int groupTokenCount = 4;
    [SerializeField] private int maximumRows = 100;

    // ============================================================
    // PROCESSING SETTINGS
    // ============================================================

    [SerializeField] private ProcessingSource processingSource = ProcessingSource.CheckedCandidates;
    [SerializeField] private bool expandUnitySelectionToMeshChildren = true;
    // A pinned root makes the processing scope stable when the Editor selection
    // changes (for instance, after clicking an object in the results list).
    // When empty, Current Unity Selection uses the actual Hierarchy selection.
    [SerializeField] private GameObject selectionHierarchyRoot;
    [SerializeField] private bool selectionIncludeInactiveChildren = false;
    [SerializeField] private bool selectionIncludeDisabledRenderers = false;

    [SerializeField] private ProcessingAction processingAction = ProcessingAction.ProcessMeshGeometry;
    [SerializeField] private OriginalDisposition replacementOriginalDisposition = OriginalDisposition.Disable;
    [SerializeField] private bool autoKeyDuplicateSourceNames = true;

    [SerializeField] private GeometryProcessingAlgorithm geometryAlgorithm =
        GeometryProcessingAlgorithm.PcaPrimitiveDecomposition;

    // Constrained QEM Edge Collapse
    [SerializeField, Range(0.05f, 1f)] private float qemTargetTriangleRatio = 0.35f;
    [SerializeField] private float qemWeldToleranceRelative = 0.00001f;
    [SerializeField] private bool qemProtectOpenBoundaries = true;
    [SerializeField] private bool qemProtectMaterialBoundaries = true;
    [SerializeField] private bool qemProtectUvSeams = false;
    [SerializeField] private bool qemProtectSharpEdges = true;
    [SerializeField, Range(5f, 120f)] private float qemSharpEdgeAngle = 45f;
    [SerializeField, Range(5f, 85f)] private float qemMaxFaceNormalDeviation = 70f;

    // Normal-Aware Spatial Vertex Clustering
    // Grid resolution is the algorithm's direct control: lower values mean larger
    // spatial cells and therefore more aggressive clustering.
    [SerializeField, Range(4, 128)] private int clusterGridResolution = 24;
    [SerializeField] private bool clusterProtectSurfaceNormals = true;
    [SerializeField, Range(5f, 120f)] private float clusterNormalMergeAngle = 45f;

    // PCA Primitive Decomposition
    // Components are reconstructed from position-coincident IFC vertices, then
    // partitioned into local segments and replaced by fitted boxes/prisms/panels.
    [SerializeField] private float proxyWeldToleranceRelative = 0.00001f;
    [SerializeField, Range(0.02f, 1f)] private float proxyMaxSegmentLengthRelative = 0.20f;
    [SerializeField, Range(1, 24)] private int proxyMaxSegmentsPerComponent = 8;
    [SerializeField, Range(2f, 12f)] private float proxyElongatedAspectThreshold = 3.0f;
    [SerializeField, Range(0.01f, 0.5f)] private float proxyPanelThicknessRatio = 0.12f;
    [SerializeField, Range(0f, 0.10f)] private float proxyMinimumComponentSizeRelative = 0.005f;
    [SerializeField, Range(4, 12)] private int proxyBeamCrossSectionSides = 4;
    [SerializeField] private bool proxyUsePrismsForElongatedComponents = true;
    [SerializeField] private bool proxySplitComponentsByMaterial = true;
    [SerializeField] private bool proxyPreserveSubmeshMaterials = true;

    [SerializeField] private bool recalculateNormals = true;
    [SerializeField] private bool recalculateTangents = true;
    [SerializeField] private bool preserveVertexColors = true;
    [SerializeField] private bool preserveUv2 = true;

    // Procedural Fence proxy: locally aligned, flat-shaded box rails and posts.
    [SerializeField, Min(0.1f)] private float fencePostSpacing = 2.5f;
    [SerializeField, Min(0.001f)] private float fencePostWidth = 0.075f;
    [SerializeField, Range(0f, 1f)] private float fencePostHeightFraction = 1f;
    [SerializeField, Range(1, 4)] private int fenceRailCount = 2;
    [SerializeField, Min(0.001f)] private float fenceRailThickness = 0.045f;
    [SerializeField] private bool fenceIncludeEndPosts = true;
    // Connected-component OBB decomposition, with conservative volume-aware merge.
    [SerializeField, Min(0.0000001f)] private float boxDecompWeldRelative = 0.00001f;
    [SerializeField, Min(1)] private int boxDecompMinimumTriangles = 2;
    [SerializeField, Min(0.00001f)] private float boxDecompMinThickness = 0.02f;
    [SerializeField] private bool boxDecompMerge = true;
    [SerializeField, Range(0f, 30f)] private float boxDecompMergeAngle = 5f;
    [SerializeField, Min(0f)] private float boxDecompMergeGap = 0.05f;
    [SerializeField, Range(0f, 1f)] private float boxDecompMergeOverhead = 0.10f;
    [SerializeField, Min(1)] private int boxDecompMaxBoxes = 512;
    [SerializeField, Range(3, 16)] private int cylinderSides = 6;
    [SerializeField] private Material primitiveMaterialOverride;
    [SerializeField] private GameObject customReplacementPrefab;

    // All replacements are siblings; paired via the name suffix alone.
    [SerializeField] private GameObject maintenanceRoot;
    [SerializeField] private CleanupScope cleanupScope = CleanupScope.PairedDisabledOriginals;
    [SerializeField] private bool showMaintenance = true;
    private string maintenanceStatus = "";
    private int maintenancePreviewCount = -1;

    // ============================================================
    // UI STATE
    // ============================================================

    private Vector2 mainScroll;
    private Vector2 candidatesScroll;

    private bool showSummary = true;
    private bool showCategories = true;
    private bool showCandidates = true;
    private bool showProcessing = true;
    private bool showHistory = true;

    // ============================================================
    // ANALYSIS DATA
    // ============================================================

    private readonly List<ObjectStats> objectStats = new();
    private readonly List<ObjectStats> candidateStats = new();
    private readonly List<ObjectStats> searchResultStats = new();
    private readonly List<CategoryStats> categoryStats = new();

    // Transient EntityIds of candidates checked for processing.
    private readonly HashSet<EntityId> checkedCandidateIds = new();

    private long totalTriangles;
    private long totalVertices;
    private int totalMeshInstances;
    private int totalUniqueMeshes;
    private int totalRenderers;
    private int totalMaterialSlots;

    [SerializeField] private string lastLogAssetPath;
    [SerializeField] private string lastBatchName;
    [SerializeField] private long lastBatchSourceTriangles;
    [SerializeField] private long lastBatchReplacementTriangles;
    [SerializeField] private bool lastBatchReverted;
    private string lastStatusMessage;

    private enum CandidateMetric
    {
        Triangles,
        Vertices,
        Either
    }

    private enum SearchMatchMode
    {
        Contains,
        StartsWith,
        Exact,
        Regex
    }

    private enum ProcessingSource
    {
        CheckedCandidates,
        CurrentSearchResults,
        CurrentUnitySelection
    }

    private enum ProcessingAction
    {
        DisableOnly,
        DeleteOnly,
        ProcessMeshGeometry,
        ReplaceBoundsBox,
        ReplaceLowPolyCylinder,
        ReplaceCustomPrefab,
        ReplaceProceduralFence,
        ReplaceBoxDecomposition
    }

    private enum GeometryProcessingAlgorithm
    {
        ConstrainedQemEdgeCollapse,
        NormalAwareSpatialVertexClustering,
        PcaPrimitiveDecomposition
    }

    private enum BatchPreviewMode
    {
        Original,
        Optimized,
        Applied
    }

    private enum OriginalDisposition
    {
        Keep,
        Disable,
        Delete
    }

    private enum CleanupScope
    {
        PairedDisabledOriginals,
        AllLocallyDisabledDescendants
    }

    // ============================================================
    // WINDOW
    // ============================================================

    [MenuItem("Tools/Optimization/Context Optimization Tool")]
    public static void ShowWindow()
    {
        ContextOptimizationTool window =
            GetWindow<ContextOptimizationTool>("Context Optimization");

        window.minSize = new Vector2(680f, 620f);
    }

    private void OnGUI()
    {
        mainScroll = EditorGUILayout.BeginScrollView(mainScroll);

        DrawHeader();
        DrawAnalysisSettings();

        if (objectStats.Count > 0)
        {
            EditorGUILayout.Space(12);
            DrawSummary();

            EditorGUILayout.Space(12);
            DrawCategorySection();

            EditorGUILayout.Space(12);
            DrawCandidateSection();
        }

        // Keep processing/history available even when the latest operation
        // disabled every analyzed object. This is important because the user
        // must always be able to preview or revert the last batch.
        EditorGUILayout.Space(16);
        DrawProcessingSection();

        EditorGUILayout.Space(16);
        DrawHistorySection();

        EditorGUILayout.Space(16);
        DrawMaintenanceSection();

        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Context Optimization Tool", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "Two-stage workflow for IFC/BIM context geometry:\n" +
            "1) Analyze and identify dense objects.\n" +
            "2) Create optimized siblings next to source meshes with name-only pairing.\n" +
            "3) Switch the root between original and optimized geometry or clean disabled objects.",
            MessageType.Info
        );

        EditorGUILayout.HelpBox(
            "Pairing uses GameObject names only (e.g. FencePost_opt_pca). " +
            "Repeated identical source names can receive a __optkey_ suffix to make pairing unambiguous. " +
            "Deleting disabled originals is permanent; copy your scene first.",
            MessageType.Warning
        );
    }

    // ============================================================
    // ANALYSIS UI
    // ============================================================

    private void DrawAnalysisSettings()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Layer 1 — Analyze & Select", EditorStyles.boldLabel);

        analysisRoot = (GameObject)EditorGUILayout.ObjectField(
            "Root Object",
            analysisRoot,
            typeof(GameObject),
            true
        );

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Scene Filters", EditorStyles.miniBoldLabel);

        searchInactiveObjects = EditorGUILayout.Toggle(
            new GUIContent(
                "Search Inactive Objects",
                "Traverse inactive children too. This is useful together with 'Only Active In Hierarchy' because it lets the scan see everything and then deliberately exclude disabled legacy geometry."
            ),
            searchInactiveObjects
        );

        onlyActiveInHierarchy = EditorGUILayout.Toggle(
            new GUIContent(
                "Only Active In Hierarchy",
                "Exclude GameObjects that are disabled themselves or by a disabled parent."
            ),
            onlyActiveInHierarchy
        );

        onlyEnabledRenderers = EditorGUILayout.Toggle(
            new GUIContent(
                "Only Enabled Renderers",
                "Exclude objects whose MeshRenderer / SkinnedMeshRenderer component is disabled."
            ),
            onlyEnabledRenderers
        );

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Semantic Search", EditorStyles.miniBoldLabel);

        searchText = EditorGUILayout.TextField(
            new GUIContent(
                "Search",
                "Optional search used during analysis and available later as a processing source."
            ),
            searchText
        );

        searchMatchMode = (SearchMatchMode)EditorGUILayout.EnumPopup(
            "Match Mode",
            searchMatchMode
        );

        searchCaseSensitive = EditorGUILayout.Toggle(
            "Case Sensitive",
            searchCaseSensitive
        );

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel("Search In");
        searchGameObjectNames = GUILayout.Toggle(searchGameObjectNames, "Object", EditorStyles.miniButtonLeft);
        searchMeshNames = GUILayout.Toggle(searchMeshNames, "Mesh", EditorStyles.miniButtonMid);
        searchMaterialNames = GUILayout.Toggle(searchMaterialNames, "Material", EditorStyles.miniButtonRight);
        EditorGUILayout.EndHorizontal();

        searchMatchesBypassDensityThreshold = EditorGUILayout.Toggle(
            new GUIContent(
                "Search Bypasses Density",
                "When enabled, matching objects become candidates even if they are individually cheap."
            ),
            searchMatchesBypassDensityThreshold
        );

        prioritizeSearchMatches = EditorGUILayout.Toggle(
            new GUIContent(
                "Prioritize Search",
                "When Search is non-empty, matching objects become the primary optimization set: " +
                "they always count as candidates, appear first in result tables, are checked automatically, " +
                "and Layer 2 switches to Current Search Results after Analyze."
            ),
            prioritizeSearchMatches
        );

        if (prioritizeSearchMatches && string.IsNullOrWhiteSpace(searchText))
        {
            EditorGUILayout.HelpBox(
                "Prioritize Search is enabled, but the Search field is empty. " +
                "Priority behavior will activate once a search string is provided.",
                MessageType.None
            );
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Aggregate Family Detection", EditorStyles.miniBoldLabel);

        flagAggregateGroups = EditorGUILayout.Toggle(
            new GUIContent(
                "Flag Expensive Families",
                "Treat a repeated IFC/name family as a candidate when its aggregate triangle count or instance count is high."
            ),
            flagAggregateGroups
        );

        if (flagAggregateGroups)
        {
            minimumAggregateTriangles = Math.Max(
                0,
                EditorGUILayout.LongField(
                    "Min Family Triangles",
                    minimumAggregateTriangles
                )
            );

            minimumAggregateInstances = Mathf.Max(
                1,
                EditorGUILayout.IntField(
                    "Min Family Instances",
                    minimumAggregateInstances
                )
            );
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Candidate Threshold", EditorStyles.miniBoldLabel);

        candidateMetric = (CandidateMetric)EditorGUILayout.EnumPopup(
            new GUIContent(
                "Metric",
                "Triangles = candidate by triangle threshold. Vertices = candidate by vertex threshold. Either = candidate if either threshold is exceeded."
            ),
            candidateMetric
        );

        if (candidateMetric == CandidateMetric.Triangles ||
            candidateMetric == CandidateMetric.Either)
        {
            minimumTriangles = Mathf.Max(
                0,
                EditorGUILayout.IntField("Minimum Triangles", minimumTriangles)
            );
        }

        if (candidateMetric == CandidateMetric.Vertices ||
            candidateMetric == CandidateMetric.Either)
        {
            minimumVertices = Mathf.Max(
                0,
                EditorGUILayout.IntField("Minimum Vertices", minimumVertices)
            );
        }

        groupTokenCount = EditorGUILayout.IntSlider(
            new GUIContent(
                "IFC Name Tokens",
                "How many '-' / '_' separated name tokens are used when grouping similar IFC objects."
            ),
            groupTokenCount,
            1,
            10
        );

        maximumRows = EditorGUILayout.IntSlider(
            "Maximum Rows",
            maximumRows,
            10,
            500
        );

        EditorGUILayout.Space(8);

        using (new EditorGUI.DisabledScope(analysisRoot == null))
        {
            if (GUILayout.Button("Analyze", GUILayout.Height(32)))
            {
                Analyze();
            }
        }
    }

    private void DrawSummary()
    {
        showSummary = EditorGUILayout.Foldout(showSummary, "Analysis Summary", true);

        if (!showSummary)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        DrawStat("Triangles", totalTriangles);
        DrawStat("Vertices", totalVertices);
        DrawStat("Mesh Instances", totalMeshInstances);
        DrawStat("Unique Meshes", totalUniqueMeshes);
        DrawStat("Renderers", totalRenderers);
        DrawStat("Material Slots", totalMaterialSlots);
        DrawStat("Optimization Candidates", candidateStats.Count);
        DrawStat("Search Results", searchResultStats.Count);
        DrawStat(
            "Search Result Triangles",
            searchResultStats.Sum(x => x.triangles)
        );
        DrawStat("Checked Candidates", checkedCandidateIds.Count);

        if (searchResultStats.Count > 0)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Select Search Results"))
            {
                Selection.objects =
                    searchResultStats
                        .Where(x => x.gameObject != null)
                        .Select(x => (UnityEngine.Object)x.gameObject)
                        .ToArray();
            }

            if ((searchMatchesBypassDensityThreshold || prioritizeSearchMatches) &&
                GUILayout.Button("Check Search Results"))
            {
                foreach (ObjectStats stat in searchResultStats)
                {
                    if (stat.gameObject != null)
                    {
                        checkedCandidateIds.Add(
                            stat.gameObject.GetEntityId()
                        );
                    }
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndVertical();
    }

    private static void DrawStat(string label, long value)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label);
        GUILayout.FlexibleSpace();
        EditorGUILayout.LabelField(value.ToString("N0"), GUILayout.Width(140));
        EditorGUILayout.EndHorizontal();
    }

    private void DrawCategorySection()
    {
        showCategories = EditorGUILayout.Foldout(
            showCategories,
            "Grouped Optimization Candidates",
            true
        );

        if (!showCategories)
            return;

        EditorGUILayout.HelpBox(
            "Categories are built from IFC-style object names. Families can be surfaced by individual density, semantic search, or aggregate cost. Clicking a category selects the whole family in the Unity hierarchy.",
            MessageType.None
        );

        DrawCategoryHeader();

        int rows = Mathf.Min(maximumRows, categoryStats.Count);

        for (int i = 0; i < rows; i++)
        {
            CategoryStats stat = categoryStats[i];

            EditorGUILayout.BeginHorizontal();

            GUILayout.Label((i + 1).ToString(), GUILayout.Width(28));

            if (GUILayout.Button(
                    stat.categoryName,
                    EditorStyles.linkLabel,
                    GUILayout.Width(270)))
            {
                Selection.objects = stat.objects
                    .Where(x => x != null)
                    .Cast<UnityEngine.Object>()
                    .ToArray();

                if (stat.objects.Count > 0 && stat.objects[0] != null)
                    EditorGUIUtility.PingObject(stat.objects[0]);
            }

            GUILayout.Label(stat.instances.ToString("N0"), GUILayout.Width(65));
            GUILayout.Label(stat.triangles.ToString("N0"), GUILayout.Width(95));
            GUILayout.Label(stat.AverageTriangles.ToString("N0"), GUILayout.Width(85));
            GUILayout.Label(stat.vertices.ToString("N0"), GUILayout.Width(95));

            if (GUILayout.Button("Check", GUILayout.Width(52)))
            {
                foreach (GameObject go in stat.candidateObjects)
                {
                    if (go != null)
                        checkedCandidateIds.Add(go.GetEntityId());
                }
            }

            EditorGUILayout.EndHorizontal();
        }
    }

    private static void DrawCategoryHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("#", GUILayout.Width(28));
        GUILayout.Label("Category", GUILayout.Width(270));
        GUILayout.Label("Count", GUILayout.Width(65));
        GUILayout.Label("Total Tris", GUILayout.Width(95));
        GUILayout.Label("Avg Tris", GUILayout.Width(85));
        GUILayout.Label("Total Verts", GUILayout.Width(95));
        GUILayout.Label("", GUILayout.Width(52));
        EditorGUILayout.EndHorizontal();
    }

    private void DrawCandidateSection()
    {
        showCandidates = EditorGUILayout.Foldout(
            showCandidates,
            "Dense Object Candidates",
            true
        );

        if (!showCandidates)
            return;

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Check All", GUILayout.Width(90)))
        {
            foreach (ObjectStats stat in candidateStats)
            {
                if (stat.gameObject != null)
                    checkedCandidateIds.Add(stat.gameObject.GetEntityId());
            }
        }

        if (GUILayout.Button("Clear", GUILayout.Width(70)))
        {
            checkedCandidateIds.Clear();
        }

        if (GUILayout.Button("Select Checked In Scene", GUILayout.Width(155)))
        {
            Selection.objects = candidateStats
                .Where(x =>
                    x.gameObject != null &&
                    checkedCandidateIds.Contains(x.gameObject.GetEntityId()))
                .Select(x => (UnityEngine.Object)x.gameObject)
                .ToArray();
        }

        GUILayout.FlexibleSpace();

        EditorGUILayout.LabelField(
            $"{checkedCandidateIds.Count:N0} checked",
            GUILayout.Width(105)
        );

        EditorGUILayout.EndHorizontal();

        DrawCandidateHeader();

        candidatesScroll = EditorGUILayout.BeginScrollView(
            candidatesScroll,
            GUILayout.MinHeight(180),
            GUILayout.MaxHeight(360)
        );

        int rows = Mathf.Min(maximumRows, candidateStats.Count);

        for (int i = 0; i < rows; i++)
        {
            ObjectStats stat = candidateStats[i];

            if (stat.gameObject == null)
                continue;

            EntityId id = stat.gameObject.GetEntityId();
            bool wasChecked = checkedCandidateIds.Contains(id);

            EditorGUILayout.BeginHorizontal();

            bool nowChecked = EditorGUILayout.Toggle(
                wasChecked,
                GUILayout.Width(22)
            );

            if (nowChecked != wasChecked)
            {
                if (nowChecked)
                    checkedCandidateIds.Add(id);
                else
                    checkedCandidateIds.Remove(id);
            }

            GUILayout.Label((i + 1).ToString(), GUILayout.Width(28));

            if (GUILayout.Button(
                    stat.gameObject.name,
                    EditorStyles.linkLabel,
                    GUILayout.Width(310)))
            {
                Selection.activeGameObject = stat.gameObject;
                EditorGUIUtility.PingObject(stat.gameObject);
            }

            GUILayout.Label(stat.triangles.ToString("N0"), GUILayout.Width(90));
            GUILayout.Label(stat.vertices.ToString("N0"), GUILayout.Width(90));
            GUILayout.Label(stat.materials.ToString("N0"), GUILayout.Width(50));

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    private static void DrawCandidateHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("", GUILayout.Width(22));
        GUILayout.Label("#", GUILayout.Width(28));
        GUILayout.Label("GameObject", GUILayout.Width(310));
        GUILayout.Label("Tris", GUILayout.Width(90));
        GUILayout.Label("Verts", GUILayout.Width(90));
        GUILayout.Label("Mats", GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();
    }

    // ============================================================
    // PROCESSING UI
    // ============================================================

    private void DrawProcessingSection()
    {
        showProcessing = EditorGUILayout.Foldout(
            showProcessing,
            "Layer 2 — Process Geometry",
            true
        );

        if (!showProcessing)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        processingSource = (ProcessingSource)EditorGUILayout.EnumPopup(
            new GUIContent(
                "Objects To Process",
                "Choose checked candidates, the semantic search results, or the current Unity selection."
            ),
            processingSource
        );

        if (processingSource == ProcessingSource.CurrentSearchResults)
        {
            EditorGUILayout.LabelField(
                $"Current search: \"{searchText}\" — {searchResultStats.Count:N0} result(s)",
                EditorStyles.miniLabel
            );
        }

        if (processingSource == ProcessingSource.CurrentUnitySelection)
        {
            EditorGUILayout.Space(3);
            EditorGUILayout.BeginHorizontal();

            selectionHierarchyRoot =
                (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent(
                        "Hierarchy Root (Optional)",
                        "Pin a parent in this field to use it even if you change the Unity Hierarchy selection. " +
                        "Leave empty to follow the live Unity selection instead."
                    ),
                    selectionHierarchyRoot,
                    typeof(GameObject),
                    true
                );

            if (GUILayout.Button("Use Selected", GUILayout.Width(105)))
                selectionHierarchyRoot = Selection.activeGameObject;

            if (GUILayout.Button("Clear", GUILayout.Width(50)))
                selectionHierarchyRoot = null;

            EditorGUILayout.EndHorizontal();

            expandUnitySelectionToMeshChildren = EditorGUILayout.Toggle(
                new GUIContent(
                    "Include Mesh Descendants",
                    "For a selected parent, process every MeshFilter + MeshRenderer descendant (including the parent's own mesh). " +
                    "Also works with several selected parents and avoids processing shared descendants twice."
                ),
                expandUnitySelectionToMeshChildren
            );

            if (expandUnitySelectionToMeshChildren)
            {
                EditorGUI.indentLevel++;
                selectionIncludeInactiveChildren = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Include Inactive Children",
                        "Off by default: don't process previously disabled original geometry or content beneath disabled parents."
                    ),
                    selectionIncludeInactiveChildren
                );
                selectionIncludeDisabledRenderers = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Include Disabled Renderers",
                        "Off by default: process only enabled MeshRenderer components."
                    ),
                    selectionIncludeDisabledRenderers
                );
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.HelpBox(
                selectionHierarchyRoot != null
                    ? $"Pinned processing root: {selectionHierarchyRoot.name}. " +
                      "The operation will include its eligible mesh descendants, regardless of your current Hierarchy selection."
                    : "Using live Unity Hierarchy selection. Select a parent/group to collect all its eligible meshes, " +
                      "or pin a root with Use Selected to prevent accidental selection changes.",
                MessageType.None
            );
        }

        EditorGUILayout.Space(5);

        processingAction = (ProcessingAction)EditorGUILayout.EnumPopup(
            "Action",
            processingAction
        );

        bool createsReplacement =
            processingAction == ProcessingAction.ProcessMeshGeometry ||
            processingAction == ProcessingAction.ReplaceBoundsBox ||
            processingAction == ProcessingAction.ReplaceLowPolyCylinder ||
            processingAction == ProcessingAction.ReplaceProceduralFence ||
            processingAction == ProcessingAction.ReplaceBoxDecomposition ||
            processingAction == ProcessingAction.ReplaceCustomPrefab;

        if (createsReplacement)
        {
            replacementOriginalDisposition =
                (OriginalDisposition)EditorGUILayout.EnumPopup(
                    new GUIContent(
                        "Original After Replacement",
                        "Keep leaves the source object untouched. Disable is recommended and is fully reversible. Delete is destructive."
                    ),
                    replacementOriginalDisposition
                );
        }

        if (processingAction == ProcessingAction.ProcessMeshGeometry)
        {
            geometryAlgorithm =
                (GeometryProcessingAlgorithm)EditorGUILayout.EnumPopup(
                    new GUIContent(
                        "Geometry Algorithm",
                        "Constrained QEM preserves the source surface. " +
                        "Normal-Aware Spatial Vertex Clustering is a fast grid-based decimator. " +
                        "PCA Primitive Decomposition reconstructs structural context using fitted low-poly primitives."
                    ),
                    geometryAlgorithm
                );

            EditorGUILayout.Space(4);

            if (geometryAlgorithm ==
                GeometryProcessingAlgorithm.ConstrainedQemEdgeCollapse)
            {
                EditorGUILayout.LabelField(
                    "Constrained QEM Edge Collapse",
                    EditorStyles.miniBoldLabel
                );

                qemTargetTriangleRatio = EditorGUILayout.Slider(
                    new GUIContent(
                        "Target Triangle Ratio",
                        "Requested fraction of the original triangle count. " +
                        "The solver may stop above this target if further legal collapses violate protected features."
                    ),
                    qemTargetTriangleRatio,
                    0.05f,
                    1f
                );

                EditorGUILayout.LabelField(
                    $"Requested target: ~{qemTargetTriangleRatio:P0} of source triangles",
                    EditorStyles.miniLabel
                );

                qemWeldToleranceRelative = Mathf.Max(
                    1e-8f,
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Connectivity Weld Tolerance",
                            "Relative to the mesh's largest local bounds dimension. " +
                            "Used only to reconstruct geometric connectivity across duplicated IFC render vertices."
                        ),
                        qemWeldToleranceRelative
                    )
                );

                qemProtectOpenBoundaries = EditorGUILayout.Toggle(
                    "Protect Open Boundaries",
                    qemProtectOpenBoundaries
                );

                qemProtectMaterialBoundaries = EditorGUILayout.Toggle(
                    "Protect Material Boundaries",
                    qemProtectMaterialBoundaries
                );

                qemProtectUvSeams = EditorGUILayout.Toggle(
                    "Protect UV0 Seams",
                    qemProtectUvSeams
                );

                qemProtectSharpEdges = EditorGUILayout.Toggle(
                    "Protect Sharp Edges",
                    qemProtectSharpEdges
                );

                qemSharpEdgeAngle = EditorGUILayout.Slider(
                    new GUIContent(
                        "Sharp Edge Angle",
                        "Edges sharper than this angle are treated as protected features. " +
                        "The same angle is used when rebuilding smoothing groups."
                    ),
                    qemSharpEdgeAngle,
                    5f,
                    120f
                );

                qemMaxFaceNormalDeviation = EditorGUILayout.Slider(
                    new GUIContent(
                        "Max Collapse Face Deviation",
                        "Rejects an edge collapse if an affected surviving face rotates beyond this angle."
                    ),
                    qemMaxFaceNormalDeviation,
                    5f,
                    85f
                );

                EditorGUILayout.HelpBox(
                    "Use this when preserving the original silhouette/surface matters more than extreme reduction. " +
                    "It is intentionally conservative.",
                    MessageType.None
                );
            }
            else if (geometryAlgorithm ==
                     GeometryProcessingAlgorithm.NormalAwareSpatialVertexClustering)
            {
                EditorGUILayout.LabelField(
                    "Normal-Aware Spatial Vertex Clustering",
                    EditorStyles.miniBoldLabel
                );

                clusterGridResolution = EditorGUILayout.IntSlider(
                    new GUIContent(
                        "Grid Resolution",
                        "Number of spatial cells along the mesh's normalized bounds. " +
                        "Lower values create larger cells and stronger geometry processing; higher values retain more geometry."
                    ),
                    clusterGridResolution,
                    4,
                    128
                );

                clusterProtectSurfaceNormals = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Split Clusters By Normal",
                        "Within one spatial cell, keep separate clusters for source vertices whose normals differ substantially."
                    ),
                    clusterProtectSurfaceNormals
                );

                if (clusterProtectSurfaceNormals)
                {
                    clusterNormalMergeAngle = EditorGUILayout.Slider(
                        new GUIContent(
                            "Normal Merge Angle",
                            "Vertices may share a cluster only when their source normals differ by no more than this angle."
                        ),
                        clusterNormalMergeAngle,
                        5f,
                        120f
                    );
                }

                EditorGUILayout.HelpBox(
                    "This algorithm is fast and directly controlled by spatial grid resolution. " +
                    "It is appropriate for disposable/distant geometry, but can change structure because proximity—not semantic shape—drives the geometry processing.",
                    MessageType.None
                );
            }
            else
            {
                EditorGUILayout.LabelField(
                    "PCA Primitive Decomposition",
                    EditorStyles.miniBoldLabel
                );

                proxyWeldToleranceRelative = Mathf.Max(
                    1e-8f,
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Connectivity Weld Tolerance",
                            "Relative tolerance used to reconstruct connected structural components from duplicated IFC vertices."
                        ),
                        proxyWeldToleranceRelative
                    )
                );

                proxyMaxSegmentLengthRelative = EditorGUILayout.Slider(
                    new GUIContent(
                        "Max Primitive Segment Length",
                        "Maximum fitted primitive length relative to the whole object's largest bounds dimension. " +
                        "Smaller values preserve curved/complex structures with more primitive segments; larger values make the proxy blockier."
                    ),
                    proxyMaxSegmentLengthRelative,
                    0.02f,
                    1f
                );

                proxyMaxSegmentsPerComponent = EditorGUILayout.IntSlider(
                    new GUIContent(
                        "Max Segments Per Component",
                        "Upper limit on how many local primitive fits may represent one reconstructed component."
                    ),
                    proxyMaxSegmentsPerComponent,
                    1,
                    24
                );

                proxyElongatedAspectThreshold = EditorGUILayout.Slider(
                    new GUIContent(
                        "Beam Aspect Threshold",
                        "If a fitted component's longest dimension is at least this many times larger than its cross-section, it is treated as a beam/pipe."
                    ),
                    proxyElongatedAspectThreshold,
                    2f,
                    12f
                );

                proxyPanelThicknessRatio = EditorGUILayout.Slider(
                    new GUIContent(
                        "Panel Thickness Ratio",
                        "If the thinnest PCA dimension is below this fraction of the largest dimension, the component is treated as a panel/thin box."
                    ),
                    proxyPanelThicknessRatio,
                    0.01f,
                    0.5f
                );

                proxyMinimumComponentSizeRelative = EditorGUILayout.Slider(
                    new GUIContent(
                        "Discard Component Below",
                        "Drop reconstructed components whose local bounds diagonal is smaller than this fraction of the object's bounds diagonal. " +
                        "Useful for eliminating bolts, caps and tiny IFC fittings."
                    ),
                    proxyMinimumComponentSizeRelative,
                    0f,
                    0.10f
                );

                proxyUsePrismsForElongatedComponents = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Use Prisms For Beams",
                        "If disabled, elongated components use oriented boxes. If enabled they use low-sided prisms."
                    ),
                    proxyUsePrismsForElongatedComponents
                );

                if (proxyUsePrismsForElongatedComponents)
                {
                    proxyBeamCrossSectionSides = EditorGUILayout.IntSlider(
                        new GUIContent(
                            "Beam Cross-Section Sides",
                            "4 produces a deliberately boxy beam/pipe. Increase only when roundness matters."
                        ),
                        proxyBeamCrossSectionSides,
                        4,
                        12
                    );
                }

                proxySplitComponentsByMaterial = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Split Components At Materials",
                        "Do not treat touching triangles from different source submeshes/material slots as one structural component. " +
                        "Recommended for IFC assemblies where frames, panels and fittings use different materials."
                    ),
                    proxySplitComponentsByMaterial
                );

                proxyPreserveSubmeshMaterials = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Preserve Material Groups",
                        "Assign each generated primitive to the dominant source submesh/material of the triangles it represents."
                    ),
                    proxyPreserveSubmeshMaterials
                );

                EditorGUILayout.HelpBox(
                    "Recommended for structural site context such as MUGA frames, rails, posts and panel assemblies. " +
                    "This is not a surface decimator: it intentionally replaces reconstructed components with fitted oriented primitives.",
                    MessageType.Info
                );
            }

            EditorGUILayout.Space(4);

            recalculateNormals = EditorGUILayout.Toggle(
                "Recalculate Normals",
                recalculateNormals
            );

            recalculateTangents = EditorGUILayout.Toggle(
                "Recalculate Tangents",
                recalculateTangents
            );

            preserveUv2 = EditorGUILayout.Toggle(
                new GUIContent(
                    "Preserve UV2 (Decimators)",
                    "QEM and vertex clustering preserve UV2 mesh data for a future bake. " +
                    "Primitive decomposition generates fresh proxy topology and does not reuse source UV2."
                ),
                preserveUv2
            );

            preserveVertexColors = EditorGUILayout.Toggle(
                new GUIContent(
                    "Preserve Vertex Colors (Decimators)",
                    "Relevant to QEM / vertex clustering. Primitive decomposition uses generated primitive vertices."
                ),
                preserveVertexColors
            );
        }

        if (processingAction == ProcessingAction.ReplaceBoundsBox ||
            processingAction == ProcessingAction.ReplaceLowPolyCylinder ||
            processingAction == ProcessingAction.ReplaceProceduralFence ||
            processingAction == ProcessingAction.ReplaceBoxDecomposition)
        {
            primitiveMaterialOverride =
                (Material)EditorGUILayout.ObjectField(
                    new GUIContent(
                        "Material Override",
                        "Optional. If empty, the first original material is reused."
                    ),
                    primitiveMaterialOverride,
                    typeof(Material),
                    false
                );
        }

        if (processingAction == ProcessingAction.ReplaceLowPolyCylinder)
        {
            cylinderSides = EditorGUILayout.IntSlider(
                "Cylinder Sides",
                cylinderSides,
                3,
                16
            );
        }

        if (processingAction == ProcessingAction.ReplaceProceduralFence)
        {
            EditorGUILayout.HelpBox(
                "Builds a NEW flat-shaded fence from mesh bounds, with posts and horizontal rails. " +
                "The longest local horizontal bounds dimension sets the fence direction. " +
                "Good for approximately straight fence segments; curved/rotated meshes may need grouping first.",
                MessageType.Info);
            fencePostSpacing = Mathf.Max(0.1f, EditorGUILayout.FloatField("Post Spacing (local units)", fencePostSpacing));
            fencePostWidth = Mathf.Max(0.001f, EditorGUILayout.FloatField("Post Width", fencePostWidth));
            fencePostHeightFraction = EditorGUILayout.Slider("Post Height Fraction", fencePostHeightFraction, 0.1f, 1f);
            fenceRailCount = EditorGUILayout.IntSlider("Horizontal Rails", fenceRailCount, 1, 4);
            fenceRailThickness = Mathf.Max(0.001f, EditorGUILayout.FloatField("Rail Thickness", fenceRailThickness));
            fenceIncludeEndPosts = EditorGUILayout.Toggle("End Posts", fenceIncludeEndPosts);
        }

        if (processingAction == ProcessingAction.ReplaceBoxDecomposition)
        {
            EditorGUILayout.HelpBox("Pass 1: weld coincident mesh vertices, find triangle-connected islands and fit a PCA oriented box to each. Pass 2: conservatively merge nearly aligned, adjacent boxes when their merged volume is close to the original volumes. Boxes are combined into ONE mesh per source object. No fabricated posts or rails.", MessageType.Info);
            boxDecompWeldRelative = Mathf.Max(0.0000001f, EditorGUILayout.FloatField("Weld Tolerance (relative)", boxDecompWeldRelative));
            boxDecompMinimumTriangles = Mathf.Max(1, EditorGUILayout.IntField("Min Component Triangles", boxDecompMinimumTriangles));
            boxDecompMinThickness = Mathf.Max(0.00001f, EditorGUILayout.FloatField("Minimum Box Thickness", boxDecompMinThickness));
            boxDecompMerge = EditorGUILayout.Toggle("Merge Adjacent Boxes", boxDecompMerge);
            if (boxDecompMerge)
            {
                boxDecompMergeAngle = EditorGUILayout.Slider("Merge Angle Tolerance", boxDecompMergeAngle, 0f, 30f);
                boxDecompMergeGap = Mathf.Max(0f, EditorGUILayout.FloatField("Merge Gap Tolerance", boxDecompMergeGap));
                boxDecompMergeOverhead = EditorGUILayout.Slider("Max Merge Volume Overhead", boxDecompMergeOverhead, 0f, 1f);
            }
            boxDecompMaxBoxes = Mathf.Max(1, EditorGUILayout.IntField("Max Boxes / Source Mesh", boxDecompMaxBoxes));
        }

        if (processingAction == ProcessingAction.ReplaceCustomPrefab)
        {
            customReplacementPrefab =
                (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent(
                        "Replacement Prefab",
                        "Prefab or GameObject used as the replacement. Its transform is matched to the source object's world transform."
                    ),
                    customReplacementPrefab,
                    typeof(GameObject),
                    false
                );
        }

        if (createsReplacement)
        {
            autoKeyDuplicateSourceNames = EditorGUILayout.Toggle(
                new GUIContent("Key Duplicate Source Names",
                    "For name-only pairing, identical-named IFC siblings need distinct names. " +
                    "Enable to append __optkey_<id> to sources being optimized. " +
                    "Disable to SKIP ambiguous duplicates without renaming source objects."),
                autoKeyDuplicateSourceNames);
            EditorGUILayout.HelpBox(
                "Replacements are created as siblings of their source objects. " +
                "Names use _opt_pca, _opt_qem, _opt_cluster, etc. " +
                "Choose Disable originals for an immediate A/B comparison; Keep displays both. " +
                (autoKeyDuplicateSourceNames
                    ? "Repeated sibling IFC names are disambiguated with __optkey_ before optimization."
                    : "Duplicate sibling names will be skipped to avoid ambiguous comparisons."),
                MessageType.None
            );
        }

        EditorGUILayout.Space(6);

        List<GameObject> previewSet = GetProcessingSet(showWarnings: false);

        int previewObjects = previewSet.Count;
        long previewTriangles = previewSet
            .Select(GetDirectMesh)
            .Where(m => m != null)
            .Sum(m => GetTriangleCount(m));

        EditorGUILayout.LabelField(
            $"Will process: {previewObjects:N0} object(s), {previewTriangles:N0} source triangles",
            EditorStyles.miniBoldLabel
        );

        if (processingSource == ProcessingSource.CurrentUnitySelection &&
            expandUnitySelectionToMeshChildren)
        {
            EditorGUILayout.LabelField(
                "Hierarchy expansion: only unoptimized, eligible renderable meshes are counted.",
                EditorStyles.miniLabel
            );

            using (new EditorGUI.DisabledScope(previewObjects == 0 || previewObjects > 2000))
            {
                if (GUILayout.Button("Select Resolved Meshes In Hierarchy", GUILayout.Height(23)))
                {
                    // Use the pinned root to retain this scope after selecting
                    // individual objects. Avoid multi-selecting excessive counts.
                    Selection.objects = previewSet
                        .Select(x => (UnityEngine.Object)x)
                        .ToArray();
                }
            }
        }

        if (processingAction == ProcessingAction.ProcessMeshGeometry)
        {
            if (geometryAlgorithm ==
                GeometryProcessingAlgorithm.ConstrainedQemEdgeCollapse)
            {
                long estimated = (long)Math.Round(
                    previewTriangles * qemTargetTriangleRatio
                );

                EditorGUILayout.LabelField(
                    $"QEM requested target: ~{estimated:N0} triangles",
                    EditorStyles.miniLabel
                );
            }
            else if (geometryAlgorithm ==
                     GeometryProcessingAlgorithm.NormalAwareSpatialVertexClustering)
            {
                EditorGUILayout.LabelField(
                    $"Spatial clustering resolution: {clusterGridResolution} cells/axis",
                    EditorStyles.miniLabel
                );
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"Primitive proxy: max segment {proxyMaxSegmentLengthRelative:P0} of object size, " +
                    $"beam sides {(proxyUsePrismsForElongatedComponents ? proxyBeamCrossSectionSides : 4)}",
                    EditorStyles.miniLabel
                );
            }
        }

        EditorGUILayout.Space(8);

        using (new EditorGUI.DisabledScope(
                   previewObjects == 0 ||
                   (processingAction == ProcessingAction.ReplaceCustomPrefab &&
                    customReplacementPrefab == null)))
        {
            if (GUILayout.Button("Apply Operation", GUILayout.Height(34)))
            {
                // Capture the exact preview set *before* the delayed action.
                // Otherwise Selection.gameObjects can change between the click
                // and the actual editor operation, especially for bulk work.
                List<GameObject> targetsSnapshot = new List<GameObject>(previewSet);
                ScheduleEditorAction(() => ApplyOperation(targetsSnapshot));
            }
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField(
            "Last Batch Comparison / Revert (JSON)",
            EditorStyles.miniBoldLabel
        );

        bool hasLastBatch =
            HasReadableLastLog();

        using (new EditorGUI.DisabledScope(
                   !hasLastBatch ||
                   lastBatchReverted))
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Preview Original"))
                ScheduleEditorAction(
                    () => SetLastBatchPreview(BatchPreviewMode.Original)
                );

            if (GUILayout.Button("Preview Optimized"))
                ScheduleEditorAction(
                    () => SetLastBatchPreview(BatchPreviewMode.Optimized)
                );

            if (GUILayout.Button("Restore Applied State"))
                ScheduleEditorAction(
                    () => SetLastBatchPreview(BatchPreviewMode.Applied)
                );

            EditorGUILayout.EndHorizontal();

            GUI.backgroundColor = new Color(1f, 0.75f, 0.55f);

            if (GUILayout.Button("Revert Last Batch (JSON)", GUILayout.Height(26)))
                ScheduleEditorAction(RevertLastBatch);

            GUI.backgroundColor = Color.white;
        }

        if (!string.IsNullOrWhiteSpace(lastStatusMessage))
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(lastStatusMessage, MessageType.Info);
        }

        EditorGUILayout.EndVertical();
    }

    private void ScheduleEditorAction(Action action)
    {
        if (action == null)
            return;

        EditorApplication.delayCall += () =>
        {
            if (this == null)
                return;

            try
            {
                action();
                Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        };

        // We are deliberately about to mutate scene/assets outside the
        // current IMGUI pass. Exit immediately so Begin/End layout state
        // cannot be invalidated by the operation.
        GUIUtility.ExitGUI();
    }

    private void DrawHistorySection()
    {
        showHistory = EditorGUILayout.Foldout(
            showHistory,
            "Audit / Generated Data",
            true
        );

        if (!showHistory)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.LabelField(
            "Generated assets and JSON logs:",
            GeneratedBaseFolder
        );

        if (!string.IsNullOrWhiteSpace(lastBatchName))
            EditorGUILayout.LabelField("Last Batch", lastBatchName);

        if (!string.IsNullOrWhiteSpace(lastLogAssetPath))
        {
            EditorGUILayout.LabelField("Last JSON Log", lastLogAssetPath);

            EditorGUILayout.LabelField(
                "Batch Source Triangles",
                lastBatchSourceTriangles.ToString("N0")
            );

            if (lastBatchReplacementTriangles > 0)
            {
                EditorGUILayout.LabelField(
                    "Batch Replacement Triangles",
                    lastBatchReplacementTriangles.ToString("N0")
                );

                if (lastBatchSourceTriangles > 0)
                {
                    float ratio =
                        lastBatchReplacementTriangles /
                        (float)lastBatchSourceTriangles;

                    EditorGUILayout.LabelField(
                        "Geometry Retained",
                        ratio.ToString("P1")
                    );
                }
            }

            EditorGUILayout.LabelField(
                "Batch Status",
                lastBatchReverted
                    ? "Reverted"
                    : "Applied"
            );

            if (GUILayout.Button("Refresh Log Summary"))
            {
                TryLoadLastLog(
                    out _,
                    out _
                );
            }

            if (GUILayout.Button("Ping Last Log"))
            {
                UnityEngine.Object logAsset =
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                        lastLogAssetPath
                    );

                if (logAsset != null)
                    EditorGUIUtility.PingObject(logAsset);
            }
        }

        EditorGUILayout.EndVertical();
    }

    // ============================================================
    // ROOT-SCOPED A/B COMPARISON AND CLEANUP
    // ============================================================

    private GameObject CurrentMaintenanceRoot =>
        maintenanceRoot != null ? maintenanceRoot : analysisRoot;

    private void DrawMaintenanceSection()
    {
        showMaintenance = EditorGUILayout.Foldout(
            showMaintenance, "Root A/B Comparison & Cleanup", true);
        if (!showMaintenance) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.HelpBox(
            "This operates on a selected root and all its descendants, including " +
            "inactive ones. Pairing is derived entirely from GameObject names " +
            "(_opt_pca, _opt_qem, etc.). Ambiguous duplicate matches are skipped.",
            MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        maintenanceRoot = (GameObject)EditorGUILayout.ObjectField(
            "Maintenance Root", maintenanceRoot,
            typeof(GameObject), true);
        if (GUILayout.Button("Use Selection", GUILayout.Width(105)))
        {
            if (Selection.activeGameObject != null)
            {
                maintenanceRoot = Selection.activeGameObject;
                maintenancePreviewCount = -1;
            }
        }
        EditorGUILayout.EndHorizontal();
        GameObject root = CurrentMaintenanceRoot;
        if (root == null)
        {
            EditorGUILayout.HelpBox(
                "Choose a Maintenance Root, or use the Analysis Root above.",
                MessageType.None);
            EditorGUILayout.EndVertical();
            return;
        }

        var pairs = GetValidPairs(root);
        EditorGUILayout.LabelField(
            $"Root: {root.name}  |  Reversible original/optimized pairs: {pairs.Count:N0}",
            EditorStyles.miniBoldLabel);

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(pairs.Count == 0))
        {
            if (GUILayout.Button("Show Originals", GUILayout.Height(28)))
                ScheduleEditorAction(() => ToggleRootPairs(root, true));
            if (GUILayout.Button("Show Optimized", GUILayout.Height(28)))
                ScheduleEditorAction(() => ToggleRootPairs(root, false));
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Disabled-Object Cleanup", EditorStyles.miniBoldLabel);
        cleanupScope = (CleanupScope)EditorGUILayout.EnumPopup(
            new GUIContent("Cleanup Scope",
                "Paired Disabled Originals is recommended. All Locally Disabled Descendants " +
                "also removes unrelated inactive GameObjects in this root."), cleanupScope);

        if (cleanupScope == CleanupScope.AllLocallyDisabledDescendants)
            EditorGUILayout.HelpBox(
                "Broad cleanup deletes EVERY descendant whose own checkbox is off " +
                "(activeSelf=false), including unrelated or intentionally archived objects. " +
                "A disabled parent also takes its whole subtree with it.",
                MessageType.Warning);
        else
            EditorGUILayout.HelpBox(
                "Safe scope: only disabled original GameObjects with a valid, " +
                "enabled optimized counterpart are removed. For safety, only leaf originals " +
                "are deleted in this mode. Matching audit records are marked committed.",
                MessageType.None);


        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Count Disabled Candidates"))
        {
            maintenancePreviewCount = GetCleanupCandidates(root).Count;
            maintenanceStatus =
                $"Found {maintenancePreviewCount:N0} cleanup target(s) below '{root.name}'.";
        }
        if (maintenancePreviewCount >= 0)
            GUILayout.Label($"{maintenancePreviewCount:N0} target(s)", GUILayout.Width(105));
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Delete Disabled Candidates...", GUILayout.Height(29)))
            ScheduleEditorAction(() => CleanupDisabled(root));

        EditorGUILayout.Space(6);
        if (GUILayout.Button("Migrate Legacy v5 Pair Components to Names...", GUILayout.Height(25)))
            ScheduleEditorAction(() => MigrateLegacyPairComponents(root));

        if (!string.IsNullOrWhiteSpace(maintenanceStatus))
            EditorGUILayout.HelpBox(maintenanceStatus, MessageType.None);

        EditorGUILayout.EndVertical();
    }

    // ============================================================
    // NAME-ONLY PAIRING
    // ============================================================

    // The recognized algorithm suffix is the LAST part of an optimized name.
    // Legacy v5 spellings are accepted for existing scenes, but v6 writes only
    // _opt_pca, _opt_qem, etc.
    private static readonly string[] OptimizationSuffixes =
    {
        "_opt_decomp", "_opt_fence", "_opt_cylinder", "_opt_cluster", "_opt_prefab",
        "_opt_pca", "_opt_qem", "_opt_box",
        "_fence_opt", "_cylinder_opt", "_cluster_opt", "_prefab_opt",
        "_pca_opt", "_qem_opt", "_box_opt"
    };

    private sealed class NamedPair
    {
        public GameObject original;
        public GameObject optimized;
    }

    private static bool TryExtractSourceName(string optimizedName, out string sourceName)
    {
        sourceName = null;
        if (string.IsNullOrEmpty(optimizedName)) return false;
        foreach (string suffix in OptimizationSuffixes)
        {
            if (!optimizedName.EndsWith(suffix, StringComparison.Ordinal)) continue;
            sourceName = optimizedName.Substring(0, optimizedName.Length - suffix.Length);
            return !string.IsNullOrEmpty(sourceName);
        }
        return false;
    }

    private static bool IsOptimizedName(string name) =>
        TryExtractSourceName(name, out _);

    private static List<NamedPair> GetValidPairs(GameObject root)
    {
        List<NamedPair> matches = new List<NamedPair>();
        if (root == null) return matches;

        // One linear hierarchy traversal. First pair immediate siblings. If an
        // iterative grouping moved the objects apart, match a globally UNIQUE
        // name within the root as a conservative fallback. Names with multiple
        // possible matches are skipped, never arbitrarily paired.
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        Dictionary<string, List<GameObject>> originalsByName =
            new Dictionary<string, List<GameObject>>(StringComparer.Ordinal);
        Dictionary<string, List<GameObject>> optimizedByBase =
            new Dictionary<string, List<GameObject>>(StringComparer.Ordinal);
        Dictionary<Transform, Dictionary<string, List<GameObject>>> byParent =
            new Dictionary<Transform, Dictionary<string, List<GameObject>>>();
        Dictionary<Transform, Dictionary<string, int>> optimizedCountsByParent =
            new Dictionary<Transform, Dictionary<string, int>>();

        foreach (Transform t in transforms)
        {
            if (t == null || t == root.transform) continue;
            string name = t.name;
            if (TryExtractSourceName(name, out string baseName))
            {
                if (!optimizedByBase.TryGetValue(baseName, out List<GameObject> optimized))
                    optimizedByBase[baseName] = optimized = new List<GameObject>();
                optimized.Add(t.gameObject);
                Transform parent = t.parent;
                if (!optimizedCountsByParent.TryGetValue(parent, out var perParent))
                    optimizedCountsByParent[parent] = perParent =
                        new Dictionary<string, int>(StringComparer.Ordinal);
                perParent.TryGetValue(baseName, out int prior);
                perParent[baseName] = prior + 1;
            }
            else
            {
                if (!originalsByName.TryGetValue(name, out List<GameObject> originals))
                    originalsByName[name] = originals = new List<GameObject>();
                originals.Add(t.gameObject);
                Transform parent = t.parent;
                if (!byParent.TryGetValue(parent, out Dictionary<string, List<GameObject>> lookup))
                    byParent[parent] = lookup = new Dictionary<string, List<GameObject>>(StringComparer.Ordinal);
                if (!lookup.TryGetValue(name, out List<GameObject> siblings))
                    lookup[name] = siblings = new List<GameObject>();
                siblings.Add(t.gameObject);
            }
        }

        HashSet<GameObject> usedOriginals = new HashSet<GameObject>();
        HashSet<GameObject> usedOptimized = new HashSet<GameObject>();
        foreach (var group in optimizedByBase)
        {
            foreach (GameObject candidate in group.Value)
            {
                if (candidate == null || candidate.transform.parent == null) continue;
                if (!byParent.TryGetValue(candidate.transform.parent, out var lookup) ||
                    !lookup.TryGetValue(group.Key, out var siblings) || siblings.Count != 1)
                    continue;
                // A source with more than one optimization variant is
                // ambiguous for A/B comparison, so don't toggle either.
                int localVariants = optimizedCountsByParent[candidate.transform.parent][group.Key];
                if (localVariants != 1 || usedOriginals.Contains(siblings[0])) continue;
                matches.Add(new NamedPair { original = siblings[0], optimized = candidate });
                usedOriginals.Add(siblings[0]);
                usedOptimized.Add(candidate);
            }
        }

        foreach (var group in optimizedByBase)
        {
            if (!originalsByName.TryGetValue(group.Key, out var originals)) continue;
            if (originals.Count != 1 || group.Value.Count != 1) continue;
            GameObject original = originals[0];
            GameObject optimized = group.Value[0];
            if (usedOriginals.Contains(original) || usedOptimized.Contains(optimized)) continue;
            matches.Add(new NamedPair { original = original, optimized = optimized });
        }
        return matches;
    }

    private static bool HasOptimizedCounterpart(GameObject original)
    {
        if (original == null || IsOptimizedName(original.name)) return false;
        Transform parent = original.transform.parent;
        if (parent == null) return false;
        foreach (Transform sibling in parent)
        {
            if (sibling == original.transform) continue;
            if (TryExtractSourceName(sibling.name, out string baseName) &&
                baseName == original.name)
                return true;
        }
        return false;
    }

    // When several siblings have an identical IFC name, there is no safe
    // name-only association. Assign a short stable key to the one being
    // optimized, and use its new complete name as the proxy's base name.
    private static bool EnsurePairableOriginalName(
        GameObject original,
        Dictionary<Transform, Dictionary<string, int>> counts)
    {
        if (original == null || original.transform.parent == null) return false;
        Transform parent = original.transform.parent;
        if (!counts.TryGetValue(parent, out var perParent) ||
            !perParent.TryGetValue(original.name, out int equalNames) || equalNames < 2)
            return false;

        string oldName = original.name;
        string newName;
        do
        {
            newName = oldName + "__optkey_" + Guid.NewGuid().ToString("N").Substring(0, 10);
        } while (perParent.ContainsKey(newName));

        perParent[oldName] = equalNames - 1;
        perParent[newName] = 1;
        original.name = newName;
        EditorUtility.SetDirty(original);
        return true;
    }

    // Single-pass per-parent index for batch operations: important when an
    // IFC site has thousands of same-named objects directly under one root.
    private static void BuildBatchNameIndex(
        List<GameObject> targets,
        out Dictionary<Transform, Dictionary<string, int>> sourceNameCounts,
        out Dictionary<Transform, HashSet<string>> optimizedBases)
    {
        sourceNameCounts = new Dictionary<Transform, Dictionary<string, int>>();
        optimizedBases = new Dictionary<Transform, HashSet<string>>();
        foreach (Transform parent in targets.Where(x => x != null && x.transform.parent != null)
                     .Select(x => x.transform.parent).Distinct())
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var bases = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transform child in parent)
            {
                if (TryExtractSourceName(child.name, out string name)) bases.Add(name);
                else
                {
                    counts.TryGetValue(child.name, out int n);
                    counts[child.name] = n + 1;
                }
            }
            sourceNameCounts[parent] = counts;
            optimizedBases[parent] = bases;
        }
    }

    private sealed class LegacyPairMigration
    {
        public MonoBehaviour marker;
        public GameObject source;
        public GameObject optimized;
        public string suffix;
    }

    // Optional one-time migration from v5. Uses SerializedObject rather than
    // referencing the old MonoBehaviour type, so this v6 tool remains one file.
    // Run BEFORE removing ContextOptimizationPair.cs from the Unity project.
    private void MigrateLegacyPairComponents(GameObject root)
    {
        if (root == null) return;
        List<LegacyPairMigration> legacy = new List<LegacyPairMigration>();
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            foreach (MonoBehaviour script in transform.GetComponents<MonoBehaviour>())
            {
                if (script == null || script.GetType().Name != "ContextOptimizationPair")
                    continue;
                SerializedObject so = new SerializedObject(script);
                GameObject source = so.FindProperty("original")?.objectReferenceValue as GameObject;
                if (source == null || !TryExtractSourceName(transform.name, out _)) continue;
                if (!IsUnderRoot(root.transform, source.transform)) continue;
                legacy.Add(new LegacyPairMigration
                {
                    marker = script,
                    source = source,
                    optimized = transform.gameObject,
                    suffix = NormalizeOptimizationSuffix(transform.name)
                });
            }
        }
        if (legacy.Count == 0)
        {
            maintenanceStatus = "No loadable v5 pairing components were found. " +
                                "If you already removed the script, restore it temporarily before migration.";
            return;
        }

        int ambiguousSources = legacy.GroupBy(x => x.source)
            .Count(g => g.Count() > 1);
        if (ambiguousSources > 0)
        {
            EditorUtility.DisplayDialog("Migration Blocked",
                $"Found {ambiguousSources:N0} source object(s) with multiple legacy optimized counterparts. " +
                "Resolve these manually so name-only A/B cannot pair the wrong objects.", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog("Migrate v5 Pair Components?",
            $"Convert {legacy.Count:N0} v5 pairing component(s) into name-only pairs under '{root.name}'?\n\n" +
            "The tool may add a short __optkey_ token to source names when identical IFC names " +
            "would make pairing ambiguous. Optimized names become *_opt_pca, *_opt_qem, etc. " +
            "The old marker components are then permanently deleted.\n\n" +
            "This has no Unity Undo. Save a backup scene first.",
            "Migrate Names", "Cancel")) return;

        Dictionary<string, int> originalsByName =
            new Dictionary<string, int>(StringComparer.Ordinal);
        HashSet<string> allNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            allNames.Add(transform.name);
            if (IsOptimizedName(transform.name)) continue;
            originalsByName.TryGetValue(transform.name, out int count);
            originalsByName[transform.name] = count + 1;
        }

        int migrated = 0;
        foreach (LegacyPairMigration item in legacy)
        {
            if (item.marker == null || item.source == null || item.optimized == null)
                continue;
            string oldName = item.source.name;
            bool ambiguous = originalsByName.TryGetValue(oldName, out int count) && count > 1;
            string desiredProxy = oldName + item.suffix;
            if (ambiguous || (allNames.Contains(desiredProxy) && item.optimized.name != desiredProxy))
            {
                string candidate;
                do
                {
                    candidate = oldName + "__optkey_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                } while (allNames.Contains(candidate) || allNames.Contains(candidate + item.suffix));
                allNames.Add(candidate);
                allNames.Add(candidate + item.suffix);
                originalsByName[oldName] = Mathf.Max(0, count - 1);
                originalsByName[candidate] = 1;
                item.source.name = candidate;
                EditorUtility.SetDirty(item.source);
            }
            item.optimized.name = item.source.name + item.suffix;
            EditorUtility.SetDirty(item.optimized);
            UnityEngine.Object.DestroyImmediate(item.marker);
            migrated++;
        }
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
        maintenanceStatus = $"Migrated {migrated:N0} v5 components to name-only pairs. " +
                            "You can now delete ContextOptimizationPair.cs after checking the pairs.";
        maintenancePreviewCount = -1;
        Debug.Log("Context Optimization: " + maintenanceStatus);
    }

    private static bool IsUnderRoot(Transform root, Transform child)
    {
        for (Transform current = child; current != null; current = current.parent)
            if (current == root) return true;
        return false;
    }

    private static string NormalizeOptimizationSuffix(string name)
    {
        if (name.EndsWith("_opt_decomp", StringComparison.Ordinal)) return "_opt_decomp";
        if (name.EndsWith("_fence_opt", StringComparison.Ordinal) ||
            name.EndsWith("_opt_fence", StringComparison.Ordinal)) return "_opt_fence";
        if (name.EndsWith("_pca_opt", StringComparison.Ordinal) ||
            name.EndsWith("_opt_pca", StringComparison.Ordinal)) return "_opt_pca";
        if (name.EndsWith("_qem_opt", StringComparison.Ordinal) ||
            name.EndsWith("_opt_qem", StringComparison.Ordinal)) return "_opt_qem";
        if (name.EndsWith("_cluster_opt", StringComparison.Ordinal) ||
            name.EndsWith("_opt_cluster", StringComparison.Ordinal)) return "_opt_cluster";
        if (name.EndsWith("_box_opt", StringComparison.Ordinal) ||
            name.EndsWith("_opt_box", StringComparison.Ordinal)) return "_opt_box";
        if (name.EndsWith("_cylinder_opt", StringComparison.Ordinal) ||
            name.EndsWith("_opt_cylinder", StringComparison.Ordinal)) return "_opt_cylinder";
        return "_opt_prefab";
    }

    private void ToggleRootPairs(GameObject root, bool showOriginals)
    {
        if (root == null) return;
        int touched = 0;
        foreach (NamedPair pair in GetValidPairs(root))
        {
            if (pair.original == null || pair.optimized == null) continue;
            if (showOriginals)
            {
                pair.optimized.SetActive(false);
                pair.original.SetActive(true);
                Renderer r = pair.original.GetComponent<Renderer>();
                if (r != null) r.enabled = true;
            }
            else
            {
                pair.original.SetActive(false);
                pair.optimized.SetActive(true);
                Renderer r = pair.optimized.GetComponent<Renderer>();
                if (r != null) r.enabled = true;
            }
            EditorUtility.SetDirty(pair.original);
            EditorUtility.SetDirty(pair.optimized);
            touched++;
        }
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
        maintenanceStatus =
            $"{(showOriginals ? "Original" : "Optimized")} view: toggled {touched:N0} unambiguous named pair(s).";
        maintenancePreviewCount = -1;
    }

    private List<GameObject> GetCleanupCandidates(GameObject root)
    {
        List<GameObject> candidates = new List<GameObject>();
        if (root == null) return candidates;
        if (cleanupScope == CleanupScope.PairedDisabledOriginals)
        {
            foreach (NamedPair pair in GetValidPairs(root))
                if (pair.original != root && !pair.original.activeSelf &&
                    pair.original.transform.childCount == 0 &&
                    pair.optimized.activeSelf && pair.optimized.activeInHierarchy)
                    candidates.Add(pair.original);
        }
        else
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t != null && t.gameObject != root && !t.gameObject.activeSelf)
                    candidates.Add(t.gameObject);
        }

        // Only top-most victims: destroying a parent destroys its children.
        HashSet<GameObject> set = new HashSet<GameObject>(candidates);
        return candidates.Distinct().Where(go =>
        {
            for (Transform p = go.transform.parent; p != null; p = p.parent)
                if (set.Contains(p.gameObject)) return false;
            return true;
        }).ToList();
    }

    private static bool HasAncestorInSet(Transform current, HashSet<Transform> ancestors)
    {
        for (; current != null; current = current.parent)
            if (ancestors.Contains(current)) return true;
        return false;
    }

    private void CleanupDisabled(GameObject root)
    {
        if (root == null) return;
        List<GameObject> candidates = GetCleanupCandidates(root);
        maintenancePreviewCount = candidates.Count;
        if (candidates.Count == 0)
        {
            maintenanceStatus = "No disabled objects match the chosen cleanup scope.";
            return;
        }

        bool broad = cleanupScope == CleanupScope.AllLocallyDisabledDescendants;
        bool confirmed = EditorUtility.DisplayDialog(
            "Permanently Delete Disabled Objects?",
            $"Root: {GetHierarchyPath(root.transform)}\n" +
            $"Delete {candidates.Count:N0} disabled GameObject root(s) and their children?\n\n" +
            (broad
                ? "BROAD MODE: includes every inactive descendant, even unrelated content.\n\n"
                : "PAIRED MODE: only disabled originals with enabled uniquely matched optimized counterparts.\n\n") +
            "There is NO Unity Undo. Save a backup scene first. Mesh assets and JSON logs remain.",
            "Delete Permanently", "Cancel");
        if (!confirmed) return;

        HashSet<Transform> victimAncestors = new HashSet<Transform>(
            candidates.Where(x => x != null).Select(x => x.transform));
        HashSet<string> committedIds = new HashSet<string>();
        HashSet<string> committedPaths = new HashSet<string>();
        int committed = 0;
        foreach (NamedPair pair in GetValidPairs(root))
        {
            bool sourceDeleted = HasAncestorInSet(pair.original.transform, victimAncestors);
            bool optimizedDeleted = HasAncestorInSet(pair.optimized.transform, victimAncestors);
            if (!sourceDeleted || optimizedDeleted) continue;
            committedIds.Add(GetGlobalObjectIdString(pair.optimized));
            committedPaths.Add(GetHierarchyPath(pair.optimized.transform));
            committed++;
        }

        foreach (GameObject victim in candidates)
            if (victim != null) UnityEngine.Object.DestroyImmediate(victim);

        MarkCommittedRecordsInAuditLogs(committedIds, committedPaths, root.scene.path);
        if (root != null && root.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(root.scene);
        maintenancePreviewCount = -1;
        maintenanceStatus =
            $"Deleted {candidates.Count:N0} disabled GameObject root(s); " +
            $"finalized {committed:N0} surviving optimized object(s).";
        Debug.Log("Context Optimization: " + maintenanceStatus);
    }

    private static void MarkCommittedRecordsInAuditLogs(
        HashSet<string> committedIds, HashSet<string> committedPaths, string scenePath)
    {
        string logsFolder = GeneratedBaseFolder + "/Logs";
        if (!AssetDatabase.IsValidFolder(logsFolder)) return;
        foreach (string guid in AssetDatabase.FindAssets("t:TextAsset", new[] { logsFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) continue;
            try
            {
                OptimizationLog log = JsonUtility.FromJson<OptimizationLog>(asset.text);
                if (log == null || log.records == null ||
                    (!string.IsNullOrEmpty(log.scenePath) && log.scenePath != scenePath))
                    continue;
                bool changed = false;
                foreach (OptimizationRecord record in log.records)
                {
                    bool idMatch = !string.IsNullOrWhiteSpace(record.replacementGlobalObjectId) &&
                                   committedIds.Contains(record.replacementGlobalObjectId);
                    bool pathMatch = !string.IsNullOrWhiteSpace(record.replacementHierarchyPath) &&
                                     committedPaths.Contains(record.replacementHierarchyPath);
                    if (idMatch || pathMatch)
                    {
                        record.committedByCleanup = true;
                        changed = true;
                    }
                }
                if (changed) WriteOptimizationLog(path, log);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not mark optimization audit in {path}: {ex.Message}");
            }
        }
    }

    // ============================================================
    // BATCH PREVIEW / REVERT
    // ============================================================

    private bool HasReadableLastLog()
    {
        if (!string.IsNullOrWhiteSpace(lastLogAssetPath) &&
            AssetDatabase.LoadAssetAtPath<TextAsset>(
                lastLogAssetPath) != null)
        {
            if (string.IsNullOrWhiteSpace(lastBatchName))
            {
                TryLoadLastLog(
                    out _,
                    out _
                );
            }

            return true;
        }

        string logsFolder =
            GeneratedBaseFolder + "/Logs";

        if (!AssetDatabase.IsValidFolder(logsFolder))
            return false;

        string[] guids =
            AssetDatabase.FindAssets(
                "Batch_ t:TextAsset",
                new[] { logsFolder }
            );

        string latest = guids
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(x =>
                !string.IsNullOrWhiteSpace(x) &&
                x.EndsWith(
                    ".json",
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(latest))
            return false;

        lastLogAssetPath = latest;

        TryLoadLastLog(
            out _,
            out _
        );

        return true;
    }

    private bool TryLoadLastLog(
        out OptimizationLog log,
        out string assetPath)
    {
        log = null;
        assetPath = lastLogAssetPath;

        if (string.IsNullOrWhiteSpace(assetPath) ||
            AssetDatabase.LoadAssetAtPath<TextAsset>(
                assetPath) == null)
        {
            string logsFolder =
                GeneratedBaseFolder + "/Logs";

            if (!AssetDatabase.IsValidFolder(logsFolder))
                return false;

            string[] guids =
                AssetDatabase.FindAssets(
                    "Batch_ t:TextAsset",
                    new[] { logsFolder }
                );

            assetPath = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x) &&
                    x.EndsWith(
                        ".json",
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(assetPath))
                return false;
        }

        try
        {
            TextAsset logAsset =
                AssetDatabase.LoadAssetAtPath<TextAsset>(
                    assetPath
                );

            if (logAsset == null)
                return false;

            log = JsonUtility.FromJson<OptimizationLog>(
                logAsset.text
            );

            if (log == null)
                return false;

            lastLogAssetPath = assetPath;
            lastBatchName = log.batchName;
            lastBatchSourceTriangles =
                log.records.Sum(x => x.originalTriangles);
            lastBatchReplacementTriangles =
                log.records.Sum(x => x.replacementTriangles);
            lastBatchReverted = log.reverted;

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "Could not read optimization log: " +
                ex.Message
            );

            return false;
        }
    }

    private static void WriteOptimizationLog(
        string assetPath,
        OptimizationLog log)
    {
        string json =
            JsonUtility.ToJson(log, true);

        string projectRoot =
            Directory.GetParent(
                Application.dataPath
            )?.FullName ?? "";

        string absolutePath =
            Path.Combine(
                projectRoot,
                assetPath
            );

        File.WriteAllText(
            absolutePath,
            json
        );

        AssetDatabase.ImportAsset(assetPath);
        AssetDatabase.SaveAssets();
    }

    private void SetLastBatchPreview(
        BatchPreviewMode mode)
    {
        if (!TryLoadLastLog(
                out OptimizationLog log,
                out _))
        {
            return;
        }

        if (log.reverted)
        {
            lastStatusMessage =
                "The last batch has already been reverted.";

            return;
        }

        int touched = 0;

        foreach (OptimizationRecord record in log.records)
        {
            GameObject original =
                ResolveGameObject(
                    record.originalGlobalObjectId,
                    record.originalHierarchyPath
                );

            GameObject replacement =
                ResolveGameObject(
                    record.replacementGlobalObjectId,
                    record.replacementHierarchyPath
                );

            if (replacement != null)
            {
                replacement.SetActive(
                    mode != BatchPreviewMode.Original
                );

                EditorUtility.SetDirty(replacement);
                touched++;
            }

            if (original == null)
                continue;


            Renderer originalRenderer =
                original.GetComponent<Renderer>();

            switch (mode)
            {
                case BatchPreviewMode.Original:
                    original.SetActive(
                        record.originalActiveSelf
                    );

                    if (originalRenderer != null)
                    {
                        originalRenderer.enabled =
                            record.originalRendererEnabled;
                    }
                    break;

                case BatchPreviewMode.Optimized:
                    // A/B comparison: hide the source whenever there is a
                    // replacement. Disable-only operations remain disabled.
                    if (replacement != null ||
                        record.requestedAction ==
                        ProcessingAction.DisableOnly.ToString())
                    {
                        original.SetActive(false);
                    }
                    else
                    {
                        original.SetActive(
                            record.originalActiveSelf
                        );
                    }
                    break;

                case BatchPreviewMode.Applied:
                    ApplyRecordedState(
                        original,
                        originalRenderer,
                        record
                    );
                    break;
            }

            EditorUtility.SetDirty(original);
            touched++;
        }

        MarkAnalysisSceneDirty();

        lastStatusMessage =
            $"Last batch preview set to {mode}. " +
            $"{touched:N0} scene object(s) touched.";
    }

    private void RevertLastBatch()
    {
        if (!TryLoadLastLog(
                out OptimizationLog log,
                out string logPath))
        {
            return;
        }

        if (log.reverted)
        {
            lastStatusMessage =
                "The last batch is already marked as reverted.";
            return;
        }

        if (log.records.Any(r => r.committedByCleanup))
        {
            EditorUtility.DisplayDialog("Cannot Revert Finalized Batch",
                "This batch contains optimized objects committed by disabled-original " +
                "cleanup. The original geometry no longer exists in this scene. " +
                "Restore a backup instead of reverting the batch.", "OK");
            return;
        }

        // A committed proxy has no source to restore. This guard and the
        // JSON audit work without any pairing MonoBehaviour.
        foreach (OptimizationRecord record in log.records)
        {
            GameObject replacement = ResolveGameObject(
                record.replacementGlobalObjectId, record.replacementHierarchyPath);
            if (replacement == null) continue;
            GameObject source = ResolveGameObject(
                record.originalGlobalObjectId, record.originalHierarchyPath);
            if (source == null && !string.IsNullOrEmpty(record.replacementType))
            {
                EditorUtility.DisplayDialog("Cannot Revert Committed Proxy",
                    "One or more originals in this batch are missing or committed. " +
                    "Reverting would risk deleting the only remaining geometry. " +
                    "Restore a backed-up scene instead.", "OK");
                return;
            }
        }

        bool containsDestructiveDeletion =
            log.records.Any(x =>
                x.originalDispositionApplied ==
                    OriginalDisposition.Delete.ToString() ||
                x.requestedAction ==
                    ProcessingAction.DeleteOnly.ToString());

        if (containsDestructiveDeletion)
        {
            bool continuePartial =
                EditorUtility.DisplayDialog(
                    "Partial Revert Only",
                    "This batch deleted one or more original GameObjects. " +
                    "The audit log cannot reconstruct arbitrary IFC components/metadata after deletion.\n\n" +
                    "I can still remove generated replacements and restore any originals that still exist.\n\nContinue with a partial revert?",
                    "Partial Revert",
                    "Cancel"
                );

            if (!continuePartial)
                return;
        }
        else
        {
            bool confirmed =
                EditorUtility.DisplayDialog(
                    "Revert Last Optimization Batch",
                    $"Revert {log.batchName}?\n\n" +
                    "Original active/renderer states will be restored and generated replacements/assets from this batch will be removed.\n\nNo Unity Undo is recorded; save a backup first.",
                    "Revert",
                    "Cancel"
                );

            if (!confirmed)
                return;
        }

        int restored = 0;
        int removedReplacements = 0;
        HashSet<string> generatedMeshAssets =
            new HashSet<string>();


        foreach (OptimizationRecord record in log.records)
        {
            GameObject original =
                ResolveGameObject(
                    record.originalGlobalObjectId,
                    record.originalHierarchyPath
                );

            if (original != null)
            {
                original.SetActive(
                    record.originalActiveSelf
                );

                Renderer renderer =
                    original.GetComponent<Renderer>();

                if (renderer != null)
                {
                    renderer.enabled =
                        record.originalRendererEnabled;

                    EditorUtility.SetDirty(renderer);
                }

                if (record.originalRenamedForPairing &&
                    !string.IsNullOrWhiteSpace(record.originalName))
                    original.name = record.originalName;
                EditorUtility.SetDirty(original);
                restored++;
            }

            GameObject replacement =
                ResolveGameObject(
                    record.replacementGlobalObjectId,
                    record.replacementHierarchyPath
                );

            if (replacement != null)
            {
                UnityEngine.Object.DestroyImmediate(replacement);
                removedReplacements++;
            }

            if (!string.IsNullOrWhiteSpace(
                    record.replacementMeshAssetPath) &&
                record.replacementMeshAssetPath.StartsWith(
                    GeneratedBaseFolder + "/Meshes/",
                    StringComparison.Ordinal))
            {
                generatedMeshAssets.Add(
                    record.replacementMeshAssetPath
                );
            }
        }

        GameObject batchRoot =
            ResolveGameObject(
                log.batchRootGlobalObjectId
            );

        if (batchRoot == null &&
            !string.IsNullOrWhiteSpace(log.generatedRootName) &&
            !string.IsNullOrWhiteSpace(log.batchName))
        {
            GameObject generatedRoot =
                FindRootObjectByName(
                    log.generatedRootName
                );

            if (generatedRoot != null)
            {
                Transform child =
                    generatedRoot.transform.Find(
                        log.batchName
                    );

                if (child != null)
                    batchRoot = child.gameObject;
            }
        }

        if (batchRoot != null)
            UnityEngine.Object.DestroyImmediate(batchRoot);


        foreach (string assetPath in generatedMeshAssets)
        {
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);
        }

        string batchMeshFolder =
            GeneratedBaseFolder +
            "/Meshes/" +
            log.batchName;

        if (AssetDatabase.IsValidFolder(batchMeshFolder))
            AssetDatabase.DeleteAsset(batchMeshFolder);

        log.reverted = true;
        log.revertedTimestamp =
            DateTime.Now.ToString("O");

        WriteOptimizationLog(logPath, log);
        lastBatchReverted = true;

        MarkAnalysisSceneDirty();

        string completion =
            $"Reverted {log.batchName}: restored {restored:N0} original(s), " +
            $"removed {removedReplacements:N0} replacement(s).";

        Debug.Log(completion);

        Analyze();
        lastStatusMessage = completion;
    }

    private static void ApplyRecordedState(
        GameObject original,
        Renderer renderer,
        OptimizationRecord record)
    {
        if (original == null)
            return;

        string disposition =
            record.originalDispositionApplied;

        if (disposition ==
                OriginalDisposition.Disable.ToString() ||
            disposition ==
                OriginalDisposition.Delete.ToString() ||
            record.requestedAction ==
                ProcessingAction.DisableOnly.ToString() ||
            record.requestedAction ==
                ProcessingAction.DeleteOnly.ToString())
        {
            original.SetActive(false);
            return;
        }

        original.SetActive(
            record.originalActiveSelf
        );

        if (renderer != null)
        {
            renderer.enabled =
                record.originalRendererEnabled;
        }
    }

    private static GameObject ResolveGameObject(
        string globalObjectIdString,
        string hierarchyPath = null)
    {
        if (!string.IsNullOrWhiteSpace(
                globalObjectIdString) &&
            GlobalObjectId.TryParse(
                globalObjectIdString,
                out GlobalObjectId id))
        {
            GameObject byId =
                GlobalObjectId
                    .GlobalObjectIdentifierToObjectSlow(id)
                    as GameObject;

            if (byId != null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(
                hierarchyPath))
        {
            return FindByHierarchyPath(
                hierarchyPath
            );
        }

        return null;
    }

    private static GameObject FindByHierarchyPath(
        string hierarchyPath)
    {
        if (string.IsNullOrWhiteSpace(hierarchyPath))
            return null;

        string[] parts =
            hierarchyPath.Split('/');

        if (parts.Length == 0)
            return null;

        for (int sceneIndex = 0;
             sceneIndex < SceneManager.sceneCount;
             sceneIndex++)
        {
            Scene scene =
                SceneManager.GetSceneAt(sceneIndex);

            if (!scene.isLoaded)
                continue;

            GameObject root =
                scene.GetRootGameObjects()
                    .FirstOrDefault(x =>
                        x.name == parts[0]);

            if (root == null)
                continue;

            Transform current =
                root.transform;

            bool valid = true;

            for (int i = 1;
                 i < parts.Length;
                 i++)
            {
                Transform next =
                    current.Find(parts[i]);

                if (next == null)
                {
                    valid = false;
                    break;
                }

                current = next;
            }

            if (valid)
                return current.gameObject;
        }

        return null;
    }

    private static GameObject FindRootObjectByName(
        string rootName)
    {
        for (int sceneIndex = 0;
             sceneIndex < SceneManager.sceneCount;
             sceneIndex++)
        {
            Scene scene =
                SceneManager.GetSceneAt(sceneIndex);

            if (!scene.isLoaded)
                continue;

            GameObject root =
                scene.GetRootGameObjects()
                    .FirstOrDefault(x =>
                        x.name == rootName);

            if (root != null)
                return root;
        }

        return null;
    }

    private void MarkAnalysisSceneDirty()
    {
        if (analysisRoot != null &&
            analysisRoot.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(
                analysisRoot.scene
            );

            return;
        }

        Scene scene =
            SceneManager.GetActiveScene();

        if (scene.IsValid())
            EditorSceneManager.MarkSceneDirty(scene);
    }

    // ============================================================
    // ANALYSIS
    // ============================================================

    private void Analyze()
    {
        objectStats.Clear();
        candidateStats.Clear();
        searchResultStats.Clear();
        categoryStats.Clear();
        checkedCandidateIds.Clear();

        totalTriangles = 0;
        totalVertices = 0;
        totalMeshInstances = 0;
        totalUniqueMeshes = 0;
        totalRenderers = 0;
        totalMaterialSlots = 0;

        if (analysisRoot == null)
            return;

        HashSet<Mesh> uniqueMeshes = new();

        // First pass: collect every eligible object. Candidate selection is a
        // second pass so aggregate-family cost can influence the result.
        MeshFilter[] filters =
            analysisRoot.GetComponentsInChildren<MeshFilter>(
                searchInactiveObjects
            );

        foreach (MeshFilter filter in filters)
        {
            GameObject go = filter.gameObject;

            if (!ShouldAnalyze(go))
                continue;

            Mesh mesh = filter.sharedMesh;
            if (mesh == null)
                continue;

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();

            if (onlyEnabledRenderers &&
                (renderer == null || !renderer.enabled))
                continue;

            ObjectStats stat = BuildStats(go, mesh, renderer);
            stat.category = GenerateCategoryName(go.name);
            stat.matchesSearch = MatchesSearch(stat);

            objectStats.Add(stat);

            totalTriangles += stat.triangles;
            totalVertices += stat.vertices;
            totalMeshInstances++;
            uniqueMeshes.Add(mesh);

            if (renderer != null)
            {
                totalRenderers++;
                totalMaterialSlots += stat.materials;
            }
        }

        SkinnedMeshRenderer[] skinnedRenderers =
            analysisRoot.GetComponentsInChildren<SkinnedMeshRenderer>(
                searchInactiveObjects
            );

        foreach (SkinnedMeshRenderer renderer in skinnedRenderers)
        {
            GameObject go = renderer.gameObject;

            if (!ShouldAnalyze(go))
                continue;

            if (onlyEnabledRenderers && !renderer.enabled)
                continue;

            Mesh mesh = renderer.sharedMesh;
            if (mesh == null)
                continue;

            ObjectStats stat = BuildStats(go, mesh, renderer);
            stat.skinned = true;
            stat.category = GenerateCategoryName(go.name);
            stat.matchesSearch = MatchesSearch(stat);

            objectStats.Add(stat);

            totalTriangles += stat.triangles;
            totalVertices += stat.vertices;
            totalMeshInstances++;
            uniqueMeshes.Add(mesh);
            totalRenderers++;
            totalMaterialSlots += stat.materials;
        }

        totalUniqueMeshes = uniqueMeshes.Count;

        Dictionary<string, CategoryStats> allCategories = new();

        foreach (ObjectStats stat in objectStats)
        {
            if (!allCategories.TryGetValue(
                    stat.category,
                    out CategoryStats category))
            {
                category = new CategoryStats
                {
                    categoryName = stat.category
                };

                allCategories.Add(stat.category, category);
            }

            category.instances++;
            category.triangles += stat.triangles;
            category.vertices += stat.vertices;
            category.objects.Add(stat.gameObject);
            category.stats.Add(stat);

            if (stat.matchesSearch)
            {
                category.containsSearchMatch = true;
                searchResultStats.Add(stat);
            }
        }

        foreach (CategoryStats category in allCategories.Values)
        {
            category.isAggregateCandidate =
                flagAggregateGroups &&
                (category.triangles >= minimumAggregateTriangles ||
                 category.instances >= minimumAggregateInstances);

            bool categoryHasCandidate = false;

            foreach (ObjectStats stat in category.stats)
            {
                bool dense = IsCandidate(stat);
                bool searchCandidate =
                    stat.matchesSearch &&
                    (searchMatchesBypassDensityThreshold ||
                     prioritizeSearchMatches);

                bool candidate =
                    dense ||
                    searchCandidate ||
                    category.isAggregateCandidate;

                if (!candidate)
                    continue;

                if (prioritizeSearchMatches && stat.matchesSearch)
                    stat.candidateReason = "Priority search";
                else if (dense)
                    stat.candidateReason = "Density";
                else if (searchCandidate)
                    stat.candidateReason = "Search";
                else
                    stat.candidateReason = "Aggregate family";

                candidateStats.Add(stat);
                category.candidateObjects.Add(stat.gameObject);
                categoryHasCandidate = true;
            }

            if (categoryHasCandidate)
                categoryStats.Add(category);
        }

        bool searchPriorityActive =
            prioritizeSearchMatches &&
            !string.IsNullOrWhiteSpace(searchText);

        objectStats.Sort(
            (a, b) => CompareObjectStatsForDisplay(a, b, searchPriorityActive)
        );

        candidateStats.Sort(
            (a, b) => CompareObjectStatsForDisplay(a, b, searchPriorityActive)
        );

        searchResultStats.Sort(
            (a, b) => b.triangles.CompareTo(a.triangles)
        );

        categoryStats.Sort(
            (a, b) => CompareCategoriesForDisplay(a, b, searchPriorityActive)
        );

        // Carry priority search directly into Layer 2.
        if (searchPriorityActive && searchResultStats.Count > 0 &&
            processingSource != ProcessingSource.CurrentUnitySelection)
        {
            checkedCandidateIds.Clear();

            foreach (ObjectStats stat in searchResultStats)
            {
                if (stat.gameObject != null)
                    checkedCandidateIds.Add(stat.gameObject.GetEntityId());
            }

            processingSource = ProcessingSource.CurrentSearchResults;
        }

        lastStatusMessage =
            $"Analysis complete: {candidateStats.Count:N0} optimization candidate(s), " +
            $"{searchResultStats.Count:N0} search result(s)." +
            (searchPriorityActive
                ? " Search priority is active."
                : "");

        Repaint();
    }

    private static int CompareObjectStatsForDisplay(
        ObjectStats a,
        ObjectStats b,
        bool prioritizeSearch)
    {
        if (prioritizeSearch)
        {
            int searchCompare =
                b.matchesSearch.CompareTo(a.matchesSearch);

            if (searchCompare != 0)
                return searchCompare;
        }

        int triangleCompare =
            b.triangles.CompareTo(a.triangles);

        if (triangleCompare != 0)
            return triangleCompare;

        return string.Compare(
            a.gameObject != null ? a.gameObject.name : "",
            b.gameObject != null ? b.gameObject.name : "",
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static int CompareCategoriesForDisplay(
        CategoryStats a,
        CategoryStats b,
        bool prioritizeSearch)
    {
        if (prioritizeSearch)
        {
            int searchCompare =
                b.containsSearchMatch.CompareTo(a.containsSearchMatch);

            if (searchCompare != 0)
                return searchCompare;
        }

        int triangleCompare =
            b.triangles.CompareTo(a.triangles);

        if (triangleCompare != 0)
            return triangleCompare;

        return string.Compare(
            a.categoryName,
            b.categoryName,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private bool ShouldAnalyze(GameObject go)
    {
        if (onlyActiveInHierarchy && !go.activeInHierarchy)
            return false;

        return true;
    }

    private ObjectStats BuildStats(
        GameObject go,
        Mesh mesh,
        Renderer renderer)
    {
        return new ObjectStats
        {
            gameObject = go,
            mesh = mesh,
            triangles = GetTriangleCount(mesh),
            vertices = mesh.vertexCount,
            subMeshes = mesh.subMeshCount,
            materials = renderer != null
                ? renderer.sharedMaterials.Length
                : 0
        };
    }

    private bool MatchesSearch(ObjectStats stat)
    {
        if (stat == null || stat.gameObject == null)
            return false;

        if (string.IsNullOrWhiteSpace(searchText))
            return false;

        List<string> haystacks = new();

        if (searchGameObjectNames)
            haystacks.Add(stat.gameObject.name ?? "");

        if (searchMeshNames && stat.mesh != null)
            haystacks.Add(stat.mesh.name ?? "");

        if (searchMaterialNames)
        {
            Renderer renderer = stat.gameObject.GetComponent<Renderer>();

            if (renderer != null)
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null)
                        haystacks.Add(material.name ?? "");
                }
            }
        }

        StringComparison comparison =
            searchCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

        foreach (string value in haystacks)
        {
            switch (searchMatchMode)
            {
                case SearchMatchMode.Contains:
                    if (value.IndexOf(searchText, comparison) >= 0)
                        return true;
                    break;

                case SearchMatchMode.StartsWith:
                    if (value.StartsWith(searchText, comparison))
                        return true;
                    break;

                case SearchMatchMode.Exact:
                    if (string.Equals(value, searchText, comparison))
                        return true;
                    break;

                case SearchMatchMode.Regex:
                    try
                    {
                        RegexOptions options =
                            searchCaseSensitive
                                ? RegexOptions.None
                                : RegexOptions.IgnoreCase;

                        if (Regex.IsMatch(value, searchText, options))
                            return true;
                    }
                    catch (ArgumentException)
                    {
                        // Invalid regex: simply report no match. The UI status
                        // below will still show zero search results.
                        return false;
                    }
                    break;
            }
        }

        return false;
    }

    private bool IsCandidate(ObjectStats stat)
    {
        return candidateMetric switch
        {
            CandidateMetric.Triangles =>
                stat.triangles >= minimumTriangles,

            CandidateMetric.Vertices =>
                stat.vertices >= minimumVertices,

            CandidateMetric.Either =>
                stat.triangles >= minimumTriangles ||
                stat.vertices >= minimumVertices,

            _ => false
        };
    }

    private string GenerateCategoryName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return "<Unnamed>";

        string[] tokens = Regex.Split(
                objectName,
                @"[-_\s]+"
            )
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (tokens.Length == 0)
            return objectName;

        int count = Mathf.Min(groupTokenCount, tokens.Length);

        return string.Join("-", tokens.Take(count));
    }

    // ============================================================
    // PROCESSING
    // ============================================================

    private void ApplyOperation(List<GameObject> capturedTargets = null)
    {
        List<GameObject> targets = capturedTargets != null
            ? capturedTargets.Where(x => x != null).Distinct().ToList()
            : GetProcessingSet(showWarnings: true);

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Context Optimization",
                "There are no valid objects to process.",
                "OK"
            );
            return;
        }

        bool createsReplacement =
            processingAction == ProcessingAction.ProcessMeshGeometry ||
            processingAction == ProcessingAction.ReplaceBoundsBox ||
            processingAction == ProcessingAction.ReplaceLowPolyCylinder ||
            processingAction == ProcessingAction.ReplaceProceduralFence ||
            processingAction == ProcessingAction.ReplaceBoxDecomposition ||
            processingAction == ProcessingAction.ReplaceCustomPrefab;

        bool destructive =
            processingAction == ProcessingAction.DeleteOnly ||
            (createsReplacement &&
             replacementOriginalDisposition == OriginalDisposition.Delete);

        if (destructive)
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "Destructive Operation",
                "This operation will delete original scene GameObjects.\n\n" +
                "The JSON audit log cannot reconstruct arbitrary IFC metadata/components after deletion. " +
                "The batch-revert feature is fully reliable only when originals are kept or disabled.\n\n" +
                "Disable is safer while iterating.\n\nContinue?",
                "Delete Originals",
                "Cancel"
            );

            if (!confirmed)
                return;
        }

        // Capture scene handles before processing because the action may
        // destroy original GameObjects and invalidate references in targets.
        List<Scene> targetScenes = targets
            .Where(x => x != null && x.scene.IsValid())
            .Select(x => x.scene)
            .Distinct()
            .ToList();

        string batchId = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string batchName = "Batch_" + batchId;

        EnsureAssetFolder(GeneratedBaseFolder);
        EnsureAssetFolder(GeneratedBaseFolder + "/Logs");
        EnsureAssetFolder(GeneratedBaseFolder + "/Meshes");

        string meshBatchFolder =
            GeneratedBaseFolder + "/Meshes/" + batchName;

        if (createsReplacement)
            EnsureAssetFolder(meshBatchFolder);

        // No scene-level __OPTIMIZED_CONTEXT__ root is created in v5.
        // All replacements are placed as siblings of their original objects.

        OptimizationLog log = new OptimizationLog
        {
            toolVersion = ToolVersion,
            localTimestamp = DateTime.Now.ToString("O"),
            scenePath = targets.Count > 0 && targets[0] != null &&
                        targets[0].scene.IsValid()
                ? targets[0].scene.path
                : SceneManager.GetActiveScene().path,
            batchName = batchName,
            generatedRootName = "",
            batchRootGlobalObjectId = "",
            action = processingAction.ToString(),
            originalDisposition = createsReplacement
                ? replacementOriginalDisposition.ToString()
                : processingAction.ToString(),
            keepTriangleRatio =
                geometryAlgorithm == GeometryProcessingAlgorithm.ConstrainedQemEdgeCollapse
                    ? qemTargetTriangleRatio
                    : -1f,
            simplifierMode = geometryAlgorithm.ToString(),

            geometryAlgorithm = geometryAlgorithm.ToString(),

            qemTargetTriangleRatio = qemTargetTriangleRatio,
            qemWeldToleranceRelative = qemWeldToleranceRelative,
            qemProtectOpenBoundaries = qemProtectOpenBoundaries,
            qemProtectMaterialBoundaries = qemProtectMaterialBoundaries,
            qemProtectUvSeams = qemProtectUvSeams,
            qemProtectSharpEdges = qemProtectSharpEdges,
            qemSharpEdgeAngle = qemSharpEdgeAngle,
            qemMaxFaceNormalDeviation = qemMaxFaceNormalDeviation,

            clusterGridResolution = clusterGridResolution,
            clusterProtectSurfaceNormals = clusterProtectSurfaceNormals,
            clusterNormalMergeAngle = clusterNormalMergeAngle,

            proxyWeldToleranceRelative = proxyWeldToleranceRelative,
            proxyMaxSegmentLengthRelative = proxyMaxSegmentLengthRelative,
            proxyMaxSegmentsPerComponent = proxyMaxSegmentsPerComponent,
            proxyElongatedAspectThreshold = proxyElongatedAspectThreshold,
            proxyPanelThicknessRatio = proxyPanelThicknessRatio,
            proxyMinimumComponentSizeRelative = proxyMinimumComponentSizeRelative,
            proxyBeamCrossSectionSides = proxyBeamCrossSectionSides,
            proxyUsePrismsForElongatedComponents = proxyUsePrismsForElongatedComponents,
            proxySplitComponentsByMaterial = proxySplitComponentsByMaterial,
            proxyPreserveSubmeshMaterials = proxyPreserveSubmeshMaterials,

            searchText = searchText,
            prioritizeSearchMatches = prioritizeSearchMatches,
            reverted = false
        };

        // Build the source-name and optimized-suffix lookup once per parent.
        // On IFC hierarchies this avoids quadratic sibling scans.
        Dictionary<Transform, Dictionary<string, int>> sourceNameCounts = null;
        Dictionary<Transform, HashSet<string>> optimizedBases = null;
        // Also detect pairs that a later hierarchy-organizing pass moved
        // apart. The name-only matcher handles globally unique source keys.
        HashSet<GameObject> alreadyPairedOriginals = new HashSet<GameObject>();
        if (createsReplacement)
        {
            BuildBatchNameIndex(targets, out sourceNameCounts, out optimizedBases);
            for (int si = 0; si < SceneManager.sceneCount; si++)
            {
                Scene scene = SceneManager.GetSceneAt(si);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (NamedPair pair in GetValidPairs(root))
                        if (pair.original != null) alreadyPairedOriginals.Add(pair.original);
            }
        }

        Dictionary<Mesh, Mesh> simplifiedCache = new();
        Dictionary<Mesh, string> simplifiedAssetPathCache = new();

        Dictionary<Mesh, Mesh> boxCache = new();
        Dictionary<Mesh, string> boxAssetPathCache = new();

        Dictionary<Mesh, Mesh> fenceCache = new();
        Dictionary<Mesh, string> fenceAssetPathCache = new();

        Dictionary<Mesh, Mesh> cylinderCache = new();
        Dictionary<Mesh, string> cylinderAssetPathCache = new();

        int successCount = 0;
        int skippedCount = 0;
        int failedCount = 0;


        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                GameObject original = targets[i];

                if (original == null)
                {
                    skippedCount++;
                    continue;
                }
                if (createsReplacement &&
                    (alreadyPairedOriginals.Contains(original) ||
                     (original.transform.parent == null
                        ? HasOptimizedCounterpart(original)
                        : (optimizedBases.TryGetValue(original.transform.parent, out var existingBases) &&
                           existingBases.Contains(original.name)))))
                {
                    skippedCount++;
                    continue;
                }

                float progress =
                    targets.Count <= 1
                        ? 1f
                        : i / (float)(targets.Count - 1);

                EditorUtility.DisplayProgressBar(
                    "Context Optimization",
                    $"Processing {i + 1:N0}/{targets.Count:N0}: {original.name}",
                    progress
                );

                if (createsReplacement && !autoKeyDuplicateSourceNames &&
                    original.transform.parent != null &&
                    sourceNameCounts.TryGetValue(original.transform.parent, out var perParentCounts) &&
                    perParentCounts.TryGetValue(original.name, out int sameNameCount) &&
                    sameNameCount > 1)
                {
                    skippedCount++;
                    continue;
                }

                OptimizationRecord record =
                    CaptureOriginalRecord(original);

                record.requestedAction = processingAction.ToString();
                record.batchName = batchName;
                // No MonoBehaviour or identifier component is attached to replacements.
                // A unique source-name key is added only for ambiguous duplicate IFC names.
                bool renamedForPairing = createsReplacement && autoKeyDuplicateSourceNames &&
                                         EnsurePairableOriginalName(original, sourceNameCounts);
                record.originalRenamedForPairing = renamedForPairing;
                if (renamedForPairing)
                    record.originalHierarchyPath = GetHierarchyPath(original.transform);

                bool success;

                switch (processingAction)
                {
                    case ProcessingAction.DisableOnly:
                        success = DisableOriginal(original, record);
                        break;

                    case ProcessingAction.DeleteOnly:
                        success = DeleteOriginal(original, record);
                        break;

                    case ProcessingAction.ProcessMeshGeometry:
                        success = ReplaceWithProcessedMesh(
                            original,
                            null,
                            meshBatchFolder,
                            simplifiedCache,
                            simplifiedAssetPathCache,
                            record
                        );
                        break;

                    case ProcessingAction.ReplaceBoundsBox:
                        success = ReplaceWithBoundsBox(
                            original,
                            null,
                            meshBatchFolder,
                            boxCache,
                            boxAssetPathCache,
                            record
                        );
                        break;

                    case ProcessingAction.ReplaceLowPolyCylinder:
                        success = ReplaceWithLowPolyCylinder(
                            original,
                            null,
                            meshBatchFolder,
                            cylinderCache,
                            cylinderAssetPathCache,
                            record
                        );
                        break;

                    case ProcessingAction.ReplaceProceduralFence:
                        success = ReplaceWithProceduralFence(
                            original, meshBatchFolder,
                            fenceCache, fenceAssetPathCache, record);
                        break;

                    case ProcessingAction.ReplaceBoxDecomposition:
                        success = ReplaceWithBoxDecomposition(original, meshBatchFolder,
                            record);
                        break;

                    case ProcessingAction.ReplaceCustomPrefab:
                        success = ReplaceWithCustomPrefab(
                            original,
                            null,
                            record
                        );
                        break;

                    default:
                        success = false;
                        record.status = "Unsupported action.";
                        break;
                }

                if (!success && renamedForPairing && original != null)
                {
                    var counts = sourceNameCounts[original.transform.parent];
                    counts.Remove(original.name);
                    counts.TryGetValue(record.originalName, out int prior);
                    counts[record.originalName] = prior + 1;
                    original.name = record.originalName;
                    EditorUtility.SetDirty(original);
                }
                if (success && createsReplacement && original != null &&
                    original.transform.parent != null)
                {
                    optimizedBases[original.transform.parent].Add(original.name);
                }

                if (success && createsReplacement)
                {
                    ApplyOriginalDisposition(
                        original,
                        replacementOriginalDisposition,
                        record
                    );
                }

                if (success)
                    successCount++;
                else if (!string.IsNullOrEmpty(record.status) &&
                         record.status.StartsWith(
                             "Skipped",
                             StringComparison.OrdinalIgnoreCase))
                    skippedCount++;
                else
                    failedCount++;

                log.records.Add(record);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();

        }

        // In hierarchy-selection mode, the target scene need not be the same
        // scene as the analysis root. Mark every processed scene dirty.
        foreach (Scene scene in targetScenes)
        {
            if (scene.IsValid() && scene.isLoaded)
                EditorSceneManager.MarkSceneDirty(scene);
        }

        string logAssetPath =
            GeneratedBaseFolder + "/Logs/" + batchName + ".json";

        WriteOptimizationLog(logAssetPath, log);

        lastLogAssetPath = logAssetPath;
        lastBatchName = batchName;
        lastBatchSourceTriangles =
            log.records.Sum(x => x.originalTriangles);
        lastBatchReplacementTriangles =
            log.records.Sum(x => x.replacementTriangles);
        lastBatchReverted = false;

        string completionMessage =
            $"Batch complete. Success: {successCount:N0}, " +
            $"Skipped: {skippedCount:N0}, Failed: {failedCount:N0}. " +
            $"Audit log: {logAssetPath}";

        Debug.Log(completionMessage);

        Analyze();
        lastStatusMessage = completionMessage;
    }

    private List<GameObject> GetProcessingSet(bool showWarnings)
    {
        IEnumerable<GameObject> source;

        switch (processingSource)
        {
            case ProcessingSource.CheckedCandidates:
                source = candidateStats
                    .Where(x =>
                        x.gameObject != null &&
                        checkedCandidateIds.Contains(
                            x.gameObject.GetEntityId()))
                    .Select(x => x.gameObject);
                break;

            case ProcessingSource.CurrentSearchResults:
                source = searchResultStats
                    .Where(x => x.gameObject != null)
                    .Select(x => x.gameObject);
                break;

            case ProcessingSource.CurrentUnitySelection:
            default:
            {
                // Explicit hierarchy root takes precedence. Otherwise follow
                // the Hierarchy's current selection, as before.
                IEnumerable<GameObject> roots = selectionHierarchyRoot != null
                    ? new[] { selectionHierarchyRoot }
                    : Selection.gameObjects;

                source = expandUnitySelectionToMeshChildren
                    ? CollectSelectionMeshDescendants(roots)
                    : roots;
                break;
            }
        }

        List<GameObject> result = source
            .Where(x => x != null)
            .Distinct()
            .ToList();

        // Never optimize proxies a second time by accidentally expanding a
        // high-level selected parent. All previously generated proxies use
        // algorithm suffixes such as _opt_pca or _opt_qem.
        result = result.Where(x => !IsOptimizedName(x.name)).ToList();

        bool requiresStaticMesh =
            processingAction == ProcessingAction.ProcessMeshGeometry ||
            processingAction == ProcessingAction.ReplaceBoundsBox ||
            processingAction == ProcessingAction.ReplaceLowPolyCylinder ||
            processingAction == ProcessingAction.ReplaceProceduralFence ||
            processingAction == ProcessingAction.ReplaceBoxDecomposition;

        if (requiresStaticMesh)
        {
            int before = result.Count;

            result = result
                .Where(x =>
                    x.TryGetComponent<MeshFilter>(out MeshFilter filter) &&
                    filter.sharedMesh != null &&
                    x.TryGetComponent<MeshRenderer>(out MeshRenderer renderer))
                .ToList();

            if (showWarnings && before != result.Count)
            {
                Debug.LogWarning(
                    $"{before - result.Count} selected object(s) were ignored " +
                    "because this replacement mode requires MeshFilter + MeshRenderer."
                );
            }
        }

        // For a bulk selection, descendents are processed deepest first so
        // a mesh-bearing parent is not disabled before its nested meshes.
        if (processingSource == ProcessingSource.CurrentUnitySelection &&
            expandUnitySelectionToMeshChildren)
        {
            result.Sort((a, b) =>
                GetTransformHierarchyDepth(b.transform).CompareTo(
                    GetTransformHierarchyDepth(a.transform)));
        }

        return result;
    }

    private List<GameObject> CollectSelectionMeshDescendants(
        IEnumerable<GameObject> selectedRoots)
    {
        List<GameObject> collected = new List<GameObject>();
        HashSet<GameObject> seen = new HashSet<GameObject>();

        // The selected root is included if it has its own MeshFilter and
        // MeshRenderer. Empty grouping parents are not processing targets.
        foreach (GameObject root in selectedRoots.Where(x => x != null).Distinct())
        {
            // Never recursively process a proxy root or prefab asset.
            if (IsOptimizedName(root.name) || EditorUtility.IsPersistent(root))
                continue;

            // Always enumerate inactive objects here and filter explicitly:
            // selecting an inactive root then turning on Include Inactive
            // Children must behave consistently on all Unity versions.
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (MeshFilter filter in filters)
            {
                if (filter == null || filter.sharedMesh == null)
                    continue;

                GameObject candidate = filter.gameObject;
                if (!selectionIncludeInactiveChildren && !candidate.activeInHierarchy)
                    continue;

                MeshRenderer renderer = candidate.GetComponent<MeshRenderer>();
                if (renderer == null)
                    continue;

                if (!selectionIncludeDisabledRenderers && !renderer.enabled)
                    continue;

                if (IsOptimizedName(candidate.name) ||
                    IsInsideOptimizedSubtree(candidate.transform, root.transform))
                    continue;

                if (seen.Add(candidate))
                    collected.Add(candidate);
            }

            // Retain skinned-mesh support for Disable/Delete actions, as the
            // actual mesh-decomposition actions require static MeshFilters.
            SkinnedMeshRenderer[] skinned =
                root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer renderer in skinned)
            {
                if (renderer == null || renderer.sharedMesh == null)
                    continue;

                GameObject candidate = renderer.gameObject;
                if (!selectionIncludeInactiveChildren && !candidate.activeInHierarchy)
                    continue;

                if (!selectionIncludeDisabledRenderers && !renderer.enabled)
                    continue;

                if (IsOptimizedName(candidate.name) ||
                    IsInsideOptimizedSubtree(candidate.transform, root.transform))
                    continue;

                if (seen.Add(candidate))
                    collected.Add(candidate);
            }
        }

        return collected;
    }

    private static bool IsInsideOptimizedSubtree(Transform item, Transform selectedRoot)
    {
        for (Transform current = item.parent;
             current != null;
             current = current.parent)
        {
            if (IsOptimizedName(current.name))
                return true;
            if (current == selectedRoot)
                break;
        }
        return false;
    }

    private static int GetTransformHierarchyDepth(Transform item)
    {
        int depth = 0;
        for (Transform t = item; t != null; t = t.parent)
            depth++;
        return depth;
    }

    private bool DisableOriginal(
        GameObject original,
        OptimizationRecord record)
    {

        original.SetActive(false);
        EditorUtility.SetDirty(original);

        record.originalDispositionApplied =
            OriginalDisposition.Disable.ToString();

        record.status = "Success: original GameObject disabled.";
        return true;
    }

    private bool DeleteOriginal(
        GameObject original,
        OptimizationRecord record)
    {
        record.originalDispositionApplied =
            OriginalDisposition.Delete.ToString();

        record.status = "Success: original GameObject deleted.";
        UnityEngine.Object.DestroyImmediate(original);

        return true;
    }

    private void ApplyOriginalDisposition(
        GameObject original,
        OriginalDisposition disposition,
        OptimizationRecord record)
    {
        if (original == null)
            return;

        record.originalDispositionApplied = disposition.ToString();

        switch (disposition)
        {
            case OriginalDisposition.Keep:
                break;

            case OriginalDisposition.Disable:


                original.SetActive(false);
                EditorUtility.SetDirty(original);
                break;

            case OriginalDisposition.Delete:
                UnityEngine.Object.DestroyImmediate(original);
                break;
        }
    }

    private bool ReplaceWithProcessedMesh(
        GameObject original,
        GameObject batchRoot,
        string meshBatchFolder,
        Dictionary<Mesh, Mesh> meshCache,
        Dictionary<Mesh, string> meshAssetPathCache,
        OptimizationRecord record)
    {
        MeshFilter sourceFilter = original.GetComponent<MeshFilter>();
        MeshRenderer sourceRenderer = original.GetComponent<MeshRenderer>();

        if (sourceFilter == null ||
            sourceFilter.sharedMesh == null ||
            sourceRenderer == null)
        {
            record.status =
                "Skipped: replacement requires MeshFilter + MeshRenderer.";

            return false;
        }

        Mesh sourceMesh = sourceFilter.sharedMesh;

        if (!sourceMesh.isReadable)
        {
            record.status =
                "Skipped: geometry processing requires a readable source mesh. Enable Read/Write on the model import settings, or use a direct primitive/custom proxy action.";

            return false;
        }

        Mesh replacementMesh;
        string replacementMeshAssetPath;

        if (!meshCache.TryGetValue(sourceMesh, out replacementMesh))
        {
            try
            {
                switch (geometryAlgorithm)
                {
                    case GeometryProcessingAlgorithm.ConstrainedQemEdgeCollapse:
                    {
                        float targetRatio =
                            Mathf.Clamp(qemTargetTriangleRatio, 0.05f, 1f);

                        if (targetRatio >= 0.999f)
                        {
                            replacementMesh =
                                UnityEngine.Object.Instantiate(sourceMesh);
                        }
                        else
                        {
                            replacementMesh =
                                FeatureAwareQemSimplifier.Simplify(
                                    sourceMesh,
                                    targetRatio,
                                    recalculateNormals,
                                    recalculateTangents,
                                    preserveVertexColors,
                                    preserveUv2,
                                    qemWeldToleranceRelative,
                                    qemProtectOpenBoundaries,
                                    qemProtectMaterialBoundaries,
                                    qemProtectUvSeams,
                                    qemProtectSharpEdges,
                                    qemSharpEdgeAngle,
                                    qemMaxFaceNormalDeviation
                                );
                        }

                        replacementMesh.name =
                            sourceMesh.name +
                            "_QEM_" +
                            Mathf.RoundToInt(targetRatio * 100f) +
                            "pct";

                        if (FeatureAwareQemSimplifier.LastDiagnostics != null)
                        {
                            Debug.Log(
                                $"Constrained QEM: {FeatureAwareQemSimplifier.LastDiagnostics}"
                            );
                        }

                        break;
                    }

                    case GeometryProcessingAlgorithm.NormalAwareSpatialVertexClustering:
                    {
                        replacementMesh =
                            VertexClusterSimplifier.SimplifyAtGridResolution(
                                sourceMesh,
                                clusterGridResolution,
                                recalculateNormals,
                                preserveVertexColors,
                                preserveUv2,
                                recalcTangents: recalculateTangents,
                                protectSurfaceNormals: clusterProtectSurfaceNormals,
                                normalMergeAngleDegrees: clusterNormalMergeAngle
                            );

                        replacementMesh.name =
                            sourceMesh.name +
                            "_Cluster_" +
                            clusterGridResolution;

                        break;
                    }

                    case GeometryProcessingAlgorithm.PcaPrimitiveDecomposition:
                    {
                        replacementMesh =
                            PcaPrimitiveDecomposer.BuildProxy(
                                sourceMesh,
                                proxyWeldToleranceRelative,
                                proxyMaxSegmentLengthRelative,
                                proxyMaxSegmentsPerComponent,
                                proxyElongatedAspectThreshold,
                                proxyPanelThicknessRatio,
                                proxyMinimumComponentSizeRelative,
                                proxyUsePrismsForElongatedComponents,
                                proxyBeamCrossSectionSides,
                                proxySplitComponentsByMaterial,
                                proxyPreserveSubmeshMaterials,
                                recalculateTangents
                            );

                        replacementMesh.name =
                            sourceMesh.name + "_PCA_PrimitiveProxy";

                        if (!string.IsNullOrEmpty(
                                PcaPrimitiveDecomposer.LastDiagnostics))
                        {
                            Debug.Log(
                                $"PCA Primitive Decomposition: " +
                                PcaPrimitiveDecomposer.LastDiagnostics
                            );
                        }

                        break;
                    }

                    default:
                        throw new InvalidOperationException(
                            "Unknown geometry processing algorithm."
                        );
                }

                replacementMeshAssetPath =
                    SaveMeshAsset(
                        replacementMesh,
                        meshBatchFolder,
                        replacementMesh.name
                    );

                meshCache.Add(sourceMesh, replacementMesh);
                meshAssetPathCache.Add(
                    sourceMesh,
                    replacementMeshAssetPath
                );
            }
            catch (Exception ex)
            {
                record.status =
                    "Failed: mesh geometry processing threw an exception: " +
                    ex.Message;

                Debug.LogException(ex);
                return false;
            }
        }
        else
        {
            replacementMeshAssetPath =
                meshAssetPathCache[sourceMesh];
        }

        GameObject replacement =
            CreateReplacementObject(
                original,
                batchRoot
            );

        replacement.name = original.name +
            (geometryAlgorithm == GeometryProcessingAlgorithm.PcaPrimitiveDecomposition
                ? "_opt_pca"
                : geometryAlgorithm == GeometryProcessingAlgorithm.ConstrainedQemEdgeCollapse
                    ? "_opt_qem" : "_opt_cluster");

        MeshFilter targetFilter =
            replacement.AddComponent<MeshFilter>();

        MeshRenderer targetRenderer =
            replacement.AddComponent<MeshRenderer>();

        targetFilter.sharedMesh = replacementMesh;

        CopyRendererSettings(
            sourceRenderer,
            targetRenderer,
            preserveAllMaterials: true,

            // A topology-changing geometry processing cannot safely sample the
            // source object's old baked lightmap, even if UV2 is preserved.
            // Keep UV2 as mesh data for a future bake, but use realtime/probes
            // until that new bake exists.
            preserveLightmapData: false
        );

        if (geometryAlgorithm ==
                GeometryProcessingAlgorithm.PcaPrimitiveDecomposition &&
            !proxyPreserveSubmeshMaterials)
        {
            Material[] sourceMaterials =
                sourceRenderer.sharedMaterials;

            targetRenderer.sharedMaterials =
                sourceMaterials != null &&
                sourceMaterials.Length > 0
                    ? new[] { sourceMaterials[0] }
                    : Array.Empty<Material>();
        }

        record.replacementType =
            "ProcessedMesh/" + geometryAlgorithm;
        record.replacementName = replacement.name;
        record.replacementHierarchyPath =
            GetHierarchyPath(replacement.transform);
        record.replacementGlobalObjectId =
            GetGlobalObjectIdString(replacement);
        record.replacementMeshAssetPath =
            replacementMeshAssetPath;
        record.replacementTriangles =
            GetTriangleCount(replacementMesh);
        record.replacementVertices =
            replacementMesh.vertexCount;
        record.status =
            $"Success: processed replacement created ({record.originalTriangles:N0} -> {record.replacementTriangles:N0} tris).";


        return true;
    }

    private bool ReplaceWithBoundsBox(
        GameObject original,
        GameObject batchRoot,
        string meshBatchFolder,
        Dictionary<Mesh, Mesh> meshCache,
        Dictionary<Mesh, string> meshAssetPathCache,
        OptimizationRecord record)
    {
        MeshFilter sourceFilter = original.GetComponent<MeshFilter>();
        MeshRenderer sourceRenderer = original.GetComponent<MeshRenderer>();

        if (sourceFilter == null ||
            sourceFilter.sharedMesh == null ||
            sourceRenderer == null)
        {
            record.status =
                "Skipped: bounds replacement requires MeshFilter + MeshRenderer.";

            return false;
        }

        Mesh sourceMesh = sourceFilter.sharedMesh;

        Mesh boxMesh;
        string boxMeshAssetPath;

        if (!meshCache.TryGetValue(sourceMesh, out boxMesh))
        {
            boxMesh = BuildBoundsBoxMesh(sourceMesh.bounds);
            boxMesh.name = sourceMesh.name + "_OPT_Box";

            boxMeshAssetPath =
                SaveMeshAsset(
                    boxMesh,
                    meshBatchFolder,
                    boxMesh.name
                );

            meshCache.Add(sourceMesh, boxMesh);
            meshAssetPathCache.Add(sourceMesh, boxMeshAssetPath);
        }
        else
        {
            boxMeshAssetPath =
                meshAssetPathCache[sourceMesh];
        }

        GameObject replacement =
            CreateReplacementObject(
                original,
                batchRoot
            );

        replacement.name = original.name + "_opt_box";

        MeshFilter targetFilter =
            replacement.AddComponent<MeshFilter>();

        MeshRenderer targetRenderer =
            replacement.AddComponent<MeshRenderer>();

        targetFilter.sharedMesh = boxMesh;

        CopyRendererSettings(
            sourceRenderer,
            targetRenderer,
            preserveAllMaterials: false,
            preserveLightmapData: false
        );

        ApplyPrimitiveMaterial(sourceRenderer, targetRenderer);

        record.replacementType = "BoundsBox";
        record.replacementName = replacement.name;
        record.replacementHierarchyPath =
            GetHierarchyPath(replacement.transform);
        record.replacementGlobalObjectId =
            GetGlobalObjectIdString(replacement);
        record.replacementMeshAssetPath =
            boxMeshAssetPath;
        record.replacementTriangles =
            GetTriangleCount(boxMesh);
        record.replacementVertices =
            boxMesh.vertexCount;
        record.status = "Success: bounds-box replacement created.";


        return true;
    }

    private bool ReplaceWithProceduralFence(
        GameObject original,
        string meshBatchFolder,
        Dictionary<Mesh, Mesh> meshCache,
        Dictionary<Mesh, string> assetPathCache,
        OptimizationRecord record)
    {
        MeshFilter sourceFilter = original.GetComponent<MeshFilter>();
        MeshRenderer sourceRenderer = original.GetComponent<MeshRenderer>();
        if (sourceFilter == null || sourceFilter.sharedMesh == null || sourceRenderer == null)
        {
            record.status = "Skipped: procedural fence requires MeshFilter and MeshRenderer.";
            return false;
        }

        Mesh sourceMesh = sourceFilter.sharedMesh;
        if (!meshCache.TryGetValue(sourceMesh, out Mesh fenceMesh))
        {
            fenceMesh = BuildProceduralFenceMesh(sourceMesh.bounds,
                fencePostSpacing, fencePostWidth, fencePostHeightFraction,
                fenceRailCount, fenceRailThickness, fenceIncludeEndPosts);
            fenceMesh.name = sourceMesh.name + "_OPT_Fence";
            string assetPath = SaveMeshAsset(fenceMesh, meshBatchFolder, fenceMesh.name);
            meshCache.Add(sourceMesh, fenceMesh);
            assetPathCache.Add(sourceMesh, assetPath);
        }

        GameObject replacement = CreateReplacementObject(original, null);
        replacement.name = original.name + "_opt_fence";
        MeshFilter targetFilter = replacement.AddComponent<MeshFilter>();
        MeshRenderer targetRenderer = replacement.AddComponent<MeshRenderer>();
        targetFilter.sharedMesh = fenceMesh;
        CopyRendererSettings(sourceRenderer, targetRenderer,
            preserveAllMaterials: false, preserveLightmapData: false);
        ApplyPrimitiveMaterial(sourceRenderer, targetRenderer);

        record.replacementType = "ProceduralFence";
        record.replacementName = replacement.name;
        record.replacementHierarchyPath = GetHierarchyPath(replacement.transform);
        record.replacementGlobalObjectId = GetGlobalObjectIdString(replacement);
        record.replacementMeshAssetPath = assetPathCache[sourceMesh];
        record.replacementTriangles = GetTriangleCount(fenceMesh);
        record.replacementVertices = fenceMesh.vertexCount;
        record.status = $"Success: procedural fence replacement ({record.originalTriangles:N0} -> {record.replacementTriangles:N0} tris).";
        return true;
    }

    private bool ReplaceWithBoxDecomposition(GameObject original, string meshBatchFolder,
        OptimizationRecord record)
    {
        var filter = original.GetComponent<MeshFilter>();
        var renderer = original.GetComponent<MeshRenderer>();
        if (filter == null || filter.sharedMesh == null || renderer == null)
        {
            record.status = "Skipped: requires MeshFilter + MeshRenderer.";
            return false;
        }
        Mesh proxy;
        int initial, afterMerge;
        try
        {
            proxy = BoxDecompositionV9.Build(filter.sharedMesh, boxDecompWeldRelative,
                boxDecompMinimumTriangles, boxDecompMinThickness, boxDecompMerge,
                boxDecompMergeAngle, boxDecompMergeGap, boxDecompMergeOverhead,
                boxDecompMaxBoxes, out initial, out afterMerge);
        }
        catch (Exception e)
        {
            record.status = "Skipped: box decomposition failed: " + e.Message;
            Debug.LogWarning(record.status, original);
            return false;
        }
        if (proxy == null || afterMerge == 0)
        {
            record.status = "Skipped: no eligible connected components.";
            return false;
        }
        proxy.name = filter.sharedMesh.name + "_OPT_Decomp";
        string path = SaveMeshAsset(proxy, meshBatchFolder, proxy.name);
        GameObject replacement = CreateReplacementObject(original, null);
        replacement.name = original.name + "_opt_decomp";
        replacement.AddComponent<MeshFilter>().sharedMesh = proxy;
        var targetRenderer = replacement.AddComponent<MeshRenderer>();
        CopyRendererSettings(renderer, targetRenderer, false, false);
        ApplyPrimitiveMaterial(renderer, targetRenderer);
        record.replacementType = "BoxDecomposition";
        record.replacementName = replacement.name;
        record.replacementHierarchyPath = GetHierarchyPath(replacement.transform);
        record.replacementGlobalObjectId = GetGlobalObjectIdString(replacement);
        record.replacementMeshAssetPath = path;
        record.replacementTriangles = GetTriangleCount(proxy);
        record.replacementVertices = proxy.vertexCount;
        record.status = $"Success: {initial} island boxes -> {afterMerge} boxes, " +
                        $"{record.originalTriangles:N0} -> {record.replacementTriangles:N0} tris.";
        return true;
    }

    // Mesh-space connectivity => PCA oriented boxes => conservative merging => one flat-normal mesh.
    private static class BoxDecompositionV9
    {
        private struct Box
        {
            public Vector3 center, extents;
            public Quaternion rotation;
        }

        private sealed class DSU
        {
            private readonly int[] parent, size;
            public DSU(int n) { parent = new int[n]; size = new int[n]; for (int i = 0; i < n; i++) { parent[i] = i; size[i] = 1; } }
            public int Root(int n) { while (parent[n] != n) { parent[n] = parent[parent[n]]; n = parent[n]; } return n; }
            public void Union(int a, int b) { a = Root(a); b = Root(b); if (a == b) return; if (size[a] < size[b]) { int t = a; a = b; b = t; } parent[b] = a; size[a] += size[b]; }
        }

        private static Vector3 Principal(List<Vector3> points, Vector3 center, Vector3 orthogonal)
        {
            Vector3 v = orthogonal == Vector3.zero ? new Vector3(.77f, .43f, .32f) : Vector3.Cross(orthogonal, Vector3.up);
            if (v.sqrMagnitude < .01f) v = Vector3.Cross(orthogonal, Vector3.right);
            v.Normalize();
            for (int iter = 0; iter < 18; iter++)
            {
                Vector3 next = Vector3.zero;
                foreach (var p in points)
                {
                    Vector3 d = p - center;
                    if (orthogonal != Vector3.zero) d -= orthogonal * Vector3.Dot(d, orthogonal);
                    next += d * Vector3.Dot(d, v);
                }
                if (orthogonal != Vector3.zero) next -= orthogonal * Vector3.Dot(next, orthogonal);
                if (next.sqrMagnitude < 1e-12f) break;
                v = next.normalized;
            }
            return v;
        }

        private static Box Fit(List<Vector3> points, float minThickness)
        {
            Vector3 centroid = Vector3.zero;
            foreach (var p in points) centroid += p;
            centroid /= points.Count;
            Vector3 major = Principal(points, centroid, Vector3.zero);
            Vector3 secondary = Principal(points, centroid, major);
            if (secondary.sqrMagnitude < .1f || Mathf.Abs(Vector3.Dot(major, secondary)) > .99f)
                secondary = Vector3.Cross(major, Mathf.Abs(major.y) < .8f ? Vector3.up : Vector3.right).normalized;
            Vector3 tertiary = Vector3.Cross(major, secondary).normalized;
            secondary = Vector3.Cross(tertiary, major).normalized;
            Quaternion q = Quaternion.LookRotation(tertiary, secondary);
            // Quaternion.LookRotation maps local X to 'major' for this basis.
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            foreach (var p in points)
            {
                var d = Quaternion.Inverse(q) * (p - centroid);
                min = Vector3.Min(min, d); max = Vector3.Max(max, d);
            }
            Vector3 extent = (max - min) * .5f;
            extent = Vector3.Max(extent, Vector3.one * (minThickness * .5f));
            return new Box { rotation = q, center = centroid + q * ((min + max) * .5f), extents = extent };
        }

        private static float Volume(Box b) => 8f * b.extents.x * b.extents.y * b.extents.z;

        private static bool TryMerge(Box a, Box b, float angle, float gap, float overhead, out Box result)
        {
            result = a;
            float cosine = Mathf.Cos(angle * Mathf.Deg2Rad);
            // Compare corresponding axes, allowing sign flips.
            if (Mathf.Abs(Vector3.Dot(a.rotation * Vector3.right, b.rotation * Vector3.right)) < cosine ||
                Mathf.Abs(Vector3.Dot(a.rotation * Vector3.up, b.rotation * Vector3.up)) < cosine ||
                Mathf.Abs(Vector3.Dot(a.rotation * Vector3.forward, b.rotation * Vector3.forward)) < cosine)
                return false;
            Quaternion inverse = Quaternion.Inverse(a.rotation);
            Vector3 amin = -a.extents, amax = a.extents;
            Vector3 bmin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 bmax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 local = inverse * (b.center + b.rotation * Vector3.Scale(b.extents, new Vector3(x,y,z)) - a.center);
                bmin = Vector3.Min(bmin, local); bmax = Vector3.Max(bmax, local);
            }
            Vector3 delta = Vector3.Max(Vector3.zero, Vector3.Max(bmin - amax, amin - bmax));
            if (delta.magnitude > gap) return false;
            Vector3 lo = Vector3.Min(amin,bmin), hi = Vector3.Max(amax,bmax);
            Vector3 size = hi - lo;
            float mergedVolume = size.x * size.y * size.z;
            // Using sum of box volumes is a conservative test for disjoint structures;
            // overlapping members may fail to merge (safe failure).
            if (mergedVolume > (Volume(a) + Volume(b)) * (1f + overhead)) return false;
            result = new Box { rotation = a.rotation, center = a.center + a.rotation * ((lo + hi) * .5f), extents = size * .5f };
            return true;
        }

        public static Mesh Build(Mesh source, float weldRelative, int minTriangles, float minThickness,
            bool merge, float angle, float gap, float overhead, int maxBoxes, out int initial, out int merged)
        {
            initial = merged = 0;
            var vertices = source.vertices;
            if (vertices == null || vertices.Length == 0) return null;
            float step = Mathf.Max(1e-7f, source.bounds.size.magnitude * weldRelative);
            var welded = new Dictionary<(long,long,long), int>();
            int[] canonical = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = vertices[i];
                var key = ((long)Math.Round(p.x / step), (long)Math.Round(p.y / step), (long)Math.Round(p.z / step));
                if (!welded.TryGetValue(key, out int id)) { id = i; welded[key] = id; }
                canonical[i] = id;
            }
            List<int> triangles = new List<int>();
            for (int sm = 0; sm < source.subMeshCount; sm++) triangles.AddRange(source.GetTriangles(sm));
            int count = triangles.Count / 3;
            var dsu = new DSU(vertices.Length);
            for (int t = 0; t < count; t++)
            {
                int i = 3*t;
                dsu.Union(canonical[triangles[i]],canonical[triangles[i+1]]);
                dsu.Union(canonical[triangles[i]],canonical[triangles[i+2]]);
            }
            var components = new Dictionary<int, HashSet<int>>();
            var triCount = new Dictionary<int,int>();
            for (int t = 0; t < count; t++)
            {
                int i = 3*t, root = dsu.Root(canonical[triangles[i]]);
                if (!components.TryGetValue(root, out var component))
                { component = new HashSet<int>(); components[root] = component; triCount[root]=0; }
                component.Add(canonical[triangles[i]]);
                component.Add(canonical[triangles[i+1]]);
                component.Add(canonical[triangles[i+2]]);
                triCount[root]++;
            }
            var boxes = new List<Box>();
            foreach (var kv in components)
            {
                if (triCount[kv.Key] < minTriangles) continue;
                var points = kv.Value.Select(id => vertices[id]).ToList();
                if (points.Count >= 3) boxes.Add(Fit(points, minThickness));
                if (boxes.Count > maxBoxes)
                    throw new InvalidOperationException("Component count exceeds Max Boxes / Source Mesh. Increase the cap or process smaller meshes.");
            }
            initial = boxes.Count;
            if (merge)
            {
                // Quadratic worst case, deliberately capped by Max Boxes.
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    for (int i = 0; i < boxes.Count && !changed; i++)
                        for (int j = i+1; j < boxes.Count; j++)
                            if (TryMerge(boxes[i], boxes[j], angle, gap, overhead, out Box union))
                            { boxes[i] = union; boxes.RemoveAt(j); changed = true; break; }
                }
            }
            merged = boxes.Count;
            if (merged == 0) return null;
            var vv = new List<Vector3>(merged*24);
            var nn = new List<Vector3>(merged*24);
            var uv = new List<Vector2>(merged*24);
            var tt = new List<int>(merged*36);
            foreach (var box in boxes)
            {
                // Generate six correctly oriented, independently shaded faces.
                Mesh cube = BuildBoundsBoxMesh(new Bounds(Vector3.zero, box.extents * 2f));
                int offset = vv.Count;
                foreach (var v in cube.vertices) vv.Add(box.center + box.rotation * v);
                foreach (var n in cube.normals) nn.Add(box.rotation * n);
                uv.AddRange(cube.uv);
                foreach (var t in cube.triangles) tt.Add(offset+t);
                UnityEngine.Object.DestroyImmediate(cube);
            }
            Mesh output = new Mesh();
            if (vv.Count > 65535) output.indexFormat = IndexFormat.UInt32;
            output.SetVertices(vv); output.SetNormals(nn); output.SetUVs(0,uv);
            output.SetTriangles(tt,0); output.RecalculateBounds();
            return output;
        }
    }

    // All pieces are 24-vertex/12-triangle hard-normal boxes. Geometry is built
    // in the source mesh's LOCAL space to preserve IFC transforms and instancing.
    private static Mesh BuildProceduralFenceMesh(
        Bounds bounds, float spacing, float postWidth, float heightFraction,
        int rails, float railThickness, bool includeEndPosts)
    {
        Vector3 size = bounds.size;
        bool alongX = size.x >= size.z;
        float length = Mathf.Max(0.001f, alongX ? size.x : size.z);
        float height = Mathf.Max(0.001f, size.y * Mathf.Clamp01(heightFraction));
        float depth = Mathf.Max(0.001f, alongX ? size.z : size.x);
        float yBottom = bounds.min.y;
        float centerSecondary = alongX ? bounds.center.z : bounds.center.x;
        float axisMin = alongX ? bounds.min.x : bounds.min.z;
        float axisMax = alongX ? bounds.max.x : bounds.max.z;

        float pw = Mathf.Min(Mathf.Max(0.001f, postWidth), length);
        float rt = Mathf.Min(Mathf.Max(0.001f, railThickness), height);
        float crossThickness = Mathf.Min(depth, Mathf.Max(pw, rt));
        int postIntervals = Mathf.Clamp(Mathf.CeilToInt(length / Mathf.Max(0.1f, spacing)), 1, 128);
        int postCount = includeEndPosts ? postIntervals + 1 : Mathf.Max(1, postIntervals - 1);

        var verts = new List<Vector3>((postCount + rails) * 24);
        var normals = new List<Vector3>((postCount + rails) * 24);
        var uvs = new List<Vector2>((postCount + rails) * 24);
        var indices = new List<int>((postCount + rails) * 36);

        Action<Vector3, Vector3> appendBox = (center, halfSize) =>
        {
            Mesh piece = BuildBoundsBoxMesh(new Bounds(center, halfSize * 2f));
            int start = verts.Count;
            verts.AddRange(piece.vertices);
            normals.AddRange(piece.normals);
            uvs.AddRange(piece.uv);
            int[] tris = piece.triangles;
            for (int t = 0; t < tris.Length; t++) indices.Add(start + tris[t]);
            UnityEngine.Object.DestroyImmediate(piece);
        };

        for (int i = 0; i < postCount; i++)
        {
            float t = includeEndPosts ? (float)i / postIntervals : (float)(i + 1) / postIntervals;
            float along = Mathf.Lerp(axisMin, axisMax, t);
            Vector3 center = alongX
                ? new Vector3(along, yBottom + height * 0.5f, centerSecondary)
                : new Vector3(centerSecondary, yBottom + height * 0.5f, along);
            Vector3 halfSize = alongX
                ? new Vector3(pw * 0.5f, height * 0.5f, crossThickness * 0.5f)
                : new Vector3(crossThickness * 0.5f, height * 0.5f, pw * 0.5f);
            appendBox(center, halfSize);
        }

        for (int i = 0; i < rails; i++)
        {
            float fraction = rails == 1 ? 0.65f : Mathf.Lerp(0.25f, 0.85f, (float)i / (rails - 1));
            float y = yBottom + height * fraction;
            Vector3 center = alongX
                ? new Vector3(bounds.center.x, y, centerSecondary)
                : new Vector3(centerSecondary, y, bounds.center.z);
            Vector3 halfSize = alongX
                ? new Vector3(length * 0.5f, rt * 0.5f, crossThickness * 0.45f)
                : new Vector3(crossThickness * 0.45f, rt * 0.5f, length * 0.5f);
            appendBox(center, halfSize);
        }

        var mesh = new Mesh { name = "ProceduralFenceProxy" };
        if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private bool ReplaceWithLowPolyCylinder(
        GameObject original,
        GameObject batchRoot,
        string meshBatchFolder,
        Dictionary<Mesh, Mesh> meshCache,
        Dictionary<Mesh, string> meshAssetPathCache,
        OptimizationRecord record)
    {
        MeshFilter sourceFilter = original.GetComponent<MeshFilter>();
        MeshRenderer sourceRenderer = original.GetComponent<MeshRenderer>();

        if (sourceFilter == null ||
            sourceFilter.sharedMesh == null ||
            sourceRenderer == null)
        {
            record.status =
                "Skipped: cylinder replacement requires MeshFilter + MeshRenderer.";

            return false;
        }

        Mesh sourceMesh = sourceFilter.sharedMesh;
        Mesh cylinderMesh;
        string cylinderMeshAssetPath;

        if (!meshCache.TryGetValue(sourceMesh, out cylinderMesh))
        {
            cylinderMesh =
                BuildBoundsCylinderMesh(
                    sourceMesh.bounds,
                    Mathf.Clamp(cylinderSides, 3, 16)
                );

            cylinderMesh.name =
                sourceMesh.name +
                "_OPT_Cylinder_" +
                cylinderSides;

            cylinderMeshAssetPath =
                SaveMeshAsset(
                    cylinderMesh,
                    meshBatchFolder,
                    cylinderMesh.name
                );

            meshCache.Add(sourceMesh, cylinderMesh);
            meshAssetPathCache.Add(
                sourceMesh,
                cylinderMeshAssetPath
            );
        }
        else
        {
            cylinderMeshAssetPath =
                meshAssetPathCache[sourceMesh];
        }

        GameObject replacement =
            CreateReplacementObject(
                original,
                batchRoot
            );

        replacement.name =
            original.name + "_opt_cylinder";

        MeshFilter targetFilter =
            replacement.AddComponent<MeshFilter>();

        MeshRenderer targetRenderer =
            replacement.AddComponent<MeshRenderer>();

        targetFilter.sharedMesh = cylinderMesh;

        CopyRendererSettings(
            sourceRenderer,
            targetRenderer,
            preserveAllMaterials: false,
            preserveLightmapData: false
        );

        ApplyPrimitiveMaterial(sourceRenderer, targetRenderer);

        record.replacementType =
            $"LowPolyCylinder/{cylinderSides}sides";
        record.replacementName = replacement.name;
        record.replacementHierarchyPath =
            GetHierarchyPath(replacement.transform);
        record.replacementGlobalObjectId =
            GetGlobalObjectIdString(replacement);
        record.replacementMeshAssetPath =
            cylinderMeshAssetPath;
        record.replacementTriangles =
            GetTriangleCount(cylinderMesh);
        record.replacementVertices =
            cylinderMesh.vertexCount;
        record.status =
            "Success: low-poly cylinder replacement created.";


        return true;
    }

    private bool ReplaceWithCustomPrefab(
        GameObject original,
        GameObject batchRoot,
        OptimizationRecord record)
    {
        if (customReplacementPrefab == null)
        {
            record.status =
                "Skipped: no custom replacement prefab was assigned.";

            return false;
        }

        GameObject replacement = null;
        if (PrefabUtility.IsPartOfPrefabAsset(customReplacementPrefab))
        {
            replacement = PrefabUtility.InstantiatePrefab(
                customReplacementPrefab, original.scene) as GameObject;
        }
        if (replacement == null)
        {
            replacement = UnityEngine.Object.Instantiate(customReplacementPrefab);
            if (replacement.scene != original.scene)
                SceneManager.MoveGameObjectToScene(replacement, original.scene);
        }
        if (replacement == null)
        {
            record.status = "Failed: custom replacement could not be instantiated.";
            return false;
        }

        replacement.name = original.name + "_opt_prefab";
        ConfigureAsSibling(original, replacement);

        record.replacementType = "CustomPrefab";
        record.replacementName = replacement.name;
        record.replacementHierarchyPath =
            GetHierarchyPath(replacement.transform);
        record.replacementGlobalObjectId =
            GetGlobalObjectIdString(replacement);

        MeshFilter replacementFilter =
            replacement.GetComponentInChildren<MeshFilter>();

        if (replacementFilter != null &&
            replacementFilter.sharedMesh != null)
        {
            record.replacementMeshAssetPath =
                AssetDatabase.GetAssetPath(
                    replacementFilter.sharedMesh
                );

            record.replacementTriangles =
                GetTriangleCount(
                    replacementFilter.sharedMesh
                );

            record.replacementVertices =
                replacementFilter.sharedMesh.vertexCount;
        }

        record.status =
            "Success: custom replacement created.";


        return true;
    }

    private void ApplyPrimitiveMaterial(
        MeshRenderer sourceRenderer,
        MeshRenderer targetRenderer)
    {
        Material material = primitiveMaterialOverride;

        if (material == null)
        {
            Material[] materials =
                sourceRenderer.sharedMaterials;

            material =
                materials != null && materials.Length > 0
                    ? materials[0]
                    : null;
        }

        targetRenderer.sharedMaterials =
            material != null
                ? new[] { material }
                : Array.Empty<Material>();
    }

    private static GameObject CreateReplacementObject(
        GameObject original,
        GameObject ignoredLegacyBatchRoot)
    {
        var replacement = new GameObject();
        ConfigureAsSibling(original, replacement);
        return replacement;
    }

    private static void ConfigureAsSibling(
        GameObject original,
        GameObject replacement)
    {
        // Parent directly beside the original, and replicate LOCAL TRS.
        // Reconstructing a world-space lossy scale under rotated parents
        // changes IFC meshes; local coordinates avoid that failure.
        Transform src = original.transform;
        Transform dst = replacement.transform;

        if (replacement.scene != original.scene && src.parent == null)
            SceneManager.MoveGameObjectToScene(replacement, original.scene);
        dst.SetParent(src.parent, false);
        dst.localPosition = src.localPosition;
        dst.localRotation = src.localRotation;
        dst.localScale = src.localScale;
        dst.SetSiblingIndex(src.GetSiblingIndex() + 1);

        replacement.layer = original.layer;
        try { replacement.tag = original.tag; }
        catch { /* Some prefabs/import tags may be unavailable. */ }

        GameObjectUtility.SetStaticEditorFlags(
            replacement,
            GameObjectUtility.GetStaticEditorFlags(original));
    }

    private static void CopyRendererSettings(
        MeshRenderer source,
        MeshRenderer target,
        bool preserveAllMaterials,
        bool preserveLightmapData)
    {
        if (source == null || target == null)
            return;

        Material[] originalMaterials =
            source.sharedMaterials;

        try
        {
            // This copies the complete serialized renderer state rather than
            // cherry-picking a handful of fields. That includes probe usage,
            // shadow settings, rendering layers, lightmap index/scale-offset,
            // motion vectors, occlusion settings, etc.
            EditorUtility.CopySerialized(source, target);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "Could not copy the complete MeshRenderer state. " +
                "Falling back to the most important renderer properties.\n" +
                ex.Message
            );

            target.shadowCastingMode = source.shadowCastingMode;
            target.receiveShadows = source.receiveShadows;
            target.lightProbeUsage = source.lightProbeUsage;
            target.reflectionProbeUsage = source.reflectionProbeUsage;
            target.motionVectorGenerationMode =
                source.motionVectorGenerationMode;
            target.allowOcclusionWhenDynamic =
                source.allowOcclusionWhenDynamic;
            target.renderingLayerMask =
                source.renderingLayerMask;
            target.lightmapIndex = source.lightmapIndex;
            target.lightmapScaleOffset =
                source.lightmapScaleOffset;
            target.realtimeLightmapIndex =
                source.realtimeLightmapIndex;
            target.realtimeLightmapScaleOffset =
                source.realtimeLightmapScaleOffset;
        }

        target.enabled = true;

        if (!preserveLightmapData)
        {
            // Primitive proxies have different UV topology, so reusing the
            // source lightmap atlas transform can produce obviously incorrect
            // baked-lighting patches. Let them use probes/realtime lighting
            // until a new bake is generated.
            target.lightmapIndex = -1;
            target.lightmapScaleOffset =
                new Vector4(1f, 1f, 0f, 0f);
            target.realtimeLightmapIndex = -1;
            target.realtimeLightmapScaleOffset =
                new Vector4(1f, 1f, 0f, 0f);
        }

        if (preserveAllMaterials)
        {
            target.sharedMaterials =
                originalMaterials;
        }
    }

    // ============================================================
    // AUDIT LOG
    // ============================================================

    private OptimizationRecord CaptureOriginalRecord(
        GameObject original)
    {
        Mesh mesh = GetDirectMesh(original);
        Renderer renderer = original.GetComponent<Renderer>();

        OptimizationRecord record = new OptimizationRecord
        {
            originalName = original.name,
            originalHierarchyPath =
                GetHierarchyPath(original.transform),
            originalGlobalObjectId =
                GetGlobalObjectIdString(original),
            originalActiveSelf = original.activeSelf,
            originalActiveInHierarchy =
                original.activeInHierarchy,
            originalLayer = original.layer,
            originalTag = original.tag,
            originalSiblingIndex = original.transform.GetSiblingIndex(),
            originalParentHierarchyPath =
                original.transform.parent != null
                    ? GetHierarchyPath(original.transform.parent)
                    : "",
            originalPosition = original.transform.position,
            originalRotation = original.transform.rotation,
            originalLossyScale = original.transform.lossyScale,
            originalLocalPosition = original.transform.localPosition,
            originalLocalRotation = original.transform.localRotation,
            originalLocalScale = original.transform.localScale,
            originalRendererEnabled =
                renderer != null && renderer.enabled,
            sourceMeshName = mesh != null ? mesh.name : "",
            sourceMeshAssetPath =
                mesh != null
                    ? AssetDatabase.GetAssetPath(mesh)
                    : "",
            originalTriangles =
                mesh != null
                    ? GetTriangleCount(mesh)
                    : 0,
            originalVertices =
                mesh != null
                    ? mesh.vertexCount
                    : 0,
            sourceMaterialAssetPaths =
                renderer != null
                    ? renderer.sharedMaterials
                        .Select(m =>
                            m != null
                                ? AssetDatabase.GetAssetPath(m)
                                : "")
                        .ToArray()
                    : Array.Empty<string>()
        };

        return record;
    }

    private static string GetGlobalObjectIdString(
        UnityEngine.Object obj)
    {
        if (obj == null)
            return "";

        try
        {
            return GlobalObjectId
                .GetGlobalObjectIdSlow(obj)
                .ToString();
        }
        catch
        {
            return "";
        }
    }

    // ============================================================
    // MESH HELPERS
    // ============================================================

    private static Mesh GetDirectMesh(GameObject go)
    {
        if (go == null)
            return null;

        MeshFilter filter = go.GetComponent<MeshFilter>();

        if (filter != null)
            return filter.sharedMesh;

        SkinnedMeshRenderer skinned =
            go.GetComponent<SkinnedMeshRenderer>();

        return skinned != null
            ? skinned.sharedMesh
            : null;
    }

    private static long GetTriangleCount(Mesh mesh)
    {
        if (mesh == null)
            return 0;

        long triangles = 0;

        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            if (mesh.GetTopology(i) != MeshTopology.Triangles)
                continue;

            triangles +=
                (long)mesh.GetIndexCount(i) / 3;
        }

        return triangles;
    }

    private static string SaveMeshAsset(
        Mesh mesh,
        string folder,
        string preferredName)
    {
        string cleanName = SanitizeFileName(preferredName);

        if (string.IsNullOrWhiteSpace(cleanName))
            cleanName = "OptimizedMesh";

        string assetPath =
            AssetDatabase.GenerateUniqueAssetPath(
                folder + "/" + cleanName + ".asset"
            );

        AssetDatabase.CreateAsset(mesh, assetPath);

        return assetPath;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        char[] invalid = Path.GetInvalidFileNameChars();

        foreach (char c in invalid)
            name = name.Replace(c, '_');

        return name
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace(':', '_');
    }

    private static Mesh BuildBoundsBoxMesh(Bounds bounds)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        // 24 vertices so each face can have a hard normal and independent UVs.
        Vector3[] vertices =
        {
            // Front (+Z)
            new Vector3(min.x, min.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(min.x, max.y, max.z),

            // Back (-Z)
            new Vector3(max.x, min.y, min.z),
            new Vector3(min.x, min.y, min.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(max.x, max.y, min.z),

            // Left (-X)
            new Vector3(min.x, min.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(min.x, max.y, min.z),

            // Right (+X)
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z),

            // Top (+Y)
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(min.x, max.y, min.z),

            // Bottom (-Y)
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(min.x, min.y, max.z)
        };

        Vector3[] normals =
        {
            Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward,
            Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            Vector3.left, Vector3.left, Vector3.left, Vector3.left,
            Vector3.right, Vector3.right, Vector3.right, Vector3.right,
            Vector3.up, Vector3.up, Vector3.up, Vector3.up,
            Vector3.down, Vector3.down, Vector3.down, Vector3.down
        };

        Vector2[] uv = new Vector2[24];

        for (int face = 0; face < 6; face++)
        {
            int o = face * 4;
            uv[o + 0] = new Vector2(0f, 0f);
            uv[o + 1] = new Vector2(1f, 0f);
            uv[o + 2] = new Vector2(1f, 1f);
            uv[o + 3] = new Vector2(0f, 1f);
        }

        int[] triangles =
        {
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
            8, 9, 10, 8, 10, 11,
            12, 13, 14, 12, 14, 15,
            16, 17, 18, 16, 18, 19,
            20, 21, 22, 20, 22, 23
        };

        Mesh mesh = new Mesh
        {
            name = "BoundsBoxProxy"
        };

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        return mesh;
    }


    private static Mesh BuildBoundsCylinderMesh(
        Bounds bounds,
        int sides)
    {
        sides = Mathf.Clamp(sides, 3, 32);

        Vector3 size = bounds.size;
        Vector3 center = bounds.center;

        // Align the cylinder to the longest local bounds axis.
        int axis;
        if (size.x >= size.y && size.x >= size.z)
            axis = 0;
        else if (size.y >= size.x && size.y >= size.z)
            axis = 1;
        else
            axis = 2;

        float length =
            axis == 0 ? size.x :
            axis == 1 ? size.y :
            size.z;

        float radiusA =
            axis == 0 ? size.y * 0.5f :
            axis == 1 ? size.x * 0.5f :
            size.x * 0.5f;

        float radiusB =
            axis == 0 ? size.z * 0.5f :
            axis == 1 ? size.z * 0.5f :
            size.y * 0.5f;

        float halfLength = length * 0.5f;

        List<Vector3> vertices = new();
        List<Vector3> normals = new();
        List<Vector2> uv = new();
        List<int> triangles = new();

        Vector3 AxisPoint(float along, float a, float b)
        {
            return axis switch
            {
                0 => center + new Vector3(along, a, b),
                1 => center + new Vector3(a, along, b),
                _ => center + new Vector3(a, b, along)
            };
        }

        Vector3 AxisNormal(float a, float b)
        {
            Vector3 n = axis switch
            {
                0 => new Vector3(0f, a, b),
                1 => new Vector3(a, 0f, b),
                _ => new Vector3(a, b, 0f)
            };

            return n.sqrMagnitude > 1e-12f
                ? n.normalized
                : Vector3.up;
        }

        // Side wall: duplicate seam vertices for clean UVs.
        for (int i = 0; i <= sides; i++)
        {
            float t = i / (float)sides;
            float angle = t * Mathf.PI * 2f;
            float ca = Mathf.Cos(angle);
            float sa = Mathf.Sin(angle);

            float a = ca * radiusA;
            float b = sa * radiusB;

            vertices.Add(AxisPoint(-halfLength, a, b));
            vertices.Add(AxisPoint(+halfLength, a, b));

            Vector3 n = AxisNormal(
                radiusA > 1e-8f ? ca / radiusA : ca,
                radiusB > 1e-8f ? sa / radiusB : sa
            );

            normals.Add(n);
            normals.Add(n);

            uv.Add(new Vector2(t, 0f));
            uv.Add(new Vector2(t, 1f));
        }

        for (int i = 0; i < sides; i++)
        {
            int a0 = i * 2;
            int a1 = a0 + 1;
            int b0 = (i + 1) * 2;
            int b1 = b0 + 1;

            triangles.Add(a0);
            triangles.Add(b0);
            triangles.Add(b1);

            triangles.Add(a0);
            triangles.Add(b1);
            triangles.Add(a1);
        }

        // Caps use separate vertices for hard normals.
        int startCapCenter = vertices.Count;
        Vector3 startNormal =
            axis == 0 ? Vector3.left :
            axis == 1 ? Vector3.down :
            Vector3.back;

        vertices.Add(AxisPoint(-halfLength, 0f, 0f));
        normals.Add(startNormal);
        uv.Add(new Vector2(0.5f, 0.5f));

        int startRing = vertices.Count;

        for (int i = 0; i < sides; i++)
        {
            float angle = i / (float)sides * Mathf.PI * 2f;
            float ca = Mathf.Cos(angle);
            float sa = Mathf.Sin(angle);

            vertices.Add(
                AxisPoint(
                    -halfLength,
                    ca * radiusA,
                    sa * radiusB
                )
            );

            normals.Add(startNormal);
            uv.Add(new Vector2(
                ca * 0.5f + 0.5f,
                sa * 0.5f + 0.5f
            ));
        }

        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;

            triangles.Add(startCapCenter);
            triangles.Add(startRing + next);
            triangles.Add(startRing + i);
        }

        int endCapCenter = vertices.Count;
        Vector3 endNormal = -startNormal;

        vertices.Add(AxisPoint(+halfLength, 0f, 0f));
        normals.Add(endNormal);
        uv.Add(new Vector2(0.5f, 0.5f));

        int endRing = vertices.Count;

        for (int i = 0; i < sides; i++)
        {
            float angle = i / (float)sides * Mathf.PI * 2f;
            float ca = Mathf.Cos(angle);
            float sa = Mathf.Sin(angle);

            vertices.Add(
                AxisPoint(
                    +halfLength,
                    ca * radiusA,
                    sa * radiusB
                )
            );

            normals.Add(endNormal);
            uv.Add(new Vector2(
                ca * 0.5f + 0.5f,
                sa * 0.5f + 0.5f
            ));
        }

        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;

            triangles.Add(endCapCenter);
            triangles.Add(endRing + i);
            triangles.Add(endRing + next);
        }

        Mesh mesh = new Mesh
        {
            name = "BoundsCylinderProxy"
        };

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uv);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        if (vertices.Count > 0)
            mesh.RecalculateTangents();

        return mesh;
    }

    // ============================================================
    // ASSET FOLDERS / PATHS
    // ============================================================

    private static void EnsureAssetFolder(string folderPath)
    {
        folderPath =
            folderPath.Replace('\\', '/').TrimEnd('/');

        if (AssetDatabase.IsValidFolder(folderPath))
            return;

        string[] parts = folderPath.Split('/');

        if (parts.Length == 0 || parts[0] != "Assets")
        {
            throw new ArgumentException(
                "Asset folder must start with 'Assets/'."
            );
        }

        string current = "Assets";

        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];

            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);

            current = next;
        }
    }

    private static string GetHierarchyPath(Transform transform)
    {
        if (transform == null)
            return "";

        string path = transform.name;

        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }

        return path;
    }

    // ============================================================
    // DATA TYPES
    // ============================================================

    private class ObjectStats
    {
        public GameObject gameObject;
        public Mesh mesh;

        public long triangles;
        public long vertices;

        public int subMeshes;
        public int materials;
        public bool skinned;

        public string category;
        public bool matchesSearch;
        public string candidateReason;
    }

    private class CategoryStats
    {
        public string categoryName;
        public int instances;
        public long triangles;
        public long vertices;

        public bool containsSearchMatch;
        public bool isAggregateCandidate;

        public readonly List<GameObject> objects = new();
        public readonly List<GameObject> candidateObjects = new();
        public readonly List<ObjectStats> stats = new();

        public long AverageTriangles =>
            instances > 0
                ? triangles / instances
                : 0;
    }

    [Serializable]
    private class OptimizationLog
    {
        public string toolVersion;
        public string localTimestamp;
        public string scenePath;

        public string batchName;
        public string generatedRootName;
        public string batchRootGlobalObjectId;

        public string action;
        public string originalDisposition;

        // Geometry-processing configuration. Legacy fields are retained for
        // compatibility with older logs, while the explicit algorithm fields
        // make v4 batches self-describing.
        public float keepTriangleRatio;
        public string simplifierMode;

        public string geometryAlgorithm;

        public float qemTargetTriangleRatio;
        public float qemWeldToleranceRelative;
        public bool qemProtectOpenBoundaries;
        public bool qemProtectMaterialBoundaries;
        public bool qemProtectUvSeams;
        public bool qemProtectSharpEdges;
        public float qemSharpEdgeAngle;
        public float qemMaxFaceNormalDeviation;

        public int clusterGridResolution;
        public bool clusterProtectSurfaceNormals;
        public float clusterNormalMergeAngle;

        public float proxyWeldToleranceRelative;
        public float proxyMaxSegmentLengthRelative;
        public int proxyMaxSegmentsPerComponent;
        public float proxyElongatedAspectThreshold;
        public float proxyPanelThicknessRatio;
        public float proxyMinimumComponentSizeRelative;
        public int proxyBeamCrossSectionSides;
        public bool proxyUsePrismsForElongatedComponents;
        public bool proxySplitComponentsByMaterial;
        public bool proxyPreserveSubmeshMaterials;

        public string searchText;
        public bool prioritizeSearchMatches;

        public bool reverted;
        public string revertedTimestamp;

        public List<OptimizationRecord> records = new();
    }

    [Serializable]
    private class OptimizationRecord
    {
        public string originalName;
        public string originalHierarchyPath;
        public string originalGlobalObjectId;

        public bool originalActiveSelf;
        public bool originalActiveInHierarchy;

        public int originalLayer;
        public string originalTag;
        public int originalSiblingIndex;
        public string originalParentHierarchyPath;

        public Vector3 originalPosition;
        public Quaternion originalRotation;
        public Vector3 originalLossyScale;

        public Vector3 originalLocalPosition;
        public Quaternion originalLocalRotation;
        public Vector3 originalLocalScale;

        public bool originalRendererEnabled;

        public string sourceMeshName;
        public string sourceMeshAssetPath;
        public long originalTriangles;
        public long originalVertices;

        public string[] sourceMaterialAssetPaths;

        public string requestedAction;
        public string batchName;
        public bool originalRenamedForPairing;
        public bool committedByCleanup;
        public string originalDispositionApplied;

        public string replacementType;
        public string replacementName;
        public string replacementHierarchyPath;
        public string replacementGlobalObjectId;
        public string replacementMeshAssetPath;

        public long replacementTriangles;
        public long replacementVertices;

        public string status;
    }


    // ============================================================
    // PCA PRIMITIVE DECOMPOSITION
    // ============================================================

    /// <summary>
    /// Reconstructs approximate geometric connectivity from duplicated IFC
    /// render vertices, partitions connected components into local segments,
    /// fits PCA-oriented primitive frames, and emits low-poly boxes/prisms.
    ///
    /// This is deliberately an abstraction algorithm, not a surface decimator.
    /// It is intended for rails, posts, structural frames, panel assemblies,
    /// MUGA equipment and other distant site-context geometry.
    /// </summary>
    private static class PcaPrimitiveDecomposer
    {
        public static string LastDiagnostics { get; private set; }

        private struct SourceTriangle
        {
            public int a;
            public int b;
            public int c;
            public int submesh;
            public Vector3 centroid;
        }

        private class Component
        {
            public readonly List<int> triangleIndices = new();
            public readonly HashSet<int> canonicalVertices = new();
        }

        private struct PcaFrame
        {
            public Vector3 center;
            public Vector3 axis0;
            public Vector3 axis1;
            public Vector3 axis2;
            public Vector3 min;
            public Vector3 max;

            public Vector3 Size => max - min;
        }

        private struct PrimitiveFit
        {
            public PcaFrame frame;
            public int submesh;
            public bool elongated;
            public bool panel;
        }

        public static Mesh BuildProxy(
            Mesh source,
            float weldToleranceRelative,
            float maxSegmentLengthRelative,
            int maxSegmentsPerComponent,
            float elongatedAspectThreshold,
            float panelThicknessRatio,
            float minimumComponentSizeRelative,
            bool usePrismsForElongated,
            int beamCrossSectionSides,
            bool splitComponentsByMaterial,
            bool preserveSubmeshMaterials,
            bool recalcTangents)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (!source.isReadable)
            {
                throw new InvalidOperationException(
                    $"Mesh '{source.name}' is not readable."
                );
            }

            Vector3[] sourceVertices = source.vertices;
            float objectMaxExtent = Mathf.Max(
                source.bounds.size.x,
                Mathf.Max(
                    source.bounds.size.y,
                    source.bounds.size.z
                )
            );

            float objectDiagonal =
                source.bounds.size.magnitude;

            float weldTolerance =
                Mathf.Max(
                    1e-7f,
                    objectMaxExtent *
                    Mathf.Max(1e-8f, weldToleranceRelative)
                );

            int[] sourceToCanonical;
            List<Vector3> canonicalPositions;

            BuildCanonicalVertices(
                sourceVertices,
                weldTolerance,
                out sourceToCanonical,
                out canonicalPositions
            );

            List<SourceTriangle> triangles =
                BuildSourceTriangles(
                    source,
                    sourceVertices
                );

            List<Component> components =
                BuildComponents(
                    triangles,
                    sourceToCanonical,
                    splitComponentsByMaterial
                );

            int submeshCount =
                preserveSubmeshMaterials
                    ? Mathf.Max(1, source.subMeshCount)
                    : 1;

            List<Vector3> outVertices = new();
            List<Vector3> outNormals = new();
            List<Vector2> outUv = new();

            List<int>[] outTriangles =
                new List<int>[submeshCount];

            for (int i = 0; i < submeshCount; i++)
                outTriangles[i] = new List<int>();

            int keptComponents = 0;
            int discardedComponents = 0;
            int generatedPrimitives = 0;

            float maxSegmentLength =
                Mathf.Max(
                    objectMaxExtent *
                    Mathf.Clamp(
                        maxSegmentLengthRelative,
                        0.005f,
                        1f
                    ),
                    weldTolerance * 4f
                );

            foreach (Component component in components)
            {
                List<Vector3> componentPoints =
                    component.canonicalVertices
                        .Select(i => canonicalPositions[i])
                        .ToList();

                if (componentPoints.Count < 2)
                {
                    discardedComponents++;
                    continue;
                }

                Bounds componentBounds =
                    BoundsFromPoints(componentPoints);

                float componentSizeRelative =
                    objectDiagonal > 1e-8f
                        ? componentBounds.size.magnitude /
                          objectDiagonal
                        : 1f;

                if (minimumComponentSizeRelative > 0f &&
                    componentSizeRelative <
                    minimumComponentSizeRelative)
                {
                    discardedComponents++;
                    continue;
                }

                keptComponents++;

                PcaFrame componentFrame =
                    FitPcaFrame(componentPoints);

                float primaryLength =
                    Mathf.Max(
                        componentFrame.Size.x,
                        1e-6f
                    );

                int segmentCount =
                    Mathf.Clamp(
                        Mathf.CeilToInt(
                            primaryLength /
                            maxSegmentLength
                        ),
                        1,
                        Mathf.Max(1, maxSegmentsPerComponent)
                    );

                List<List<int>> segmentTriangles =
                    PartitionComponentTriangles(
                        component,
                        triangles,
                        componentFrame,
                        segmentCount
                    );

                foreach (List<int> segment in segmentTriangles)
                {
                    if (segment.Count == 0)
                        continue;

                    HashSet<int> segmentCanonical =
                        new HashSet<int>();

                    Dictionary<int, int> submeshVotes =
                        new Dictionary<int, int>();

                    foreach (int triIndex in segment)
                    {
                        SourceTriangle tri =
                            triangles[triIndex];

                        segmentCanonical.Add(
                            sourceToCanonical[tri.a]
                        );
                        segmentCanonical.Add(
                            sourceToCanonical[tri.b]
                        );
                        segmentCanonical.Add(
                            sourceToCanonical[tri.c]
                        );

                        if (!submeshVotes.ContainsKey(tri.submesh))
                            submeshVotes[tri.submesh] = 0;

                        submeshVotes[tri.submesh]++;
                    }

                    List<Vector3> segmentPoints =
                        segmentCanonical
                            .Select(i => canonicalPositions[i])
                            .ToList();

                    if (segmentPoints.Count < 2)
                        continue;

                    PcaFrame frame =
                        FitPcaFrame(segmentPoints);

                    Vector3 size =
                        frame.Size;

                    float longest =
                        Mathf.Max(
                            size.x,
                            Mathf.Max(size.y, size.z)
                        );

                    float middle =
                        MedianOfThree(
                            size.x,
                            size.y,
                            size.z
                        );

                    float shortest =
                        Mathf.Min(
                            size.x,
                            Mathf.Min(size.y, size.z)
                        );

                    float elongatedRatio =
                        middle > 1e-8f
                            ? longest / middle
                            : float.PositiveInfinity;

                    float panelRatio =
                        longest > 1e-8f
                            ? shortest / longest
                            : 1f;

                    bool elongated =
                        elongatedRatio >=
                        elongatedAspectThreshold;

                    bool panel =
                        !elongated &&
                        panelRatio <=
                        panelThicknessRatio;

                    int targetSubmesh = 0;

                    if (preserveSubmeshMaterials &&
                        submeshVotes.Count > 0)
                    {
                        targetSubmesh =
                            Mathf.Clamp(
                                submeshVotes
                                    .OrderByDescending(kv => kv.Value)
                                    .First()
                                    .Key,
                                0,
                                submeshCount - 1
                            );
                    }

                    if (elongated &&
                        usePrismsForElongated)
                    {
                        AppendOrientedPrism(
                            frame,
                            Mathf.Clamp(
                                beamCrossSectionSides,
                                4,
                                12
                            ),
                            outVertices,
                            outNormals,
                            outUv,
                            outTriangles[targetSubmesh]
                        );
                    }
                    else
                    {
                        AppendOrientedBox(
                            frame,
                            outVertices,
                            outNormals,
                            outUv,
                            outTriangles[targetSubmesh]
                        );
                    }

                    generatedPrimitives++;
                }
            }

            Mesh result = new Mesh
            {
                name = source.name + "_PCA_PrimitiveProxy"
            };

            if (outVertices.Count > 65535)
                result.indexFormat = IndexFormat.UInt32;

            result.SetVertices(outVertices);
            result.SetNormals(outNormals);
            result.SetUVs(0, outUv);

            result.subMeshCount =
                outTriangles.Length;

            for (int i = 0; i < outTriangles.Length; i++)
            {
                result.SetTriangles(
                    outTriangles[i],
                    i,
                    false
                );
            }

            result.RecalculateBounds();

            if (recalcTangents &&
                outUv.Count == outVertices.Count &&
                outVertices.Count > 0)
            {
                result.RecalculateTangents();
            }

            LastDiagnostics =
                $"render verts {source.vertexCount:N0} -> " +
                $"canonical {canonicalPositions.Count:N0}; " +
                $"components {components.Count:N0}, " +
                $"kept {keptComponents:N0}, " +
                $"discarded {discardedComponents:N0}; " +
                $"generated primitives {generatedPrimitives:N0}; " +
                $"triangles {GetTriangleCount(source):N0} -> " +
                $"{GetTriangleCount(result):N0}.";

            return result;
        }

        private static List<SourceTriangle> BuildSourceTriangles(
            Mesh source,
            Vector3[] vertices)
        {
            List<SourceTriangle> triangles =
                new List<SourceTriangle>();

            for (int submesh = 0;
                 submesh < source.subMeshCount;
                 submesh++)
            {
                if (source.GetTopology(submesh) !=
                    MeshTopology.Triangles)
                {
                    continue;
                }

                int[] indices =
                    source.GetTriangles(submesh);

                for (int i = 0;
                     i + 2 < indices.Length;
                     i += 3)
                {
                    int a = indices[i + 0];
                    int b = indices[i + 1];
                    int c = indices[i + 2];

                    if (a == b || b == c || a == c)
                        continue;

                    triangles.Add(
                        new SourceTriangle
                        {
                            a = a,
                            b = b,
                            c = c,
                            submesh = submesh,
                            centroid =
                                (vertices[a] +
                                 vertices[b] +
                                 vertices[c]) / 3f
                        }
                    );
                }
            }

            return triangles;
        }

        private static void BuildCanonicalVertices(
            Vector3[] positions,
            float tolerance,
            out int[] sourceToCanonical,
            out List<Vector3> canonicalPositions)
        {
            sourceToCanonical =
                new int[positions.Length];

            canonicalPositions =
                new List<Vector3>();

            Dictionary<GridKey3, List<int>> buckets =
                new Dictionary<GridKey3, List<int>>();

            float inv =
                1f / Mathf.Max(tolerance, 1e-8f);

            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 p = positions[i];

                GridKey3 key =
                    new GridKey3(
                        Mathf.RoundToInt(p.x * inv),
                        Mathf.RoundToInt(p.y * inv),
                        Mathf.RoundToInt(p.z * inv)
                    );

                int canonical = -1;

                for (int dx = -1; dx <= 1 && canonical < 0; dx++)
                for (int dy = -1; dy <= 1 && canonical < 0; dy++)
                for (int dz = -1; dz <= 1 && canonical < 0; dz++)
                {
                    GridKey3 neighbor =
                        new GridKey3(
                            key.x + dx,
                            key.y + dy,
                            key.z + dz
                        );

                    if (!buckets.TryGetValue(
                            neighbor,
                            out List<int> candidates))
                    {
                        continue;
                    }

                    foreach (int candidate in candidates)
                    {
                        if ((canonicalPositions[candidate] - p)
                            .sqrMagnitude <=
                            tolerance * tolerance)
                        {
                            canonical = candidate;
                            break;
                        }
                    }
                }

                if (canonical < 0)
                {
                    canonical =
                        canonicalPositions.Count;

                    canonicalPositions.Add(p);

                    if (!buckets.TryGetValue(
                            key,
                            out List<int> list))
                    {
                        list = new List<int>();
                        buckets.Add(key, list);
                    }

                    list.Add(canonical);
                }

                sourceToCanonical[i] = canonical;
            }
        }

        private static List<Component> BuildComponents(
            List<SourceTriangle> triangles,
            int[] sourceToCanonical,
            bool splitByMaterial)
        {
            Dictionary<int, List<int>> vertexToTriangles =
                new Dictionary<int, List<int>>();

            for (int i = 0; i < triangles.Count; i++)
            {
                SourceTriangle tri = triangles[i];

                int[] canonical =
                {
                    sourceToCanonical[tri.a],
                    sourceToCanonical[tri.b],
                    sourceToCanonical[tri.c]
                };

                foreach (int vertex in canonical.Distinct())
                {
                    if (!vertexToTriangles.TryGetValue(
                            vertex,
                            out List<int> list))
                    {
                        list = new List<int>();
                        vertexToTriangles.Add(vertex, list);
                    }

                    list.Add(i);
                }
            }

            bool[] visited =
                new bool[triangles.Count];

            List<Component> components =
                new List<Component>();

            Queue<int> queue =
                new Queue<int>();

            for (int seed = 0;
                 seed < triangles.Count;
                 seed++)
            {
                if (visited[seed])
                    continue;

                Component component =
                    new Component();

                visited[seed] = true;
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    int triIndex =
                        queue.Dequeue();

                    component.triangleIndices.Add(
                        triIndex
                    );

                    SourceTriangle tri =
                        triangles[triIndex];

                    int[] canonical =
                    {
                        sourceToCanonical[tri.a],
                        sourceToCanonical[tri.b],
                        sourceToCanonical[tri.c]
                    };

                    foreach (int vertex in canonical.Distinct())
                    {
                        component.canonicalVertices.Add(
                            vertex
                        );

                        if (!vertexToTriangles.TryGetValue(
                                vertex,
                                out List<int> adjacent))
                        {
                            continue;
                        }

                        foreach (int next in adjacent)
                        {
                            if (visited[next])
                                continue;

                            if (splitByMaterial &&
                                triangles[next].submesh != tri.submesh)
                            {
                                continue;
                            }

                            visited[next] = true;
                            queue.Enqueue(next);
                        }
                    }
                }

                components.Add(component);
            }

            return components;
        }

        private static List<List<int>> PartitionComponentTriangles(
            Component component,
            List<SourceTriangle> triangles,
            PcaFrame frame,
            int segmentCount)
        {
            List<List<int>> segments =
                new List<List<int>>();

            for (int i = 0; i < segmentCount; i++)
                segments.Add(new List<int>());

            float min = frame.min.x;
            float max = frame.max.x;
            float range =
                Mathf.Max(max - min, 1e-8f);

            foreach (int triIndex in component.triangleIndices)
            {
                Vector3 local =
                    ToFrameLocal(
                        triangles[triIndex].centroid,
                        frame
                    );

                float t =
                    Mathf.Clamp01(
                        (local.x - min) /
                        range
                    );

                int bin =
                    Mathf.Min(
                        segmentCount - 1,
                        Mathf.FloorToInt(
                            t * segmentCount
                        )
                    );

                segments[bin].Add(triIndex);
            }

            return segments;
        }

        private static PcaFrame FitPcaFrame(
            List<Vector3> points)
        {
            Vector3 center = Vector3.zero;

            foreach (Vector3 p in points)
                center += p;

            center /= Mathf.Max(1, points.Count);

            SymmetricMatrix3 covariance =
                new SymmetricMatrix3();

            foreach (Vector3 p in points)
            {
                Vector3 d = p - center;
                covariance.AddOuterProduct(d);
            }

            covariance.Scale(
                1f / Mathf.Max(1, points.Count)
            );

            Vector3 axis0;
            Vector3 axis1;
            Vector3 axis2;

            JacobiEigenvectors(
                covariance,
                out axis0,
                out axis1,
                out axis2
            );

            // Order axes by fitted extent so x is always the longest.
            Vector3[] axes =
            {
                axis0.normalized,
                axis1.normalized,
                axis2.normalized
            };

            float[] extents =
            {
                AxisRange(points, center, axes[0]),
                AxisRange(points, center, axes[1]),
                AxisRange(points, center, axes[2])
            };

            int[] order = { 0, 1, 2 };

            Array.Sort(
                order,
                (a, b) =>
                    extents[b].CompareTo(extents[a])
            );

            axis0 = axes[order[0]].normalized;
            axis1 = axes[order[1]].normalized;

            axis2 =
                Vector3.Cross(axis0, axis1).normalized;

            if (axis2.sqrMagnitude < 1e-8f)
                axis2 = axes[order[2]].normalized;

            axis1 =
                Vector3.Cross(axis2, axis0).normalized;

            Vector3 min =
                new Vector3(
                    float.PositiveInfinity,
                    float.PositiveInfinity,
                    float.PositiveInfinity
                );

            Vector3 max =
                new Vector3(
                    float.NegativeInfinity,
                    float.NegativeInfinity,
                    float.NegativeInfinity
                );

            foreach (Vector3 p in points)
            {
                Vector3 d = p - center;

                Vector3 local =
                    new Vector3(
                        Vector3.Dot(d, axis0),
                        Vector3.Dot(d, axis1),
                        Vector3.Dot(d, axis2)
                    );

                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }

            // Avoid zero-thickness generated meshes.
            Vector3 size = max - min;
            float epsilon =
                Mathf.Max(size.x, Mathf.Max(size.y, size.z)) *
                0.0025f;

            epsilon = Mathf.Max(epsilon, 1e-5f);

            if (size.x < epsilon)
            {
                float c = (min.x + max.x) * 0.5f;
                min.x = c - epsilon * 0.5f;
                max.x = c + epsilon * 0.5f;
            }

            if (size.y < epsilon)
            {
                float c = (min.y + max.y) * 0.5f;
                min.y = c - epsilon * 0.5f;
                max.y = c + epsilon * 0.5f;
            }

            if (size.z < epsilon)
            {
                float c = (min.z + max.z) * 0.5f;
                min.z = c - epsilon * 0.5f;
                max.z = c + epsilon * 0.5f;
            }

            return new PcaFrame
            {
                center = center,
                axis0 = axis0,
                axis1 = axis1,
                axis2 = axis2,
                min = min,
                max = max
            };
        }

        private static float AxisRange(
            List<Vector3> points,
            Vector3 center,
            Vector3 axis)
        {
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;

            foreach (Vector3 p in points)
            {
                float value =
                    Vector3.Dot(
                        p - center,
                        axis
                    );

                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }

            return max - min;
        }

        private static Vector3 ToFrameLocal(
            Vector3 world,
            PcaFrame frame)
        {
            Vector3 d = world - frame.center;

            return new Vector3(
                Vector3.Dot(d, frame.axis0),
                Vector3.Dot(d, frame.axis1),
                Vector3.Dot(d, frame.axis2)
            );
        }

        private static Vector3 FromFrameLocal(
            Vector3 local,
            PcaFrame frame)
        {
            return frame.center +
                   frame.axis0 * local.x +
                   frame.axis1 * local.y +
                   frame.axis2 * local.z;
        }

        private static void AppendOrientedBox(
            PcaFrame frame,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uv,
            List<int> triangles)
        {
            Vector3 min = frame.min;
            Vector3 max = frame.max;

            Vector3[] corners =
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, max.z),
                new Vector3(min.x, max.y, max.z)
            };

            int[,] faces =
            {
                { 0, 3, 2, 1 }, // -Z
                { 4, 5, 6, 7 }, // +Z
                { 0, 4, 7, 3 }, // -X
                { 1, 2, 6, 5 }, // +X
                { 3, 7, 6, 2 }, // +Y
                { 0, 1, 5, 4 }  // -Y
            };

            Vector3[] faceNormals =
            {
                -frame.axis2,
                frame.axis2,
                -frame.axis0,
                frame.axis0,
                frame.axis1,
                -frame.axis1
            };

            for (int face = 0; face < 6; face++)
            {
                int baseIndex = vertices.Count;

                for (int i = 0; i < 4; i++)
                {
                    vertices.Add(
                        FromFrameLocal(
                            corners[faces[face, i]],
                            frame
                        )
                    );

                    normals.Add(faceNormals[face]);

                    uv.Add(
                        i switch
                        {
                            0 => new Vector2(0f, 0f),
                            1 => new Vector2(0f, 1f),
                            2 => new Vector2(1f, 1f),
                            _ => new Vector2(1f, 0f)
                        }
                    );
                }

                triangles.Add(baseIndex + 0);
                triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 2);

                triangles.Add(baseIndex + 0);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 3);
            }
        }

        private static void AppendOrientedPrism(
            PcaFrame frame,
            int sides,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uv,
            List<int> triangles)
        {
            sides = Mathf.Clamp(sides, 4, 12);

            float minX = frame.min.x;
            float maxX = frame.max.x;

            float centerY =
                (frame.min.y + frame.max.y) * 0.5f;

            float centerZ =
                (frame.min.z + frame.max.z) * 0.5f;

            float radiusY =
                Mathf.Max(
                    (frame.max.y - frame.min.y) * 0.5f,
                    1e-5f
                );

            float radiusZ =
                Mathf.Max(
                    (frame.max.z - frame.min.z) * 0.5f,
                    1e-5f
                );

            // Sides: duplicate vertices per quad for clean flat-ish proxy shading.
            for (int side = 0; side < sides; side++)
            {
                float a0 =
                    side * Mathf.PI * 2f / sides;

                float a1 =
                    (side + 1) * Mathf.PI * 2f / sides;

                Vector3 p00 =
                    new Vector3(
                        minX,
                        centerY + Mathf.Cos(a0) * radiusY,
                        centerZ + Mathf.Sin(a0) * radiusZ
                    );

                Vector3 p01 =
                    new Vector3(
                        maxX,
                        centerY + Mathf.Cos(a0) * radiusY,
                        centerZ + Mathf.Sin(a0) * radiusZ
                    );

                Vector3 p10 =
                    new Vector3(
                        minX,
                        centerY + Mathf.Cos(a1) * radiusY,
                        centerZ + Mathf.Sin(a1) * radiusZ
                    );

                Vector3 p11 =
                    new Vector3(
                        maxX,
                        centerY + Mathf.Cos(a1) * radiusY,
                        centerZ + Mathf.Sin(a1) * radiusZ
                    );

                int baseIndex =
                    vertices.Count;

                Vector3 w00 =
                    FromFrameLocal(p00, frame);

                Vector3 w01 =
                    FromFrameLocal(p01, frame);

                Vector3 w10 =
                    FromFrameLocal(p10, frame);

                Vector3 w11 =
                    FromFrameLocal(p11, frame);

                // Winding is chosen so the side faces point away from the
                // prism axis. This matters because these generated proxies
                // intentionally use clean, explicit normals.
                Vector3 outwardNormal =
                    Vector3.Cross(
                        w10 - w00,
                        w11 - w00
                    ).normalized;

                vertices.Add(w00);
                vertices.Add(w10);
                vertices.Add(w11);
                vertices.Add(w01);

                normals.Add(outwardNormal);
                normals.Add(outwardNormal);
                normals.Add(outwardNormal);
                normals.Add(outwardNormal);

                uv.Add(new Vector2(0f, 0f));
                uv.Add(new Vector2(0f, 1f));
                uv.Add(new Vector2(1f, 1f));
                uv.Add(new Vector2(1f, 0f));

                triangles.Add(baseIndex + 0);
                triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 2);

                triangles.Add(baseIndex + 0);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 3);
            }

            // Caps
            AppendPrismCap(
                frame,
                minX,
                centerY,
                centerZ,
                radiusY,
                radiusZ,
                sides,
                false,
                vertices,
                normals,
                uv,
                triangles
            );

            AppendPrismCap(
                frame,
                maxX,
                centerY,
                centerZ,
                radiusY,
                radiusZ,
                sides,
                true,
                vertices,
                normals,
                uv,
                triangles
            );
        }

        private static void AppendPrismCap(
            PcaFrame frame,
            float x,
            float centerY,
            float centerZ,
            float radiusY,
            float radiusZ,
            int sides,
            bool positive,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uv,
            List<int> triangles)
        {
            int centerIndex =
                vertices.Count;

            vertices.Add(
                FromFrameLocal(
                    new Vector3(
                        x,
                        centerY,
                        centerZ
                    ),
                    frame
                )
            );

            Vector3 capNormal =
                positive
                    ? frame.axis0
                    : -frame.axis0;

            normals.Add(capNormal);
            uv.Add(new Vector2(0.5f, 0.5f));

            int ringStart =
                vertices.Count;

            for (int i = 0; i < sides; i++)
            {
                float angle =
                    i * Mathf.PI * 2f / sides;

                float y =
                    centerY +
                    Mathf.Cos(angle) * radiusY;

                float z =
                    centerZ +
                    Mathf.Sin(angle) * radiusZ;

                vertices.Add(
                    FromFrameLocal(
                        new Vector3(x, y, z),
                        frame
                    )
                );

                normals.Add(capNormal);

                uv.Add(
                    new Vector2(
                        Mathf.Cos(angle) * 0.5f + 0.5f,
                        Mathf.Sin(angle) * 0.5f + 0.5f
                    )
                );
            }

            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;

                triangles.Add(centerIndex);

                if (positive)
                {
                    triangles.Add(ringStart + i);
                    triangles.Add(ringStart + next);
                }
                else
                {
                    triangles.Add(ringStart + next);
                    triangles.Add(ringStart + i);
                }
            }
        }

        private static Bounds BoundsFromPoints(
            List<Vector3> points)
        {
            Bounds bounds =
                new Bounds(
                    points[0],
                    Vector3.zero
                );

            for (int i = 1; i < points.Count; i++)
                bounds.Encapsulate(points[i]);

            return bounds;
        }

        private static float MedianOfThree(
            float a,
            float b,
            float c)
        {
            if (a > b)
            {
                float t = a;
                a = b;
                b = t;
            }

            if (b > c)
            {
                float t = b;
                b = c;
                c = t;
            }

            if (a > b)
            {
                float t = a;
                a = b;
                b = t;
            }

            return b;
        }

        private struct GridKey3 :
            IEquatable<GridKey3>
        {
            public readonly int x;
            public readonly int y;
            public readonly int z;

            public GridKey3(
                int x,
                int y,
                int z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public bool Equals(GridKey3 other)
            {
                return x == other.x &&
                       y == other.y &&
                       z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is GridKey3 other &&
                       Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + x;
                    hash = hash * 31 + y;
                    hash = hash * 31 + z;
                    return hash;
                }
            }
        }

        private struct SymmetricMatrix3
        {
            public float xx;
            public float xy;
            public float xz;
            public float yy;
            public float yz;
            public float zz;

            public void AddOuterProduct(Vector3 v)
            {
                xx += v.x * v.x;
                xy += v.x * v.y;
                xz += v.x * v.z;
                yy += v.y * v.y;
                yz += v.y * v.z;
                zz += v.z * v.z;
            }

            public void Scale(float scalar)
            {
                xx *= scalar;
                xy *= scalar;
                xz *= scalar;
                yy *= scalar;
                yz *= scalar;
                zz *= scalar;
            }
        }

        private static void JacobiEigenvectors(
            SymmetricMatrix3 m,
            out Vector3 axis0,
            out Vector3 axis1,
            out Vector3 axis2)
        {
            float[,] a =
            {
                { m.xx, m.xy, m.xz },
                { m.xy, m.yy, m.yz },
                { m.xz, m.yz, m.zz }
            };

            float[,] v =
            {
                { 1f, 0f, 0f },
                { 0f, 1f, 0f },
                { 0f, 0f, 1f }
            };

            for (int iteration = 0;
                 iteration < 16;
                 iteration++)
            {
                int p = 0;
                int q = 1;
                float max =
                    Mathf.Abs(a[0, 1]);

                if (Mathf.Abs(a[0, 2]) > max)
                {
                    p = 0;
                    q = 2;
                    max = Mathf.Abs(a[0, 2]);
                }

                if (Mathf.Abs(a[1, 2]) > max)
                {
                    p = 1;
                    q = 2;
                    max = Mathf.Abs(a[1, 2]);
                }

                if (max < 1e-10f)
                    break;

                float app = a[p, p];
                float aqq = a[q, q];
                float apq = a[p, q];

                float phi =
                    0.5f *
                    Mathf.Atan2(
                        2f * apq,
                        aqq - app
                    );

                float c = Mathf.Cos(phi);
                float s = Mathf.Sin(phi);

                for (int k = 0; k < 3; k++)
                {
                    float aik = a[p, k];
                    float aqk = a[q, k];

                    a[p, k] =
                        c * aik - s * aqk;

                    a[q, k] =
                        s * aik + c * aqk;
                }

                for (int k = 0; k < 3; k++)
                {
                    float akp = a[k, p];
                    float akq = a[k, q];

                    a[k, p] =
                        c * akp - s * akq;

                    a[k, q] =
                        s * akp + c * akq;
                }

                for (int k = 0; k < 3; k++)
                {
                    float vip = v[k, p];
                    float viq = v[k, q];

                    v[k, p] =
                        c * vip - s * viq;

                    v[k, q] =
                        s * vip + c * viq;
                }
            }

            axis0 =
                new Vector3(
                    v[0, 0],
                    v[1, 0],
                    v[2, 0]
                ).normalized;

            axis1 =
                new Vector3(
                    v[0, 1],
                    v[1, 1],
                    v[2, 1]
                ).normalized;

            axis2 =
                new Vector3(
                    v[0, 2],
                    v[1, 2],
                    v[2, 2]
                ).normalized;

            if (axis0.sqrMagnitude < 1e-8f)
                axis0 = Vector3.right;

            if (axis1.sqrMagnitude < 1e-8f)
                axis1 = Vector3.up;

            if (axis2.sqrMagnitude < 1e-8f)
                axis2 = Vector3.forward;
        }
    }

    // ============================================================
    // FEATURE-AWARE SIMPLIFIER
    // ============================================================
    // FEATURE-AWARE QEM SIMPLIFIER
    // ============================================================

    /// <summary>
    /// Feature-aware, dependency-free QEM edge-collapse simplifier aimed at
    /// IFC/BIM meshes. It first reconstructs geometric connectivity from
    /// position-coincident render vertices, then simplifies the resulting
    /// topology globally while protecting important feature edges.
    ///
    /// The working topology is separate from the render topology. UV/color/
    /// normal data stays attached to triangle corners, so coincident vertices
    /// can participate in one geometric surface without erasing material/UV
    /// seams in the final render mesh.
    /// </summary>
    private static class FeatureAwareQemSimplifier
    {
        public sealed class Diagnostics
        {
            public int sourceRenderVertices;
            public int geometricVertices;
            public int sourceTriangles;
            public int cleanedTriangles;
            public int targetTriangles;
            public int resultTriangles;
            public int collapses;
            public int rejectedFlips;
            public int protectedEdges;

            public float GeometricVertexRatio =>
                sourceRenderVertices > 0
                    ? geometricVertices / (float)sourceRenderVertices
                    : 1f;

            public float TriangleRatio =>
                sourceTriangles > 0
                    ? resultTriangles / (float)sourceTriangles
                    : 1f;

            public override string ToString()
            {
                return
                    $"Render verts {sourceRenderVertices:N0} -> geometric {geometricVertices:N0}; " +
                    $"tris {sourceTriangles:N0} -> {resultTriangles:N0} " +
                    $"(target {targetTriangles:N0}); collapses {collapses:N0}; " +
                    $"protected edges {protectedEdges:N0}.";
            }
        }

        public static Diagnostics LastDiagnostics { get; private set; }

        public static Mesh Simplify(
            Mesh source,
            float targetTriangleRatio,
            bool recalcNormals,
            bool recalcTangents,
            bool preserveColors,
            bool preserveUv2,
            float weldToleranceRelative,
            bool protectOpenBoundaries,
            bool protectMaterialBoundaries,
            bool protectUvSeams,
            bool protectSharpEdges,
            float sharpEdgeAngleDegrees,
            float maxTriangleNormalDeviationDegrees)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (!source.isReadable)
            {
                throw new InvalidOperationException(
                    $"Mesh '{source.name}' is not readable."
                );
            }

            targetTriangleRatio =
                Mathf.Clamp(targetTriangleRatio, 0.01f, 1f);

            if (targetTriangleRatio >= 0.999f)
                return UnityEngine.Object.Instantiate(source);

            Vector3[] sourcePositions = source.vertices;
            Vector3[] sourceNormals = source.normals;
            Vector2[] sourceUv0 = source.uv;
            Vector2[] sourceUv2 = source.uv2;
            Vector4[] sourceTangents = source.tangents;
            Color[] sourceColors = source.colors;

            bool hasNormals =
                sourceNormals != null &&
                sourceNormals.Length == source.vertexCount;

            bool hasUv0 =
                sourceUv0 != null &&
                sourceUv0.Length == source.vertexCount;

            bool hasUv2 =
                preserveUv2 &&
                sourceUv2 != null &&
                sourceUv2.Length == source.vertexCount;

            bool hasTangents =
                sourceTangents != null &&
                sourceTangents.Length == source.vertexCount;

            bool hasColors =
                preserveColors &&
                sourceColors != null &&
                sourceColors.Length == source.vertexCount;

            float maxExtent = Mathf.Max(
                source.bounds.size.x,
                Mathf.Max(source.bounds.size.y, source.bounds.size.z)
            );

            if (maxExtent <= 1e-8f)
                maxExtent = 1f;

            float weldTolerance = Mathf.Max(
                1e-7f,
                maxExtent * Mathf.Clamp(
                    weldToleranceRelative,
                    1e-8f,
                    1e-2f
                )
            );

            GeometryBuilder builder = new GeometryBuilder(
                sourcePositions,
                sourceNormals,
                sourceUv0,
                sourceUv2,
                sourceTangents,
                sourceColors,
                hasNormals,
                hasUv0,
                hasUv2,
                hasTangents,
                hasColors,
                weldTolerance
            );

            builder.Build(source);

            List<WorkingVertex> vertices = builder.vertices;
            List<WorkingTriangle> triangles = builder.triangles;

            int sourceTriangleCount =
                (int)Math.Min(
                    int.MaxValue,
                    GetTriangleCount(source)
                );

            int currentTriangleCount = triangles.Count;

            int targetTriangleCount = Mathf.Max(
                4,
                Mathf.RoundToInt(
                    sourceTriangleCount * targetTriangleRatio
                )
            );

            targetTriangleCount = Mathf.Min(
                targetTriangleCount,
                currentTriangleCount
            );

            // Build original feature constraints before any collapses.
            Dictionary<EdgeKey, EdgeInfo> edgeInfos =
                BuildInitialEdgeInfo(
                    vertices,
                    triangles,
                    hasUv0
                );

            float sharpCos = Mathf.Cos(
                Mathf.Clamp(
                    sharpEdgeAngleDegrees,
                    1f,
                    179f
                ) * Mathf.Deg2Rad
            );

            int protectedEdgeCount = 0;

            foreach (KeyValuePair<EdgeKey, EdgeInfo> pair in edgeInfos)
            {
                EdgeKey edge = pair.Key;
                EdgeInfo info = pair.Value;

                bool protect = false;

                if (protectOpenBoundaries && info.useCount != 2)
                    protect = true;

                if (protectMaterialBoundaries && info.materialBoundary)
                    protect = true;

                if (protectUvSeams && info.uvSeam)
                    protect = true;

                if (protectSharpEdges &&
                    info.firstTriangle >= 0 &&
                    info.secondTriangle >= 0)
                {
                    Vector3 n0 = GetTriangleNormal(
                        vertices,
                        triangles[info.firstTriangle]
                    );

                    Vector3 n1 = GetTriangleNormal(
                        vertices,
                        triangles[info.secondTriangle]
                    );

                    if (n0.sqrMagnitude > 1e-12f &&
                        n1.sqrMagnitude > 1e-12f &&
                        Vector3.Dot(n0, n1) < sharpCos)
                    {
                        protect = true;
                    }
                }

                if (!protect)
                    continue;

                vertices[edge.a].protectedNeighbors.Add(edge.b);
                vertices[edge.b].protectedNeighbors.Add(edge.a);
                protectedEdgeCount++;
            }

            // Build triangle adjacency and QEM quadrics.
            for (int i = 0; i < triangles.Count; i++)
            {
                WorkingTriangle tri = triangles[i];

                vertices[tri.a].triangles.Add(i);
                vertices[tri.b].triangles.Add(i);
                vertices[tri.c].triangles.Add(i);

                AddTriangleQuadric(vertices, tri);
            }

            MinHeap heap = new MinHeap();

            foreach (EdgeKey edge in edgeInfos.Keys)
            {
                PushCandidate(
                    edge.a,
                    edge.b,
                    vertices,
                    triangles,
                    heap
                );
            }

            float minCollapseNormalDot = Mathf.Cos(
                Mathf.Clamp(
                    maxTriangleNormalDeviationDegrees,
                    1f,
                    89.9f
                ) * Mathf.Deg2Rad
            );

            int collapseCount = 0;
            int rejectedFlips = 0;

            int safetyLimit = Mathf.Max(
                1000,
                currentTriangleCount * 4
            );

            int safety = 0;

            while (currentTriangleCount > targetTriangleCount &&
                   heap.Count > 0 &&
                   safety++ < safetyLimit)
            {
                EdgeCandidate candidate = heap.Pop();

                if (!IsCandidateCurrent(candidate, vertices))
                    continue;

                if (!AreAdjacent(
                        candidate.keep,
                        candidate.remove,
                        vertices,
                        triangles))
                {
                    continue;
                }

                EdgeCandidate refreshed;

                if (!TryBuildCandidate(
                        candidate.keep,
                        candidate.remove,
                        vertices,
                        triangles,
                        out refreshed))
                {
                    continue;
                }

                if (!CollapsePreservesFaces(
                        refreshed,
                        vertices,
                        triangles,
                        minCollapseNormalDot))
                {
                    rejectedFlips++;
                    continue;
                }

                HashSet<int> touchedVertices;

                int removedTriangles = CollapseEdge(
                    refreshed,
                    vertices,
                    triangles,
                    out touchedVertices
                );

                if (removedTriangles <= 0)
                    continue;

                currentTriangleCount -= removedTriangles;
                collapseCount++;

                // Refresh all local edges whose cost/topology may have changed.
                HashSet<EdgeKey> localEdges = new HashSet<EdgeKey>();

                foreach (int vertexId in touchedVertices)
                {
                    if (vertexId < 0 ||
                        vertexId >= vertices.Count ||
                        !vertices[vertexId].valid)
                    {
                        continue;
                    }

                    CollectIncidentEdges(
                        vertexId,
                        vertices,
                        triangles,
                        localEdges
                    );
                }

                foreach (EdgeKey edge in localEdges)
                {
                    PushCandidate(
                        edge.a,
                        edge.b,
                        vertices,
                        triangles,
                        heap
                    );
                }
            }

            Mesh output = BuildOutputMesh(
                source.name + "_FeatureAwareQEM",
                vertices,
                triangles,
                source.subMeshCount,
                hasNormals,
                hasUv0,
                hasUv2,
                hasTangents,
                hasColors,
                recalcNormals,
                recalcTangents,
                sharpEdgeAngleDegrees
            );

            int resultTriangles =
                (int)Math.Min(
                    int.MaxValue,
                    GetTriangleCount(output)
                );

            LastDiagnostics = new Diagnostics
            {
                sourceRenderVertices = source.vertexCount,
                geometricVertices = builder.geometricVertexCount,
                sourceTriangles = sourceTriangleCount,
                cleanedTriangles = triangles.Count,
                targetTriangles = targetTriangleCount,
                resultTriangles = resultTriangles,
                collapses = collapseCount,
                rejectedFlips = rejectedFlips,
                protectedEdges = protectedEdgeCount
            };

            return output;
        }

        // ------------------------------------------------------------
        // BUILD GEOMETRIC TOPOLOGY
        // ------------------------------------------------------------

        private sealed class GeometryBuilder
        {
            private readonly Vector3[] positions;
            private readonly Vector3[] normals;
            private readonly Vector2[] uv0;
            private readonly Vector2[] uv2;
            private readonly Vector4[] tangents;
            private readonly Color[] colors;

            private readonly bool hasNormals;
            private readonly bool hasUv0;
            private readonly bool hasUv2;
            private readonly bool hasTangents;
            private readonly bool hasColors;

            private readonly float tolerance;
            private readonly float toleranceSq;
            private readonly Dictionary<CellKey, List<int>> cells =
                new Dictionary<CellKey, List<int>>();

            private readonly int[] sourceToGeometric;

            public readonly List<WorkingVertex> vertices =
                new List<WorkingVertex>();

            public readonly List<WorkingTriangle> triangles =
                new List<WorkingTriangle>();

            public int geometricVertexCount => vertices.Count;

            public GeometryBuilder(
                Vector3[] positions,
                Vector3[] normals,
                Vector2[] uv0,
                Vector2[] uv2,
                Vector4[] tangents,
                Color[] colors,
                bool hasNormals,
                bool hasUv0,
                bool hasUv2,
                bool hasTangents,
                bool hasColors,
                float tolerance)
            {
                this.positions = positions;
                this.normals = normals;
                this.uv0 = uv0;
                this.uv2 = uv2;
                this.tangents = tangents;
                this.colors = colors;

                this.hasNormals = hasNormals;
                this.hasUv0 = hasUv0;
                this.hasUv2 = hasUv2;
                this.hasTangents = hasTangents;
                this.hasColors = hasColors;

                this.tolerance = tolerance;
                toleranceSq = tolerance * tolerance;

                sourceToGeometric = new int[positions.Length];

                for (int i = 0; i < sourceToGeometric.Length; i++)
                    sourceToGeometric[i] = -1;
            }

            public void Build(Mesh source)
            {
                HashSet<InitialTriangleKey> uniqueTriangles =
                    new HashSet<InitialTriangleKey>();

                for (int submesh = 0;
                     submesh < source.subMeshCount;
                     submesh++)
                {
                    if (source.GetTopology(submesh) !=
                        MeshTopology.Triangles)
                    {
                        continue;
                    }

                    int[] indices = source.GetTriangles(submesh);

                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        int sourceA = indices[i + 0];
                        int sourceB = indices[i + 1];
                        int sourceC = indices[i + 2];

                        int a = GetGeometricVertex(sourceA);
                        int b = GetGeometricVertex(sourceB);
                        int c = GetGeometricVertex(sourceC);

                        if (a == b || b == c || a == c)
                            continue;

                        InitialTriangleKey triangleKey =
                            new InitialTriangleKey(a, b, c, submesh);

                        if (!uniqueTriangles.Add(triangleKey))
                            continue;

                        WorkingTriangle tri = new WorkingTriangle
                        {
                            a = a,
                            b = b,
                            c = c,
                            submesh = submesh,
                            valid = true,

                            uv0A = hasUv0 ? uv0[sourceA] : Vector2.zero,
                            uv0B = hasUv0 ? uv0[sourceB] : Vector2.zero,
                            uv0C = hasUv0 ? uv0[sourceC] : Vector2.zero,

                            uv2A = hasUv2 ? uv2[sourceA] : Vector2.zero,
                            uv2B = hasUv2 ? uv2[sourceB] : Vector2.zero,
                            uv2C = hasUv2 ? uv2[sourceC] : Vector2.zero,

                            colorA = hasColors ? colors[sourceA] : Color.white,
                            colorB = hasColors ? colors[sourceB] : Color.white,
                            colorC = hasColors ? colors[sourceC] : Color.white,

                            normalA = hasNormals ? normals[sourceA] : Vector3.zero,
                            normalB = hasNormals ? normals[sourceB] : Vector3.zero,
                            normalC = hasNormals ? normals[sourceC] : Vector3.zero,

                            tangentA = hasTangents
                                ? tangents[sourceA]
                                : new Vector4(1f, 0f, 0f, 1f),

                            tangentB = hasTangents
                                ? tangents[sourceB]
                                : new Vector4(1f, 0f, 0f, 1f),

                            tangentC = hasTangents
                                ? tangents[sourceC]
                                : new Vector4(1f, 0f, 0f, 1f)
                        };

                        Vector3 face = Vector3.Cross(
                            vertices[b].position - vertices[a].position,
                            vertices[c].position - vertices[a].position
                        );

                        if (face.sqrMagnitude <= 1e-16f)
                            continue;

                        triangles.Add(tri);
                    }
                }
            }

            private int GetGeometricVertex(int sourceIndex)
            {
                int existing = sourceToGeometric[sourceIndex];

                if (existing >= 0)
                    return existing;

                Vector3 p = positions[sourceIndex];
                CellKey baseCell = CellKey.FromPosition(p, tolerance);

                int best = -1;
                float bestDistance = float.MaxValue;

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            CellKey key = new CellKey(
                                baseCell.x + dx,
                                baseCell.y + dy,
                                baseCell.z + dz
                            );

                            if (!cells.TryGetValue(
                                    key,
                                    out List<int> candidates))
                            {
                                continue;
                            }

                            for (int c = 0; c < candidates.Count; c++)
                            {
                                int candidate = candidates[c];

                                float distance =
                                    (vertices[candidate].position - p)
                                    .sqrMagnitude;

                                if (distance <= toleranceSq &&
                                    distance < bestDistance)
                                {
                                    best = candidate;
                                    bestDistance = distance;
                                }
                            }
                        }
                    }
                }

                if (best < 0)
                {
                    best = vertices.Count;

                    vertices.Add(new WorkingVertex
                    {
                        position = p,
                        valid = true,
                        version = 0
                    });

                    if (!cells.TryGetValue(
                            baseCell,
                            out List<int> cellList))
                    {
                        cellList = new List<int>();
                        cells.Add(baseCell, cellList);
                    }

                    cellList.Add(best);
                }

                sourceToGeometric[sourceIndex] = best;
                return best;
            }
        }

        // ------------------------------------------------------------
        // FEATURE EDGES
        // ------------------------------------------------------------

        private static Dictionary<EdgeKey, EdgeInfo> BuildInitialEdgeInfo(
            List<WorkingVertex> vertices,
            List<WorkingTriangle> triangles,
            bool hasUv0)
        {
            Dictionary<EdgeKey, EdgeInfo> edges =
                new Dictionary<EdgeKey, EdgeInfo>();

            for (int triangleIndex = 0;
                 triangleIndex < triangles.Count;
                 triangleIndex++)
            {
                WorkingTriangle tri = triangles[triangleIndex];

                AddEdgeInfo(
                    tri.a,
                    tri.b,
                    tri.uv0A,
                    tri.uv0B,
                    tri.submesh,
                    triangleIndex,
                    hasUv0,
                    edges
                );

                AddEdgeInfo(
                    tri.b,
                    tri.c,
                    tri.uv0B,
                    tri.uv0C,
                    tri.submesh,
                    triangleIndex,
                    hasUv0,
                    edges
                );

                AddEdgeInfo(
                    tri.c,
                    tri.a,
                    tri.uv0C,
                    tri.uv0A,
                    tri.submesh,
                    triangleIndex,
                    hasUv0,
                    edges
                );
            }

            return edges;
        }

        private static void AddEdgeInfo(
            int from,
            int to,
            Vector2 uvFrom,
            Vector2 uvTo,
            int submesh,
            int triangleIndex,
            bool hasUv0,
            Dictionary<EdgeKey, EdgeInfo> edges)
        {
            EdgeKey key = new EdgeKey(from, to);

            bool forward = key.a == from;
            Vector2 uvA = forward ? uvFrom : uvTo;
            Vector2 uvB = forward ? uvTo : uvFrom;

            if (!edges.TryGetValue(key, out EdgeInfo info))
            {
                info = new EdgeInfo
                {
                    useCount = 1,
                    firstTriangle = triangleIndex,
                    secondTriangle = -1,
                    firstSubmesh = submesh,
                    materialBoundary = false,
                    uvSeam = false,
                    firstUvA = uvA,
                    firstUvB = uvB
                };

                edges.Add(key, info);
                return;
            }

            info.useCount++;

            if (info.secondTriangle < 0)
                info.secondTriangle = triangleIndex;

            if (info.firstSubmesh != submesh)
                info.materialBoundary = true;

            if (hasUv0)
            {
                const float uvToleranceSq = 1e-8f;

                if ((info.firstUvA - uvA).sqrMagnitude > uvToleranceSq ||
                    (info.firstUvB - uvB).sqrMagnitude > uvToleranceSq)
                {
                    info.uvSeam = true;
                }
            }
        }

        // ------------------------------------------------------------
        // QUADRICS / CANDIDATES
        // ------------------------------------------------------------

        private static void AddTriangleQuadric(
            List<WorkingVertex> vertices,
            WorkingTriangle tri)
        {
            Vector3 a = vertices[tri.a].position;
            Vector3 b = vertices[tri.b].position;
            Vector3 c = vertices[tri.c].position;

            Vector3 n = Vector3.Cross(b - a, c - a);
            float magnitude = n.magnitude;

            if (magnitude <= 1e-10f)
                return;

            n /= magnitude;
            double d = -Vector3.Dot(n, a);

            Quadric q = Quadric.FromPlane(
                n.x,
                n.y,
                n.z,
                d
            );

            vertices[tri.a].quadric += q;
            vertices[tri.b].quadric += q;
            vertices[tri.c].quadric += q;
        }

        private static void PushCandidate(
            int a,
            int b,
            List<WorkingVertex> vertices,
            List<WorkingTriangle> triangles,
            MinHeap heap)
        {
            EdgeCandidate candidate;

            if (TryBuildCandidate(
                    a,
                    b,
                    vertices,
                    triangles,
                    out candidate))
            {
                heap.Push(candidate);
            }
        }

        private static bool TryBuildCandidate(
            int a,
            int b,
            List<WorkingVertex> vertices,
            List<WorkingTriangle> triangles,
            out EdgeCandidate candidate)
        {
            candidate = default(EdgeCandidate);

            if (a == b ||
                a < 0 || b < 0 ||
                a >= vertices.Count ||
                b >= vertices.Count)
            {
                return false;
            }

            WorkingVertex va = vertices[a];
            WorkingVertex vb = vertices[b];

            if (!va.valid || !vb.valid)
                return false;

            if (!AreAdjacent(a, b, vertices, triangles))
                return false;

            if (va.protectedNeighbors.Contains(b))
                return false;

            bool aFeature = va.protectedNeighbors.Count > 0;
            bool bFeature = vb.protectedNeighbors.Count > 0;

            // Do not merge two distinct feature vertices. An ordinary vertex
            // may still collapse into a protected feature vertex.
            if (aFeature && bFeature)
                return false;

            int keep;
            int remove;
            Vector3 position;

            Quadric combined = va.quadric + vb.quadric;

            if (aFeature)
            {
                keep = a;
                remove = b;
                position = va.position;
            }
            else if (bFeature)
            {
                keep = b;
                remove = a;
                position = vb.position;
            }
            else
            {
                keep = a;
                remove = b;

                if (!combined.TryOptimalPosition(out position))
                {
                    Vector3 midpoint =
                        (va.position + vb.position) * 0.5f;

                    double costA = combined.Evaluate(va.position);
                    double costB = combined.Evaluate(vb.position);
                    double costM = combined.Evaluate(midpoint);

                    position = va.position;
                    double best = costA;

                    if (costB < best)
                    {
                        best = costB;
                        position = vb.position;
                    }

                    if (costM < best)
                        position = midpoint;
                }
            }

            candidate = new EdgeCandidate
            {
                keep = keep,
                remove = remove,
                versionKeep = vertices[keep].version,
                versionRemove = vertices[remove].version,
                position = position,
                cost = combined.Evaluate(position)
            };

            return true;
        }

        private static bool IsCandidateCurrent(
            EdgeCandidate candidate,
            List<WorkingVertex> vertices)
        {
            if (candidate.keep < 0 ||
                candidate.remove < 0 ||
                candidate.keep >= vertices.Count ||
                candidate.remove >= vertices.Count)
            {
                return false;
            }

            WorkingVertex keep = vertices[candidate.keep];
            WorkingVertex remove = vertices[candidate.remove];

            return
                keep.valid &&
                remove.valid &&
                keep.version == candidate.versionKeep &&
                remove.version == candidate.versionRemove;
        }

        private static bool AreAdjacent(
            int a,
            int b,
            List<WorkingVertex> vertices,
            List<WorkingTriangle> triangles)
        {
            HashSet<int> triSet =
                vertices[a].triangles.Count <= vertices[b].triangles.Count
                    ? vertices[a].triangles
                    : vertices[b].triangles;

            foreach (int triIndex in triSet)
            {
                WorkingTriangle tri = triangles[triIndex];

                if (!tri.valid)
                    continue;

                if (tri.Contains(a) && tri.Contains(b))
                    return true;
            }

            return false;
        }

        private static bool CollapsePreservesFaces(
            EdgeCandidate candidate,
            List<WorkingVertex> vertices,
            List<WorkingTriangle> triangles,
            float minimumNormalDot)
        {
            HashSet<int> affected = new HashSet<int>();
            affected.UnionWith(vertices[candidate.keep].triangles);
            affected.UnionWith(vertices[candidate.remove].triangles);

            foreach (int triIndex in affected)
            {
                WorkingTriangle tri = triangles[triIndex];

                if (!tri.valid)
                    continue;

                int a = tri.a == candidate.remove
                    ? candidate.keep
                    : tri.a;

                int b = tri.b == candidate.remove
                    ? candidate.keep
                    : tri.b;

                int c = tri.c == candidate.remove
                    ? candidate.keep
                    : tri.c;

                if (a == b || b == c || a == c)
                    continue;

                Vector3 oldA = vertices[tri.a].position;
                Vector3 oldB = vertices[tri.b].position;
                Vector3 oldC = vertices[tri.c].position;

                Vector3 oldNormal =
                    Vector3.Cross(oldB - oldA, oldC - oldA);

                Vector3 newA = a == candidate.keep
                    ? candidate.position
                    : vertices[a].position;

                Vector3 newB = b == candidate.keep
                    ? candidate.position
                    : vertices[b].position;

                Vector3 newC = c == candidate.keep
                    ? candidate.position
                    : vertices[c].position;

                Vector3 newNormal =
                    Vector3.Cross(newB - newA, newC - newA);

                if (newNormal.sqrMagnitude <= 1e-16f)
                    continue;

                if (oldNormal.sqrMagnitude <= 1e-16f)
                    continue;

                float dot = Vector3.Dot(
                    oldNormal.normalized,
                    newNormal.normalized
                );

                if (dot < minimumNormalDot)
                    return false;
            }

            return true;
        }

        // ------------------------------------------------------------
        // COLLAPSE
        // ------------------------------------------------------------

        private static int CollapseEdge(
            EdgeCandidate candidate,
            List<WorkingVertex> vertices,
            List<WorkingTriangle> triangles,
            out HashSet<int> touchedVertices)
        {
            int keep = candidate.keep;
            int remove = candidate.remove;

            HashSet<int> affectedTriangles = new HashSet<int>();
            affectedTriangles.UnionWith(vertices[keep].triangles);
            affectedTriangles.UnionWith(vertices[remove].triangles);

            touchedVertices = new HashSet<int>();

            // Remove stale triangle-adjacency references before editing them.
            foreach (int triIndex in affectedTriangles)
            {
                WorkingTriangle tri = triangles[triIndex];

                if (!tri.valid)
                    continue;

                vertices[tri.a].triangles.Remove(triIndex);
                vertices[tri.b].triangles.Remove(triIndex);
                vertices[tri.c].triangles.Remove(triIndex);

                touchedVertices.Add(tri.a);
                touchedVertices.Add(tri.b);
                touchedVertices.Add(tri.c);
            }

            vertices[keep].position = candidate.position;
            vertices[keep].quadric += vertices[remove].quadric;

            // Preserve protected-feature topology when one endpoint disappears.
            List<int> removeProtected =
                vertices[remove].protectedNeighbors.ToList();

            for (int i = 0; i < removeProtected.Count; i++)
            {
                int neighbor = removeProtected[i];

                if (neighbor < 0 || neighbor >= vertices.Count)
                    continue;

                vertices[neighbor].protectedNeighbors.Remove(remove);

                if (neighbor == keep || !vertices[neighbor].valid)
                    continue;

                vertices[neighbor].protectedNeighbors.Add(keep);
                vertices[keep].protectedNeighbors.Add(neighbor);
            }

            vertices[keep].protectedNeighbors.Remove(remove);
            vertices[remove].protectedNeighbors.Clear();

            int removedTriangles = 0;

            foreach (int triIndex in affectedTriangles)
            {
                WorkingTriangle tri = triangles[triIndex];

                if (!tri.valid)
                    continue;

                if (tri.a == remove)
                    tri.a = keep;

                if (tri.b == remove)
                    tri.b = keep;

                if (tri.c == remove)
                    tri.c = keep;

                if (tri.a == tri.b ||
                    tri.b == tri.c ||
                    tri.a == tri.c)
                {
                    tri.valid = false;
                    removedTriangles++;
                    continue;
                }

                Vector3 face = Vector3.Cross(
                    vertices[tri.b].position - vertices[tri.a].position,
                    vertices[tri.c].position - vertices[tri.a].position
                );

                if (face.sqrMagnitude <= 1e-16f)
                {
                    tri.valid = false;
                    removedTriangles++;
                    continue;
                }

                vertices[tri.a].triangles.Add(triIndex);
                vertices[tri.b].triangles.Add(triIndex);
                vertices[tri.c].triangles.Add(triIndex);

                touchedVertices.Add(tri.a);
                touchedVertices.Add(tri.b);
                touchedVertices.Add(tri.c);
            }

            vertices[remove].triangles.Clear();
            vertices[remove].valid = false;

            touchedVertices.Add(keep);
            touchedVertices.Add(remove);

            foreach (int vertexId in touchedVertices)
            {
                if (vertexId >= 0 &&
                    vertexId < vertices.Count)
                {
                    vertices[vertexId].version++;
                }
            }

            return removedTriangles;
        }

        private static void CollectIncidentEdges(
            int vertexId,
            List<WorkingVertex> vertices,
            List<WorkingTriangle> triangles,
            HashSet<EdgeKey> edges)
        {
            foreach (int triIndex in vertices[vertexId].triangles)
            {
                WorkingTriangle tri = triangles[triIndex];

                if (!tri.valid)
                    continue;

                edges.Add(new EdgeKey(tri.a, tri.b));
                edges.Add(new EdgeKey(tri.b, tri.c));
                edges.Add(new EdgeKey(tri.c, tri.a));
            }
        }

        // ------------------------------------------------------------
        // OUTPUT / NORMAL RECONSTRUCTION
        // ------------------------------------------------------------

        private static Mesh BuildOutputMesh(
            string name,
            List<WorkingVertex> vertices,
            List<WorkingTriangle> triangles,
            int submeshCount,
            bool hasSourceNormals,
            bool hasUv0,
            bool hasUv2,
            bool hasTangents,
            bool hasColors,
            bool recalcNormals,
            bool recalcTangents,
            float smoothingAngleDegrees)
        {
            List<int> validTriangleIds = new List<int>();
            List<Vector3> faceAreaVectors = new List<Vector3>();
            List<Vector3> faceNormals = new List<Vector3>();

            // Remove exact duplicate triangles created by edge collapses.
            HashSet<FinalTriangleKey> unique =
                new HashSet<FinalTriangleKey>();

            for (int i = 0; i < triangles.Count; i++)
            {
                WorkingTriangle tri = triangles[i];

                if (!tri.valid)
                    continue;

                FinalTriangleKey key =
                    new FinalTriangleKey(
                        tri.a,
                        tri.b,
                        tri.c,
                        tri.submesh
                    );

                if (!unique.Add(key))
                    continue;

                Vector3 areaVector = Vector3.Cross(
                    vertices[tri.b].position - vertices[tri.a].position,
                    vertices[tri.c].position - vertices[tri.a].position
                );

                if (areaVector.sqrMagnitude <= 1e-16f)
                    continue;

                validTriangleIds.Add(i);
                faceAreaVectors.Add(areaVector);
                faceNormals.Add(areaVector.normalized);
            }

            // Map working-triangle index -> dense final triangle index.
            Dictionary<int, int> denseTriangleIndex =
                new Dictionary<int, int>();

            for (int i = 0; i < validTriangleIds.Count; i++)
                denseTriangleIndex[validTriangleIds[i]] = i;

            Dictionary<CornerGroupKey, int> smoothingGroups =
                BuildSmoothingGroups(
                    vertices,
                    triangles,
                    validTriangleIds,
                    denseTriangleIndex,
                    faceNormals,
                    smoothingAngleDegrees
                );

            Dictionary<int, Vector3> groupNormalSums =
                new Dictionary<int, Vector3>();

            for (int denseIndex = 0;
                 denseIndex < validTriangleIds.Count;
                 denseIndex++)
            {
                int triIndex = validTriangleIds[denseIndex];
                WorkingTriangle tri = triangles[triIndex];
                Vector3 area = faceAreaVectors[denseIndex];

                AccumulateGroupNormal(
                    smoothingGroups,
                    groupNormalSums,
                    triIndex,
                    tri.a,
                    area
                );

                AccumulateGroupNormal(
                    smoothingGroups,
                    groupNormalSums,
                    triIndex,
                    tri.b,
                    area
                );

                AccumulateGroupNormal(
                    smoothingGroups,
                    groupNormalSums,
                    triIndex,
                    tri.c,
                    area
                );
            }

            List<Vector3> outPositions = new List<Vector3>();
            List<Vector3> outNormals = new List<Vector3>();
            List<Vector2> outUv0 = new List<Vector2>();
            List<Vector2> outUv2 = new List<Vector2>();
            List<Vector4> outTangents = new List<Vector4>();
            List<Color> outColors = new List<Color>();

            List<int>[] outSubmeshes =
                new List<int>[submeshCount];

            for (int i = 0; i < submeshCount; i++)
                outSubmeshes[i] = new List<int>();

            Dictionary<RenderVertexKey, int> renderVertices =
                new Dictionary<RenderVertexKey, int>();

            for (int denseIndex = 0;
                 denseIndex < validTriangleIds.Count;
                 denseIndex++)
            {
                int triIndex = validTriangleIds[denseIndex];
                WorkingTriangle tri = triangles[triIndex];

                int ia = GetOrCreateRenderVertex(
                    triIndex,
                    0,
                    tri,
                    vertices,
                    smoothingGroups,
                    groupNormalSums,
                    renderVertices,
                    outPositions,
                    outNormals,
                    outUv0,
                    outUv2,
                    outTangents,
                    outColors,
                    hasSourceNormals,
                    hasUv0,
                    hasUv2,
                    hasTangents,
                    hasColors,
                    recalcNormals
                );

                int ib = GetOrCreateRenderVertex(
                    triIndex,
                    1,
                    tri,
                    vertices,
                    smoothingGroups,
                    groupNormalSums,
                    renderVertices,
                    outPositions,
                    outNormals,
                    outUv0,
                    outUv2,
                    outTangents,
                    outColors,
                    hasSourceNormals,
                    hasUv0,
                    hasUv2,
                    hasTangents,
                    hasColors,
                    recalcNormals
                );

                int ic = GetOrCreateRenderVertex(
                    triIndex,
                    2,
                    tri,
                    vertices,
                    smoothingGroups,
                    groupNormalSums,
                    renderVertices,
                    outPositions,
                    outNormals,
                    outUv0,
                    outUv2,
                    outTangents,
                    outColors,
                    hasSourceNormals,
                    hasUv0,
                    hasUv2,
                    hasTangents,
                    hasColors,
                    recalcNormals
                );

                outSubmeshes[tri.submesh].Add(ia);
                outSubmeshes[tri.submesh].Add(ib);
                outSubmeshes[tri.submesh].Add(ic);
            }

            Mesh output = new Mesh
            {
                name = name
            };

            if (outPositions.Count > 65535)
                output.indexFormat = IndexFormat.UInt32;

            output.SetVertices(outPositions);

            if (outNormals.Count == outPositions.Count)
                output.SetNormals(outNormals);

            if (hasUv0 && outUv0.Count == outPositions.Count)
                output.SetUVs(0, outUv0);

            if (hasUv2 && outUv2.Count == outPositions.Count)
                output.SetUVs(1, outUv2);

            if (hasColors && outColors.Count == outPositions.Count)
                output.SetColors(outColors);

            if (!recalcTangents &&
                hasTangents &&
                outTangents.Count == outPositions.Count)
            {
                output.SetTangents(outTangents);
            }

            output.subMeshCount = submeshCount;

            for (int submesh = 0;
                 submesh < submeshCount;
                 submesh++)
            {
                output.SetTriangles(
                    outSubmeshes[submesh],
                    submesh,
                    false
                );
            }

            if (recalcTangents && hasUv0 && outPositions.Count > 0)
                output.RecalculateTangents();

            output.RecalculateBounds();
            return output;
        }

        private static Dictionary<CornerGroupKey, int> BuildSmoothingGroups(
            List<WorkingVertex> vertices,
            List<WorkingTriangle> triangles,
            List<int> validTriangleIds,
            Dictionary<int, int> denseTriangleIndex,
            List<Vector3> faceNormals,
            float smoothingAngleDegrees)
        {
            Dictionary<int, List<int>> incident =
                new Dictionary<int, List<int>>();

            for (int i = 0; i < validTriangleIds.Count; i++)
            {
                int triIndex = validTriangleIds[i];
                WorkingTriangle tri = triangles[triIndex];

                AddIncident(incident, tri.a, triIndex);
                AddIncident(incident, tri.b, triIndex);
                AddIncident(incident, tri.c, triIndex);
            }

            float cosThreshold = Mathf.Cos(
                Mathf.Clamp(
                    smoothingAngleDegrees,
                    1f,
                    179f
                ) * Mathf.Deg2Rad
            );

            Dictionary<CornerGroupKey, int> result =
                new Dictionary<CornerGroupKey, int>();

            int nextGroup = 0;

            foreach (KeyValuePair<int, List<int>> pair in incident)
            {
                int vertexId = pair.Key;
                List<int> tris = pair.Value;
                LocalUnionFind union = new LocalUnionFind(tris.Count);

                for (int i = 0; i < tris.Count; i++)
                {
                    WorkingTriangle ti = triangles[tris[i]];
                    int denseI = denseTriangleIndex[tris[i]];

                    for (int j = i + 1; j < tris.Count; j++)
                    {
                        WorkingTriangle tj = triangles[tris[j]];

                        // Keep shading separated across material/submesh borders.
                        if (ti.submesh != tj.submesh)
                            continue;

                        int denseJ = denseTriangleIndex[tris[j]];

                        if (Vector3.Dot(
                                faceNormals[denseI],
                                faceNormals[denseJ]) >= cosThreshold)
                        {
                            union.Union(i, j);
                        }
                    }
                }

                Dictionary<int, int> rootToGroup =
                    new Dictionary<int, int>();

                for (int i = 0; i < tris.Count; i++)
                {
                    int root = union.Find(i);

                    if (!rootToGroup.TryGetValue(
                            root,
                            out int group))
                    {
                        group = nextGroup++;
                        rootToGroup.Add(root, group);
                    }

                    result[new CornerGroupKey(
                        tris[i],
                        vertexId
                    )] = group;
                }
            }

            return result;
        }

        private static void AddIncident(
            Dictionary<int, List<int>> incident,
            int vertex,
            int triangle)
        {
            if (!incident.TryGetValue(
                    vertex,
                    out List<int> list))
            {
                list = new List<int>();
                incident.Add(vertex, list);
            }

            list.Add(triangle);
        }

        private static void AccumulateGroupNormal(
            Dictionary<CornerGroupKey, int> smoothingGroups,
            Dictionary<int, Vector3> sums,
            int triangle,
            int vertex,
            Vector3 areaVector)
        {
            int group =
                smoothingGroups[new CornerGroupKey(
                    triangle,
                    vertex
                )];

            if (sums.TryGetValue(group, out Vector3 sum))
                sums[group] = sum + areaVector;
            else
                sums.Add(group, areaVector);
        }

        private static int GetOrCreateRenderVertex(
            int triangleIndex,
            int corner,
            WorkingTriangle tri,
            List<WorkingVertex> vertices,
            Dictionary<CornerGroupKey, int> smoothingGroups,
            Dictionary<int, Vector3> groupNormalSums,
            Dictionary<RenderVertexKey, int> renderVertices,
            List<Vector3> outPositions,
            List<Vector3> outNormals,
            List<Vector2> outUv0,
            List<Vector2> outUv2,
            List<Vector4> outTangents,
            List<Color> outColors,
            bool hasSourceNormals,
            bool hasUv0,
            bool hasUv2,
            bool hasTangents,
            bool hasColors,
            bool recalcNormals)
        {
            int vertexId = tri.GetVertex(corner);
            Vector2 uv0 = tri.GetUv0(corner);
            Vector2 uv2 = tri.GetUv2(corner);
            Color color = tri.GetColor(corner);
            Vector3 sourceNormal = tri.GetNormal(corner);
            Vector4 sourceTangent = tri.GetTangent(corner);

            int smoothingGroup =
                smoothingGroups[new CornerGroupKey(
                    triangleIndex,
                    vertexId
                )];

            RenderVertexKey key = new RenderVertexKey(
                vertexId,
                tri.submesh,
                smoothingGroup,
                hasUv0 ? uv0 : Vector2.zero,
                hasUv2 ? uv2 : Vector2.zero,
                hasColors ? color : Color.white,
                recalcNormals
                    ? Vector3.zero
                    : sourceNormal,
                (!recalcNormals && hasTangents)
                    ? sourceTangent
                    : Vector4.zero
            );

            if (renderVertices.TryGetValue(key, out int existing))
                return existing;

            int index = outPositions.Count;
            renderVertices.Add(key, index);

            outPositions.Add(vertices[vertexId].position);

            Vector3 normal;

            if (recalcNormals || !hasSourceNormals)
            {
                normal = groupNormalSums[smoothingGroup];

                if (normal.sqrMagnitude > 1e-12f)
                    normal.Normalize();
                else
                    normal = Vector3.up;
            }
            else
            {
                normal = sourceNormal.sqrMagnitude > 1e-12f
                    ? sourceNormal.normalized
                    : Vector3.up;
            }

            outNormals.Add(normal);

            if (hasUv0)
                outUv0.Add(uv0);

            if (hasUv2)
                outUv2.Add(uv2);

            if (hasTangents)
                outTangents.Add(sourceTangent);

            if (hasColors)
                outColors.Add(color);

            return index;
        }

        // ------------------------------------------------------------
        // HELPERS / DATA
        // ------------------------------------------------------------

        private static Vector3 GetTriangleNormal(
            List<WorkingVertex> vertices,
            WorkingTriangle tri)
        {
            Vector3 normal = Vector3.Cross(
                vertices[tri.b].position - vertices[tri.a].position,
                vertices[tri.c].position - vertices[tri.a].position
            );

            if (normal.sqrMagnitude > 1e-12f)
                normal.Normalize();

            return normal;
        }

        private sealed class WorkingVertex
        {
            public Vector3 position;
            public Quadric quadric;
            public bool valid;
            public int version;

            public readonly HashSet<int> triangles =
                new HashSet<int>();

            public readonly HashSet<int> protectedNeighbors =
                new HashSet<int>();
        }

        private sealed class WorkingTriangle
        {
            public int a;
            public int b;
            public int c;
            public int submesh;
            public bool valid;

            public Vector2 uv0A;
            public Vector2 uv0B;
            public Vector2 uv0C;

            public Vector2 uv2A;
            public Vector2 uv2B;
            public Vector2 uv2C;

            public Color colorA;
            public Color colorB;
            public Color colorC;

            public Vector3 normalA;
            public Vector3 normalB;
            public Vector3 normalC;

            public Vector4 tangentA;
            public Vector4 tangentB;
            public Vector4 tangentC;

            public bool Contains(int v)
            {
                return a == v || b == v || c == v;
            }

            public int GetVertex(int corner)
            {
                return corner == 0 ? a : (corner == 1 ? b : c);
            }

            public Vector2 GetUv0(int corner)
            {
                return corner == 0 ? uv0A : (corner == 1 ? uv0B : uv0C);
            }

            public Vector2 GetUv2(int corner)
            {
                return corner == 0 ? uv2A : (corner == 1 ? uv2B : uv2C);
            }

            public Color GetColor(int corner)
            {
                return corner == 0
                    ? colorA
                    : (corner == 1 ? colorB : colorC);
            }

            public Vector3 GetNormal(int corner)
            {
                return corner == 0
                    ? normalA
                    : (corner == 1 ? normalB : normalC);
            }

            public Vector4 GetTangent(int corner)
            {
                return corner == 0
                    ? tangentA
                    : (corner == 1 ? tangentB : tangentC);
            }
        }

        private sealed class EdgeInfo
        {
            public int useCount;
            public int firstTriangle;
            public int secondTriangle;
            public int firstSubmesh;
            public bool materialBoundary;
            public bool uvSeam;
            public Vector2 firstUvA;
            public Vector2 firstUvB;
        }

        private struct EdgeCandidate
        {
            public int keep;
            public int remove;
            public int versionKeep;
            public int versionRemove;
            public Vector3 position;
            public double cost;
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly int a;
            public readonly int b;

            public EdgeKey(int x, int y)
            {
                if (x <= y)
                {
                    a = x;
                    b = y;
                }
                else
                {
                    a = y;
                    b = x;
                }
            }

            public bool Equals(EdgeKey other)
            {
                return a == other.a && b == other.b;
            }

            public override bool Equals(object obj)
            {
                return obj is EdgeKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (a * 397) ^ b;
                }
            }
        }

        private struct CellKey : IEquatable<CellKey>
        {
            public readonly int x;
            public readonly int y;
            public readonly int z;

            public CellKey(int x, int y, int z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public static CellKey FromPosition(
                Vector3 position,
                float cellSize)
            {
                return new CellKey(
                    Mathf.FloorToInt(position.x / cellSize),
                    Mathf.FloorToInt(position.y / cellSize),
                    Mathf.FloorToInt(position.z / cellSize)
                );
            }

            public bool Equals(CellKey other)
            {
                return x == other.x &&
                       y == other.y &&
                       z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is CellKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + x;
                    hash = hash * 31 + y;
                    hash = hash * 31 + z;
                    return hash;
                }
            }
        }

        private struct InitialTriangleKey : IEquatable<InitialTriangleKey>
        {
            private readonly int a;
            private readonly int b;
            private readonly int c;
            private readonly int submesh;

            public InitialTriangleKey(int i0, int i1, int i2, int submesh)
            {
                Sort3(ref i0, ref i1, ref i2);
                a = i0;
                b = i1;
                c = i2;
                this.submesh = submesh;
            }

            public bool Equals(InitialTriangleKey other)
            {
                return a == other.a &&
                       b == other.b &&
                       c == other.c &&
                       submesh == other.submesh;
            }

            public override bool Equals(object obj)
            {
                return obj is InitialTriangleKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + a;
                    hash = hash * 31 + b;
                    hash = hash * 31 + c;
                    hash = hash * 31 + submesh;
                    return hash;
                }
            }
        }

        private struct FinalTriangleKey : IEquatable<FinalTriangleKey>
        {
            private readonly int a;
            private readonly int b;
            private readonly int c;
            private readonly int submesh;

            public FinalTriangleKey(int i0, int i1, int i2, int submesh)
            {
                Sort3(ref i0, ref i1, ref i2);
                a = i0;
                b = i1;
                c = i2;
                this.submesh = submesh;
            }

            public bool Equals(FinalTriangleKey other)
            {
                return a == other.a &&
                       b == other.b &&
                       c == other.c &&
                       submesh == other.submesh;
            }

            public override bool Equals(object obj)
            {
                return obj is FinalTriangleKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + a;
                    hash = hash * 31 + b;
                    hash = hash * 31 + c;
                    hash = hash * 31 + submesh;
                    return hash;
                }
            }
        }

        private struct CornerGroupKey : IEquatable<CornerGroupKey>
        {
            private readonly int triangle;
            private readonly int vertex;

            public CornerGroupKey(int triangle, int vertex)
            {
                this.triangle = triangle;
                this.vertex = vertex;
            }

            public bool Equals(CornerGroupKey other)
            {
                return triangle == other.triangle &&
                       vertex == other.vertex;
            }

            public override bool Equals(object obj)
            {
                return obj is CornerGroupKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (triangle * 397) ^ vertex;
                }
            }
        }

        private struct RenderVertexKey : IEquatable<RenderVertexKey>
        {
            private readonly int vertex;
            private readonly int submesh;
            private readonly int smoothingGroup;
            private readonly Vector2 uv0;
            private readonly Vector2 uv2;
            private readonly Color color;
            private readonly Vector3 sourceNormal;
            private readonly Vector4 sourceTangent;

            public RenderVertexKey(
                int vertex,
                int submesh,
                int smoothingGroup,
                Vector2 uv0,
                Vector2 uv2,
                Color color,
                Vector3 sourceNormal,
                Vector4 sourceTangent)
            {
                this.vertex = vertex;
                this.submesh = submesh;
                this.smoothingGroup = smoothingGroup;
                this.uv0 = uv0;
                this.uv2 = uv2;
                this.color = color;
                this.sourceNormal = sourceNormal;
                this.sourceTangent = sourceTangent;
            }

            public bool Equals(RenderVertexKey other)
            {
                return vertex == other.vertex &&
                       submesh == other.submesh &&
                       smoothingGroup == other.smoothingGroup &&
                       uv0.Equals(other.uv0) &&
                       uv2.Equals(other.uv2) &&
                       color.Equals(other.color) &&
                       sourceNormal.Equals(other.sourceNormal) &&
                       sourceTangent.Equals(other.sourceTangent);
            }

            public override bool Equals(object obj)
            {
                return obj is RenderVertexKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + vertex;
                    hash = hash * 31 + submesh;
                    hash = hash * 31 + smoothingGroup;
                    hash = hash * 31 + uv0.GetHashCode();
                    hash = hash * 31 + uv2.GetHashCode();
                    hash = hash * 31 + color.GetHashCode();
                    hash = hash * 31 + sourceNormal.GetHashCode();
                    hash = hash * 31 + sourceTangent.GetHashCode();
                    return hash;
                }
            }
        }

        private struct Quadric
        {
            private double q00;
            private double q01;
            private double q02;
            private double q03;
            private double q11;
            private double q12;
            private double q13;
            private double q22;
            private double q23;
            private double q33;

            public static Quadric FromPlane(
                double a,
                double b,
                double c,
                double d)
            {
                return new Quadric
                {
                    q00 = a * a,
                    q01 = a * b,
                    q02 = a * c,
                    q03 = a * d,
                    q11 = b * b,
                    q12 = b * c,
                    q13 = b * d,
                    q22 = c * c,
                    q23 = c * d,
                    q33 = d * d
                };
            }

            public static Quadric operator +(
                Quadric a,
                Quadric b)
            {
                return new Quadric
                {
                    q00 = a.q00 + b.q00,
                    q01 = a.q01 + b.q01,
                    q02 = a.q02 + b.q02,
                    q03 = a.q03 + b.q03,
                    q11 = a.q11 + b.q11,
                    q12 = a.q12 + b.q12,
                    q13 = a.q13 + b.q13,
                    q22 = a.q22 + b.q22,
                    q23 = a.q23 + b.q23,
                    q33 = a.q33 + b.q33
                };
            }

            public double Evaluate(Vector3 p)
            {
                double x = p.x;
                double y = p.y;
                double z = p.z;

                return
                    q00 * x * x +
                    2.0 * q01 * x * y +
                    2.0 * q02 * x * z +
                    2.0 * q03 * x +
                    q11 * y * y +
                    2.0 * q12 * y * z +
                    2.0 * q13 * y +
                    q22 * z * z +
                    2.0 * q23 * z +
                    q33;
            }

            public bool TryOptimalPosition(out Vector3 position)
            {
                double a00 = q00;
                double a01 = q01;
                double a02 = q02;
                double a10 = q01;
                double a11 = q11;
                double a12 = q12;
                double a20 = q02;
                double a21 = q12;
                double a22 = q22;

                double b0 = -q03;
                double b1 = -q13;
                double b2 = -q23;

                double det =
                    a00 * (a11 * a22 - a12 * a21) -
                    a01 * (a10 * a22 - a12 * a20) +
                    a02 * (a10 * a21 - a11 * a20);

                if (Math.Abs(det) < 1e-12)
                {
                    position = Vector3.zero;
                    return false;
                }

                double invDet = 1.0 / det;

                double i00 = (a11 * a22 - a12 * a21) * invDet;
                double i01 = (a02 * a21 - a01 * a22) * invDet;
                double i02 = (a01 * a12 - a02 * a11) * invDet;

                double i10 = (a12 * a20 - a10 * a22) * invDet;
                double i11 = (a00 * a22 - a02 * a20) * invDet;
                double i12 = (a02 * a10 - a00 * a12) * invDet;

                double i20 = (a10 * a21 - a11 * a20) * invDet;
                double i21 = (a01 * a20 - a00 * a21) * invDet;
                double i22 = (a00 * a11 - a01 * a10) * invDet;

                double x = i00 * b0 + i01 * b1 + i02 * b2;
                double y = i10 * b0 + i11 * b1 + i12 * b2;
                double z = i20 * b0 + i21 * b1 + i22 * b2;

                if (double.IsNaN(x) ||
                    double.IsNaN(y) ||
                    double.IsNaN(z) ||
                    double.IsInfinity(x) ||
                    double.IsInfinity(y) ||
                    double.IsInfinity(z))
                {
                    position = Vector3.zero;
                    return false;
                }

                position = new Vector3(
                    (float)x,
                    (float)y,
                    (float)z
                );

                return true;
            }
        }

        private sealed class MinHeap
        {
            private readonly List<EdgeCandidate> items =
                new List<EdgeCandidate>();

            public int Count => items.Count;

            public void Push(EdgeCandidate item)
            {
                items.Add(item);
                int index = items.Count - 1;

                while (index > 0)
                {
                    int parent = (index - 1) / 2;

                    if (items[parent].cost <= item.cost)
                        break;

                    items[index] = items[parent];
                    index = parent;
                }

                items[index] = item;
            }

            public EdgeCandidate Pop()
            {
                EdgeCandidate root = items[0];
                int lastIndex = items.Count - 1;
                EdgeCandidate last = items[lastIndex];
                items.RemoveAt(lastIndex);

                if (items.Count == 0)
                    return root;

                int index = 0;

                while (true)
                {
                    int left = index * 2 + 1;

                    if (left >= items.Count)
                        break;

                    int right = left + 1;
                    int child = left;

                    if (right < items.Count &&
                        items[right].cost < items[left].cost)
                    {
                        child = right;
                    }

                    if (items[child].cost >= last.cost)
                        break;

                    items[index] = items[child];
                    index = child;
                }

                items[index] = last;
                return root;
            }
        }

        private sealed class LocalUnionFind
        {
            private readonly int[] parent;
            private readonly byte[] rank;

            public LocalUnionFind(int count)
            {
                parent = new int[count];
                rank = new byte[count];

                for (int i = 0; i < count; i++)
                    parent[i] = i;
            }

            public int Find(int x)
            {
                if (parent[x] != x)
                    parent[x] = Find(parent[x]);

                return parent[x];
            }

            public void Union(int a, int b)
            {
                int ra = Find(a);
                int rb = Find(b);

                if (ra == rb)
                    return;

                if (rank[ra] < rank[rb])
                    parent[ra] = rb;
                else if (rank[ra] > rank[rb])
                    parent[rb] = ra;
                else
                {
                    parent[rb] = ra;
                    rank[ra]++;
                }
            }
        }

        private static void Sort3(ref int a, ref int b, ref int c)
        {
            if (a > b)
            {
                int tmp = a;
                a = b;
                b = tmp;
            }

            if (b > c)
            {
                int tmp = b;
                b = c;
                c = tmp;
            }

            if (a > b)
            {
                int tmp = a;
                a = b;
                b = tmp;
            }
        }
    }


    // ============================================================
    // VERTEX-CLUSTERING SIMPLIFIER
    // ============================================================

    /// <summary>
    /// Lightweight editor-oriented simplifier.
    ///
    /// It spatially clusters vertices into a regular 3D grid, averages the
    /// surviving vertex attributes, remaps each submesh, and removes collapsed
    /// / duplicate triangles.
    ///
    /// This is not a production QEM decimator. It is intentionally dependency-free
    /// and appropriate for distant, static site context where aggressive reduction
    /// is more important than preserving every local contour.
    /// </summary>
    private static class VertexClusterSimplifier
    {
        public static Mesh Simplify(
            Mesh source,
            float targetTriangleRatio,
            bool recalcNormals,
            bool preserveColors,
            bool preserveUv2 = true,
            bool recalcTangents = false,
            bool protectSurfaceNormals = true,
            float normalMergeAngleDegrees = 45f)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (!source.isReadable)
            {
                throw new InvalidOperationException(
                    $"Mesh '{source.name}' is not readable."
                );
            }

            targetTriangleRatio =
                Mathf.Clamp(targetTriangleRatio, 0.01f, 1f);

            long originalTrianglesLong =
                GetTriangleCount(source);

            if (originalTrianglesLong <= 0)
                return UnityEngine.Object.Instantiate(source);

            int originalTriangles =
                originalTrianglesLong > int.MaxValue
                    ? int.MaxValue
                    : (int)originalTrianglesLong;

            int targetTriangles = Mathf.Max(
                4,
                Mathf.RoundToInt(
                    originalTriangles * targetTriangleRatio
                )
            );

            if (targetTriangles >= originalTriangles)
                return UnityEngine.Object.Instantiate(source);

            SourceMeshData sourceData =
                SourceMeshData.FromMesh(
                    source,
                    preserveColors,
                    preserveUv2
                );

            // Resolution increases => less clustering => generally more triangles.
            // 2..512 gives a useful range for the sort of IFC context geometry
            // this tool is aimed at.
            int low = 2;
            int high = 512;

            ClusterResult best = null;
            long bestDifference = long.MaxValue;

            while (low <= high)
            {
                int resolution = (low + high) / 2;

                ClusterResult result =
                    BuildClusteredMesh(
                        sourceData,
                        source.bounds,
                        resolution,
                        protectSurfaceNormals,
                        normalMergeAngleDegrees
                    );

                int triangles = result.TotalTriangles;

                // Avoid choosing a fully collapsed result unless unavoidable.
                if (triangles > 0)
                {
                    long difference =
                        Math.Abs((long)triangles - targetTriangles);

                    if (difference < bestDifference)
                    {
                        best = result;
                        bestDifference = difference;
                    }
                }

                if (triangles > targetTriangles)
                {
                    // Too detailed -> cluster more aggressively.
                    high = resolution - 1;
                }
                else
                {
                    // Too simple -> permit more spatial cells.
                    low = resolution + 1;
                }
            }

            if (best == null || best.TotalTriangles <= 0)
            {
                // Conservative fallback.
                best = BuildClusteredMesh(
                    sourceData,
                    source.bounds,
                    4,
                    protectSurfaceNormals,
                    normalMergeAngleDegrees
                );
            }

            Mesh output =
                best.ToMesh(
                    source.name + "_Simplified",
                    recalcNormals,
                    recalcTangents
                );

            return output;
        }

        public static Mesh SimplifyAtGridResolution(
            Mesh source,
            int gridResolution,
            bool recalcNormals,
            bool preserveColors,
            bool preserveUv2 = true,
            bool recalcTangents = false,
            bool protectSurfaceNormals = true,
            float normalMergeAngleDegrees = 45f)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (!source.isReadable)
            {
                throw new InvalidOperationException(
                    $"Mesh '{source.name}' is not readable."
                );
            }

            gridResolution = Mathf.Clamp(gridResolution, 2, 512);

            SourceMeshData sourceData =
                SourceMeshData.FromMesh(
                    source,
                    preserveColors,
                    preserveUv2
                );

            ClusterResult result =
                BuildClusteredMesh(
                    sourceData,
                    source.bounds,
                    gridResolution,
                    protectSurfaceNormals,
                    normalMergeAngleDegrees
                );

            if (result == null || result.TotalTriangles <= 0)
            {
                Mesh fallback =
                    UnityEngine.Object.Instantiate(source);

                fallback.name =
                    source.name + "_ClusterFallback";

                return fallback;
            }

            return result.ToMesh(
                source.name + "_Clustered",
                recalcNormals,
                recalcTangents
            );
        }

        private static ClusterResult BuildClusteredMesh(
            SourceMeshData source,
            Bounds bounds,
            int resolution,
            bool protectSurfaceNormals,
            float normalMergeAngleDegrees)
        {
            // One spatial cell may now contain several clusters when their
            // source normals represent different surfaces/hard edges.
            Dictionary<GridKey, List<int>> clusterLookup = new();
            List<ClusterAccumulator> clusters = new();

            float normalDotThreshold =
                Mathf.Cos(
                    Mathf.Clamp(normalMergeAngleDegrees, 5f, 179f) *
                    Mathf.Deg2Rad
                );

            int vertexCount = source.positions.Length;
            int[] oldToNew = new int[vertexCount];

            Vector3 min = bounds.min;
            Vector3 size = bounds.size;

            float invX =
                Mathf.Abs(size.x) > 1e-8f
                    ? 1f / size.x
                    : 0f;

            float invY =
                Mathf.Abs(size.y) > 1e-8f
                    ? 1f / size.y
                    : 0f;

            float invZ =
                Mathf.Abs(size.z) > 1e-8f
                    ? 1f / size.z
                    : 0f;

            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 p = source.positions[i];

                float nx =
                    invX > 0f
                        ? Mathf.Clamp01((p.x - min.x) * invX)
                        : 0f;

                float ny =
                    invY > 0f
                        ? Mathf.Clamp01((p.y - min.y) * invY)
                        : 0f;

                float nz =
                    invZ > 0f
                        ? Mathf.Clamp01((p.z - min.z) * invZ)
                        : 0f;

                GridKey key = new GridKey(
                    Mathf.Min(
                        resolution - 1,
                        Mathf.FloorToInt(nx * resolution)
                    ),
                    Mathf.Min(
                        resolution - 1,
                        Mathf.FloorToInt(ny * resolution)
                    ),
                    Mathf.Min(
                        resolution - 1,
                        Mathf.FloorToInt(nz * resolution)
                    )
                );

                if (!clusterLookup.TryGetValue(
                        key,
                        out List<int> cellClusters))
                {
                    cellClusters = new List<int>(2);
                    clusterLookup.Add(key, cellClusters);
                }

                int clusterIndex = -1;

                // Legacy behavior: one cluster per spatial cell.
                if (!protectSurfaceNormals || !source.hasNormals)
                {
                    if (cellClusters.Count > 0)
                        clusterIndex = cellClusters[0];
                }
                else
                {
                    Vector3 sourceNormal = source.normals[i];

                    if (sourceNormal.sqrMagnitude > 1e-12f)
                        sourceNormal.Normalize();

                    // Reuse a spatial cluster only if its average source
                    // normal is compatible with this vertex. This prevents
                    // opposite sides of thin geometry and hard edges from
                    // being welded together merely because they are nearby.
                    for (int c = 0; c < cellClusters.Count; c++)
                    {
                        int candidateIndex = cellClusters[c];
                        Vector3 averageNormal =
                            clusters[candidateIndex].normalSum;

                        bool sourceNormalInvalid =
                            sourceNormal.sqrMagnitude <= 1e-12f;

                        bool candidateNormalInvalid =
                            averageNormal.sqrMagnitude <= 1e-12f;

                        if (sourceNormalInvalid ||
                            candidateNormalInvalid)
                        {
                            clusterIndex = candidateIndex;
                            break;
                        }

                        averageNormal.Normalize();

                        if (Vector3.Dot(
                                averageNormal,
                                sourceNormal) >= normalDotThreshold)
                        {
                            clusterIndex = candidateIndex;
                            break;
                        }
                    }
                }

                if (clusterIndex < 0)
                {
                    clusterIndex = clusters.Count;

                    clusters.Add(new ClusterAccumulator(
                        source.hasNormals,
                        source.hasUv0,
                        source.hasUv2,
                        source.hasTangents,
                        source.hasColors
                    ));

                    cellClusters.Add(clusterIndex);
                }

                ClusterAccumulator accumulator =
                    clusters[clusterIndex];

                accumulator.Add(
                    source,
                    i
                );

                clusters[clusterIndex] = accumulator;
                oldToNew[i] = clusterIndex;
            }

            ClusterResult result =
                new ClusterResult(
                    clusters.Count,
                    source.submeshTriangles.Length,
                    source.hasNormals,
                    source.hasUv0,
                    source.hasUv2,
                    source.hasTangents,
                    source.hasColors
                );

            for (int i = 0; i < clusters.Count; i++)
            {
                ClusterAccumulator c = clusters[i];

                result.positions[i] =
                    c.positionSum / c.count;

                if (source.hasNormals)
                {
                    Vector3 n =
                        c.normalSum.sqrMagnitude > 1e-12f
                            ? c.normalSum.normalized
                            : Vector3.up;

                    result.normals[i] = n;
                }

                if (source.hasUv0)
                    result.uv0[i] = c.uvSum / c.count;

                if (source.hasUv2)
                    result.uv2[i] = c.uv2Sum / c.count;

                if (source.hasTangents)
                {
                    Vector4 t = c.tangentSum / c.count;

                    Vector3 xyz =
                        new Vector3(t.x, t.y, t.z);

                    if (xyz.sqrMagnitude > 1e-12f)
                        xyz.Normalize();

                    result.tangents[i] =
                        new Vector4(
                            xyz.x,
                            xyz.y,
                            xyz.z,
                            t.w >= 0f ? 1f : -1f
                        );
                }

                if (source.hasColors)
                    result.colors[i] = c.colorSum / c.count;
            }

            for (int submesh = 0;
                 submesh < source.submeshTriangles.Length;
                 submesh++)
            {
                int[] sourceTriangles =
                    source.submeshTriangles[submesh];

                List<int> outputTriangles =
                    new List<int>(
                        sourceTriangles.Length
                    );

                HashSet<TriangleKey> uniqueTriangles =
                    new HashSet<TriangleKey>();

                float maxExtent = Mathf.Max(
                    bounds.size.x,
                    Mathf.Max(bounds.size.y, bounds.size.z)
                );

                // Cross-product magnitude is twice triangle area. Scale the
                // rejection threshold with the object's size so that tiny
                // collapsed triangles are removed consistently.
                float minimumDoubleArea =
                    Mathf.Max(
                        1e-10f,
                        maxExtent * maxExtent * 1e-8f
                    );

                for (int t = 0;
                     t + 2 < sourceTriangles.Length;
                     t += 3)
                {
                    int sourceA = sourceTriangles[t + 0];
                    int sourceB = sourceTriangles[t + 1];
                    int sourceC = sourceTriangles[t + 2];

                    int a = oldToNew[sourceA];
                    int b = oldToNew[sourceB];
                    int c = oldToNew[sourceC];

                    if (a == b || b == c || a == c)
                        continue;

                    Vector3 newFaceNormal = Vector3.Cross(
                        result.positions[b] - result.positions[a],
                        result.positions[c] - result.positions[a]
                    );

                    if (newFaceNormal.magnitude <= minimumDoubleArea)
                        continue;

                    Vector3 sourceFaceNormal = Vector3.Cross(
                        source.positions[sourceB] -
                        source.positions[sourceA],
                        source.positions[sourceC] -
                        source.positions[sourceA]
                    );

                    // Spatial clustering can move vertices far enough for a
                    // triangle to invert. RecalculateNormals() does NOT fix
                    // winding; it simply follows it. Preserve the source face
                    // orientation explicitly before rebuilding normals.
                    if (sourceFaceNormal.sqrMagnitude > 1e-16f &&
                        Vector3.Dot(
                            newFaceNormal,
                            sourceFaceNormal) < 0f)
                    {
                        int tmp = b;
                        b = c;
                        c = tmp;
                    }

                    TriangleKey key =
                        new TriangleKey(a, b, c);

                    if (!uniqueTriangles.Add(key))
                        continue;

                    outputTriangles.Add(a);
                    outputTriangles.Add(b);
                    outputTriangles.Add(c);
                }

                result.submeshTriangles[submesh] =
                    outputTriangles.ToArray();

                result.TotalTriangles +=
                    outputTriangles.Count / 3;
            }

            return result;
        }

        private struct SourceMeshData
        {
            public Vector3[] positions;
            public Vector3[] normals;
            public Vector2[] uv0;
            public Vector2[] uv2;
            public Vector4[] tangents;
            public Color[] colors;
            public int[][] submeshTriangles;

            public bool hasNormals;
            public bool hasUv0;
            public bool hasUv2;
            public bool hasTangents;
            public bool hasColors;

            public static SourceMeshData FromMesh(
                Mesh mesh,
                bool preserveColors,
                bool preserveUv2)
            {
                SourceMeshData data =
                    new SourceMeshData();

                data.positions = mesh.vertices;

                Vector3[] meshNormals = mesh.normals;
                Vector2[] meshUv = mesh.uv;
                Vector2[] meshUv2 = mesh.uv2;
                Vector4[] meshTangents = mesh.tangents;
                Color[] meshColors = mesh.colors;

                data.hasNormals =
                    meshNormals != null &&
                    meshNormals.Length == mesh.vertexCount;

                data.hasUv0 =
                    meshUv != null &&
                    meshUv.Length == mesh.vertexCount;

                data.hasUv2 =
                    preserveUv2 &&
                    meshUv2 != null &&
                    meshUv2.Length == mesh.vertexCount;

                data.hasTangents =
                    meshTangents != null &&
                    meshTangents.Length == mesh.vertexCount;

                data.hasColors =
                    preserveColors &&
                    meshColors != null &&
                    meshColors.Length == mesh.vertexCount;

                data.normals =
                    data.hasNormals
                        ? meshNormals
                        : Array.Empty<Vector3>();

                data.uv0 =
                    data.hasUv0
                        ? meshUv
                        : Array.Empty<Vector2>();

                data.uv2 =
                    data.hasUv2
                        ? meshUv2
                        : Array.Empty<Vector2>();

                data.tangents =
                    data.hasTangents
                        ? meshTangents
                        : Array.Empty<Vector4>();

                data.colors =
                    data.hasColors
                        ? meshColors
                        : Array.Empty<Color>();

                data.submeshTriangles =
                    new int[mesh.subMeshCount][];

                for (int i = 0;
                     i < mesh.subMeshCount;
                     i++)
                {
                    if (mesh.GetTopology(i) !=
                        MeshTopology.Triangles)
                    {
                        data.submeshTriangles[i] =
                            Array.Empty<int>();

                        continue;
                    }

                    data.submeshTriangles[i] =
                        mesh.GetTriangles(i);
                }

                return data;
            }
        }

        private struct ClusterAccumulator
        {
            public int count;

            public Vector3 positionSum;
            public Vector3 normalSum;
            public Vector2 uvSum;
            public Vector2 uv2Sum;
            public Vector4 tangentSum;
            public Color colorSum;

            private readonly bool hasNormals;
            private readonly bool hasUv0;
            private readonly bool hasUv2;
            private readonly bool hasTangents;
            private readonly bool hasColors;

            public ClusterAccumulator(
                bool hasNormals,
                bool hasUv0,
                bool hasUv2,
                bool hasTangents,
                bool hasColors)
            {
                count = 0;

                positionSum = Vector3.zero;
                normalSum = Vector3.zero;
                uvSum = Vector2.zero;
                uv2Sum = Vector2.zero;
                tangentSum = Vector4.zero;
                colorSum = Color.clear;

                this.hasNormals = hasNormals;
                this.hasUv0 = hasUv0;
                this.hasUv2 = hasUv2;
                this.hasTangents = hasTangents;
                this.hasColors = hasColors;
            }

            public void Add(
                SourceMeshData source,
                int index)
            {
                count++;
                positionSum += source.positions[index];

                if (hasNormals)
                    normalSum += source.normals[index];

                if (hasUv0)
                    uvSum += source.uv0[index];

                if (hasUv2)
                    uv2Sum += source.uv2[index];

                if (hasTangents)
                    tangentSum += source.tangents[index];

                if (hasColors)
                    colorSum += source.colors[index];
            }
        }

        private class ClusterResult
        {
            public readonly Vector3[] positions;
            public readonly Vector3[] normals;
            public readonly Vector2[] uv0;
            public readonly Vector2[] uv2;
            public readonly Vector4[] tangents;
            public readonly Color[] colors;
            public readonly int[][] submeshTriangles;

            private readonly bool hasNormals;
            private readonly bool hasUv0;
            private readonly bool hasUv2;
            private readonly bool hasTangents;
            private readonly bool hasColors;

            public int TotalTriangles;

            public ClusterResult(
                int vertexCount,
                int submeshCount,
                bool hasNormals,
                bool hasUv0,
                bool hasUv2,
                bool hasTangents,
                bool hasColors)
            {
                positions = new Vector3[vertexCount];

                normals =
                    hasNormals
                        ? new Vector3[vertexCount]
                        : Array.Empty<Vector3>();

                uv0 =
                    hasUv0
                        ? new Vector2[vertexCount]
                        : Array.Empty<Vector2>();

                uv2 =
                    hasUv2
                        ? new Vector2[vertexCount]
                        : Array.Empty<Vector2>();

                tangents =
                    hasTangents
                        ? new Vector4[vertexCount]
                        : Array.Empty<Vector4>();

                colors =
                    hasColors
                        ? new Color[vertexCount]
                        : Array.Empty<Color>();

                submeshTriangles =
                    new int[submeshCount][];

                this.hasNormals = hasNormals;
                this.hasUv0 = hasUv0;
                this.hasUv2 = hasUv2;
                this.hasTangents = hasTangents;
                this.hasColors = hasColors;
            }

            public Mesh ToMesh(
                string name,
                bool recalcNormals,
                bool recalcTangents)
            {
                Mesh mesh = new Mesh
                {
                    name = name
                };

                if (positions.Length > 65535)
                    mesh.indexFormat = IndexFormat.UInt32;

                mesh.vertices = positions;

                if (hasUv0)
                    mesh.uv = uv0;

                if (hasUv2)
                    mesh.uv2 = uv2;

                if (hasColors)
                    mesh.colors = colors;

                if (hasTangents)
                    mesh.tangents = tangents;

                mesh.subMeshCount =
                    submeshTriangles.Length;

                for (int i = 0;
                     i < submeshTriangles.Length;
                     i++)
                {
                    mesh.SetTriangles(
                        submeshTriangles[i] ??
                        Array.Empty<int>(),
                        i,
                        false
                    );
                }

                if (recalcNormals || !hasNormals)
                    mesh.RecalculateNormals();
                else
                    mesh.normals = normals;

                if (recalcTangents && hasUv0)
                    mesh.RecalculateTangents();
                else if (hasTangents)
                    mesh.tangents = tangents;

                mesh.RecalculateBounds();

                return mesh;
            }
        }

        private readonly struct GridKey :
            IEquatable<GridKey>
        {
            private readonly int x;
            private readonly int y;
            private readonly int z;

            public GridKey(int x, int y, int z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public bool Equals(GridKey other)
            {
                return x == other.x &&
                       y == other.y &&
                       z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is GridKey other &&
                       Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + x;
                    hash = hash * 31 + y;
                    hash = hash * 31 + z;
                    return hash;
                }
            }
        }

        /// <summary>
        /// Winding-independent triangle identity used only to remove exact
        /// duplicates after clustering. We still output the first triangle's
        /// original winding.
        /// </summary>
        private readonly struct TriangleKey :
            IEquatable<TriangleKey>
        {
            private readonly int a;
            private readonly int b;
            private readonly int c;

            public TriangleKey(int i0, int i1, int i2)
            {
                if (i0 > i1)
                    Swap(ref i0, ref i1);

                if (i1 > i2)
                    Swap(ref i1, ref i2);

                if (i0 > i1)
                    Swap(ref i0, ref i1);

                a = i0;
                b = i1;
                c = i2;
            }

            private static void Swap(ref int x, ref int y)
            {
                int tmp = x;
                x = y;
                y = tmp;
            }

            public bool Equals(TriangleKey other)
            {
                return a == other.a &&
                       b == other.b &&
                       c == other.c;
            }

            public override bool Equals(object obj)
            {
                return obj is TriangleKey other &&
                       Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + a;
                    hash = hash * 31 + b;
                    hash = hash * 31 + c;
                    return hash;
                }
            }
        }
    }
}
