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
/// Connectivity-first hierarchy organizer for large IFC/BIM site-context scenes.
///
/// Stage 1 — Analyze
///   1. Collect renderable descendants below a selected root.
///   2. Build a physical-connectivity graph independently of semantic category.
///   3. Extract connected components ("structures").
///   4. Group objects semantically inside each structure.
///   5. Optionally split large semantic groups into spatial sections.
///   6. Keep true singletons and connectivity-excluded objects in separate plans.
///
/// Stage 2 — Apply
///   Duplicate the selected analysis root, organize the COPIED renderable
///   objects under a new connectivity hierarchy, then disable the original root.
///
///   <original root>                 (disabled, untouched)
///   <duplicated organized root>    (enabled)
///     ├── Connected_Clusters
///     │   ├── Cluster_000
///     │   │   ├── Category_...
///     │   │   │   ├── Section_000   (optional)
///     │   │   │   └── Section_001
///     │   │   └── Category_...
///     │   └── Cluster_001
///     ├── Singletons
///     │   └── Category_...
///     └── Excluded_From_Connectivity
///         └── Category_...
///
/// Raw physical connected components are preserved during analysis, then
/// spatially consolidated into at most nine top-level clusters so the resulting
/// hierarchy remains semantically manageable. Each cluster receives an editable
/// heuristic name based on its dominant contents and position in the site.
///
/// Any generated group can later be selected as a new analysis root, allowing
/// iterative refinement with different parameters.
///
/// Suggested location:
///     Assets/Editor/SiteContextHierarchyOrganizer.cs
/// </summary>
public class SiteContextHierarchyOrganizer : EditorWindow
{
    private const string ToolVersion = "4.0.0";
    private const string AuditBaseFolder =
        "Assets/Generated/HierarchyClustering/Logs";

    // ============================================================
    // ROOT / OBJECT FILTERS
    // ============================================================

    [SerializeField] private GameObject analysisRoot;
    [SerializeField] private bool searchInactiveObjects = true;
    [SerializeField] private bool onlyActiveInHierarchy = true;
    [SerializeField] private bool onlyEnabledRenderers = true;

    // ============================================================
    // CONNECTIVITY GRAPH SETTINGS
    // ============================================================

    [SerializeField, Min(0f)]
    private float contactTolerance = 0.15f;

    [SerializeField]
    private bool enableNearConnections = true;

    [SerializeField, Min(0f)]
    private float nearConnectionDistance = 0.75f;

    [SerializeField]
    private NearConnectionPolicy nearConnectionPolicy =
        NearConnectionPolicy.SemanticOrAxisOverlap;

    [SerializeField, Range(1, 3)]
    private int nearMinimumOverlapAxes = 2;

    [SerializeField]
    private bool excludeLargeObjectsFromConnectivity = true;

    [SerializeField, Range(0.05f, 1f)]
    private float maxConnectivityObjectExtentRatio = 0.45f;

    [SerializeField]
    private string connectivityIgnoreNameContains =
        "terrain;ifcsite;ground";

    // The raw graph may legitimately produce dozens of connected components.
    // For hierarchy semantics, consolidate those physical components into a
    // deliberately small number of spatially coherent top-level clusters.
    [SerializeField]
    private bool limitTopLevelClusters = true;

    [SerializeField, Range(2, 9)]
    private int maxTopLevelClusters = 8;

    // ============================================================
    // SEMANTIC SETTINGS
    // ============================================================

    [SerializeField]
    private SemanticGroupingMode semanticMode =
        SemanticGroupingMode.NameTokens;

    [SerializeField, Range(1, 10)]
    private int semanticTokenCount = 3;

    [SerializeField]
    private bool includeMeshNameInSemanticKey = false;

    [SerializeField]
    private bool stripLikelyIds = true;

    [SerializeField]
    private bool stripPureNumericTokens = true;

    [SerializeField]
    private bool caseInsensitiveSemantics = true;

    [SerializeField]
    private string semanticRegex =
        @"^(?<group>[^-_]+[-_][^-_]+[-_][^-_]+)";

    // ============================================================
    // OPTIONAL LEVEL-3 SPATIAL SECTIONS
    // ============================================================

    [SerializeField]
    private bool buildSpatialSections = false;

    [SerializeField, Min(2)]
    private int maxObjectsPerSection = 100;

    [SerializeField, Min(0.1f)]
    private float maxSectionExtent = 30f;

    // ============================================================
    // APPLY SETTINGS
    // ============================================================

    [SerializeField]
    private string generatedRootPrefix =
        "__CONNECTED_CLUSTERED__";

    [SerializeField]
    private bool writeJsonAudit = true;

    [SerializeField]
    private bool pruneEmptyTransformOnlyContainers = true;

    // Cluster naming is deliberately heuristic and editable after analysis.
    [SerializeField]
    private bool addSpatialSuffixToClusterNames = true;

    [SerializeField, Range(0.05f, 0.45f)]
    private float spatialNameCenterBand = 0.18f;

    [SerializeField]
    private bool positiveXMeansEast = true;

    [SerializeField]
    private bool positiveZMeansNorth = true;

    // ============================================================
    // UI SETTINGS / STATE
    // ============================================================

    [SerializeField, Range(10, 500)]
    private int maxStructuresShown = 100;

    [SerializeField, Range(5, 100)]
    private int maxCategoriesShownPerStructure = 30;

    private Vector2 mainScroll;
    private bool showAnalysisSummary = true;
    private bool showConnectedStructures = true;
    private bool showSingletons = true;
    private bool showExcluded = false;
    private bool showApply = true;
    private bool showSemanticAdvanced = false;
    private bool showConnectivityAdvanced = true;

    private string statusMessage;

    // ============================================================
    // ANALYSIS RESULTS
    // ============================================================

    private readonly List<ObjectInfo> allObjects =
        new List<ObjectInfo>();

    private readonly List<StructurePlan> structures =
        new List<StructurePlan>();

    private readonly List<SemanticGroupPlan> singletonGroups =
        new List<SemanticGroupPlan>();

    private readonly List<SemanticGroupPlan> excludedGroups =
        new List<SemanticGroupPlan>();

    private int analyzedTriangles;
    private int participantCount;
    private int rawConnectedComponentCount;
    private int connectedObjectCount;
    private int singletonObjectCount;
    private int excludedObjectCount;
    private int semanticGroupsInsideStructures;
    private DateTime analysisTimestamp;

    private Bounds rootRenderableBounds;
    private bool hasRootRenderableBounds;


    // ============================================================
    // ENUMS
    // ============================================================

    private enum NearConnectionPolicy
    {
        AnyWithinDistance,
        SameSemanticFamily,
        AxisOverlap,
        SemanticOrAxisOverlap
    }

    private enum SemanticGroupingMode
    {
        None,
        NameTokens,
        RegexCapture,
        FullNormalizedName
    }

    // ============================================================
    // WINDOW
    // ============================================================

    [MenuItem("Tools/Optimization/Site Context Hierarchy Organizer")]
    public static void ShowWindow()
    {
        SiteContextHierarchyOrganizer window =
            GetWindow<SiteContextHierarchyOrganizer>(
                "Hierarchy Organizer"
            );

        window.minSize = new Vector2(800f, 650f);
    }

    private void OnGUI()
    {
        mainScroll =
            EditorGUILayout.BeginScrollView(mainScroll);

        DrawHeader();
        DrawAnalysisSettings();

        if (allObjects.Count > 0)
        {
            EditorGUILayout.Space(14);
            DrawSummary();

            EditorGUILayout.Space(14);
            DrawConnectivityPlan();

            EditorGUILayout.Space(18);
            DrawApplySection();
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField(
            "Site Context Hierarchy Organizer — Connectivity First",
            EditorStyles.boldLabel
        );

        EditorGUILayout.HelpBox(
            "The primary hierarchy is physical connectivity, not object name. " +
            "Fence posts, panels, barbed wire and kerbs can therefore belong to the same Structure even when their semantic categories differ. " +
            "Semantic groups are created inside each connected Structure, with optional spatial Sections below them.",
            MessageType.Info
        );
    }

    // ============================================================
    // STAGE 1 UI
    // ============================================================

    private void DrawAnalysisSettings()
    {
        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField(
            "Stage 1 — Connectivity Analysis",
            EditorStyles.boldLabel
        );

        EditorGUILayout.BeginHorizontal();

        GameObject newRoot =
            (GameObject)EditorGUILayout.ObjectField(
                "Analysis Root",
                analysisRoot,
                typeof(GameObject),
                true
            );

        if (newRoot != analysisRoot)
        {
            analysisRoot = newRoot;
            ClearAnalysis();
        }

        if (GUILayout.Button(
                "Use Selection",
                GUILayout.Width(105)))
        {
            if (Selection.activeGameObject != null)
            {
                analysisRoot = Selection.activeGameObject;
                ClearAnalysis();
            }
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(5);

        EditorGUILayout.LabelField(
            "Object Filters",
            EditorStyles.miniBoldLabel
        );

        searchInactiveObjects =
            EditorGUILayout.Toggle(
                "Search Inactive Objects",
                searchInactiveObjects
            );

        onlyActiveInHierarchy =
            EditorGUILayout.Toggle(
                "Only Active In Hierarchy",
                onlyActiveInHierarchy
            );

        onlyEnabledRenderers =
            EditorGUILayout.Toggle(
                "Only Enabled Renderers",
                onlyEnabledRenderers
            );

        EditorGUILayout.Space(9);

        EditorGUILayout.LabelField(
            "Physical Connectivity Graph",
            EditorStyles.miniBoldLabel
        );

        contactTolerance =
            Mathf.Max(
                0f,
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Contact Tolerance",
                        "Objects whose world-space renderer AABBs are separated by no more than this distance are always connected, regardless of name/category."
                    ),
                    contactTolerance
                )
            );

        enableNearConnections =
            EditorGUILayout.Toggle(
                new GUIContent(
                    "Enable Near Connections",
                    "Adds a second, looser connection test for IFC modelling gaps that are larger than the contact tolerance."
                ),
                enableNearConnections
            );

        if (enableNearConnections)
        {
            EditorGUI.indentLevel++;

            nearConnectionDistance =
                Mathf.Max(
                    contactTolerance,
                    EditorGUILayout.FloatField(
                        "Near Connection Distance",
                        nearConnectionDistance
                    )
                );

            nearConnectionPolicy =
                (NearConnectionPolicy)EditorGUILayout.EnumPopup(
                    new GUIContent(
                        "Near Connection Rule",
                        "Contact connections are always accepted. This rule applies only between Contact Tolerance and Near Connection Distance."
                    ),
                    nearConnectionPolicy
                );

            if (nearConnectionPolicy ==
                    NearConnectionPolicy.AxisOverlap ||
                nearConnectionPolicy ==
                    NearConnectionPolicy.SemanticOrAxisOverlap)
            {
                nearMinimumOverlapAxes =
                    EditorGUILayout.IntSlider(
                        new GUIContent(
                            "Required Overlap Axes",
                            "How many world AABB axes must overlap (allowing Contact Tolerance) for a loose near-connection to be accepted."
                        ),
                        nearMinimumOverlapAxes,
                        1,
                        3
                    );
            }

            EditorGUI.indentLevel--;
        }

        showConnectivityAdvanced =
            EditorGUILayout.Foldout(
                showConnectivityAdvanced,
                "Connectivity Exclusions",
                true
            );

        if (showConnectivityAdvanced)
        {
            EditorGUI.indentLevel++;

            excludeLargeObjectsFromConnectivity =
                EditorGUILayout.Toggle(
                    new GUIContent(
                        "Exclude Very Large Connectors",
                        "Prevents a giant slab/site/terrain object from connecting an otherwise unrelated site into one component. Excluded objects are still analyzed and placed under Excluded_From_Connectivity."
                    ),
                    excludeLargeObjectsFromConnectivity
                );

            if (excludeLargeObjectsFromConnectivity)
            {
                maxConnectivityObjectExtentRatio =
                    EditorGUILayout.Slider(
                        new GUIContent(
                            "Max Extent vs Root",
                            "If an object's largest bounds dimension exceeds this fraction of the complete analyzed root bounds, it does not participate in the connectivity graph."
                        ),
                        maxConnectivityObjectExtentRatio,
                        0.05f,
                        1f
                    );
            }

            connectivityIgnoreNameContains =
                EditorGUILayout.TextField(
                    new GUIContent(
                        "Ignore Name Contains",
                        "Semicolon-separated case-insensitive fragments. Matching objects are excluded only from connectivity; they remain in the analysis."
                    ),
                    connectivityIgnoreNameContains
                );

            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(7);

        EditorGUILayout.LabelField(
            "Top-Level Cluster Semantics",
            EditorStyles.miniBoldLabel
        );

        limitTopLevelClusters =
            EditorGUILayout.Toggle(
                new GUIContent(
                    "Limit Top-Level Clusters",
                    "Keep raw physical connected components internally, but spatially consolidate them into a small number of top-level hierarchy clusters."
                ),
                limitTopLevelClusters
            );

        if (limitTopLevelClusters)
        {
            maxTopLevelClusters =
                EditorGUILayout.IntSlider(
                    new GUIContent(
                        "Maximum Clusters",
                        "Maximum number of top-level connected clusters. Raw connected components are never split by this step; nearby components are grouped spatially."
                    ),
                    maxTopLevelClusters,
                    2,
                    9
                );

            EditorGUILayout.HelpBox(
                "The physical graph is still analyzed at full resolution. This setting only consolidates its connected components into a manageable number of top-level site clusters.",
                MessageType.None
            );
        }

        EditorGUILayout.Space(9);

        EditorGUILayout.LabelField(
            "Semantic Level Inside Clusters",
            EditorStyles.miniBoldLabel
        );

        semanticMode =
            (SemanticGroupingMode)EditorGUILayout.EnumPopup(
                "Semantic Algorithm",
                semanticMode
            );

        if (semanticMode ==
            SemanticGroupingMode.NameTokens)
        {
            semanticTokenCount =
                EditorGUILayout.IntSlider(
                    new GUIContent(
                        "Name Token Depth",
                        "Number of '-' / '_' / whitespace separated tokens used as the semantic family."
                    ),
                    semanticTokenCount,
                    1,
                    10
                );

            EditorGUILayout.HelpBox(
                "Example: PEV-site-fence-HalfConcApron-... at depth 3 becomes PEV-site-fence.",
                MessageType.None
            );
        }
        else if (semanticMode ==
                 SemanticGroupingMode.RegexCapture)
        {
            semanticRegex =
                EditorGUILayout.TextField(
                    new GUIContent(
                        "Regex",
                        "Use a named capture group called 'group', or the first capture group. Otherwise the full match is used."
                    ),
                    semanticRegex
                );
        }

        if (semanticMode !=
            SemanticGroupingMode.None)
        {
            showSemanticAdvanced =
                EditorGUILayout.Foldout(
                    showSemanticAdvanced,
                    "Semantic Normalization",
                    true
                );

            if (showSemanticAdvanced)
            {
                EditorGUI.indentLevel++;

                includeMeshNameInSemanticKey =
                    EditorGUILayout.Toggle(
                        "Include Mesh Name",
                        includeMeshNameInSemanticKey
                    );

                stripLikelyIds =
                    EditorGUILayout.Toggle(
                        "Strip Likely IFC/Hash IDs",
                        stripLikelyIds
                    );

                stripPureNumericTokens =
                    EditorGUILayout.Toggle(
                        "Strip Pure Numeric Tokens",
                        stripPureNumericTokens
                    );

                caseInsensitiveSemantics =
                    EditorGUILayout.Toggle(
                        "Case-Insensitive Groups",
                        caseInsensitiveSemantics
                    );

                EditorGUI.indentLevel--;
            }
        }

        EditorGUILayout.Space(9);

        EditorGUILayout.LabelField(
            "Optional Level 3 — Spatial Sections",
            EditorStyles.miniBoldLabel
        );

        buildSpatialSections =
            EditorGUILayout.Toggle(
                new GUIContent(
                    "Build Spatial Sections",
                    "After Structure -> Category, recursively split oversized semantic categories along their principal spatial axis. Leave disabled for the cleanest first connectivity pass."
                ),
                buildSpatialSections
            );

        if (buildSpatialSections)
        {
            EditorGUI.indentLevel++;

            maxObjectsPerSection =
                Mathf.Max(
                    2,
                    EditorGUILayout.IntField(
                        "Max Objects / Section",
                        maxObjectsPerSection
                    )
                );

            maxSectionExtent =
                Mathf.Max(
                    0.1f,
                    EditorGUILayout.FloatField(
                        "Max Section Extent",
                        maxSectionExtent
                    )
                );

            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(8);

        maxStructuresShown =
            EditorGUILayout.IntSlider(
                "Max Structures Shown",
                maxStructuresShown,
                10,
                500
            );

        maxCategoriesShownPerStructure =
            EditorGUILayout.IntSlider(
                "Max Categories / Structure",
                maxCategoriesShownPerStructure,
                5,
                100
            );

        EditorGUILayout.Space(8);

        using (new EditorGUI.DisabledScope(
                   analysisRoot == null))
        {
            if (GUILayout.Button(
                    "Analyze / Rebuild Connectivity Plan",
                    GUILayout.Height(34)))
            {
                Analyze();
            }
        }
    }

    // ============================================================
    // SUMMARY UI
    // ============================================================

    private void DrawSummary()
    {
        showAnalysisSummary =
            EditorGUILayout.Foldout(
                showAnalysisSummary,
                "Analysis Summary",
                true
            );

        if (!showAnalysisSummary)
            return;

        EditorGUILayout.BeginVertical(
            EditorStyles.helpBox
        );

        DrawStat(
            "Renderable Objects",
            allObjects.Count
        );

        DrawStat(
            "Triangles",
            analyzedTriangles
        );

        DrawStat(
            "Connectivity Participants",
            participantCount
        );

        DrawStat(
            "Raw Connected Components",
            rawConnectedComponentCount
        );

        DrawStat(
            "Top-Level Connected Clusters",
            structures.Count
        );

        DrawStat(
            "Objects In Structures",
            connectedObjectCount
        );

        DrawStat(
            "Singleton Objects",
            singletonObjectCount
        );

        DrawStat(
            "Connectivity-Excluded Objects",
            excludedObjectCount
        );

        DrawStat(
            "Semantic Groups In Structures",
            semanticGroupsInsideStructures
        );

        if (structures.Count > 0)
        {
            StructurePlan largest =
                structures
                    .OrderByDescending(
                        x => x.objects.Count
                    )
                    .First();

            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField(
                "Largest Cluster",
                $"{largest.objects.Count:N0} objects, " +
                $"{largest.triangles:N0} tris, " +
                $"{FormatBoundsSize(largest.bounds)}"
            );
        }

        if (analysisTimestamp != default(DateTime))
        {
            EditorGUILayout.LabelField(
                "Analyzed",
                analysisTimestamp.ToString(
                    "yyyy-MM-dd HH:mm:ss"
                )
            );
        }

        if (!string.IsNullOrWhiteSpace(
                statusMessage))
        {
            EditorGUILayout.Space(4);

            EditorGUILayout.HelpBox(
                statusMessage,
                MessageType.None
            );
        }

        EditorGUILayout.EndVertical();
    }

    private static void DrawStat(
        string label,
        long value)
    {
        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField(label);

        GUILayout.FlexibleSpace();

        EditorGUILayout.LabelField(
            value.ToString("N0"),
            GUILayout.Width(140)
        );

        EditorGUILayout.EndHorizontal();
    }

    // ============================================================
    // PLAN UI
    // ============================================================

    private void DrawConnectivityPlan()
    {
        DrawConnectedStructures();
        DrawSingletonGroups();
        DrawExcludedGroups();
    }

    private void DrawConnectedStructures()
    {
        showConnectedStructures =
            EditorGUILayout.Foldout(
                showConnectedStructures,
                $"Connected Clusters ({structures.Count:N0})",
                true
            );

        if (!showConnectedStructures)
            return;

        EditorGUILayout.HelpBox(
            "The physical graph first finds raw connected components. If the cluster cap is enabled, those components are then spatially consolidated into a small number of top-level Clusters. " +
            "Semantic categories are discovered only after that connectivity stage.",
            MessageType.Info
        );

        int rows =
            Mathf.Min(
                maxStructuresShown,
                structures.Count
            );

        for (int i = 0; i < rows; i++)
        {
            StructurePlan structure =
                structures[i];

            EditorGUILayout.BeginVertical(
                EditorStyles.helpBox
            );

            EditorGUILayout.BeginHorizontal();

            string displayClusterName =
                string.IsNullOrWhiteSpace(
                    structure.clusterName)
                    ? $"Cluster_{i:000}"
                    : structure.clusterName;

            structure.expanded =
                EditorGUILayout.Foldout(
                    structure.expanded,
                    $"{displayClusterName}   " +
                    $"[{structure.objects.Count:N0} objects, " +
                    $"{structure.triangles:N0} tris, " +
                    $"{structure.connectedComponentCount:N0} component(s), " +
                    $"{structure.semanticGroups.Count:N0} categories, " +
                    $"{FormatBoundsSize(structure.bounds)}]",
                    true
                );

            if (GUILayout.Button(
                    "Select Cluster",
                    GUILayout.Width(110)))
            {
                SelectObjects(
                    structure.objects
                );
            }

            EditorGUILayout.EndHorizontal();

            if (structure.expanded)
            {
                EditorGUI.indentLevel++;

                structure.clusterName =
                    SanitizeHierarchyName(
                        EditorGUILayout.TextField(
                            new GUIContent(
                                "Cluster Name",
                                "Heuristic suggestion. Edit this freely before Apply."
                            ),
                            structure.clusterName
                        )
                    );

                int categoryRows =
                    Mathf.Min(
                        maxCategoriesShownPerStructure,
                        structure.semanticGroups.Count
                    );

                for (int c = 0;
                     c < categoryRows;
                     c++)
                {
                    SemanticGroupPlan group =
                        structure.semanticGroups[c];

                    EditorGUILayout.BeginHorizontal();

                    GUILayout.Space(14);

                    if (GUILayout.Button(
                            group.key,
                            EditorStyles.linkLabel,
                            GUILayout.Width(270)))
                    {
                        SelectObjects(
                            group.objects
                        );
                    }

                    GUILayout.Label(
                        $"{group.objects.Count:N0} objs",
                        GUILayout.Width(78)
                    );

                    GUILayout.Label(
                        $"{group.triangles:N0} tris",
                        GUILayout.Width(90)
                    );

                    if (buildSpatialSections)
                    {
                        GUILayout.Label(
                            $"{group.sections.Count:N0} sec",
                            GUILayout.Width(55)
                        );
                    }

                    if (GUILayout.Button(
                            "Select",
                            GUILayout.Width(58)))
                    {
                        SelectObjects(
                            group.objects
                        );
                    }

                    EditorGUILayout.EndHorizontal();

                    if (buildSpatialSections &&
                        group.sections.Count > 1 &&
                        group.showSections)
                    {
                        EditorGUI.indentLevel++;

                        for (int s = 0;
                             s < group.sections.Count;
                             s++)
                        {
                            SpatialSectionPlan section =
                                group.sections[s];

                            EditorGUILayout.BeginHorizontal();

                            GUILayout.Space(30);

                            if (GUILayout.Button(
                                    $"Section_{s:000}",
                                    EditorStyles.linkLabel,
                                    GUILayout.Width(105)))
                            {
                                SelectObjects(
                                    section.objects
                                );
                            }

                            GUILayout.Label(
                                $"{section.objects.Count:N0} objs",
                                GUILayout.Width(78)
                            );

                            GUILayout.Label(
                                FormatBoundsSize(
                                    section.bounds
                                ),
                                GUILayout.Width(180)
                            );

                            EditorGUILayout.EndHorizontal();
                        }

                        EditorGUI.indentLevel--;
                    }

                    if (buildSpatialSections &&
                        group.sections.Count > 1)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(30);

                        if (GUILayout.Button(
                                group.showSections
                                    ? "Hide Sections"
                                    : "Show Sections",
                                GUILayout.Width(105)))
                        {
                            group.showSections =
                                !group.showSections;
                        }

                        EditorGUILayout.EndHorizontal();
                    }
                }

                if (structure.semanticGroups.Count >
                    maxCategoriesShownPerStructure)
                {
                    EditorGUILayout.LabelField(
                        $"... {structure.semanticGroups.Count - maxCategoriesShownPerStructure:N0} more categories hidden.",
                        EditorStyles.miniLabel
                    );
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        if (structures.Count >
            maxStructuresShown)
        {
            EditorGUILayout.HelpBox(
                $"{structures.Count - maxStructuresShown:N0} additional clusters are hidden by Max Structures Shown.",
                MessageType.None
            );
        }
    }

    private void DrawSingletonGroups()
    {
        showSingletons =
            EditorGUILayout.Foldout(
                showSingletons,
                $"Singletons ({singletonObjectCount:N0} objects)",
                true
            );

        if (!showSingletons)
            return;

        EditorGUILayout.HelpBox(
            "Singletons participated in connectivity analysis but had no accepted physical connection. " +
            "They are grouped semantically only so thousands of isolated objects do not clutter the primary Structure list.",
            MessageType.None
        );

        foreach (SemanticGroupPlan group
                 in singletonGroups)
        {
            EditorGUILayout.BeginHorizontal(
                EditorStyles.helpBox
            );

            if (GUILayout.Button(
                    group.key,
                    EditorStyles.linkLabel,
                    GUILayout.Width(300)))
            {
                SelectObjects(group.objects);
            }

            GUILayout.Label(
                $"{group.objects.Count:N0} objs",
                GUILayout.Width(90)
            );

            GUILayout.Label(
                $"{group.triangles:N0} tris",
                GUILayout.Width(100)
            );

            if (GUILayout.Button(
                    "Select",
                    GUILayout.Width(60)))
            {
                SelectObjects(group.objects);
            }

            EditorGUILayout.EndHorizontal();
        }
    }

    private void DrawExcludedGroups()
    {
        showExcluded =
            EditorGUILayout.Foldout(
                showExcluded,
                $"Excluded From Connectivity ({excludedObjectCount:N0} objects)",
                true
            );

        if (!showExcluded)
            return;

        EditorGUILayout.HelpBox(
            "These objects remain part of the hierarchy plan, but they were not allowed to act as graph connectors. " +
            "Typical examples are terrain/site slabs or explicit ignore-name matches.",
            MessageType.None
        );

        foreach (SemanticGroupPlan group
                 in excludedGroups)
        {
            EditorGUILayout.BeginHorizontal(
                EditorStyles.helpBox
            );

            if (GUILayout.Button(
                    group.key,
                    EditorStyles.linkLabel,
                    GUILayout.Width(300)))
            {
                SelectObjects(group.objects);
            }

            GUILayout.Label(
                $"{group.objects.Count:N0} objs",
                GUILayout.Width(90)
            );

            GUILayout.Label(
                $"{group.triangles:N0} tris",
                GUILayout.Width(100)
            );

            if (GUILayout.Button(
                    "Select",
                    GUILayout.Width(60)))
            {
                SelectObjects(group.objects);
            }

            EditorGUILayout.EndHorizontal();
        }
    }

    // ============================================================
    // STAGE 2 UI
    // ============================================================

    private void DrawApplySection()
    {
        showApply =
            EditorGUILayout.Foldout(
                showApply,
                "Stage 2 — Duplicate + Apply Hierarchy",
                true
            );

        if (!showApply)
            return;

        EditorGUILayout.BeginVertical(
            EditorStyles.helpBox
        );

        generatedRootPrefix =
            EditorGUILayout.TextField(
                "Generated Root Prefix",
                generatedRootPrefix
            );

        EditorGUILayout.Space(4);

        EditorGUILayout.LabelField(
            "Heuristic Cluster Naming",
            EditorStyles.miniBoldLabel
        );

        addSpatialSuffixToClusterNames =
            EditorGUILayout.Toggle(
                new GUIContent(
                    "Add Spatial Suffix",
                    "Append north/south/east/west/center according to the cluster center relative to the analyzed root bounds."
                ),
                addSpatialSuffixToClusterNames
            );

        if (addSpatialSuffixToClusterNames)
        {
            EditorGUI.indentLevel++;

            spatialNameCenterBand =
                EditorGUILayout.Slider(
                    new GUIContent(
                        "Center Band",
                        "Normalized half-width of the central region. Clusters inside this band are labelled center rather than east/west or north/south."
                    ),
                    spatialNameCenterBand,
                    0.05f,
                    0.45f
                );

            positiveXMeansEast =
                EditorGUILayout.Toggle(
                    "+X Means East",
                    positiveXMeansEast
                );

            positiveZMeansNorth =
                EditorGUILayout.Toggle(
                    "+Z Means North",
                    positiveZMeansNorth
                );

            EditorGUI.indentLevel--;
        }

        EditorGUILayout.HelpBox(
            "Suggested names are generated during Analyze (for example 00_fence_set_northwest or 01_muga_field_south). " +
            "You can edit each cluster name directly in the analysis list before applying.",
            MessageType.None
        );

        EditorGUILayout.Space(5);

        writeJsonAudit =
            EditorGUILayout.Toggle(
                "Write JSON Audit",
                writeJsonAudit
            );

        pruneEmptyTransformOnlyContainers =
            EditorGUILayout.Toggle(
                new GUIContent(
                    "Prune Empty Transform-Only Containers",
                    "After copied renderable objects are reorganized, remove empty helper GameObjects in the DUPLICATED root only when they contain no components other than Transform."
                ),
                pruneEmptyTransformOnlyContainers
            );

        EditorGUILayout.HelpBox(
            "Apply duplicates the selected root, organizes only the COPY, enables that copy, then disables the original root. " +
            "No Unity Undo records are created, which avoids undo-stack overflow on hierarchies containing thousands of objects. " +
            "To revert manually, delete the generated copy and re-enable the original root.",
            MessageType.Info
        );

        using (new EditorGUI.DisabledScope(
                   analysisRoot == null ||
                   allObjects.Count == 0))
        {
            if (GUILayout.Button(
                    "Duplicate Root + Apply Connectivity Hierarchy",
                    GUILayout.Height(36)))
            {
                ScheduleEditorAction(
                    ApplyHierarchy
                );
            }
        }

        EditorGUILayout.EndVertical();
    }

    // ============================================================
    // ANALYSIS
    // ============================================================

    private void Analyze()
    {
        ClearAnalysis();

        if (analysisRoot == null)
            return;

        List<ObjectInfo> collected =
            CollectRenderableObjects(
                analysisRoot
            );

        allObjects.AddRange(collected);

        if (allObjects.Count == 0)
        {
            statusMessage =
                "No renderable objects passed the current filters.";
            return;
        }

        analyzedTriangles =
            allObjects.Sum(
                x => x.triangles
            );

        rootRenderableBounds =
            CalculateCombinedBounds(
                allObjects
            );

        hasRootRenderableBounds = true;

        // Semantic keys are computed before graph building, but they do NOT
        // determine the primary groups. They are only an optional heuristic
        // for the loose near-connection rule and the second hierarchy level.
        foreach (ObjectInfo info in allObjects)
        {
            info.semanticKey =
                BuildSemanticKey(info);

            info.excludedFromConnectivity =
                ShouldExcludeFromConnectivity(
                    info
                );
        }

        List<ObjectInfo> participants =
            allObjects
                .Where(
                    x => !x.excludedFromConnectivity
                )
                .ToList();

        List<ObjectInfo> excluded =
            allObjects
                .Where(
                    x => x.excludedFromConnectivity
                )
                .ToList();

        participantCount =
            participants.Count;

        excludedObjectCount =
            excluded.Count;

        BuildConnectivityStructures(
            participants
        );

        BuildExcludedGroups(excluded);

        connectedObjectCount =
            structures.Sum(
                x => x.objects.Count
            );

        singletonObjectCount =
            singletonGroups.Sum(
                x => x.objects.Count
            );

        semanticGroupsInsideStructures =
            structures.Sum(
                x => x.semanticGroups.Count
            );

        analysisTimestamp =
            DateTime.Now;

        statusMessage =
            $"Connectivity-first plan built from '{analysisRoot.name}'. " +
            $"{rawConnectedComponentCount:N0} raw connected component(s) were represented by {structures.Count:N0} top-level cluster(s). " +
            $"Select any Cluster/category now, or Apply and use the duplicated organized root (or one of its children) as a new root for another pass.";

        Repaint();
    }

    private void ClearAnalysis()
    {
        allObjects.Clear();
        structures.Clear();
        singletonGroups.Clear();
        excludedGroups.Clear();

        analyzedTriangles = 0;
        participantCount = 0;
        rawConnectedComponentCount = 0;
        connectedObjectCount = 0;
        singletonObjectCount = 0;
        excludedObjectCount = 0;
        semanticGroupsInsideStructures = 0;

        hasRootRenderableBounds = false;
        statusMessage = "";
    }

    private List<ObjectInfo> CollectRenderableObjects(
        GameObject root)
    {
        List<ObjectInfo> result =
            new List<ObjectInfo>();

        MeshRenderer[] meshRenderers =
            root.GetComponentsInChildren<MeshRenderer>(
                searchInactiveObjects
            );

        foreach (MeshRenderer renderer
                 in meshRenderers)
        {
            if (renderer == null)
                continue;

            GameObject go =
                renderer.gameObject;

            if (go == root)
                continue;

            if (onlyActiveInHierarchy &&
                !go.activeInHierarchy)
            {
                continue;
            }

            if (onlyEnabledRenderers &&
                !renderer.enabled)
            {
                continue;
            }

            MeshFilter filter =
                go.GetComponent<MeshFilter>();

            Mesh mesh =
                filter != null
                    ? filter.sharedMesh
                    : null;

            result.Add(
                new ObjectInfo
                {
                    gameObject = go,
                    renderer = renderer,
                    bounds = renderer.bounds,
                    center = renderer.bounds.center,
                    triangles =
                        mesh != null
                            ? GetTriangleCount(mesh)
                            : 0,
                    meshName =
                        mesh != null
                            ? mesh.name
                            : ""
                }
            );
        }

        SkinnedMeshRenderer[] skinnedRenderers =
            root.GetComponentsInChildren<SkinnedMeshRenderer>(
                searchInactiveObjects
            );

        foreach (SkinnedMeshRenderer renderer
                 in skinnedRenderers)
        {
            if (renderer == null)
                continue;

            GameObject go =
                renderer.gameObject;

            if (go == root)
                continue;

            if (onlyActiveInHierarchy &&
                !go.activeInHierarchy)
            {
                continue;
            }

            if (onlyEnabledRenderers &&
                !renderer.enabled)
            {
                continue;
            }

            Mesh mesh =
                renderer.sharedMesh;

            result.Add(
                new ObjectInfo
                {
                    gameObject = go,
                    renderer = renderer,
                    bounds = renderer.bounds,
                    center = renderer.bounds.center,
                    triangles =
                        mesh != null
                            ? GetTriangleCount(mesh)
                            : 0,
                    meshName =
                        mesh != null
                            ? mesh.name
                            : ""
                }
            );
        }

        return result
            .GroupBy(
                x => x.gameObject
            )
            .Select(
                x => x.First()
            )
            .ToList();
    }

    // ============================================================
    // CONNECTIVITY GRAPH
    // ============================================================

    private void BuildConnectivityStructures(
        List<ObjectInfo> participants)
    {
        if (participants.Count == 0)
            return;

        float broadPhaseDistance =
            enableNearConnections
                ? Mathf.Max(
                    contactTolerance,
                    nearConnectionDistance
                )
                : contactTolerance;

        broadPhaseDistance =
            Mathf.Max(
                broadPhaseDistance,
                0.001f
            );

        UnionFind uf =
            new UnionFind(
                participants.Count
            );

        int[] degrees =
            new int[participants.Count];

        Dictionary<Int3, List<int>>
            spatialHash =
                BuildSpatialHash(
                    participants,
                    broadPhaseDistance
                );

        HashSet<ULongPair> testedPairs =
            new HashSet<ULongPair>();

        foreach (List<int> bucket
                 in spatialHash.Values)
        {
            for (int a = 0;
                 a < bucket.Count;
                 a++)
            {
                int ia =
                    bucket[a];

                for (int b = a + 1;
                     b < bucket.Count;
                     b++)
                {
                    int ib =
                        bucket[b];

                    if (ia == ib)
                        continue;

                    ULongPair pair =
                        new ULongPair(
                            ia,
                            ib
                        );

                    if (!testedPairs.Add(pair))
                        continue;

                    ObjectInfo objectA =
                        participants[ia];

                    ObjectInfo objectB =
                        participants[ib];

                    float gap =
                        BoundsGapDistance(
                            objectA.bounds,
                            objectB.bounds
                        );

                    bool connected =
                        false;

                    if (gap <=
                        contactTolerance)
                    {
                        connected = true;
                    }
                    else if (enableNearConnections &&
                             gap <=
                             nearConnectionDistance)
                    {
                        connected =
                            PassesNearConnectionRule(
                                objectA,
                                objectB
                            );
                    }

                    if (!connected)
                        continue;

                    uf.Union(
                        ia,
                        ib
                    );

                    degrees[ia]++;
                    degrees[ib]++;
                }
            }
        }

        Dictionary<int, List<ObjectInfo>>
            components =
                new Dictionary<
                    int,
                    List<ObjectInfo>>();

        for (int i = 0;
             i < participants.Count;
             i++)
        {
            int root =
                uf.Find(i);

            if (!components.TryGetValue(
                    root,
                    out List<ObjectInfo> list))
            {
                list =
                    new List<ObjectInfo>();

                components.Add(
                    root,
                    list
                );
            }

            list.Add(
                participants[i]
            );
        }

        List<ObjectInfo> singletons =
            new List<ObjectInfo>();

        List<StructurePlan> rawStructures =
            new List<StructurePlan>();

        foreach (List<ObjectInfo> component
                 in components.Values)
        {
            if (component.Count <= 1)
            {
                singletons.AddRange(
                    component
                );
                continue;
            }

            StructurePlan structure =
                BuildStructurePlan(
                    component
                );

            structure.connectedComponentCount = 1;

            rawStructures.Add(
                structure
            );
        }

        rawConnectedComponentCount =
            rawStructures.Count;

        List<StructurePlan> finalClusters =
            limitTopLevelClusters
                ? ConsolidateStructuresToLimit(
                    rawStructures,
                    Mathf.Clamp(
                        maxTopLevelClusters,
                        2,
                        9
                    )
                )
                : rawStructures;

        finalClusters =
            finalClusters
                .OrderByDescending(
                    x => x.objects.Count
                )
                .ThenByDescending(
                    x => x.triangles
                )
                .ThenBy(
                    x => x.bounds.center.x
                )
                .ThenBy(
                    x => x.bounds.center.z
                )
                .ToList();

        AssignHeuristicClusterNames(
            finalClusters
        );

        structures.AddRange(
            finalClusters
        );

        singletonGroups.AddRange(
            BuildSemanticGroups(
                singletons,
                false
            )
        );
    }

    private List<StructurePlan>
        ConsolidateStructuresToLimit(
            List<StructurePlan> rawStructures,
            int maximumClusters)
    {
        if (rawStructures == null ||
            rawStructures.Count <= maximumClusters)
        {
            return rawStructures ??
                   new List<StructurePlan>();
        }

        // Work with groups of raw connected components. We repeatedly split
        // the geographically broadest macro-group along its principal axis
        // until we reach the requested number of top-level clusters.
        List<List<StructurePlan>> macroGroups =
            new List<List<StructurePlan>>
            {
                new List<StructurePlan>(
                    rawStructures
                )
            };

        int safety = 0;

        while (macroGroups.Count <
               maximumClusters &&
               safety++ < 1000)
        {
            int splitIndex =
                FindBestMacroGroupToSplit(
                    macroGroups
                );

            if (splitIndex < 0)
                break;

            List<StructurePlan> group =
                macroGroups[splitIndex];

            if (!TrySplitStructureGroup(
                    group,
                    out List<StructurePlan> left,
                    out List<StructurePlan> right))
            {
                break;
            }

            macroGroups.RemoveAt(
                splitIndex
            );

            macroGroups.Add(left);
            macroGroups.Add(right);
        }

        List<StructurePlan> result =
            new List<StructurePlan>();

        foreach (List<StructurePlan> macro
                 in macroGroups)
        {
            List<ObjectInfo> mergedObjects =
                macro
                    .SelectMany(
                        x => x.objects
                    )
                    .Distinct()
                    .ToList();

            StructurePlan merged =
                BuildStructurePlan(
                    mergedObjects
                );

            merged.connectedComponentCount =
                macro.Sum(
                    x => Mathf.Max(
                        1,
                        x.connectedComponentCount
                    )
                );

            result.Add(merged);
        }

        return result;
    }

    private int FindBestMacroGroupToSplit(
        List<List<StructurePlan>> macroGroups)
    {
        int bestIndex = -1;
        float bestScore = float.NegativeInfinity;

        for (int i = 0;
             i < macroGroups.Count;
             i++)
        {
            List<StructurePlan> group =
                macroGroups[i];

            if (group == null ||
                group.Count <= 1)
            {
                continue;
            }

            Bounds bounds =
                CalculateCombinedStructureBounds(
                    group
                );

            int objectCount =
                group.Sum(
                    x => x.objects.Count
                );

            float score =
                LargestDimension(
                    bounds.size
                ) *
                Mathf.Sqrt(
                    Mathf.Max(
                        1,
                        objectCount
                    )
                );

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private bool TrySplitStructureGroup(
        List<StructurePlan> group,
        out List<StructurePlan> left,
        out List<StructurePlan> right)
    {
        left =
            new List<StructurePlan>();

        right =
            new List<StructurePlan>();

        if (group == null ||
            group.Count <= 1)
        {
            return false;
        }

        Vector3 axis =
            CalculatePrincipalAxisForStructures(
                group
            );

        List<StructurePlan> ordered =
            group
                .OrderBy(
                    x => Vector3.Dot(
                        x.bounds.center,
                        axis
                    )
                )
                .ToList();

        int totalWeight =
            ordered.Sum(
                x => Mathf.Max(
                    1,
                    x.objects.Count
                )
            );

        int accumulated = 0;
        int splitAt = 1;

        for (int i = 0;
             i < ordered.Count - 1;
             i++)
        {
            accumulated +=
                Mathf.Max(
                    1,
                    ordered[i].objects.Count
                );

            splitAt = i + 1;

            if (accumulated >=
                totalWeight * 0.5f)
            {
                break;
            }
        }

        splitAt =
            Mathf.Clamp(
                splitAt,
                1,
                ordered.Count - 1
            );

        left =
            ordered
                .Take(splitAt)
                .ToList();

        right =
            ordered
                .Skip(splitAt)
                .ToList();

        return left.Count > 0 &&
               right.Count > 0;
    }

    private static Vector3
        CalculatePrincipalAxisForStructures(
            List<StructurePlan> structures)
    {
        if (structures == null ||
            structures.Count <= 1)
        {
            return Vector3.right;
        }

        Vector3 mean = Vector3.zero;
        float totalWeight = 0f;

        foreach (StructurePlan structure
                 in structures)
        {
            float weight =
                Mathf.Max(
                    1,
                    structure.objects.Count
                );

            mean +=
                structure.bounds.center *
                weight;

            totalWeight += weight;
        }

        mean /=
            Mathf.Max(
                1f,
                totalWeight
            );

        float xx = 0f;
        float xy = 0f;
        float xz = 0f;
        float yy = 0f;
        float yz = 0f;
        float zz = 0f;

        foreach (StructurePlan structure
                 in structures)
        {
            float weight =
                Mathf.Max(
                    1,
                    structure.objects.Count
                );

            Vector3 d =
                structure.bounds.center -
                mean;

            xx += d.x * d.x * weight;
            xy += d.x * d.y * weight;
            xz += d.x * d.z * weight;
            yy += d.y * d.y * weight;
            yz += d.y * d.z * weight;
            zz += d.z * d.z * weight;
        }

        Vector3 axis =
            new Vector3(
                1f,
                0.71f,
                0.37f
            ).normalized;

        for (int i = 0;
             i < 18;
             i++)
        {
            Vector3 next =
                new Vector3(
                    xx * axis.x +
                    xy * axis.y +
                    xz * axis.z,

                    xy * axis.x +
                    yy * axis.y +
                    yz * axis.z,

                    xz * axis.x +
                    yz * axis.y +
                    zz * axis.z
                );

            if (next.sqrMagnitude <
                1e-12f)
            {
                break;
            }

            axis = next.normalized;
        }

        return axis.sqrMagnitude > 0.5f
            ? axis.normalized
            : Vector3.right;
    }

    private static Bounds
        CalculateCombinedStructureBounds(
            List<StructurePlan> structures)
    {
        Bounds bounds =
            structures[0].bounds;

        for (int i = 1;
             i < structures.Count;
             i++)
        {
            bounds.Encapsulate(
                structures[i].bounds
            );
        }

        return bounds;
    }

    private Dictionary<Int3, List<int>>
        BuildSpatialHash(
            List<ObjectInfo> objects,
            float searchDistance)
    {
        Dictionary<Int3, List<int>>
            hash =
                new Dictionary<Int3, List<int>>();

        float cellSize =
            Mathf.Max(
                searchDistance,
                0.25f
            );

        for (int i = 0;
             i < objects.Count;
             i++)
        {
            Bounds broad =
                objects[i].bounds;

            broad.Expand(
                searchDistance * 2f
            );

            GetCellRange(
                broad,
                cellSize,
                out Int3 minCell,
                out Int3 maxCell
            );

            for (int x = minCell.x;
                 x <= maxCell.x;
                 x++)
            for (int y = minCell.y;
                 y <= maxCell.y;
                 y++)
            for (int z = minCell.z;
                 z <= maxCell.z;
                 z++)
            {
                Int3 cell =
                    new Int3(
                        x,
                        y,
                        z
                    );

                if (!hash.TryGetValue(
                        cell,
                        out List<int> list))
                {
                    list =
                        new List<int>();

                    hash.Add(
                        cell,
                        list
                    );
                }

                list.Add(i);
            }
        }

        return hash;
    }

    private bool PassesNearConnectionRule(
        ObjectInfo a,
        ObjectInfo b)
    {
        if (nearConnectionPolicy ==
            NearConnectionPolicy.AnyWithinDistance)
        {
            return true;
        }

        bool sameSemantic =
            string.Equals(
                a.semanticKey,
                b.semanticKey,
                caseInsensitiveSemantics
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            );

        if (nearConnectionPolicy ==
            NearConnectionPolicy.SameSemanticFamily)
        {
            return sameSemantic;
        }

        int overlapAxes =
            CountOverlappingAxes(
                a.bounds,
                b.bounds,
                contactTolerance
            );

        bool axisPass =
            overlapAxes >=
            nearMinimumOverlapAxes;

        if (nearConnectionPolicy ==
            NearConnectionPolicy.AxisOverlap)
        {
            return axisPass;
        }

        return sameSemantic ||
               axisPass;
    }

    private static int CountOverlappingAxes(
        Bounds a,
        Bounds b,
        float tolerance)
    {
        int count = 0;

        if (IntervalsOverlap(
                a.min.x,
                a.max.x,
                b.min.x,
                b.max.x,
                tolerance))
        {
            count++;
        }

        if (IntervalsOverlap(
                a.min.y,
                a.max.y,
                b.min.y,
                b.max.y,
                tolerance))
        {
            count++;
        }

        if (IntervalsOverlap(
                a.min.z,
                a.max.z,
                b.min.z,
                b.max.z,
                tolerance))
        {
            count++;
        }

        return count;
    }

    private static bool IntervalsOverlap(
        float minA,
        float maxA,
        float minB,
        float maxB,
        float tolerance)
    {
        return maxA + tolerance >= minB &&
               maxB + tolerance >= minA;
    }

    private static float BoundsGapDistance(
        Bounds a,
        Bounds b)
    {
        float dx =
            Mathf.Max(
                0f,
                Mathf.Max(
                    a.min.x - b.max.x,
                    b.min.x - a.max.x
                )
            );

        float dy =
            Mathf.Max(
                0f,
                Mathf.Max(
                    a.min.y - b.max.y,
                    b.min.y - a.max.y
                )
            );

        float dz =
            Mathf.Max(
                0f,
                Mathf.Max(
                    a.min.z - b.max.z,
                    b.min.z - a.max.z
                )
            );

        return Mathf.Sqrt(
            dx * dx +
            dy * dy +
            dz * dz
        );
    }

    private static void GetCellRange(
        Bounds bounds,
        float cellSize,
        out Int3 minCell,
        out Int3 maxCell)
    {
        float inv =
            1f /
            Mathf.Max(
                cellSize,
                0.0001f
            );

        minCell =
            new Int3(
                Mathf.FloorToInt(
                    bounds.min.x *
                    inv
                ),
                Mathf.FloorToInt(
                    bounds.min.y *
                    inv
                ),
                Mathf.FloorToInt(
                    bounds.min.z *
                    inv
                )
            );

        maxCell =
            new Int3(
                Mathf.FloorToInt(
                    bounds.max.x *
                    inv
                ),
                Mathf.FloorToInt(
                    bounds.max.y *
                    inv
                ),
                Mathf.FloorToInt(
                    bounds.max.z *
                    inv
                )
            );
    }

    // ============================================================
    // HEURISTIC CLUSTER NAMING
    // ============================================================

    private void AssignHeuristicClusterNames(
        List<StructurePlan> clusters)
    {
        if (clusters == null)
            return;

        for (int i = 0;
             i < clusters.Count;
             i++)
        {
            StructurePlan cluster =
                clusters[i];

            string semanticName =
                InferClusterSemanticName(
                    cluster
                );

            string spatialSuffix =
                addSpatialSuffixToClusterNames
                    ? InferSpatialSuffix(
                        cluster.bounds
                    )
                    : "";

            string indexPrefix =
                i.ToString("00");

            string name =
                indexPrefix +
                "_" +
                semanticName;

            if (!string.IsNullOrWhiteSpace(
                    spatialSuffix))
            {
                name +=
                    "_" +
                    spatialSuffix;
            }

            cluster.clusterName =
                SanitizeHierarchyName(
                    name
                );
        }
    }

    private string InferClusterSemanticName(
        StructurePlan cluster)
    {
        if (cluster == null ||
            cluster.objects == null ||
            cluster.objects.Count == 0)
        {
            return "context_set";
        }

        Dictionary<string, float> scores =
            new Dictionary<string, float>(
                StringComparer.OrdinalIgnoreCase
            );

        // Score source objects rather than only category names because some
        // IFC exporters put the useful domain term deeper in the raw name.
        foreach (ObjectInfo info
                 in cluster.objects)
        {
            if (info == null ||
                info.gameObject == null)
            {
                continue;
            }

            string haystack =
                ((info.gameObject.name ?? "") +
                 " " +
                 (info.semanticKey ?? "") +
                 " " +
                 (info.meshName ?? ""))
                .ToLowerInvariant();

            float baseWeight =
                1f +
                Mathf.Min(
                    6f,
                    info.triangles /
                    10000f
                );

            // Distinctive site features receive a modest semantic boost,
            // but thousands of fence elements will still dominate a cluster
            // that is genuinely mostly fencing.
            if (ContainsAny(
                    haystack,
                    "basket",
                    "basketball"))
            {
                AddNameScore(
                    scores,
                    "basket_field",
                    baseWeight * 3.0f
                );
            }

            if (ContainsAny(
                    haystack,
                    "muga",
                    "goalgoal",
                    "multiusegames"))
            {
                AddNameScore(
                    scores,
                    "muga_field",
                    baseWeight * 2.8f
                );
            }

            if (ContainsAny(
                    haystack,
                    "fence",
                    "fencepanel",
                    "fencepost",
                    "barbedwire",
                    "barbed-wire",
                    "barbed_wire"))
            {
                AddNameScore(
                    scores,
                    "fence_set",
                    baseWeight
                );
            }

            if (ContainsAny(
                    haystack,
                    "railing",
                    "handrail"))
            {
                AddNameScore(
                    scores,
                    "railing_set",
                    baseWeight * 1.2f
                );
            }

            if (ContainsAny(
                    haystack,
                    "tree",
                    "planting"))
            {
                AddNameScore(
                    scores,
                    "tree_set",
                    baseWeight * 1.2f
                );
            }

            if (ContainsAny(
                    haystack,
                    "turnstile"))
            {
                AddNameScore(
                    scores,
                    "turnstile_set",
                    baseWeight * 1.8f
                );
            }

            if (ContainsAny(
                    haystack,
                    "wall",
                    "ifcwall"))
            {
                AddNameScore(
                    scores,
                    "wall_set",
                    baseWeight
                );
            }

            if (ContainsAny(
                    haystack,
                    "concrete",
                    "concapron",
                    "apron",
                    "kerb"))
            {
                AddNameScore(
                    scores,
                    "concrete_set",
                    baseWeight * 0.8f
                );
            }

            if (ContainsAny(
                    haystack,
                    "pipe"))
            {
                AddNameScore(
                    scores,
                    "pipe_set",
                    baseWeight
                );
            }
        }

        if (scores.Count > 0)
        {
            KeyValuePair<string, float> winner =
                scores
                    .OrderByDescending(
                        x => x.Value
                    )
                    .First();

            return winner.Key;
        }

        // Fallback: use the dominant normalized semantic category, while
        // dropping generic IFC/export prefixes that do not help a human.
        SemanticGroupPlan dominant =
            cluster.semanticGroups
                .OrderByDescending(
                    x => x.objects.Count
                )
                .ThenByDescending(
                    x => x.triangles
                )
                .FirstOrDefault();

        if (dominant == null ||
            string.IsNullOrWhiteSpace(
                dominant.key))
        {
            return "context_set";
        }

        string cleaned =
            CleanSemanticKeyForClusterName(
                dominant.key
            );

        return string.IsNullOrWhiteSpace(
                   cleaned)
            ? "context_set"
            : cleaned;
    }

    private static void AddNameScore(
        Dictionary<string, float> scores,
        string key,
        float value)
    {
        if (!scores.ContainsKey(key))
            scores[key] = 0f;

        scores[key] += value;
    }

    private static bool ContainsAny(
        string haystack,
        params string[] needles)
    {
        if (string.IsNullOrEmpty(haystack))
            return false;

        foreach (string needle
                 in needles)
        {
            if (!string.IsNullOrEmpty(needle) &&
                haystack.IndexOf(
                    needle,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string
        CleanSemanticKeyForClusterName(
            string semanticKey)
    {
        if (string.IsNullOrWhiteSpace(
                semanticKey))
        {
            return "context_set";
        }

        List<string> tokens =
            Regex.Split(
                semanticKey.ToLowerInvariant(),
                @"[-_\s]+"
            )
            .Where(
                token =>
                    !string.IsNullOrWhiteSpace(
                        token
                    )
            )
            .Where(
                token =>
                    token != "pev" &&
                    token != "site" &&
                    token != "ifc" &&
                    token != "ifcbuilding" &&
                    token != "element" &&
                    token != "proxy"
            )
            .Take(4)
            .ToList();

        if (tokens.Count == 0)
            return "context_set";

        string result =
            string.Join(
                "_",
                tokens
            );

        if (!result.EndsWith(
                "_set",
                StringComparison.OrdinalIgnoreCase) &&
            tokens.Count == 1)
        {
            result += "_set";
        }

        return result;
    }

    private string InferSpatialSuffix(
        Bounds clusterBounds)
    {
        if (!hasRootRenderableBounds)
            return "";

        Vector3 rootCenter =
            rootRenderableBounds.center;

        Vector3 extents =
            rootRenderableBounds.extents;

        float nx =
            extents.x > 1e-5f
                ? (clusterBounds.center.x -
                   rootCenter.x) /
                  extents.x
                : 0f;

        float nz =
            extents.z > 1e-5f
                ? (clusterBounds.center.z -
                   rootCenter.z) /
                  extents.z
                : 0f;

        string eastWest = "";

        if (Mathf.Abs(nx) >
            spatialNameCenterBand)
        {
            bool positive =
                nx > 0f;

            bool east =
                positiveXMeansEast
                    ? positive
                    : !positive;

            eastWest =
                east
                    ? "east"
                    : "west";
        }

        string northSouth = "";

        if (Mathf.Abs(nz) >
            spatialNameCenterBand)
        {
            bool positive =
                nz > 0f;

            bool north =
                positiveZMeansNorth
                    ? positive
                    : !positive;

            northSouth =
                north
                    ? "north"
                    : "south";
        }

        if (string.IsNullOrEmpty(
                northSouth) &&
            string.IsNullOrEmpty(
                eastWest))
        {
            return "center";
        }

        if (string.IsNullOrEmpty(
                northSouth))
        {
            return eastWest;
        }

        if (string.IsNullOrEmpty(
                eastWest))
        {
            return northSouth;
        }

        return northSouth +
               eastWest;
    }

    // ============================================================
    // CONNECTIVITY EXCLUSIONS
    // ============================================================

    private bool ShouldExcludeFromConnectivity(
        ObjectInfo info)
    {
        if (info == null ||
            info.gameObject == null)
        {
            return true;
        }

        List<string> ignoreFragments =
            ParseIgnoreFragments(
                connectivityIgnoreNameContains
            );

        string name =
            info.gameObject.name ??
            "";

        foreach (string fragment
                 in ignoreFragments)
        {
            if (name.IndexOf(
                    fragment,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        if (!excludeLargeObjectsFromConnectivity ||
            !hasRootRenderableBounds)
        {
            return false;
        }

        float rootLargest =
            LargestDimension(
                rootRenderableBounds.size
            );

        float objectLargest =
            LargestDimension(
                info.bounds.size
            );

        if (rootLargest <=
            1e-6f)
        {
            return false;
        }

        return objectLargest /
               rootLargest >=
               maxConnectivityObjectExtentRatio;
    }

    private static List<string> ParseIgnoreFragments(
        string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw
            .Split(
                new[]
                {
                    ';',
                    ',',
                    '\n'
                },
                StringSplitOptions
                    .RemoveEmptyEntries
            )
            .Select(
                x => x.Trim()
            )
            .Where(
                x => !string.IsNullOrWhiteSpace(x)
            )
            .ToList();
    }

    // ============================================================
    // STRUCTURE / SEMANTIC PLANS
    // ============================================================

    private StructurePlan BuildStructurePlan(
        List<ObjectInfo> objects)
    {
        StructurePlan structure =
            new StructurePlan
            {
                objects =
                    new List<ObjectInfo>(
                        objects
                    ),
                bounds =
                    CalculateCombinedBounds(
                        objects
                    ),
                triangles =
                    objects.Sum(
                        x => x.triangles
                    )
            };

        structure.semanticGroups.AddRange(
            BuildSemanticGroups(
                objects,
                buildSpatialSections
            )
        );

        return structure;
    }

    private List<SemanticGroupPlan>
        BuildSemanticGroups(
            List<ObjectInfo> objects,
            bool generateSections)
    {
        StringComparer comparer =
            caseInsensitiveSemantics
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        Dictionary<string, List<ObjectInfo>>
            buckets =
                new Dictionary<
                    string,
                    List<ObjectInfo>>(
                        comparer
                    );

        foreach (ObjectInfo info in objects)
        {
            string key =
                string.IsNullOrWhiteSpace(
                    info.semanticKey)
                    ? "Uncategorized"
                    : info.semanticKey;

            if (!buckets.TryGetValue(
                    key,
                    out List<ObjectInfo> list))
            {
                list =
                    new List<ObjectInfo>();

                buckets.Add(
                    key,
                    list
                );
            }

            list.Add(info);
        }

        List<SemanticGroupPlan> plans =
            new List<SemanticGroupPlan>();

        foreach (KeyValuePair<
                     string,
                     List<ObjectInfo>> pair
                 in buckets)
        {
            SemanticGroupPlan group =
                new SemanticGroupPlan
                {
                    key = pair.Key,
                    objects =
                        pair.Value,
                    triangles =
                        pair.Value.Sum(
                            x => x.triangles
                        )
                };

            if (pair.Value.Count > 0)
            {
                group.bounds =
                    CalculateCombinedBounds(
                        pair.Value
                    );
            }

            if (generateSections &&
                pair.Value.Count > 0)
            {
                List<List<ObjectInfo>>
                    sectionGroups =
                        SplitIntoSpatialSections(
                            pair.Value
                        );

                foreach (List<ObjectInfo> section
                         in sectionGroups)
                {
                    group.sections.Add(
                        new SpatialSectionPlan
                        {
                            objects =
                                section,
                            bounds =
                                CalculateCombinedBounds(
                                    section
                                ),
                            triangles =
                                section.Sum(
                                    x => x.triangles
                                )
                        }
                    );
                }
            }
            else if (pair.Value.Count > 0)
            {
                group.sections.Add(
                    new SpatialSectionPlan
                    {
                        objects =
                            pair.Value,
                        bounds =
                            group.bounds,
                        triangles =
                            group.triangles
                    }
                );
            }

            plans.Add(group);
        }

        return plans
            .OrderByDescending(
                x => x.objects.Count
            )
            .ThenByDescending(
                x => x.triangles
            )
            .ThenBy(
                x => x.key
            )
            .ToList();
    }

    private void BuildExcludedGroups(
        List<ObjectInfo> excluded)
    {
        excludedGroups.AddRange(
            BuildSemanticGroups(
                excluded,
                false
            )
        );
    }

    // ============================================================
    // OPTIONAL SPATIAL SECTIONS
    // ============================================================

    private List<List<ObjectInfo>>
        SplitIntoSpatialSections(
            List<ObjectInfo> source)
    {
        List<List<ObjectInfo>> result =
            new List<List<ObjectInfo>>();

        if (source.Count == 0)
            return result;

        Queue<List<ObjectInfo>> queue =
            new Queue<List<ObjectInfo>>();

        queue.Enqueue(
            new List<ObjectInfo>(
                source
            )
        );

        int safety = 0;

        while (queue.Count > 0 &&
               safety++ < 100000)
        {
            List<ObjectInfo> group =
                queue.Dequeue();

            if (!NeedsSectionSplit(group) ||
                group.Count <= 1)
            {
                result.Add(group);
                continue;
            }

            Vector3 axis =
                CalculatePrincipalAxis(
                    group
                );

            List<ObjectInfo> ordered =
                group
                    .OrderBy(
                        x => Vector3.Dot(
                            x.center,
                            axis
                        )
                    )
                    .ToList();

            int midpoint =
                ordered.Count /
                2;

            if (midpoint <= 0 ||
                midpoint >=
                ordered.Count)
            {
                result.Add(group);
                continue;
            }

            List<ObjectInfo> left =
                ordered
                    .Take(midpoint)
                    .ToList();

            List<ObjectInfo> right =
                ordered
                    .Skip(midpoint)
                    .ToList();

            if (left.Count == 0 ||
                right.Count == 0)
            {
                result.Add(group);
                continue;
            }

            queue.Enqueue(left);
            queue.Enqueue(right);
        }

        return result
            .OrderBy(
                x => CalculateCombinedBounds(
                    x
                ).center.x
            )
            .ThenBy(
                x => CalculateCombinedBounds(
                    x
                ).center.z
            )
            .ToList();
    }

    private bool NeedsSectionSplit(
        List<ObjectInfo> group)
    {
        if (group.Count >
            maxObjectsPerSection)
        {
            return true;
        }

        Bounds bounds =
            CalculateCombinedBounds(
                group
            );

        return LargestDimension(
                   bounds.size
               ) >
               maxSectionExtent;
    }

    private static Vector3 CalculatePrincipalAxis(
        List<ObjectInfo> objects)
    {
        if (objects.Count <= 1)
            return Vector3.right;

        Vector3 mean =
            Vector3.zero;

        foreach (ObjectInfo info in objects)
            mean += info.center;

        mean /= objects.Count;

        float xx = 0f;
        float xy = 0f;
        float xz = 0f;
        float yy = 0f;
        float yz = 0f;
        float zz = 0f;

        foreach (ObjectInfo info in objects)
        {
            Vector3 d =
                info.center -
                mean;

            xx += d.x * d.x;
            xy += d.x * d.y;
            xz += d.x * d.z;
            yy += d.y * d.y;
            yz += d.y * d.z;
            zz += d.z * d.z;
        }

        Vector3 axis =
            new Vector3(
                1f,
                0.71f,
                0.37f
            ).normalized;

        for (int i = 0;
             i < 18;
             i++)
        {
            Vector3 next =
                new Vector3(
                    xx * axis.x +
                    xy * axis.y +
                    xz * axis.z,

                    xy * axis.x +
                    yy * axis.y +
                    yz * axis.z,

                    xz * axis.x +
                    yz * axis.y +
                    zz * axis.z
                );

            if (next.sqrMagnitude <
                1e-12f)
            {
                break;
            }

            axis =
                next.normalized;
        }

        if (axis.sqrMagnitude <
            0.5f)
        {
            Bounds bounds =
                CalculateCombinedBounds(
                    objects
                );

            Vector3 size =
                bounds.size;

            if (size.x >= size.y &&
                size.x >= size.z)
            {
                axis =
                    Vector3.right;
            }
            else if (size.y >= size.z)
            {
                axis =
                    Vector3.up;
            }
            else
            {
                axis =
                    Vector3.forward;
            }
        }

        return axis.normalized;
    }

    // ============================================================
    // SEMANTIC KEYS
    // ============================================================

    private string BuildSemanticKey(
        ObjectInfo info)
    {
        if (semanticMode ==
            SemanticGroupingMode.None)
        {
            return "All";
        }

        string sourceName =
            info.gameObject != null
                ? info.gameObject.name
                : "Unnamed";

        if (includeMeshNameInSemanticKey &&
            !string.IsNullOrWhiteSpace(
                info.meshName))
        {
            sourceName +=
                "-" +
                info.meshName;
        }

        if (semanticMode ==
            SemanticGroupingMode.RegexCapture)
        {
            try
            {
                Match match =
                    Regex.Match(
                        sourceName,
                        semanticRegex
                    );

                if (match.Success)
                {
                    Group named =
                        match.Groups["group"];

                    if (named != null &&
                        named.Success)
                    {
                        return NormalizeSemanticText(
                            named.Value
                        );
                    }

                    if (match.Groups.Count >
                        1)
                    {
                        return NormalizeSemanticText(
                            match.Groups[1]
                                .Value
                        );
                    }

                    return NormalizeSemanticText(
                        match.Value
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "Semantic regex failed: " +
                    ex.Message
                );
            }

            return NormalizeSemanticText(
                sourceName
            );
        }

        if (semanticMode ==
            SemanticGroupingMode.FullNormalizedName)
        {
            return NormalizeSemanticText(
                sourceName
            );
        }

        List<string> tokens =
            TokenizeSemanticName(
                sourceName
            );

        int count =
            Mathf.Min(
                semanticTokenCount,
                tokens.Count
            );

        if (count <= 0)
            return "Uncategorized";

        return string.Join(
            "-",
            tokens.Take(count)
        );
    }

    private string NormalizeSemanticText(
        string value)
    {
        List<string> tokens =
            TokenizeSemanticName(
                value
            );

        return tokens.Count > 0
            ? string.Join(
                "-",
                tokens
            )
            : "Uncategorized";
    }

    private List<string> TokenizeSemanticName(
        string value)
    {
        if (string.IsNullOrWhiteSpace(
                value))
        {
            return new List<string>();
        }

        string[] raw =
            Regex.Split(
                value,
                @"[-_\s]+"
            );

        List<string> tokens =
            new List<string>();

        foreach (string rawToken
                 in raw)
        {
            string token =
                rawToken.Trim();

            if (string.IsNullOrWhiteSpace(
                    token))
            {
                continue;
            }

            if (stripPureNumericTokens &&
                token.All(char.IsDigit))
            {
                continue;
            }

            if (stripLikelyIds &&
                LooksLikeExportId(
                    token
                ))
            {
                continue;
            }

            tokens.Add(
                caseInsensitiveSemantics
                    ? token
                        .ToLowerInvariant()
                    : token
            );
        }

        return tokens;
    }

    private static bool LooksLikeExportId(
        string token)
    {
        if (string.IsNullOrWhiteSpace(
                token))
        {
            return false;
        }

        if (token.Length >= 8 &&
            token.All(
                Uri.IsHexDigit
            ))
        {
            return true;
        }

        if (token.Length >= 12)
        {
            int letters = 0;
            int digits = 0;

            foreach (char c in token)
            {
                if (char.IsLetter(c))
                    letters++;
                else if (char.IsDigit(c))
                    digits++;
            }

            if (letters > 0 &&
                digits > 0 &&
                letters + digits ==
                token.Length)
            {
                return true;
            }
        }

        return false;
    }

    // ============================================================
    // APPLY
    // ============================================================

    private void ApplyHierarchy()
    {
        if (analysisRoot == null ||
            allObjects.Count == 0)
        {
            return;
        }

        List<ObjectInfo> validObjects =
            allObjects
                .Where(
                    x => x.gameObject != null
                )
                .ToList();

        if (validObjects.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Hierarchy Organizer",
                "None of the analyzed source objects still exist.",
                "OK"
            );
            return;
        }

        string timestamp =
            DateTime.Now.ToString(
                "yyyyMMdd_HHmmss_fff"
            );

        string prefix =
            string.IsNullOrWhiteSpace(
                generatedRootPrefix)
                ? "__CONNECTED_CLUSTERED__"
                : generatedRootPrefix.Trim();

        string duplicatedName =
            SanitizeHierarchyName(
                analysisRoot.name +
                "_" +
                prefix +
                "_" +
                timestamp
            );

        GameObject duplicatedRoot = null;

        bool originalActiveState =
            analysisRoot.activeSelf;

        AppliedBatch batch =
            new AppliedBatch
            {
                sourceRoot =
                    analysisRoot,
                batchName =
                    duplicatedName,
                appliedAt =
                    DateTime.Now,
                originalRootWasActiveSelf =
                    originalActiveState
            };

        try
        {
            // No Undo registration: on very large IFC hierarchies thousands of
            // SetTransformParent undo records can overflow Unity's undo stack.
            // The untouched disabled source root is itself the rollback copy.
            duplicatedRoot =
                UnityEngine.Object.Instantiate(
                    analysisRoot
                );

            duplicatedRoot.name =
                duplicatedName;

            Transform originalParent =
                analysisRoot.transform.parent;

            duplicatedRoot.transform.SetParent(
                originalParent,
                false
            );

            duplicatedRoot.transform.localPosition =
                analysisRoot.transform.localPosition;

            duplicatedRoot.transform.localRotation =
                analysisRoot.transform.localRotation;

            duplicatedRoot.transform.localScale =
                analysisRoot.transform.localScale;

            int targetSibling =
                analysisRoot.transform
                    .GetSiblingIndex() + 1;

            int maxSibling =
                originalParent != null
                    ? originalParent.childCount - 1
                    : duplicatedRoot.scene.rootCount - 1;

            duplicatedRoot.transform.SetSiblingIndex(
                Mathf.Clamp(
                    targetSibling,
                    0,
                    Mathf.Max(
                        0,
                        maxSibling
                    )
                )
            );

            Dictionary<GameObject, GameObject> cloneMap =
                BuildCloneMap(
                    analysisRoot,
                    duplicatedRoot,
                    validObjects
                );

            if (cloneMap.Count !=
                validObjects.Count)
            {
                throw new InvalidOperationException(
                    $"Could map only {cloneMap.Count:N0} of {validObjects.Count:N0} analyzed objects into the duplicated root. " +
                    "The source hierarchy appears to have changed since Analyze; rebuild the analysis plan and try again."
                );
            }

            foreach (ObjectInfo info
                     in validObjects)
            {
                MoveRecord record =
                    CaptureMoveRecord(
                        info.gameObject
                    );

                record.clonedGameObject =
                    cloneMap[
                        info.gameObject
                    ];

                batch.moves.Add(record);
            }

            batch.generatedRoot =
                duplicatedRoot;

            Transform connectedRoot =
                CreateParent(
                    duplicatedRoot.transform,
                    "Connected_Clusters",
                    batch
                ).transform;

            Transform singletonRoot =
                CreateParent(
                    duplicatedRoot.transform,
                    "Singletons",
                    batch
                ).transform;

            Transform excludedRoot =
                CreateParent(
                    duplicatedRoot.transform,
                    "Excluded_From_Connectivity",
                    batch
                ).transform;

            ApplyConnectedStructures(
                connectedRoot,
                batch,
                cloneMap
            );

            ApplySemanticGroups(
                singletonGroups,
                singletonRoot,
                false,
                batch,
                cloneMap
            );

            ApplySemanticGroups(
                excludedGroups,
                excludedRoot,
                false,
                batch,
                cloneMap
            );

            RemoveIfEmpty(
                excludedRoot.gameObject,
                batch
            );

            RemoveIfEmpty(
                singletonRoot.gameObject,
                batch
            );

            RemoveIfEmpty(
                connectedRoot.gameObject,
                batch
            );

            if (pruneEmptyTransformOnlyContainers)
            {
                PruneEmptyTransformOnlyContainers(
                    duplicatedRoot.transform,
                    batch.createdParents
                );
            }

            duplicatedRoot.SetActive(true);

            // Disable only after the duplicate has been created and organized
            // successfully. If anything above fails, the original stays live.
            analysisRoot.SetActive(false);

            EditorUtility.SetDirty(
                analysisRoot
            );

            EditorUtility.SetDirty(
                duplicatedRoot
            );

            batch.auditAssetPath =
                writeJsonAudit
                    ? WriteAudit(batch)
                    : "";

            EditorSceneManager.MarkSceneDirty(
                analysisRoot.scene
            );

            Selection.activeGameObject =
                duplicatedRoot;

            EditorGUIUtility.PingObject(
                duplicatedRoot
            );

            Debug.Log(
                $"Connectivity hierarchy applied: created '{duplicatedRoot.name}', " +
                $"organized {batch.moves.Count:N0} copied object(s) into {structures.Count:N0} named top-level cluster(s), " +
                $"and disabled the untouched original root '{analysisRoot.name}'."
            );
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);

            if (duplicatedRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(
                    duplicatedRoot
                );
            }

            if (analysisRoot != null)
            {
                analysisRoot.SetActive(
                    originalActiveState
                );
            }

            EditorUtility.DisplayDialog(
                "Hierarchy Organizer",
                "Apply failed. The generated copy was removed and the original root was left/restored unchanged.\n\n" +
                ex.Message,
                "OK"
            );
        }

        Repaint();
    }


    private void ApplyConnectedStructures(
        Transform connectedRoot,
        AppliedBatch batch,
        Dictionary<GameObject, GameObject> cloneMap)
    {
        for (int i = 0;
             i < structures.Count;
             i++)
        {
            StructurePlan structure =
                structures[i];

            string clusterName =
                string.IsNullOrWhiteSpace(
                    structure.clusterName)
                    ? $"Cluster_{i:000}"
                    : structure.clusterName;

            GameObject structureParent =
                CreateParent(
                    connectedRoot,
                    clusterName,
                    batch
                );

            ApplySemanticGroups(
                structure.semanticGroups,
                structureParent.transform,
                buildSpatialSections,
                batch,
                cloneMap
            );
        }
    }

    private void ApplySemanticGroups(
        List<SemanticGroupPlan> groups,
        Transform parent,
        bool createSections,
        AppliedBatch batch,
        Dictionary<GameObject, GameObject> cloneMap)
    {
        for (int g = 0;
             g < groups.Count;
             g++)
        {
            SemanticGroupPlan group =
                groups[g];

            string categoryName =
                "Category_" +
                SanitizeHierarchyName(
                    group.key
                );

            GameObject categoryParent =
                CreateParent(
                    parent,
                    categoryName,
                    batch
                );

            if (createSections &&
                group.sections.Count > 1)
            {
                for (int s = 0;
                     s < group.sections.Count;
                     s++)
                {
                    SpatialSectionPlan section =
                        group.sections[s];

                    GameObject sectionParent =
                        CreateParent(
                            categoryParent.transform,
                            $"Section_{s:000}",
                            batch
                        );

                    ReparentObjects(
                        section.objects,
                        sectionParent.transform,
                        cloneMap
                    );
                }
            }
            else
            {
                ReparentObjects(
                    group.objects,
                    categoryParent.transform,
                    cloneMap
                );
            }
        }
    }

    private GameObject CreateParent(
        Transform parent,
        string name,
        AppliedBatch batch)
    {
        GameObject go =
            new GameObject(
                SanitizeHierarchyName(
                    name
                )
            );

        go.transform.SetParent(
            parent,
            false
        );

        go.transform.localPosition =
            Vector3.zero;

        go.transform.localRotation =
            Quaternion.identity;

        go.transform.localScale =
            Vector3.one;

        batch.createdParents.Add(
            go
        );

        return go;
    }

    private static void ReparentObjects(
        List<ObjectInfo> objects,
        Transform newParent,
        Dictionary<GameObject, GameObject> cloneMap)
    {
        foreach (ObjectInfo info
                 in objects)
        {
            if (info == null ||
                info.gameObject == null ||
                !cloneMap.TryGetValue(
                    info.gameObject,
                    out GameObject clone) ||
                clone == null)
            {
                continue;
            }

            clone.transform.SetParent(
                newParent,
                true
            );

            EditorUtility.SetDirty(
                clone
            );
        }
    }

    private static void RemoveIfEmpty(
        GameObject go,
        AppliedBatch batch)
    {
        if (go == null ||
            go.transform.childCount > 0)
        {
            return;
        }

        batch.createdParents.Remove(go);
        UnityEngine.Object.DestroyImmediate(go);
    }

    private static Dictionary<GameObject, GameObject>
        BuildCloneMap(
            GameObject sourceRoot,
            GameObject cloneRoot,
            List<ObjectInfo> sourceObjects)
    {
        Dictionary<GameObject, GameObject> map =
            new Dictionary<GameObject, GameObject>();

        foreach (ObjectInfo info
                 in sourceObjects)
        {
            if (info == null ||
                info.gameObject == null)
            {
                continue;
            }

            List<int> relativePath =
                GetRelativeSiblingPath(
                    sourceRoot.transform,
                    info.gameObject.transform
                );

            Transform cloneTransform =
                ResolveRelativeSiblingPath(
                    cloneRoot.transform,
                    relativePath
                );

            if (cloneTransform != null)
            {
                map[
                    info.gameObject
                ] =
                    cloneTransform.gameObject;
            }
        }

        return map;
    }

    private static List<int>
        GetRelativeSiblingPath(
            Transform root,
            Transform target)
    {
        List<int> reverse =
            new List<int>();

        Transform current =
            target;

        while (current != null &&
               current != root)
        {
            reverse.Add(
                current.GetSiblingIndex()
            );

            current =
                current.parent;
        }

        if (current != root)
        {
            return new List<int>();
        }

        reverse.Reverse();
        return reverse;
    }

    private static Transform
        ResolveRelativeSiblingPath(
            Transform root,
            List<int> path)
    {
        Transform current =
            root;

        foreach (int siblingIndex
                 in path)
        {
            if (current == null ||
                siblingIndex < 0 ||
                siblingIndex >=
                    current.childCount)
            {
                return null;
            }

            current =
                current.GetChild(
                    siblingIndex
                );
        }

        return current;
    }

    private static void
        PruneEmptyTransformOnlyContainers(
            Transform root,
            List<GameObject> protectedGeneratedParents)
    {
        if (root == null)
            return;

        HashSet<GameObject> protectedSet =
            new HashSet<GameObject>(
                protectedGeneratedParents
                    .Where(
                        x => x != null
                    )
            );

        PruneRecursive(
            root,
            root,
            protectedSet
        );
    }

    private static void PruneRecursive(
        Transform current,
        Transform root,
        HashSet<GameObject> protectedSet)
    {
        for (int i = current.childCount - 1;
             i >= 0;
             i--)
        {
            Transform child =
                current.GetChild(i);

            PruneRecursive(
                child,
                root,
                protectedSet
            );
        }

        if (current == root)
            return;

        GameObject go =
            current.gameObject;

        if (protectedSet.Contains(go))
            return;

        if (current.childCount != 0)
            return;

        Component[] components =
            go.GetComponents<Component>();

        bool transformOnly =
            components.All(
                component =>
                    component == null ||
                    component is Transform
            );

        if (!transformOnly)
            return;

        UnityEngine.Object.DestroyImmediate(go);
    }

    private MoveRecord CaptureMoveRecord(
        GameObject go)
    {
        Transform parent =
            go.transform.parent;

        return new MoveRecord
        {
            gameObject =
                go,

            originalParent =
                parent,

            originalSiblingIndex =
                go.transform
                    .GetSiblingIndex(),

            originalDepth =
                GetTransformDepth(
                    go.transform
                ),

            objectGlobalId =
                GetGlobalObjectIdString(
                    go
                ),

            originalParentGlobalId =
                parent != null
                    ? GetGlobalObjectIdString(
                        parent.gameObject
                    )
                    : "",

            originalHierarchyPath =
                GetHierarchyPath(
                    go.transform
                ),

            originalParentHierarchyPath =
                parent != null
                    ? GetHierarchyPath(
                        parent
                    )
                    : ""
        };
    }

    // ============================================================
    // REVERT
    // ============================================================


    // ============================================================
    // AUDIT
    // ============================================================

    private string WriteAudit(
        AppliedBatch batch)
    {
        EnsureAssetFolder(
            AuditBaseFolder
        );

        string assetPath =
            AuditBaseFolder +
            "/" +
            SanitizeFileName(
                batch.batchName
            ) +
            ".json";

        HierarchyAuditLog log =
            new HierarchyAuditLog
            {
                toolVersion =
                    ToolVersion,

                localTimestamp =
                    batch.appliedAt
                        .ToString("O"),

                scenePath =
                    batch.sourceRoot != null
                        ? batch.sourceRoot
                            .scene.path
                        : SceneManager
                            .GetActiveScene()
                            .path,

                analysisRootName =
                    batch.sourceRoot != null
                        ? batch.sourceRoot
                            .name
                        : "",

                analysisRootGlobalId =
                    batch.sourceRoot != null
                        ? GetGlobalObjectIdString(
                            batch.sourceRoot
                        )
                        : "",

                generatedRootName =
                    batch.generatedRoot != null
                        ? batch.generatedRoot
                            .name
                        : "",

                generatedRootGlobalId =
                    batch.generatedRoot != null
                        ? GetGlobalObjectIdString(
                            batch.generatedRoot
                        )
                        : "",

                contactTolerance =
                    contactTolerance,

                enableNearConnections =
                    enableNearConnections,

                nearConnectionDistance =
                    nearConnectionDistance,

                nearConnectionPolicy =
                    nearConnectionPolicy
                        .ToString(),

                nearMinimumOverlapAxes =
                    nearMinimumOverlapAxes,

                excludeLargeObjectsFromConnectivity =
                    excludeLargeObjectsFromConnectivity,

                maxConnectivityObjectExtentRatio =
                    maxConnectivityObjectExtentRatio,

                connectivityIgnoreNameContains =
                    connectivityIgnoreNameContains,

                limitTopLevelClusters =
                    limitTopLevelClusters,

                maxTopLevelClusters =
                    maxTopLevelClusters,

                duplicatedRootApply =
                    true,

                heuristicClusterNames =
                    true,

                addSpatialSuffixToClusterNames =
                    addSpatialSuffixToClusterNames,

                spatialNameCenterBand =
                    spatialNameCenterBand,

                positiveXMeansEast =
                    positiveXMeansEast,

                positiveZMeansNorth =
                    positiveZMeansNorth,

                semanticMode =
                    semanticMode.ToString(),

                semanticTokenCount =
                    semanticTokenCount,

                semanticRegex =
                    semanticRegex,

                buildSpatialSections =
                    buildSpatialSections,

                maxObjectsPerSection =
                    maxObjectsPerSection,

                maxSectionExtent =
                    maxSectionExtent,

                connectedStructures =
                    structures.Count,

                singletonObjects =
                    singletonObjectCount,

                excludedObjects =
                    excludedObjectCount,

                reverted =
                    false
            };

        foreach (MoveRecord move
                 in batch.moves)
        {
            if (move == null)
                continue;

            log.moves.Add(
                new HierarchyAuditRecord
                {
                    objectName =
                        move.gameObject != null
                            ? move.gameObject
                                .name
                            : "",

                    objectGlobalId =
                        move.objectGlobalId,

                    originalHierarchyPath =
                        move.originalHierarchyPath,

                    originalParentHierarchyPath =
                        move.originalParentHierarchyPath,

                    originalParentGlobalId =
                        move.originalParentGlobalId,

                    originalSiblingIndex =
                        move.originalSiblingIndex,

                    newHierarchyPath =
                        move.clonedGameObject != null
                            ? GetHierarchyPath(
                                move.clonedGameObject
                                    .transform
                            )
                            : ""
                }
            );
        }

        File.WriteAllText(
            assetPath,
            JsonUtility.ToJson(
                log,
                true
            )
        );

        AssetDatabase.ImportAsset(
            assetPath
        );

        return assetPath;
    }

    private static void MarkAuditReverted(
        string assetPath)
    {
        if (string.IsNullOrWhiteSpace(
                assetPath))
        {
            return;
        }

        string fullPath =
            Path.GetFullPath(
                assetPath
            );

        if (!File.Exists(fullPath))
            return;

        try
        {
            HierarchyAuditLog log =
                JsonUtility.FromJson<
                    HierarchyAuditLog>(
                    File.ReadAllText(
                        fullPath
                    )
                );

            if (log == null)
                return;

            log.reverted = true;

            log.revertedTimestamp =
                DateTime.Now
                    .ToString("O");

            File.WriteAllText(
                fullPath,
                JsonUtility.ToJson(
                    log,
                    true
                )
            );

            AssetDatabase.ImportAsset(
                assetPath
            );
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "Could not update hierarchy audit log: " +
                ex.Message
            );
        }
    }

    private static void EnsureAssetFolder(
        string folderPath)
    {
        folderPath =
            folderPath
                .Replace(
                    '\\',
                    '/'
                )
                .TrimEnd('/');

        if (AssetDatabase.IsValidFolder(
                folderPath))
        {
            return;
        }

        string[] parts =
            folderPath.Split('/');

        if (parts.Length == 0 ||
            parts[0] != "Assets")
        {
            throw new ArgumentException(
                "Asset folder must begin with Assets/."
            );
        }

        string current =
            "Assets";

        for (int i = 1;
             i < parts.Length;
             i++)
        {
            string next =
                current +
                "/" +
                parts[i];

            if (!AssetDatabase.IsValidFolder(
                    next))
            {
                AssetDatabase.CreateFolder(
                    current,
                    parts[i]
                );
            }

            current = next;
        }
    }

    // ============================================================
    // UTILITIES
    // ============================================================

    private void ScheduleEditorAction(
        Action action)
    {
        if (action == null)
            return;

        EditorApplication.delayCall +=
            () =>
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

        GUIUtility.ExitGUI();
    }

    private static void SelectObjects(
        IEnumerable<ObjectInfo> objects)
    {
        UnityEngine.Object[] selection =
            objects
                .Where(
                    x => x != null &&
                         x.gameObject != null
                )
                .Select(
                    x =>
                        (UnityEngine.Object)
                        x.gameObject
                )
                .Distinct()
                .ToArray();

        Selection.objects =
            selection;

        if (selection.Length > 0)
        {
            EditorGUIUtility.PingObject(
                selection[0]
            );

            SceneView.FrameLastActiveSceneView();
        }
    }

    private static int GetTriangleCount(
        Mesh mesh)
    {
        if (mesh == null)
            return 0;

        long triangles = 0;

        for (int i = 0;
             i < mesh.subMeshCount;
             i++)
        {
            if (mesh.GetTopology(i) !=
                MeshTopology.Triangles)
            {
                continue;
            }

            triangles +=
                (long)mesh.GetIndexCount(i) /
                3;
        }

        return triangles >
               int.MaxValue
            ? int.MaxValue
            : (int)triangles;
    }

    private static Bounds CalculateCombinedBounds(
        List<ObjectInfo> objects)
    {
        if (objects == null ||
            objects.Count == 0)
        {
            return new Bounds(
                Vector3.zero,
                Vector3.zero
            );
        }

        Bounds bounds =
            objects[0].bounds;

        for (int i = 1;
             i < objects.Count;
             i++)
        {
            bounds.Encapsulate(
                objects[i].bounds
            );
        }

        return bounds;
    }

    private static float LargestDimension(
        Vector3 size)
    {
        return Mathf.Max(
            size.x,
            Mathf.Max(
                size.y,
                size.z
            )
        );
    }

    private static string FormatBoundsSize(
        Bounds bounds)
    {
        Vector3 size =
            bounds.size;

        return
            $"{size.x:0.0} × " +
            $"{size.y:0.0} × " +
            $"{size.z:0.0} m";
    }

    private static int GetTransformDepth(
        Transform transform)
    {
        int depth = 0;

        while (transform != null &&
               transform.parent != null)
        {
            depth++;
            transform =
                transform.parent;
        }

        return depth;
    }

    private static string GetHierarchyPath(
        Transform transform)
    {
        if (transform == null)
            return "";

        string path =
            transform.name;

        while (transform.parent != null)
        {
            transform =
                transform.parent;

            path =
                transform.name +
                "/" +
                path;
        }

        return path;
    }

    private static string GetGlobalObjectIdString(
        UnityEngine.Object obj)
    {
        if (obj == null)
            return "";

        try
        {
            return GlobalObjectId
                .GetGlobalObjectIdSlow(
                    obj
                )
                .ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string SanitizeHierarchyName(
        string value)
    {
        if (string.IsNullOrWhiteSpace(
                value))
        {
            return "Group";
        }

        string result =
            Regex.Replace(
                value,
                @"[\/\\:\*\?""<>\|]",
                "_"
            );

        result =
            Regex.Replace(
                result,
                @"\s+",
                "_"
            );

        if (result.Length > 96)
        {
            result =
                result.Substring(
                    0,
                    96
                );
        }

        return result.Trim('_');
    }

    private static string SanitizeFileName(
        string value)
    {
        if (string.IsNullOrWhiteSpace(
                value))
        {
            return "HierarchyCluster";
        }

        foreach (char c
                 in Path.GetInvalidFileNameChars())
        {
            value =
                value.Replace(
                    c,
                    '_'
                );
        }

        return value;
    }

    // ============================================================
    // DATA TYPES
    // ============================================================

    private class ObjectInfo
    {
        public GameObject gameObject;
        public Renderer renderer;
        public Bounds bounds;
        public Vector3 center;
        public int triangles;
        public string meshName;
        public string semanticKey;
        public bool excludedFromConnectivity;
    }

    private class StructurePlan
    {
        public List<ObjectInfo> objects =
            new List<ObjectInfo>();

        public Bounds bounds;
        public int triangles;

        public List<SemanticGroupPlan>
            semanticGroups =
                new List<SemanticGroupPlan>();

        public int connectedComponentCount = 1;
        public string clusterName = "";
        public bool expanded = false;
    }

    private class SemanticGroupPlan
    {
        public string key;

        public List<ObjectInfo> objects =
            new List<ObjectInfo>();

        public Bounds bounds;
        public int triangles;

        public List<SpatialSectionPlan>
            sections =
                new List<SpatialSectionPlan>();

        public bool showSections = false;
    }

    private class SpatialSectionPlan
    {
        public List<ObjectInfo> objects =
            new List<ObjectInfo>();

        public Bounds bounds;
        public int triangles;
    }

    private class AppliedBatch
    {
        public GameObject sourceRoot;
        public GameObject generatedRoot;
        public string batchName;
        public DateTime appliedAt;
        public string auditAssetPath;
        public bool originalRootWasActiveSelf;

        public readonly List<MoveRecord>
            moves =
                new List<MoveRecord>();

        public readonly List<GameObject>
            createdParents =
                new List<GameObject>();
    }

    private class MoveRecord
    {
        public GameObject gameObject;
        public GameObject clonedGameObject;
        public Transform originalParent;
        public int originalSiblingIndex;
        public int originalDepth;

        public string objectGlobalId;
        public string originalParentGlobalId;
        public string originalHierarchyPath;
        public string originalParentHierarchyPath;
    }

    [Serializable]
    private class HierarchyAuditLog
    {
        public string toolVersion;
        public string localTimestamp;
        public string revertedTimestamp;

        public string scenePath;

        public string analysisRootName;
        public string analysisRootGlobalId;

        public string generatedRootName;
        public string generatedRootGlobalId;

        public float contactTolerance;
        public bool enableNearConnections;
        public float nearConnectionDistance;
        public string nearConnectionPolicy;
        public int nearMinimumOverlapAxes;

        public bool excludeLargeObjectsFromConnectivity;
        public float maxConnectivityObjectExtentRatio;
        public string connectivityIgnoreNameContains;

        public bool limitTopLevelClusters;
        public int maxTopLevelClusters;
        public bool duplicatedRootApply;

        public bool heuristicClusterNames;
        public bool addSpatialSuffixToClusterNames;
        public float spatialNameCenterBand;
        public bool positiveXMeansEast;
        public bool positiveZMeansNorth;

        public string semanticMode;
        public int semanticTokenCount;
        public string semanticRegex;

        public bool buildSpatialSections;
        public int maxObjectsPerSection;
        public float maxSectionExtent;

        public int connectedStructures;
        public int singletonObjects;
        public int excludedObjects;

        public bool reverted;

        public List<HierarchyAuditRecord>
            moves =
                new List<HierarchyAuditRecord>();
    }

    [Serializable]
    private class HierarchyAuditRecord
    {
        public string objectName;
        public string objectGlobalId;

        public string originalHierarchyPath;
        public string originalParentHierarchyPath;
        public string originalParentGlobalId;
        public int originalSiblingIndex;

        public string newHierarchyPath;
    }

    private sealed class UnionFind
    {
        private readonly int[] parent;
        private readonly byte[] rank;

        public UnionFind(
            int count)
        {
            parent =
                new int[count];

            rank =
                new byte[count];

            for (int i = 0;
                 i < count;
                 i++)
            {
                parent[i] = i;
            }
        }

        public int Find(
            int value)
        {
            int root =
                value;

            while (parent[root] !=
                   root)
            {
                root =
                    parent[root];
            }

            while (parent[value] !=
                   value)
            {
                int next =
                    parent[value];

                parent[value] =
                    root;

                value =
                    next;
            }

            return root;
        }

        public void Union(
            int a,
            int b)
        {
            int rootA =
                Find(a);

            int rootB =
                Find(b);

            if (rootA ==
                rootB)
            {
                return;
            }

            if (rank[rootA] <
                rank[rootB])
            {
                parent[rootA] =
                    rootB;
            }
            else if (rank[rootA] >
                     rank[rootB])
            {
                parent[rootB] =
                    rootA;
            }
            else
            {
                parent[rootB] =
                    rootA;

                rank[rootA]++;
            }
        }
    }

    private readonly struct Int3 :
        IEquatable<Int3>
    {
        public readonly int x;
        public readonly int y;
        public readonly int z;

        public Int3(
            int x,
            int y,
            int z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public bool Equals(
            Int3 other)
        {
            return x == other.x &&
                   y == other.y &&
                   z == other.z;
        }

        public override bool Equals(
            object obj)
        {
            return obj is Int3 other &&
                   Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;

                hash =
                    hash * 31 + x;

                hash =
                    hash * 31 + y;

                hash =
                    hash * 31 + z;

                return hash;
            }
        }
    }

    private readonly struct ULongPair :
        IEquatable<ULongPair>
    {
        private readonly ulong packed;

        public ULongPair(
            int a,
            int b)
        {
            uint min =
                (uint)Mathf.Min(
                    a,
                    b
                );

            uint max =
                (uint)Mathf.Max(
                    a,
                    b
                );

            packed =
                ((ulong)min << 32) |
                max;
        }

        public bool Equals(
            ULongPair other)
        {
            return packed ==
                   other.packed;
        }

        public override bool Equals(
            object obj)
        {
            return obj is ULongPair other &&
                   Equals(other);
        }

        public override int GetHashCode()
        {
            return packed
                .GetHashCode();
        }
    }
}
