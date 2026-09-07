#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using OrientationMode = RoofFacetDetector.OrientationMode;
using RoofFacet = RoofFacetDetector.Facet;
using FacetProjection = RoofFacetDetector.Projection;

/// <summary>
/// Generates a deterministic, roof-aligned network of low-poly ducts,
/// pipes, supports, vents, junction boxes, and terminal equipment.
///
/// The tool is deliberately an editor generator: the resulting objects are
/// ordinary scene geometry and require no procedural runtime code in WebGL.
/// </summary>
public sealed class RooftopMechanicalGeneratorV3Window : EditorWindow
{
    private enum TerminalType
    {
        AirHandlingUnit,
        FanHousing,
        VentCluster,
        PipeManifold
    }

    private sealed class PlannedSegment
    {
        public int facetIndex;
        public int depth;
        public int index;
        public Vector2 center;
        public Vector2 direction;
        public float length;
        public float width;
        public float height;
        public int layerIndex;
        public float elevation;
        public bool primary;
        public bool addPipeBundle;
        public bool addRiserAtStart;
        public bool addRiserAtEnd;
        public readonly List<Vector2> childStarts =
            new List<Vector2>();

        public Vector2 Start
        {
            get { return center - direction * length * 0.5f; }
        }

        public Vector2 End
        {
            get { return center + direction * length * 0.5f; }
        }
    }

    private sealed class PlannedTerminal
    {
        public int facetIndex;
        public Vector2 position;
        public Vector2 direction;
        public TerminalType type;
        public Vector3 size;
        public float rotationJitter;
        public int layerIndex;
        public float elevation;
        public bool standalone;
    }

    private sealed class FacetPlan
    {
        public int facetIndex;
        public FacetProjection projection;
        public readonly List<PlannedSegment> segments =
            new List<PlannedSegment>();
        public readonly List<PlannedTerminal> terminals =
            new List<PlannedTerminal>();
        public Vector3[] boundaryLines = Array.Empty<Vector3>();
        public Vector3[] segmentLines = Array.Empty<Vector3>();
        public Vector3[] terminalLines = Array.Empty<Vector3>();
    }

    private struct SpineCandidate
    {
        public Vector2 center;
        public Vector2 direction;
        public float laneCoordinate;
        public float length;
        public float score;

        public SpineCandidate(
            Vector2 resolvedCenter,
            Vector2 resolvedDirection,
            float resolvedLaneCoordinate,
            float resolvedLength)
        {
            center = resolvedCenter;
            direction = resolvedDirection.normalized;
            laneCoordinate = resolvedLaneCoordinate;
            length = resolvedLength;
            score = 0f;
        }
    }

    [Header("References")]
    [SerializeField] private GameObject siteRoot;
    [SerializeField] private GameObject buildingRoot;
    [SerializeField] private Transform exclusionRoot;

    [Header("Materials")]
    [SerializeField] private Material ductMaterial;
    [SerializeField] private Material pipeMaterial;
    [SerializeField] private Material equipmentMaterial;
    [SerializeField] private Material supportMaterial;

    [Header("Roof Detection")]
    [SerializeField, Range(0f, 0.95f)]
    private float minimumRelativeHeight = 0.55f;

    [SerializeField, Min(0.1f)]
    private float roofSearchDepth = 6f;

    [SerializeField, Range(1f, 80f)]
    private float maximumRoofSlope = 55f;

    [SerializeField, Range(0.1f, 20f)]
    private float normalMergeTolerance = 4f;

    [SerializeField, Min(0.001f)]
    private float planeMergeTolerance = 0.18f;

    [SerializeField, Min(0.001f)]
    private float minimumTriangleArea = 0.02f;

    [SerializeField, Min(0.01f)]
    private float minimumFacetArea = 4f;

    [Header("Roof Orientation")]
    [SerializeField]
    private OrientationMode orientationMode =
        OrientationMode.AutoBestFit;

    [SerializeField]
    private float orientationOffset;

    [SerializeField]
    private bool snapPrincipalAxisToBoundary = true;

    [SerializeField, Range(0f, 45f)]
    private float boundarySnapTolerance = 14f;

    [SerializeField, Range(0f, 1f)]
    private float pcaAmbiguityThreshold = 0.08f;

    [Header("Network")]
    [SerializeField] private int seed = 3817;

    [SerializeField, Range(1, 4)]
    private int primarySpineCount = 1;

    [SerializeField, Range(0, 4)]
    private int maximumRecursionDepth = 2;

    [SerializeField, Range(0f, 1f)]
    private float branchProbability = 0.48f;

    [SerializeField, Min(1f)]
    private float branchSpacing = 7f;

    [SerializeField, Range(0f, 25f)]
    private float branchPositionJitter = 1.2f;

    [SerializeField, Range(45f, 135f)]
    private float branchAngle = 90f;

    [SerializeField, Range(0f, 20f)]
    private float branchAngleJitter = 3f;

    [SerializeField, Min(0.5f)]
    private float minimumBranchLength = 3f;

    [SerializeField, Range(0.2f, 0.95f)]
    private float lengthDecay = 0.62f;

    [SerializeField, Range(0.2f, 0.95f)]
    private float widthDecay = 0.68f;

    [SerializeField, Min(0.1f)]
    private float primaryDuctWidth = 1.25f;

    [SerializeField, Min(0.1f)]
    private float primaryDuctHeight = 0.9f;

    [SerializeField, Min(0f)]
    private float roofEdgePadding = 1f;

    [SerializeField, Min(0f)]
    private float roofClearance = 0.12f;

    [SerializeField, Min(0f)]
    private float objectSeparation = 0.18f;

    [Header("Coverage and Layering")]
    [Tooltip(
        "Number of independent roof-network passes. Additional passes may " +
        "use a perpendicular direction and a higher elevation."
    )]
    [SerializeField, Range(1, 5)]
    private int networkLayerCount = 3;

    [SerializeField]
    private bool alternateLayerDirection = true;

    [Tooltip(
        "Higher values prefer lanes farther away from routes already " +
        "planned, spreading the network across more of the roof."
    )]
    [SerializeField, Range(0f, 1f)]
    private float coverageSpread = 0.72f;

    [SerializeField, Min(0.05f)]
    private float layerHeightStep = 1.35f;

    [SerializeField, Min(0f)]
    private float layerHeightJitter = 0.16f;

    [SerializeField]
    private bool allowElevatedCrossings = true;

    [SerializeField, Min(0f)]
    private float elevatedCrossingClearance = 0.18f;

    [Tooltip(
        "Attempts shorter parallel companion routes beside primary trunks."
    )]
    [SerializeField, Range(0f, 1f)]
    private float parallelRouteRate = 0.58f;

    [SerializeField, Range(0, 3)]
    private int maximumParallelRoutes = 2;

    [SerializeField]
    private Vector2 parallelOffsetRange = new Vector2(1.0f, 2.6f);

    [SerializeField, Range(0.2f, 1f)]
    private float parallelWidthScale = 0.58f;

    [SerializeField, Range(0.5f, 1.5f)]
    private float branchReachMultiplier = 1.0f;

    [SerializeField, Range(0f, 1f)]
    private float elevatedRiserRate = 0.62f;

    [Header("Equipment")]
    [SerializeField, Range(0f, 1f)]
    private float terminalEquipmentRate = 0.72f;

    [SerializeField, Range(0f, 1f)]
    private float junctionBoxRate = 0.62f;

    [SerializeField, Range(0f, 1f)]
    private float pipeBundleRate = 0.38f;

    [SerializeField, Range(0f, 1f)]
    private float ventRate = 0.22f;

    [SerializeField, Range(1, 6)]
    private int minimumPipeCount = 2;

    [SerializeField, Range(1, 8)]
    private int maximumPipeCount = 4;

    [SerializeField, Min(0.02f)]
    private float pipeRadius = 0.12f;

    [SerializeField, Min(0.02f)]
    private float pipeSpacing = 0.18f;

    [SerializeField] private Vector2 equipmentLengthRange =
        new Vector2(2.4f, 5.2f);

    [SerializeField] private Vector2 equipmentWidthRange =
        new Vector2(1.6f, 3.4f);

    [SerializeField] private Vector2 equipmentHeightRange =
        new Vector2(0.9f, 2.1f);

    [Header("Distributed Equipment")]
    [Tooltip(
        "Number of additional equipment clusters placed over usable roof " +
        "areas, independent of branch endpoints."
    )]
    [SerializeField, Range(0, 24)]
    private int equipmentClusterCount = 7;

    [SerializeField]
    private Vector2Int equipmentPerClusterRange =
        new Vector2Int(2, 5);

    [SerializeField, Min(0.25f)]
    private float equipmentClusterRadius = 3.2f;

    [SerializeField, Min(0f)]
    private float equipmentScatterPadding = 0.7f;

    [SerializeField, Range(0f, 1f)]
    private float raisedEquipmentRate = 0.28f;

    [Header("Supports")]
    [SerializeField] private bool generateSupports = true;

    [SerializeField, Min(0.5f)]
    private float supportSpacing = 3f;

    [SerializeField, Min(0.02f)]
    private float supportThickness = 0.12f;

    [SerializeField, Min(0.02f)]
    private float supportHeight = 0.18f;

    [Header("Output")]
    [SerializeField] private string outputRootName =
        "rooftop_mechanical";

    [SerializeField] private string buildingFolderName = "";
    [SerializeField] private bool replaceExisting = true;
    [SerializeField] private bool markGeneratedStatic = true;
    [SerializeField] private bool removePrimitiveColliders = true;

    [Header("Preview")]
    [SerializeField] private bool showDetectedFacets = true;
    [SerializeField] private bool showFacetLabels = true;
    [SerializeField] private bool showNetworkPreview = true;
    [SerializeField] private bool showTerminalPreview = true;
    [SerializeField] private bool autoRebuildPreview = false;

    private readonly List<RoofFacet> facets =
        new List<RoofFacet>();

    private readonly List<FacetPlan> plans =
        new List<FacetPlan>();

    private readonly List<Bounds> exclusionBounds =
        new List<Bounds>();

    private Vector2 scroll;
    private Bounds buildingBounds;
    private int scannedTriangles;
    private int acceptedTriangles;
    private string status =
        "Assign a building and detect its roof facets.";
    private string previewStatus = "No procedural plan has been built.";
    private bool previewDirty = true;

    [MenuItem("Tools/IFC Dashboard/Rooftop Mechanical Generator V3")]
    public static void Open()
    {
        RooftopMechanicalGeneratorV3Window window =
            GetWindow<RooftopMechanicalGeneratorV3Window>(
                "Rooftop Mechanical"
            );
        window.minSize = new Vector2(420f, 700f);
        window.Show();
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += DrawScenePreview;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= DrawScenePreview;
    }

    private void OnGUI()
    {
        EditorGUI.BeginChangeCheck();

        scroll = EditorGUILayout.BeginScrollView(scroll);

        DrawReferences();
        EditorGUILayout.Space(6f);
        DrawDetection();
        EditorGUILayout.Space(6f);
        DrawFacets();
        EditorGUILayout.Space(6f);
        DrawNetwork();
        EditorGUILayout.Space(6f);
        DrawEquipment();
        EditorGUILayout.Space(6f);
        DrawOutput();
        EditorGUILayout.Space(8f);
        DrawActions();

        EditorGUILayout.EndScrollView();

        if (EditorGUI.EndChangeCheck())
        {
            previewDirty = true;
            if (autoRebuildPreview && facets.Count > 0)
            {
                BuildPreviewPlan();
            }
            SceneView.RepaintAll();
        }
    }

    private void DrawReferences()
    {
        EditorGUILayout.LabelField(
            "1. References",
            EditorStyles.boldLabel
        );

        siteRoot = (GameObject)EditorGUILayout.ObjectField(
            "Site Root",
            siteRoot,
            typeof(GameObject),
            true
        );

        buildingRoot = (GameObject)EditorGUILayout.ObjectField(
            "Building Root",
            buildingRoot,
            typeof(GameObject),
            true
        );

        exclusionRoot = (Transform)EditorGUILayout.ObjectField(
            new GUIContent(
                "Exclusion Root",
                "Optional hierarchy such as roof_panels. Generated " +
                "mechanical geometry avoids its renderer bounds."
            ),
            exclusionRoot,
            typeof(Transform),
            true
        );

        ductMaterial = (Material)EditorGUILayout.ObjectField(
            "Duct Material",
            ductMaterial,
            typeof(Material),
            false
        );
        pipeMaterial = (Material)EditorGUILayout.ObjectField(
            "Pipe Material",
            pipeMaterial,
            typeof(Material),
            false
        );
        equipmentMaterial = (Material)EditorGUILayout.ObjectField(
            "Equipment Material",
            equipmentMaterial,
            typeof(Material),
            false
        );
        supportMaterial = (Material)EditorGUILayout.ObjectField(
            "Support Material",
            supportMaterial,
            typeof(Material),
            false
        );

        if (
            buildingRoot != null &&
            siteRoot == null &&
            GUILayout.Button("Use Building Scene Root As Site Root")
        )
        {
            siteRoot = FindLikelySiteRoot(buildingRoot.transform);
        }
    }

    private void DrawDetection()
    {
        EditorGUILayout.LabelField(
            "2. Roof Detection",
            EditorStyles.boldLabel
        );

        minimumRelativeHeight = EditorGUILayout.Slider(
            "Minimum Relative Height",
            minimumRelativeHeight,
            0f,
            0.95f
        );
        roofSearchDepth = EditorGUILayout.FloatField(
            "Roof Search Depth",
            roofSearchDepth
        );
        maximumRoofSlope = EditorGUILayout.Slider(
            "Maximum Roof Slope",
            maximumRoofSlope,
            1f,
            80f
        );
        normalMergeTolerance = EditorGUILayout.Slider(
            "Normal Merge Tolerance",
            normalMergeTolerance,
            0.1f,
            20f
        );
        planeMergeTolerance = EditorGUILayout.FloatField(
            "Plane Merge Tolerance",
            planeMergeTolerance
        );
        minimumTriangleArea = EditorGUILayout.FloatField(
            "Minimum Triangle Area",
            minimumTriangleArea
        );
        minimumFacetArea = EditorGUILayout.FloatField(
            "Minimum Facet Area",
            minimumFacetArea
        );

        orientationMode =
            (OrientationMode)EditorGUILayout.EnumPopup(
                "Orientation Mode",
                orientationMode
            );
        orientationOffset = EditorGUILayout.Slider(
            "Orientation Offset",
            orientationOffset,
            -180f,
            180f
        );
        snapPrincipalAxisToBoundary = EditorGUILayout.Toggle(
            "Snap PCA To Roof Edge",
            snapPrincipalAxisToBoundary
        );

        if (snapPrincipalAxisToBoundary)
        {
            boundarySnapTolerance = EditorGUILayout.Slider(
                "Boundary Snap Tolerance",
                boundarySnapTolerance,
                0f,
                45f
            );
        }

        pcaAmbiguityThreshold = EditorGUILayout.Slider(
            "PCA Ambiguity Threshold",
            pcaAmbiguityThreshold,
            0f,
            1f
        );

        if (GUILayout.Button("Detect Roof Facets"))
        {
            DetectRoofs();
        }

        EditorGUILayout.HelpBox(status, MessageType.Info);
    }

    private void DrawFacets()
    {
        EditorGUILayout.LabelField(
            "3. Detected Roof Facets",
            EditorStyles.boldLabel
        );

        showDetectedFacets = EditorGUILayout.Toggle(
            "Show Detected Facets",
            showDetectedFacets
        );
        showFacetLabels = EditorGUILayout.Toggle(
            "Show Facet Labels",
            showFacetLabels
        );

        if (facets.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "Run roof detection first.",
                MessageType.None
            );
            return;
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Enable All"))
        {
            foreach (RoofFacet facet in facets)
            {
                facet.enabled = true;
            }
            previewDirty = true;
        }
        if (GUILayout.Button("Disable All"))
        {
            foreach (RoofFacet facet in facets)
            {
                facet.enabled = false;
            }
            previewDirty = true;
        }
        EditorGUILayout.EndHorizontal();

        for (int index = 0; index < facets.Count; index++)
        {
            RoofFacet facet = facets[index];
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            facet.enabled = EditorGUILayout.ToggleLeft(
                string.Format(
                    "Facet {0:00}    Area {1:F1} m²    Slope {2:F1}°",
                    index,
                    facet.area,
                    facet.Slope
                ),
                facet.enabled
            );
            EditorGUILayout.LabelField(
                string.Format(
                    "Height {0:F2}–{1:F2}   PCA confidence {2:F2}",
                    facet.minY,
                    facet.maxY,
                    facet.orientationConfidence
                ),
                EditorStyles.miniLabel
            );
            EditorGUILayout.EndVertical();
        }
    }

    private void DrawNetwork()
    {
        EditorGUILayout.LabelField(
            "4. Recursive Network",
            EditorStyles.boldLabel
        );

        seed = EditorGUILayout.IntField("Seed", seed);
        primarySpineCount = EditorGUILayout.IntSlider(
            "Primary Spines Per Layer",
            primarySpineCount,
            1,
            8
        );
        maximumRecursionDepth = EditorGUILayout.IntSlider(
            "Maximum Recursion Depth",
            maximumRecursionDepth,
            0,
            5
        );
        branchProbability = EditorGUILayout.Slider(
            "Branch Probability",
            branchProbability,
            0f,
            1f
        );
        branchSpacing = EditorGUILayout.FloatField(
            "Branch Spacing",
            branchSpacing
        );
        branchPositionJitter = EditorGUILayout.Slider(
            "Branch Position Jitter",
            branchPositionJitter,
            0f,
            5f
        );
        branchAngle = EditorGUILayout.Slider(
            "Branch Angle",
            branchAngle,
            45f,
            135f
        );
        branchAngleJitter = EditorGUILayout.Slider(
            "Branch Angle Jitter",
            branchAngleJitter,
            0f,
            20f
        );
        minimumBranchLength = EditorGUILayout.FloatField(
            "Minimum Branch Length",
            minimumBranchLength
        );
        lengthDecay = EditorGUILayout.Slider(
            "Length Decay",
            lengthDecay,
            0.2f,
            0.95f
        );
        widthDecay = EditorGUILayout.Slider(
            "Width Decay",
            widthDecay,
            0.2f,
            0.95f
        );
        branchReachMultiplier = EditorGUILayout.Slider(
            "Branch Reach Multiplier",
            branchReachMultiplier,
            0.5f,
            1.5f
        );
        primaryDuctWidth = EditorGUILayout.FloatField(
            "Primary Duct Width",
            primaryDuctWidth
        );
        primaryDuctHeight = EditorGUILayout.FloatField(
            "Primary Duct Height",
            primaryDuctHeight
        );
        roofEdgePadding = EditorGUILayout.FloatField(
            "Roof Edge Padding",
            roofEdgePadding
        );
        roofClearance = EditorGUILayout.FloatField(
            "Roof Clearance",
            roofClearance
        );
        objectSeparation = EditorGUILayout.FloatField(
            "Object Separation",
            objectSeparation
        );

        EditorGUILayout.Space(5f);
        EditorGUILayout.LabelField(
            "Coverage and Layering",
            EditorStyles.miniBoldLabel
        );

        networkLayerCount = EditorGUILayout.IntSlider(
            "Network Layer Count",
            networkLayerCount,
            1,
            5
        );
        alternateLayerDirection = EditorGUILayout.Toggle(
            "Alternate Layer Direction",
            alternateLayerDirection
        );
        coverageSpread = EditorGUILayout.Slider(
            "Roof Coverage Spread",
            coverageSpread,
            0f,
            1f
        );
        layerHeightStep = EditorGUILayout.FloatField(
            "Layer Height Step",
            layerHeightStep
        );
        layerHeightJitter = EditorGUILayout.FloatField(
            "Layer Height Jitter",
            layerHeightJitter
        );
        allowElevatedCrossings = EditorGUILayout.Toggle(
            "Allow Elevated Crossings",
            allowElevatedCrossings
        );

        if (allowElevatedCrossings)
        {
            elevatedCrossingClearance =
                EditorGUILayout.FloatField(
                    "Crossing Clearance",
                    elevatedCrossingClearance
                );
        }

        parallelRouteRate = EditorGUILayout.Slider(
            "Parallel Route Rate",
            parallelRouteRate,
            0f,
            1f
        );

        if (parallelRouteRate > 0f)
        {
            maximumParallelRoutes = EditorGUILayout.IntSlider(
                "Maximum Parallel Routes",
                maximumParallelRoutes,
                0,
                3
            );
            parallelOffsetRange = EditorGUILayout.Vector2Field(
                "Parallel Offset Range",
                parallelOffsetRange
            );
            parallelWidthScale = EditorGUILayout.Slider(
                "Parallel Width Scale",
                parallelWidthScale,
                0.2f,
                1f
            );
        }

        elevatedRiserRate = EditorGUILayout.Slider(
            "Elevated Riser Rate",
            elevatedRiserRate,
            0f,
            1f
        );

        EditorGUILayout.HelpBox(
            "Multiple layers alternate between the two roof axes, can " +
            "cross at separate heights, and deliberately prefer unused " +
            "roof regions. This creates the denser intertwined service " +
            "corridors visible in the reference imagery.",
            MessageType.None
        );

        if (
            GUILayout.Button(
                "Apply Dense Layered Reference Preset"
            )
        )
        {
            ApplyDenseLayeredPreset();
        }
    }

    private void ApplyDenseLayeredPreset()
    {
        primarySpineCount = 5;
        maximumRecursionDepth = 3;
        branchProbability = 0.78f;
        branchSpacing = 4.0f;
        branchPositionJitter = 1.0f;
        branchAngle = 90f;
        branchAngleJitter = 5f;
        minimumBranchLength = 1.7f;
        lengthDecay = 0.74f;
        widthDecay = 0.72f;
        branchReachMultiplier = 1.08f;
        primaryDuctWidth = 0.95f;
        primaryDuctHeight = 0.72f;
        roofEdgePadding = 0.65f;
        objectSeparation = 0.1f;

        networkLayerCount = 3;
        alternateLayerDirection = true;
        coverageSpread = 0.78f;
        layerHeightStep = 1.35f;
        layerHeightJitter = 0.14f;
        allowElevatedCrossings = true;
        elevatedCrossingClearance = 0.16f;
        parallelRouteRate = 0.72f;
        maximumParallelRoutes = 2;
        parallelOffsetRange =
            new Vector2(0.9f, 2.4f);
        parallelWidthScale = 0.55f;
        elevatedRiserRate = 0.68f;

        terminalEquipmentRate = 0.88f;
        junctionBoxRate = 0.82f;
        pipeBundleRate = 0.72f;
        ventRate = 0.48f;
        minimumPipeCount = 3;
        maximumPipeCount = 7;

        equipmentClusterCount = 9;
        equipmentPerClusterRange =
            new Vector2Int(2, 5);
        equipmentClusterRadius = 3.4f;
        equipmentScatterPadding = 0.65f;
        raisedEquipmentRate = 0.24f;

        previewDirty = true;
    }

    private void DrawEquipment()
    {
        EditorGUILayout.LabelField(
            "5. Equipment and Dressing",
            EditorStyles.boldLabel
        );

        terminalEquipmentRate = EditorGUILayout.Slider(
            "Terminal Equipment Rate",
            terminalEquipmentRate,
            0f,
            1f
        );
        junctionBoxRate = EditorGUILayout.Slider(
            "Junction Box Rate",
            junctionBoxRate,
            0f,
            1f
        );
        pipeBundleRate = EditorGUILayout.Slider(
            "Pipe Bundle Rate",
            pipeBundleRate,
            0f,
            1f
        );
        ventRate = EditorGUILayout.Slider(
            "Vent Rate",
            ventRate,
            0f,
            1f
        );
        minimumPipeCount = EditorGUILayout.IntSlider(
            "Minimum Pipe Count",
            minimumPipeCount,
            1,
            8
        );
        maximumPipeCount = EditorGUILayout.IntSlider(
            "Maximum Pipe Count",
            maximumPipeCount,
            minimumPipeCount,
            12
        );
        pipeRadius = EditorGUILayout.FloatField(
            "Pipe Radius",
            pipeRadius
        );
        pipeSpacing = EditorGUILayout.FloatField(
            "Pipe Spacing",
            pipeSpacing
        );
        equipmentLengthRange = EditorGUILayout.Vector2Field(
            "Equipment Length Range",
            equipmentLengthRange
        );
        equipmentWidthRange = EditorGUILayout.Vector2Field(
            "Equipment Width Range",
            equipmentWidthRange
        );
        equipmentHeightRange = EditorGUILayout.Vector2Field(
            "Equipment Height Range",
            equipmentHeightRange
        );

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField(
            "Distributed Roof Equipment",
            EditorStyles.miniBoldLabel
        );

        equipmentClusterCount = EditorGUILayout.IntSlider(
            "Equipment Cluster Count",
            equipmentClusterCount,
            0,
            24
        );

        if (equipmentClusterCount > 0)
        {
            equipmentPerClusterRange =
                EditorGUILayout.Vector2IntField(
                    "Equipment Per Cluster",
                    equipmentPerClusterRange
                );
            equipmentClusterRadius =
                EditorGUILayout.FloatField(
                    "Equipment Cluster Radius",
                    equipmentClusterRadius
                );
            equipmentScatterPadding =
                EditorGUILayout.FloatField(
                    "Equipment Scatter Padding",
                    equipmentScatterPadding
                );
            raisedEquipmentRate = EditorGUILayout.Slider(
                "Raised Equipment Rate",
                raisedEquipmentRate,
                0f,
                1f
            );
        }

        generateSupports = EditorGUILayout.Toggle(
            "Generate Supports",
            generateSupports
        );

        if (generateSupports)
        {
            supportSpacing = EditorGUILayout.FloatField(
                "Support Spacing",
                supportSpacing
            );
            supportThickness = EditorGUILayout.FloatField(
                "Support Thickness",
                supportThickness
            );
            supportHeight = EditorGUILayout.FloatField(
                "Support Height",
                supportHeight
            );
        }
    }

    private void DrawOutput()
    {
        EditorGUILayout.LabelField(
            "6. Output and Preview",
            EditorStyles.boldLabel
        );

        outputRootName = EditorGUILayout.TextField(
            "Output Root Name",
            outputRootName
        );
        buildingFolderName = EditorGUILayout.TextField(
            "Building Folder Name",
            buildingFolderName
        );
        replaceExisting = EditorGUILayout.Toggle(
            "Replace Existing",
            replaceExisting
        );
        markGeneratedStatic = EditorGUILayout.Toggle(
            "Mark Generated Static",
            markGeneratedStatic
        );
        removePrimitiveColliders = EditorGUILayout.Toggle(
            "Remove Primitive Colliders",
            removePrimitiveColliders
        );

        showNetworkPreview = EditorGUILayout.Toggle(
            "Show Network Preview",
            showNetworkPreview
        );
        showTerminalPreview = EditorGUILayout.Toggle(
            "Show Terminal Preview",
            showTerminalPreview
        );
        autoRebuildPreview = EditorGUILayout.Toggle(
            "Auto Rebuild Preview",
            autoRebuildPreview
        );

        EditorGUILayout.HelpBox(previewStatus, MessageType.None);
    }

    private void DrawActions()
    {
        GUI.enabled = facets.Count > 0;

        if (GUILayout.Button("Build / Refresh Preview", GUILayout.Height(28f)))
        {
            BuildPreviewPlan();
        }

        GUI.backgroundColor = new Color(0.45f, 0.85f, 0.45f);
        if (GUILayout.Button("Generate Rooftop Mechanical", GUILayout.Height(34f)))
        {
            GenerateMechanicalGeometry();
        }
        GUI.backgroundColor = Color.white;

        GUI.enabled = true;

        if (GUILayout.Button("Clear Generated Mechanical"))
        {
            ClearGeneratedMechanical();
        }
    }

    private void DetectRoofs()
    {
        facets.Clear();
        plans.Clear();

        RoofFacetDetector.DetectionSettings settings =
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
            RoofFacetDetector.Detect(buildingRoot, settings);

        buildingBounds = result.buildingBounds;
        scannedTriangles = result.scannedTriangles;
        acceptedTriangles = result.acceptedTriangles;
        status = result.status;
        facets.AddRange(result.facets);
        previewDirty = true;

        if (result.succeeded)
        {
            BuildPreviewPlan();
            LogDetectedFacets();
        }
        else
        {
            previewStatus = "No procedural plan is available.";
        }

        SceneView.RepaintAll();
        Repaint();
    }

    private void BuildPreviewPlan()
    {
        plans.Clear();
        RebuildExclusionBounds();

        int totalSegments = 0;
        int totalTerminals = 0;

        for (int facetIndex = 0;
            facetIndex < facets.Count;
            facetIndex++)
        {
            RoofFacet facet = facets[facetIndex];
            if (!facet.enabled)
            {
                continue;
            }

            FacetProjection projection =
                ResolveFacetProjection(facet);

            FacetPlan plan = new FacetPlan
            {
                facetIndex = facetIndex,
                projection = projection
            };

            BuildFacetPlan(plan, facet);
            BuildPreviewLines(plan, facet);

            if (plan.segments.Count > 0)
            {
                plans.Add(plan);
                totalSegments += plan.segments.Count;
                totalTerminals += plan.terminals.Count;
            }
        }

        previewDirty = false;
        previewStatus = string.Format(
            "Planned {0} duct segments and {1} terminal units " +
            "across {2} roof facets.",
            totalSegments,
            totalTerminals,
            plans.Count
        );

        SceneView.RepaintAll();
        Repaint();
    }

    private void BuildFacetPlan(
        FacetPlan plan,
        RoofFacet facet)
    {
        FacetProjection projection = plan.projection;
        Vector2 size = projection.max - projection.min;

        if (
            Mathf.Max(size.x, size.y) <
            minimumBranchLength + roofEdgePadding * 2f
        )
        {
            return;
        }

        System.Random random = new System.Random(
            CombineSeed(seed, plan.facetIndex)
        );

        for (int layerIndex = 0;
            layerIndex < networkLayerCount;
            layerIndex++)
        {
            bool vertical =
                alternateLayerDirection &&
                (layerIndex % 2 == 1);

            List<SpineCandidate> candidates =
                FindSpineCandidates(
                    projection,
                    primaryDuctWidth + objectSeparation,
                    vertical
                );

            if (candidates.Count == 0)
            {
                continue;
            }

            ScoreSpineCandidatesForCoverage(
                candidates,
                plan,
                projection,
                random
            );

            candidates.Sort(
                (left, right) =>
                    right.score.CompareTo(left.score)
            );

            int requestedSpines = Mathf.Clamp(
                primarySpineCount,
                1,
                candidates.Count
            );

            float minimumLaneSeparation = Mathf.Max(
                primaryDuctWidth * 1.65f,
                objectSeparation + primaryDuctWidth
            );

            List<SpineCandidate> selectedCandidates =
                new List<SpineCandidate>();

            foreach (SpineCandidate candidate in candidates)
            {
                bool tooClose = false;

                foreach (SpineCandidate selected in selectedCandidates)
                {
                    if (
                        Mathf.Abs(
                            candidate.laneCoordinate -
                            selected.laneCoordinate
                        ) < minimumLaneSeparation
                    )
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (tooClose)
                {
                    continue;
                }

                selectedCandidates.Add(candidate);

                if (
                    selectedCandidates.Count >=
                    requestedSpines
                )
                {
                    break;
                }
            }

            if (selectedCandidates.Count == 0)
            {
                selectedCandidates.Add(candidates[0]);
            }

            float layerElevation =
                layerIndex <= 0
                    ? 0f
                    : Mathf.Max(
                        0f,
                        layerIndex * layerHeightStep +
                        Range(
                            random,
                            -layerHeightJitter,
                            layerHeightJitter
                        )
                    );

            foreach (
                SpineCandidate candidate
                in selectedCandidates)
            {
                float randomInset = Range(
                    random,
                    0f,
                    Mathf.Min(candidate.length * 0.1f, 3f)
                );

                PlannedSegment primary =
                    new PlannedSegment
                    {
                        facetIndex = plan.facetIndex,
                        depth = 0,
                        index = plan.segments.Count,
                        center = candidate.center,
                        direction = candidate.direction,
                        length = Mathf.Max(
                            minimumBranchLength,
                            candidate.length - randomInset
                        ),
                        width = primaryDuctWidth,
                        height = primaryDuctHeight,
                        layerIndex = layerIndex,
                        elevation = layerElevation,
                        primary = true,
                        addPipeBundle =
                            random.NextDouble() <
                            pipeBundleRate,
                        addRiserAtStart =
                            layerElevation > 0.01f &&
                            random.NextDouble() <
                            elevatedRiserRate,
                        addRiserAtEnd =
                            layerElevation > 0.01f &&
                            random.NextDouble() <
                            elevatedRiserRate
                    };

                if (!TryFitPrimarySegment(plan, primary))
                {
                    continue;
                }

                primary.index = plan.segments.Count;
                plan.segments.Add(primary);

                TryAddParallelRoutes(
                    plan,
                    primary,
                    random
                );

                BuildBranches(
                    plan,
                    primary,
                    random,
                    1
                );

                if (
                    random.NextDouble() <
                    terminalEquipmentRate
                )
                {
                    TryAddTerminal(
                        plan,
                        primary.End,
                        primary.direction,
                        random,
                        primary.layerIndex,
                        primary.elevation,
                        false
                    );
                }

                if (
                    random.NextDouble() <
                    terminalEquipmentRate * 0.55f
                )
                {
                    TryAddTerminal(
                        plan,
                        primary.Start,
                        -primary.direction,
                        random,
                        primary.layerIndex,
                        primary.elevation,
                        false
                    );
                }
            }
        }

        BuildDistributedEquipmentClusters(
            plan,
            random
        );
    }

    private List<SpineCandidate> FindSpineCandidates(
        FacetProjection projection,
        float requiredWidth,
        bool vertical)
    {
        List<SpineCandidate> result =
            new List<SpineCandidate>();

        float halfRequiredWidth =
            Mathf.Max(requiredWidth * 0.5f, 0.05f);

        float minimumLane =
            (
                vertical
                    ? projection.min.x
                    : projection.min.y
            )
            + roofEdgePadding
            + halfRequiredWidth;

        float maximumLane =
            (
                vertical
                    ? projection.max.x
                    : projection.max.y
            )
            - roofEdgePadding
            - halfRequiredWidth;

        if (maximumLane < minimumLane)
        {
            return result;
        }

        float laneStep = Mathf.Max(
            0.3f,
            requiredWidth * 0.42f
        );

        for (float lane = minimumLane;
            lane <= maximumLane + laneStep * 0.25f;
            lane += laneStep)
        {
            float sampledLane =
                Mathf.Min(lane, maximumLane);

            float runCenter;
            float runLength;

            bool found =
                vertical
                    ? TryFindLongestVerticalRun(
                        projection,
                        sampledLane,
                        requiredWidth,
                        out runCenter,
                        out runLength
                    )
                    : TryFindLongestHorizontalRun(
                        projection,
                        sampledLane,
                        requiredWidth,
                        out runCenter,
                        out runLength
                    );

            if (
                !found ||
                runLength < minimumBranchLength
            )
            {
                continue;
            }

            result.Add(
                vertical
                    ? new SpineCandidate(
                        new Vector2(
                            sampledLane,
                            runCenter
                        ),
                        Vector2.up,
                        sampledLane,
                        runLength
                    )
                    : new SpineCandidate(
                        new Vector2(
                            runCenter,
                            sampledLane
                        ),
                        Vector2.right,
                        sampledLane,
                        runLength
                    )
            );
        }

        return result;
    }

    private void ScoreSpineCandidatesForCoverage(
        List<SpineCandidate> candidates,
        FacetPlan plan,
        FacetProjection projection,
        System.Random random)
    {
        float diagonal = Mathf.Max(
            0.01f,
            (projection.max - projection.min).magnitude
        );

        for (int index = 0;
            index < candidates.Count;
            index++)
        {
            SpineCandidate candidate = candidates[index];

            float normalizedLength =
                Mathf.Clamp01(
                    candidate.length / diagonal
                );

            float distanceScore = 0f;

            if (plan.segments.Count > 0)
            {
                float minimumDistance =
                    float.PositiveInfinity;

                foreach (
                    PlannedSegment existing
                    in plan.segments)
                {
                    if (!existing.primary)
                    {
                        continue;
                    }

                    minimumDistance = Mathf.Min(
                        minimumDistance,
                        Vector2.Distance(
                            candidate.center,
                            existing.center
                        )
                    );
                }

                if (!float.IsPositiveInfinity(minimumDistance))
                {
                    distanceScore =
                        Mathf.Clamp01(
                            minimumDistance / diagonal
                        );
                }
            }

            candidate.score =
                normalizedLength *
                (1f - coverageSpread) +
                distanceScore *
                coverageSpread +
                Range(random, 0f, 0.06f);

            candidates[index] = candidate;
        }
    }

    private bool TryFitPrimarySegment(
        FacetPlan plan,
        PlannedSegment segment)
    {
        float originalLength = segment.length;
        float shrinkStep = Mathf.Max(
            0.25f,
            segment.width * 0.3f
        );

        for (float candidateLength = originalLength;
            candidateLength >= minimumBranchLength;
            candidateLength -= shrinkStep)
        {
            segment.length = candidateLength;

            if (CanPlaceSegment(plan, segment))
            {
                return true;
            }
        }

        segment.length = originalLength;
        return false;
    }

    private void TryAddParallelRoutes(
        FacetPlan plan,
        PlannedSegment primary,
        System.Random random)
    {
        if (
            maximumParallelRoutes <= 0 ||
            random.NextDouble() > parallelRouteRate
        )
        {
            return;
        }

        int requested = random.Next(
            1,
            maximumParallelRoutes + 1
        );

        Vector2 side = new Vector2(
            -primary.direction.y,
            primary.direction.x
        );

        for (int route = 0;
            route < requested;
            route++)
        {
            float minimumOffset = Mathf.Max(
                0.1f,
                Mathf.Min(
                    Mathf.Abs(parallelOffsetRange.x),
                    Mathf.Abs(parallelOffsetRange.y)
                )
            );

            float maximumOffset = Mathf.Max(
                minimumOffset,
                Mathf.Max(
                    Mathf.Abs(parallelOffsetRange.x),
                    Mathf.Abs(parallelOffsetRange.y)
                )
            );

            float offset = Range(
                random,
                minimumOffset,
                maximumOffset
            );

            float sideSign =
                route % 2 == 0
                    ? 1f
                    : -1f;

            offset *= sideSign;

            PlannedSegment companion =
                new PlannedSegment
                {
                    facetIndex = primary.facetIndex,
                    depth = 0,
                    index = plan.segments.Count,
                    center =
                        primary.center +
                        side * offset,
                    direction = primary.direction,
                    length =
                        primary.length *
                        Range(random, 0.72f, 0.96f),
                    width = Mathf.Max(
                        0.18f,
                        primary.width *
                        parallelWidthScale
                    ),
                    height = Mathf.Max(
                        0.18f,
                        primary.height *
                        Range(random, 0.55f, 0.82f)
                    ),
                    layerIndex = primary.layerIndex,
                    elevation =
                        primary.elevation +
                        Range(
                            random,
                            -layerHeightJitter * 0.25f,
                            layerHeightJitter * 0.25f
                        ),
                    primary = false,
                    addPipeBundle =
                        random.NextDouble() <
                        Mathf.Clamp01(
                            pipeBundleRate * 1.25f
                        ),
                    addRiserAtStart =
                        primary.elevation > 0.01f &&
                        random.NextDouble() <
                        elevatedRiserRate * 0.6f,
                    addRiserAtEnd =
                        primary.elevation > 0.01f &&
                        random.NextDouble() <
                        elevatedRiserRate * 0.6f
                };

            if (!TryFitPrimarySegment(plan, companion))
            {
                continue;
            }

            companion.index = plan.segments.Count;
            plan.segments.Add(companion);

            if (
                random.NextDouble() <
                terminalEquipmentRate * 0.45f
            )
            {
                TryAddTerminal(
                    plan,
                    companion.End,
                    companion.direction,
                    random,
                    companion.layerIndex,
                    companion.elevation,
                    false
                );
            }
        }
    }

    private RoofFacetDetector.ProjectionScore
        ScoreProjectionForMechanical(
            FacetProjection projection)
    {
        List<SpineCandidate> horizontal =
            FindSpineCandidates(
                projection,
                primaryDuctWidth + objectSeparation,
                false
            );

        List<SpineCandidate> vertical =
            FindSpineCandidates(
                projection,
                primaryDuctWidth + objectSeparation,
                true
            );

        int horizontalLength = 0;
        int verticalLength = 0;

        foreach (SpineCandidate candidate in horizontal)
        {
            horizontalLength +=
                Mathf.RoundToInt(
                    candidate.length * 100f
                );
        }

        foreach (SpineCandidate candidate in vertical)
        {
            verticalLength +=
                Mathf.RoundToInt(
                    candidate.length * 100f
                );
        }

        return new RoofFacetDetector.ProjectionScore(
            horizontalLength + verticalLength,
            horizontal.Count + vertical.Count
        );
    }

    private bool TryFindLongestHorizontalRun(
        FacetProjection projection,
        float v,
        float width,
        out float centerU,
        out float length)
    {
        centerU = 0f;
        length = 0f;

        float minimumU = projection.min.x + roofEdgePadding;
        float maximumU = projection.max.x - roofEdgePadding;

        if (maximumU <= minimumU)
        {
            return false;
        }

        float sampleStep = Mathf.Max(0.35f, width * 0.45f);
        bool insideRun = false;
        float currentStart = minimumU;
        float bestStart = minimumU;
        float bestEnd = minimumU;

        for (float u = minimumU;
            u <= maximumU + sampleStep * 0.5f;
            u += sampleStep)
        {
            float sampledU = Mathf.Min(u, maximumU);
            bool valid = RoofFacetDetector.FootprintFits(
                projection,
                new Vector2(sampledU, v),
                sampleStep * 0.9f,
                width,
                3
            );

            if (valid && !insideRun)
            {
                insideRun = true;
                currentStart = sampledU;
            }

            bool closesRun =
                (!valid && insideRun) ||
                (insideRun && sampledU >= maximumU - 0.0001f);

            if (!closesRun)
            {
                continue;
            }

            float currentEnd = valid
                ? sampledU
                : Mathf.Max(currentStart, sampledU - sampleStep);

            if (currentEnd - currentStart > bestEnd - bestStart)
            {
                bestStart = currentStart;
                bestEnd = currentEnd;
            }

            insideRun = false;
        }

        length = bestEnd - bestStart;
        centerU = (bestStart + bestEnd) * 0.5f;

        return length >= minimumBranchLength;
    }


    private bool TryFindLongestVerticalRun(
        FacetProjection projection,
        float u,
        float width,
        out float centerV,
        out float length)
    {
        centerV = 0f;
        length = 0f;

        float minimumV =
            projection.min.y +
            roofEdgePadding;

        float maximumV =
            projection.max.y -
            roofEdgePadding;

        if (maximumV <= minimumV)
        {
            return false;
        }

        float sampleStep = Mathf.Max(
            0.35f,
            width * 0.45f
        );

        bool insideRun = false;
        float currentStart = minimumV;
        float bestStart = minimumV;
        float bestEnd = minimumV;

        for (float v = minimumV;
            v <= maximumV + sampleStep * 0.5f;
            v += sampleStep)
        {
            float sampledV =
                Mathf.Min(v, maximumV);

            bool valid =
                RoofFacetDetector.FootprintFits(
                    projection,
                    new Vector2(u, sampledV),
                    width,
                    sampleStep * 0.9f,
                    3
                );

            if (valid && !insideRun)
            {
                insideRun = true;
                currentStart = sampledV;
            }

            bool closesRun =
                (!valid && insideRun) ||
                (
                    insideRun &&
                    sampledV >=
                    maximumV - 0.0001f
                );

            if (!closesRun)
            {
                continue;
            }

            float currentEnd =
                valid
                    ? sampledV
                    : Mathf.Max(
                        currentStart,
                        sampledV - sampleStep
                    );

            if (
                currentEnd - currentStart >
                bestEnd - bestStart
            )
            {
                bestStart = currentStart;
                bestEnd = currentEnd;
            }

            insideRun = false;
        }

        length = bestEnd - bestStart;
        centerV = (bestStart + bestEnd) * 0.5f;

        return length >= minimumBranchLength;
    }

    private void BuildBranches(
        FacetPlan plan,
        PlannedSegment parent,
        System.Random random,
        int depth)
    {
        if (depth > maximumRecursionDepth)
        {
            return;
        }

        float usableLength = Mathf.Max(
            0f,
            parent.length - branchSpacing
        );

        int candidateCount = Mathf.Max(
            1,
            Mathf.FloorToInt(
                usableLength /
                Mathf.Max(branchSpacing, 0.5f)
            )
        );

        for (int candidate = 0;
            candidate < candidateCount;
            candidate++)
        {
            if (
                random.NextDouble() >
                branchProbability
            )
            {
                continue;
            }

            float fraction =
                (candidate + 1f) /
                (candidateCount + 1f);

            float along = Mathf.Lerp(
                -parent.length * 0.5f +
                    branchSpacing * 0.5f,
                parent.length * 0.5f -
                    branchSpacing * 0.5f,
                fraction
            );

            along += Range(
                random,
                -branchPositionJitter,
                branchPositionJitter
            );

            Vector2 branchStart =
                parent.center +
                parent.direction * along;

            float side =
                random.NextDouble() < 0.5
                    ? -1f
                    : 1f;

            float resolvedAngle =
                side *
                (
                    branchAngle +
                    Range(
                        random,
                        -branchAngleJitter,
                        branchAngleJitter
                    )
                );

            Vector2 direction =
                Rotate2D(
                    parent.direction,
                    resolvedAngle
                ).normalized;

            float desiredLength = Mathf.Max(
                minimumBranchLength,
                parent.length *
                lengthDecay *
                branchReachMultiplier *
                Range(random, 0.58f, 1.0f)
            );

            float width = Mathf.Max(
                0.18f,
                parent.width * widthDecay
            );

            float height = Mathf.Max(
                0.18f,
                parent.height *
                Range(
                    random,
                    widthDecay * 0.85f,
                    Mathf.Min(
                        1f,
                        widthDecay * 1.1f
                    )
                )
            );

            float maximumLength =
                FindMaximumBranchLength(
                    plan,
                    branchStart,
                    direction,
                    width,
                    height,
                    desiredLength,
                    parent.layerIndex,
                    parent.elevation
                );

            if (
                maximumLength <
                minimumBranchLength
            )
            {
                continue;
            }

            float length = Mathf.Min(
                desiredLength,
                maximumLength
            );

            Vector2 center =
                branchStart +
                direction * length * 0.5f;

            PlannedSegment branch =
                new PlannedSegment
                {
                    facetIndex = plan.facetIndex,
                    depth = depth,
                    index = plan.segments.Count,
                    center = center,
                    direction = direction,
                    length = length,
                    width = width,
                    height = height,
                    layerIndex = parent.layerIndex,
                    elevation = parent.elevation,
                    primary = false,
                    addPipeBundle =
                        random.NextDouble() <
                        pipeBundleRate * 0.68f,
                    addRiserAtStart = false,
                    addRiserAtEnd =
                        parent.elevation > 0.01f &&
                        random.NextDouble() <
                        elevatedRiserRate * 0.35f
                };

            if (!CanPlaceSegment(plan, branch))
            {
                continue;
            }

            parent.childStarts.Add(branchStart);
            plan.segments.Add(branch);

            BuildBranches(
                plan,
                branch,
                random,
                depth + 1
            );

            if (
                random.NextDouble() <
                terminalEquipmentRate
            )
            {
                TryAddTerminal(
                    plan,
                    branch.End,
                    branch.direction,
                    random,
                    branch.layerIndex,
                    branch.elevation,
                    false
                );
            }
        }
    }

    private float FindMaximumBranchLength(
        FacetPlan plan,
        Vector2 start,
        Vector2 direction,
        float width,
        float height,
        float desiredLength,
        int layerIndex,
        float elevation)
    {
        float step = Mathf.Max(
            0.4f,
            width * 0.65f
        );

        float acceptedLength = 0f;

        for (float length = minimumBranchLength;
            length <= desiredLength + 0.001f;
            length += step)
        {
            PlannedSegment probe =
                new PlannedSegment
                {
                    facetIndex = plan.facetIndex,
                    center =
                        start +
                        direction * length * 0.5f,
                    direction = direction,
                    length = length,
                    width = width,
                    height = height,
                    layerIndex = layerIndex,
                    elevation = elevation
                };

            if (!CanPlaceSegment(plan, probe, false))
            {
                break;
            }

            acceptedLength = length;
        }

        return acceptedLength;
    }

    private bool CanPlaceSegment(
        FacetPlan plan,
        PlannedSegment segment,
        bool testAgainstExisting = true)
    {
        if (!RoofFacetDetector.SegmentFits(
            plan.projection,
            segment.center,
            segment.direction,
            segment.length,
            segment.width + objectSeparation,
            11,
            3
        ))
        {
            return false;
        }

        Bounds worldBounds = CalculateWorldBounds(
            plan.projection,
            segment.center,
            segment.direction,
            segment.length,
            segment.width,
            segment.height,
            segment.elevation
        );

        if (IntersectsExclusion(worldBounds))
        {
            return false;
        }

        if (!testAgainstExisting)
        {
            return true;
        }

        foreach (
            PlannedSegment existing
            in plan.segments)
        {
            bool sameVerticalBand =
                VerticalRangesOverlap(
                    existing.elevation,
                    existing.height,
                    segment.elevation,
                    segment.height
                );

            if (
                !sameVerticalBand &&
                allowElevatedCrossings
            )
            {
                continue;
            }

            if (
                existing.layerIndex ==
                segment.layerIndex &&
                SegmentsShareConnection(
                    existing,
                    segment
                )
            )
            {
                continue;
            }

            Bounds existingBounds =
                CalculateWorldBounds(
                    plan.projection,
                    existing.center,
                    existing.direction,
                    existing.length,
                    existing.width +
                        objectSeparation,
                    existing.height,
                    existing.elevation
                );

            if (existingBounds.Intersects(worldBounds))
            {
                return false;
            }
        }

        return true;
    }

    private bool VerticalRangesOverlap(
        float firstElevation,
        float firstHeight,
        float secondElevation,
        float secondHeight)
    {
        float firstBottom =
            supportHeight +
            firstElevation;

        float firstTop =
            firstBottom +
            firstHeight;

        float secondBottom =
            supportHeight +
            secondElevation;

        float secondTop =
            secondBottom +
            secondHeight;

        return !(
            firstTop +
                elevatedCrossingClearance <=
                secondBottom ||
            secondTop +
                elevatedCrossingClearance <=
                firstBottom
        );
    }

    private static bool SegmentsShareConnection(
        PlannedSegment first,
        PlannedSegment second)
    {
        float threshold = Mathf.Max(
            first.width,
            second.width
        ) * 1.5f;

        return
            Vector2.Distance(
                first.Start,
                second.Start
            ) <= threshold ||
            Vector2.Distance(
                first.Start,
                second.End
            ) <= threshold ||
            Vector2.Distance(
                first.End,
                second.Start
            ) <= threshold ||
            Vector2.Distance(
                first.End,
                second.End
            ) <= threshold ||
            DistancePointToSegment(
                second.Start,
                first.Start,
                first.End
            ) <= threshold;
    }

    private bool TryAddTerminal(
        FacetPlan plan,
        Vector2 position,
        Vector2 direction,
        System.Random random,
        int layerIndex,
        float elevation,
        bool standalone)
    {
        Vector3 size = new Vector3(
            Range(
                random,
                equipmentWidthRange.x,
                equipmentWidthRange.y
            ),
            Range(
                random,
                equipmentHeightRange.x,
                equipmentHeightRange.y
            ),
            Range(
                random,
                equipmentLengthRange.x,
                equipmentLengthRange.y
            )
        );

        Vector2 center =
            standalone
                ? position
                : position +
                    direction *
                    (size.z * 0.5f + 0.25f);

        PlannedTerminal terminal =
            new PlannedTerminal
            {
                facetIndex = plan.facetIndex,
                position = center,
                direction = direction.normalized,
                type =
                    (TerminalType)random.Next(0, 4),
                size = size,
                rotationJitter =
                    Range(random, -4f, 4f),
                layerIndex = layerIndex,
                elevation = Mathf.Max(0f, elevation),
                standalone = standalone
            };

        if (!CanPlaceTerminal(plan, terminal))
        {
            return false;
        }

        plan.terminals.Add(terminal);
        return true;
    }

    private bool CanPlaceTerminal(
        FacetPlan plan,
        PlannedTerminal terminal)
    {
        if (!RoofFacetDetector.SegmentFits(
            plan.projection,
            terminal.position,
            terminal.direction,
            terminal.size.z,
            terminal.size.x,
            7,
            3
        ))
        {
            return false;
        }

        Bounds bounds = CalculateWorldBounds(
            plan.projection,
            terminal.position,
            terminal.direction,
            terminal.size.z,
            terminal.size.x,
            terminal.size.y,
            terminal.elevation
        );

        if (IntersectsExclusion(bounds))
        {
            return false;
        }

        foreach (
            PlannedTerminal existing
            in plan.terminals)
        {
            if (
                !VerticalRangesOverlap(
                    existing.elevation,
                    existing.size.y,
                    terminal.elevation,
                    terminal.size.y
                ) &&
                allowElevatedCrossings
            )
            {
                continue;
            }

            Bounds existingBounds =
                CalculateWorldBounds(
                    plan.projection,
                    existing.position,
                    existing.direction,
                    existing.size.z,
                    existing.size.x,
                    existing.size.y,
                    existing.elevation
                );

            if (existingBounds.Intersects(bounds))
            {
                return false;
            }
        }

        /*
         * Equipment may sit near or partly bridge a connected route, but
         * avoid burying it inside unrelated ducts occupying the same
         * vertical band.
         */
        foreach (
            PlannedSegment segment
            in plan.segments)
        {
            if (
                !VerticalRangesOverlap(
                    segment.elevation,
                    segment.height,
                    terminal.elevation,
                    terminal.size.y
                ) &&
                allowElevatedCrossings
            )
            {
                continue;
            }

            Bounds segmentBounds =
                CalculateWorldBounds(
                    plan.projection,
                    segment.center,
                    segment.direction,
                    segment.length,
                    Mathf.Max(
                        0.05f,
                        segment.width -
                        objectSeparation * 0.5f
                    ),
                    segment.height,
                    segment.elevation
                );

            /*
             * Terminal equipment attached to an endpoint is allowed to
             * touch a nearby duct. Standalone equipment keeps stricter
             * separation.
             */
            if (
                segmentBounds.Intersects(bounds) &&
                (
                    terminal.standalone ||
                    DistancePointToSegment(
                        terminal.position,
                        segment.Start,
                        segment.End
                    ) >
                    terminal.size.z * 0.65f
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private void BuildDistributedEquipmentClusters(
        FacetPlan plan,
        System.Random random)
    {
        if (equipmentClusterCount <= 0)
        {
            return;
        }

        int minimumPerCluster = Mathf.Max(
            1,
            Mathf.Min(
                equipmentPerClusterRange.x,
                equipmentPerClusterRange.y
            )
        );

        int maximumPerCluster = Mathf.Max(
            minimumPerCluster,
            Mathf.Max(
                equipmentPerClusterRange.x,
                equipmentPerClusterRange.y
            )
        );

        float minimumU =
            plan.projection.min.x +
            roofEdgePadding +
            equipmentScatterPadding;

        float maximumU =
            plan.projection.max.x -
            roofEdgePadding -
            equipmentScatterPadding;

        float minimumV =
            plan.projection.min.y +
            roofEdgePadding +
            equipmentScatterPadding;

        float maximumV =
            plan.projection.max.y -
            roofEdgePadding -
            equipmentScatterPadding;

        if (
            maximumU <= minimumU ||
            maximumV <= minimumV
        )
        {
            return;
        }

        int acceptedClusters = 0;
        int clusterAttempts =
            Mathf.Max(
                equipmentClusterCount * 18,
                24
            );

        for (int attempt = 0;
            attempt < clusterAttempts &&
            acceptedClusters <
                equipmentClusterCount;
            attempt++)
        {
            Vector2 clusterCenter = new Vector2(
                Range(random, minimumU, maximumU),
                Range(random, minimumV, maximumV)
            );

            if (!RoofFacetDetector.PointInFacet(
                clusterCenter,
                plan.projection.triangles
            ))
            {
                continue;
            }

            int layerIndex = 0;

            if (
                networkLayerCount > 1 &&
                random.NextDouble() <
                raisedEquipmentRate
            )
            {
                layerIndex =
                    random.Next(
                        1,
                        networkLayerCount
                    );
            }

            float elevation =
                layerIndex <= 0
                    ? 0f
                    : Mathf.Max(
                        0f,
                        layerIndex *
                            layerHeightStep +
                        Range(
                            random,
                            -layerHeightJitter,
                            layerHeightJitter
                        )
                    );

            int requestedItems = random.Next(
                minimumPerCluster,
                maximumPerCluster + 1
            );

            int acceptedItems = 0;

            for (int item = 0;
                item < requestedItems;
                item++)
            {
                float angle =
                    Range(
                        random,
                        0f,
                        Mathf.PI * 2f
                    );

                float radius =
                    item <= 0
                        ? 0f
                        : Mathf.Sqrt(
                            Range(random, 0f, 1f)
                        ) *
                        equipmentClusterRadius;

                Vector2 position =
                    clusterCenter +
                    new Vector2(
                        Mathf.Cos(angle),
                        Mathf.Sin(angle)
                    ) *
                    radius;

                Vector2 direction =
                    random.NextDouble() < 0.5
                        ? Vector2.right
                        : Vector2.up;

                if (
                    TryAddTerminal(
                        plan,
                        position,
                        direction,
                        random,
                        layerIndex,
                        elevation,
                        true
                    )
                )
                {
                    acceptedItems++;
                }
            }

            if (acceptedItems > 0)
            {
                acceptedClusters++;
            }
        }
    }

    private FacetProjection ResolveFacetProjection(RoofFacet facet)
    {
        RoofFacetDetector.OrientationSettings settings =
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
            orientationOffset,
            settings,
            projection =>
                ScoreProjectionForMechanical(projection)
        );
    }

    private void RebuildExclusionBounds()
    {
        exclusionBounds.Clear();

        if (exclusionRoot == null)
        {
            return;
        }

        Renderer[] renderers =
            exclusionRoot.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer renderer in renderers)
        {
            if (
                renderer == null ||
                !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy
            )
            {
                continue;
            }

            Bounds bounds = renderer.bounds;
            bounds.Expand(objectSeparation * 2f);
            exclusionBounds.Add(bounds);
        }
    }

    private bool IntersectsExclusion(Bounds bounds)
    {
        foreach (Bounds exclusion in exclusionBounds)
        {
            if (exclusion.Intersects(bounds))
            {
                return true;
            }
        }

        return false;
    }

    private Bounds CalculateWorldBounds(
        FacetProjection projection,
        Vector2 center,
        Vector2 direction,
        float length,
        float width,
        float height,
        float elevation)
    {
        Vector2 perpendicular = new Vector2(
            -direction.y,
            direction.x
        );

        Vector3 worldDirection = (
            projection.u * direction.x +
            projection.v * direction.y
        ).normalized;

        Vector3 worldPerpendicular = (
            projection.u * perpendicular.x +
            projection.v * perpendicular.y
        ).normalized;

        Vector3 worldCenter =
            projection.ToWorld(center) +
            projection.normal *
            (
                roofClearance +
                supportHeight +
                elevation +
                height * 0.5f
            );

        Vector3 extents =
            Abs(worldDirection) *
                (length * 0.5f) +
            Abs(worldPerpendicular) *
                (width * 0.5f) +
            Abs(projection.normal) *
                (height * 0.5f);

        Bounds bounds =
            new Bounds(
                worldCenter,
                extents * 2f
            );

        bounds.Expand(
            objectSeparation * 2f
        );

        return bounds;
    }

    private void GenerateMechanicalGeometry()
    {
        if (buildingRoot == null)
        {
            EditorUtility.DisplayDialog(
                "Rooftop Mechanical",
                "Assign a building root first.",
                "OK"
            );
            return;
        }

        if (previewDirty || plans.Count == 0)
        {
            BuildPreviewPlan();
        }

        if (plans.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Rooftop Mechanical",
                "The current settings produced no valid network.",
                "OK"
            );
            return;
        }

        Transform buildingOutput =
            GetOrCreateOutputFolder();

        if (buildingOutput == null)
        {
            return;
        }

        if (replaceExisting)
        {
            DestroyChildren(buildingOutput);
        }

        int segmentCounter = 0;
        int equipmentCounter = 0;
        int supportCounter = 0;
        int riserCounter = 0;

        foreach (FacetPlan plan in plans)
        {
            Transform facetRoot = FindOrCreateChild(
                buildingOutput,
                string.Format(
                    "network_{0:00}",
                    plan.facetIndex
                )
            );

            System.Random random =
                new System.Random(
                    CombineSeed(
                        seed + 173,
                        plan.facetIndex
                    )
                );

            foreach (
                PlannedSegment segment
                in plan.segments)
            {
                Transform layerRoot =
                    FindOrCreateChild(
                        facetRoot,
                        string.Format(
                            "layer_{0:00}",
                            segment.layerIndex
                        )
                    );

                Transform ductRoot =
                    FindOrCreateChild(
                        layerRoot,
                        "ducts"
                    );

                Transform pipeRoot =
                    FindOrCreateChild(
                        layerRoot,
                        "pipes"
                    );

                Transform equipmentRoot =
                    FindOrCreateChild(
                        layerRoot,
                        "equipment"
                    );

                Transform supportRoot =
                    FindOrCreateChild(
                        layerRoot,
                        "supports"
                    );

                Transform riserRoot =
                    FindOrCreateChild(
                        layerRoot,
                        "risers"
                    );

                CreateDuct(
                    plan.projection,
                    segment,
                    ductRoot,
                    segmentCounter++
                );

                if (
                    segment.addPipeBundle &&
                    maximumPipeCount > 0
                )
                {
                    int pipeCount = random.Next(
                        Mathf.Min(
                            minimumPipeCount,
                            maximumPipeCount
                        ),
                        maximumPipeCount + 1
                    );

                    CreatePipeBundle(
                        plan.projection,
                        segment,
                        pipeRoot,
                        pipeCount
                    );
                }

                if (generateSupports)
                {
                    supportCounter +=
                        CreateSupports(
                            plan.projection,
                            segment,
                            supportRoot,
                            supportCounter
                        );
                }

                if (segment.addRiserAtStart)
                {
                    CreateVerticalRiser(
                        plan.projection,
                        segment.Start,
                        segment,
                        riserRoot,
                        riserCounter++
                    );
                }

                if (segment.addRiserAtEnd)
                {
                    CreateVerticalRiser(
                        plan.projection,
                        segment.End,
                        segment,
                        riserRoot,
                        riserCounter++
                    );
                }

                foreach (
                    Vector2 junction
                    in segment.childStarts)
                {
                    if (
                        random.NextDouble() <=
                        junctionBoxRate
                    )
                    {
                        CreateJunctionBox(
                            plan.projection,
                            junction,
                            segment,
                            equipmentRoot,
                            equipmentCounter++
                        );
                    }
                }

                if (
                    random.NextDouble() <=
                    ventRate
                )
                {
                    CreateVentStack(
                        plan.projection,
                        segment,
                        equipmentRoot,
                        equipmentCounter++
                    );
                }
            }

            foreach (
                PlannedTerminal terminal
                in plan.terminals)
            {
                Transform layerRoot =
                    FindOrCreateChild(
                        facetRoot,
                        string.Format(
                            "layer_{0:00}",
                            terminal.layerIndex
                        )
                    );

                Transform equipmentRoot =
                    FindOrCreateChild(
                        layerRoot,
                        "equipment"
                    );

                CreateTerminalEquipment(
                    plan.projection,
                    terminal,
                    equipmentRoot,
                    equipmentCounter++
                );
            }
        }

        EditorSceneManager.MarkSceneDirty(
            buildingRoot.scene
        );

        Selection.activeTransform =
            buildingOutput;

        Debug.Log(
            string.Format(
                "[Rooftop Mechanical V3] Generated {0} duct segments, " +
                "{1} equipment objects, {2} supports, and {3} risers " +
                "under '{4}'.",
                segmentCounter,
                equipmentCounter,
                supportCounter,
                riserCounter,
                GetPath(buildingOutput)
            ),
            buildingOutput
        );
    }

    private void CreateDuct(
        FacetProjection projection,
        PlannedSegment segment,
        Transform parent,
        int index)
    {
        GameObject duct = CreatePrimitive(
            PrimitiveType.Cube,
            string.Format(
                "duct_L{0:00}_{1:000}",
                segment.layerIndex,
                index
            ),
            parent,
            ductMaterial
        );

        Vector3 direction = ToWorldDirection(
            projection,
            segment.direction
        );

        Quaternion rotation =
            Quaternion.LookRotation(
                direction,
                projection.normal
            );

        Vector3 position =
            projection.ToWorld(segment.center) +
            projection.normal *
            (
                roofClearance +
                supportHeight +
                segment.elevation +
                segment.height * 0.5f
            );

        duct.transform.SetPositionAndRotation(
            position,
            rotation
        );

        duct.transform.localScale =
            new Vector3(
                segment.width,
                segment.height,
                segment.length
            );

        ApplyGeneratedFlags(duct);
    }

    private void CreatePipeBundle(
        FacetProjection projection,
        PlannedSegment segment,
        Transform parent,
        int count)
    {
        Vector3 direction = ToWorldDirection(
            projection,
            segment.direction
        );

        Vector3 side = Vector3.Cross(
            projection.normal,
            direction
        ).normalized;

        float bundleWidth =
            count * pipeRadius * 2f +
            Mathf.Max(0, count - 1) *
            pipeSpacing;

        float startOffset =
            -bundleWidth * 0.5f +
            pipeRadius;

        for (int pipeIndex = 0;
            pipeIndex < count;
            pipeIndex++)
        {
            float sideOffset =
                startOffset +
                pipeIndex *
                (
                    pipeRadius * 2f +
                    pipeSpacing
                );

            Vector3 center =
                projection.ToWorld(
                    segment.center
                ) +
                side *
                (
                    segment.width * 0.75f +
                    sideOffset
                ) +
                projection.normal *
                (
                    roofClearance +
                    supportHeight +
                    segment.elevation +
                    pipeRadius * 1.8f
                );

            CreateCylinderBetween(
                string.Format(
                    "pipe_L{0:00}_{1:000}_{2:00}",
                    segment.layerIndex,
                    segment.index,
                    pipeIndex
                ),
                center -
                    direction *
                    segment.length * 0.46f,
                center +
                    direction *
                    segment.length * 0.46f,
                pipeRadius,
                parent,
                pipeMaterial
            );
        }
    }

    private int CreateSupports(
        FacetProjection projection,
        PlannedSegment segment,
        Transform parent,
        int startIndex)
    {
        int supportCount = Mathf.Max(
            2,
            Mathf.CeilToInt(
                segment.length /
                Mathf.Max(
                    supportSpacing,
                    0.5f
                )
            ) + 1
        );

        Vector3 direction = ToWorldDirection(
            projection,
            segment.direction
        );

        float effectiveSupportHeight =
            supportHeight +
            segment.elevation;

        for (int index = 0;
            index < supportCount;
            index++)
        {
            float t =
                supportCount <= 1
                    ? 0.5f
                    : index /
                        (float)(supportCount - 1);

            Vector2 point = Vector2.Lerp(
                segment.Start,
                segment.End,
                t
            );

            GameObject support =
                CreatePrimitive(
                    PrimitiveType.Cube,
                    string.Format(
                        "support_{0:000}",
                        startIndex + index
                    ),
                    parent,
                    supportMaterial
                );

            Quaternion rotation =
                Quaternion.LookRotation(
                    direction,
                    projection.normal
                );

            Vector3 position =
                projection.ToWorld(point) +
                projection.normal *
                (
                    roofClearance +
                    effectiveSupportHeight *
                    0.5f
                );

            support.transform.SetPositionAndRotation(
                position,
                rotation
            );

            support.transform.localScale =
                new Vector3(
                    segment.width * 1.25f,
                    Mathf.Max(
                        0.04f,
                        effectiveSupportHeight
                    ),
                    supportThickness
                );

            ApplyGeneratedFlags(support);
        }

        return supportCount;
    }

    private void CreateVerticalRiser(
        FacetProjection projection,
        Vector2 point,
        PlannedSegment segment,
        Transform parent,
        int index)
    {
        float riserHeight =
            supportHeight +
            segment.elevation +
            segment.height * 0.5f;

        if (riserHeight <= 0.05f)
        {
            return;
        }

        Vector3 basePoint =
            projection.ToWorld(point) +
            projection.normal *
            roofClearance;

        Vector3 topPoint =
            basePoint +
            projection.normal *
            riserHeight;

        CreateCylinderBetween(
            string.Format(
                "riser_L{0:00}_{1:000}",
                segment.layerIndex,
                index
            ),
            basePoint,
            topPoint,
            Mathf.Max(
                pipeRadius * 1.55f,
                segment.width * 0.11f
            ),
            parent,
            pipeMaterial
        );
    }

    private void CreateJunctionBox(
        FacetProjection projection,
        Vector2 point,
        PlannedSegment segment,
        Transform parent,
        int index)
    {
        GameObject box = CreatePrimitive(
            PrimitiveType.Cube,
            string.Format(
                "junction_L{0:00}_{1:000}",
                segment.layerIndex,
                index
            ),
            parent,
            equipmentMaterial
        );

        Vector3 direction = ToWorldDirection(
            projection,
            segment.direction
        );

        Quaternion rotation =
            Quaternion.LookRotation(
                direction,
                projection.normal
            );

        float size = Mathf.Max(
            segment.width * 1.35f,
            0.45f
        );

        Vector3 position =
            projection.ToWorld(point) +
            projection.normal *
            (
                roofClearance +
                supportHeight +
                segment.elevation +
                segment.height * 0.58f
            );

        box.transform.SetPositionAndRotation(
            position,
            rotation
        );

        box.transform.localScale =
            new Vector3(
                size,
                segment.height * 1.15f,
                size
            );

        ApplyGeneratedFlags(box);
    }

    private void CreateVentStack(
        FacetProjection projection,
        PlannedSegment segment,
        Transform parent,
        int index)
    {
        Vector3 basePosition =
            projection.ToWorld(
                segment.center
            ) +
            projection.normal *
            (
                roofClearance +
                supportHeight +
                segment.elevation +
                segment.height
            );

        int count =
            2 +
            Mathf.Abs(index % 3);

        for (int vent = 0;
            vent < count;
            vent++)
        {
            Vector3 offset =
                projection.u *
                (
                    (
                        vent -
                        (count - 1) * 0.5f
                    ) *
                    0.45f
                );

            Vector3 start =
                basePosition +
                offset;

            Vector3 end =
                start +
                projection.normal *
                (
                    0.8f +
                    vent * 0.16f
                );

            CreateCylinderBetween(
                string.Format(
                    "vent_L{0:00}_{1:000}_{2:00}",
                    segment.layerIndex,
                    index,
                    vent
                ),
                start,
                end,
                pipeRadius * 1.35f,
                parent,
                pipeMaterial
            );
        }
    }

    private void CreateTerminalEquipment(
        FacetProjection projection,
        PlannedTerminal terminal,
        Transform parent,
        int index)
    {
        switch (terminal.type)
        {
            case TerminalType.FanHousing:
                CreateFanHousing(
                    projection,
                    terminal,
                    parent,
                    index
                );
                break;

            case TerminalType.VentCluster:
                CreateTerminalVentCluster(
                    projection,
                    terminal,
                    parent,
                    index
                );
                break;

            case TerminalType.PipeManifold:
                CreateTerminalPipeManifold(
                    projection,
                    terminal,
                    parent,
                    index
                );
                break;

            default:
                CreateAirHandlingUnit(
                    projection,
                    terminal,
                    parent,
                    index
                );
                break;
        }
    }

    private void CreateEquipmentPedestal(
        FacetProjection projection,
        PlannedTerminal terminal,
        Transform parent,
        Quaternion rotation)
    {
        float pedestalHeight =
            supportHeight +
            terminal.elevation;

        if (pedestalHeight <= 0.05f)
        {
            return;
        }

        GameObject pedestal =
            CreatePrimitive(
                PrimitiveType.Cube,
                "pedestal",
                parent,
                supportMaterial
            );

        Vector3 position =
            projection.ToWorld(
                terminal.position
            ) +
            projection.normal *
            (
                roofClearance +
                pedestalHeight * 0.5f
            );

        pedestal.transform.SetPositionAndRotation(
            position,
            rotation
        );

        pedestal.transform.localScale =
            new Vector3(
                terminal.size.x * 0.58f,
                pedestalHeight,
                terminal.size.z * 0.58f
            );

        ApplyGeneratedFlags(pedestal);
    }

    private void CreateAirHandlingUnit(
        FacetProjection projection,
        PlannedTerminal terminal,
        Transform parent,
        int index)
    {
        Transform root = FindOrCreateChild(
            parent,
            string.Format(
                "ahu_L{0:00}_{1:000}",
                terminal.layerIndex,
                index
            )
        );

        Vector3 direction = ToWorldDirection(
            projection,
            terminal.direction
        );

        Quaternion rotation =
            Quaternion.LookRotation(
                direction,
                projection.normal
            ) *
            Quaternion.AngleAxis(
                terminal.rotationJitter,
                projection.normal
            );

        CreateEquipmentPedestal(
            projection,
            terminal,
            root,
            rotation
        );

        Vector3 basePosition =
            projection.ToWorld(
                terminal.position
            ) +
            projection.normal *
            (
                roofClearance +
                supportHeight +
                terminal.elevation
            );

        GameObject body = CreatePrimitive(
            PrimitiveType.Cube,
            "body",
            root,
            equipmentMaterial
        );

        body.transform.SetPositionAndRotation(
            basePosition +
                projection.normal *
                (terminal.size.y * 0.5f),
            rotation
        );

        body.transform.localScale =
            terminal.size;

        ApplyGeneratedFlags(body);

        GameObject cap = CreatePrimitive(
            PrimitiveType.Cube,
            "service_cap",
            root,
            supportMaterial
        );

        cap.transform.SetPositionAndRotation(
            basePosition +
                projection.normal *
                (
                    terminal.size.y +
                    0.12f
                ),
            rotation
        );

        cap.transform.localScale =
            new Vector3(
                terminal.size.x * 0.78f,
                0.18f,
                terminal.size.z * 0.56f
            );

        ApplyGeneratedFlags(cap);
    }

    private void CreateFanHousing(
        FacetProjection projection,
        PlannedTerminal terminal,
        Transform parent,
        int index)
    {
        Transform root = FindOrCreateChild(
            parent,
            string.Format(
                "fan_L{0:00}_{1:000}",
                terminal.layerIndex,
                index
            )
        );

        Vector3 direction = ToWorldDirection(
            projection,
            terminal.direction
        );

        Quaternion rotation =
            Quaternion.LookRotation(
                direction,
                projection.normal
            );

        CreateEquipmentPedestal(
            projection,
            terminal,
            root,
            rotation
        );

        Vector3 center =
            projection.ToWorld(
                terminal.position
            ) +
            projection.normal *
            (
                roofClearance +
                supportHeight +
                terminal.elevation +
                terminal.size.y * 0.5f
            );

        GameObject housing = CreatePrimitive(
            PrimitiveType.Cube,
            "housing",
            root,
            equipmentMaterial
        );

        housing.transform.SetPositionAndRotation(
            center,
            rotation
        );

        housing.transform.localScale =
            new Vector3(
                terminal.size.x,
                terminal.size.y,
                terminal.size.z
            );

        ApplyGeneratedFlags(housing);

        float fanRadius = Mathf.Min(
            terminal.size.x,
            terminal.size.z
        ) * 0.28f;

        Vector3 fanStart =
            center +
            projection.normal *
            (
                terminal.size.y *
                0.48f
            );

        Vector3 fanEnd =
            fanStart +
            projection.normal *
            0.22f;

        CreateCylinderBetween(
            "fan_top",
            fanStart,
            fanEnd,
            fanRadius,
            root,
            supportMaterial
        );
    }

    private void CreateTerminalVentCluster(
        FacetProjection projection,
        PlannedTerminal terminal,
        Transform parent,
        int index)
    {
        Transform root = FindOrCreateChild(
            parent,
            string.Format(
                "vent_cluster_L{0:00}_{1:000}",
                terminal.layerIndex,
                index
            )
        );

        Vector3 direction = ToWorldDirection(
            projection,
            terminal.direction
        );

        Quaternion rotation =
            Quaternion.LookRotation(
                direction,
                projection.normal
            );

        CreateEquipmentPedestal(
            projection,
            terminal,
            root,
            rotation
        );

        Vector3 center =
            projection.ToWorld(
                terminal.position
            ) +
            projection.normal *
            (
                roofClearance +
                supportHeight +
                terminal.elevation
            );

        for (int row = 0;
            row < 2;
            row++)
        {
            for (int column = 0;
                column < 3;
                column++)
            {
                Vector3 offset =
                    projection.u *
                    (
                        (column - 1f) *
                        0.55f
                    ) +
                    projection.v *
                    (
                        (row - 0.5f) *
                        0.55f
                    );

                Vector3 start =
                    center +
                    offset;

                Vector3 end =
                    start +
                    projection.normal *
                    (
                        terminal.size.y *
                        (
                            0.55f +
                            0.08f *
                            (row + column)
                        )
                    );

                CreateCylinderBetween(
                    string.Format(
                        "stack_{0}_{1}",
                        row,
                        column
                    ),
                    start,
                    end,
                    pipeRadius * 1.4f,
                    root,
                    pipeMaterial
                );
            }
        }
    }

    private void CreateTerminalPipeManifold(
        FacetProjection projection,
        PlannedTerminal terminal,
        Transform parent,
        int index)
    {
        Transform root = FindOrCreateChild(
            parent,
            string.Format(
                "manifold_L{0:00}_{1:000}",
                terminal.layerIndex,
                index
            )
        );

        Vector3 direction = ToWorldDirection(
            projection,
            terminal.direction
        );

        Quaternion rotation =
            Quaternion.LookRotation(
                direction,
                projection.normal
            );

        CreateEquipmentPedestal(
            projection,
            terminal,
            root,
            rotation
        );

        Vector3 side = Vector3.Cross(
            projection.normal,
            direction
        ).normalized;

        Vector3 center =
            projection.ToWorld(
                terminal.position
            ) +
            projection.normal *
            (
                roofClearance +
                supportHeight +
                terminal.elevation +
                pipeRadius * 2f
            );

        int count = Mathf.Clamp(
            maximumPipeCount,
            2,
            8
        );

        for (int pipe = 0;
            pipe < count;
            pipe++)
        {
            float offset =
                (
                    pipe -
                    (count - 1) * 0.5f
                ) *
                (
                    pipeRadius * 2f +
                    pipeSpacing
                );

            Vector3 pipeCenter =
                center +
                side * offset;

            CreateCylinderBetween(
                string.Format(
                    "pipe_{0:00}",
                    pipe
                ),
                pipeCenter -
                    direction *
                    terminal.size.z * 0.45f,
                pipeCenter +
                    direction *
                    terminal.size.z * 0.45f,
                pipeRadius,
                root,
                pipeMaterial
            );
        }

        GameObject header = CreatePrimitive(
            PrimitiveType.Cube,
            "header",
            root,
            equipmentMaterial
        );

        header.transform.SetPositionAndRotation(
            center +
                direction *
                terminal.size.z * 0.42f,
            Quaternion.LookRotation(
                side,
                projection.normal
            )
        );

        header.transform.localScale =
            new Vector3(
                pipeRadius * 3f,
                pipeRadius * 3f,
                terminal.size.x
            );

        ApplyGeneratedFlags(header);
    }

    private GameObject CreatePrimitive(
        PrimitiveType primitiveType,
        string name,
        Transform parent,
        Material material)
    {
        GameObject generated = GameObject.CreatePrimitive(primitiveType);
        generated.name = name;
        Undo.RegisterCreatedObjectUndo(
            generated,
            "Generate Rooftop Mechanical"
        );
        generated.transform.SetParent(parent, true);

        Renderer renderer = generated.GetComponent<Renderer>();
        if (renderer != null && material != null)
        {
            renderer.sharedMaterial = material;
        }

        if (removePrimitiveColliders)
        {
            Collider collider = generated.GetComponent<Collider>();
            if (collider != null)
            {
                DestroyImmediate(collider);
            }
        }

        return generated;
    }

    private void CreateCylinderBetween(
        string name,
        Vector3 start,
        Vector3 end,
        float radius,
        Transform parent,
        Material material)
    {
        Vector3 delta = end - start;
        float length = delta.magnitude;
        if (length <= 0.0001f)
        {
            return;
        }

        GameObject cylinder = CreatePrimitive(
            PrimitiveType.Cylinder,
            name,
            parent,
            material
        );

        cylinder.transform.position = (start + end) * 0.5f;
        cylinder.transform.rotation = Quaternion.FromToRotation(
            Vector3.up,
            delta / length
        );
        cylinder.transform.localScale = new Vector3(
            radius,
            length * 0.5f,
            radius
        );
        ApplyGeneratedFlags(cylinder);
    }

    private void ApplyGeneratedFlags(GameObject generated)
    {
        if (generated == null)
        {
            return;
        }

        if (markGeneratedStatic)
        {
            GameObjectUtility.SetStaticEditorFlags(
                generated,
                StaticEditorFlags.BatchingStatic |
                StaticEditorFlags.ContributeGI |
                StaticEditorFlags.ReflectionProbeStatic
            );
        }
    }

    private void BuildPreviewLines(
        FacetPlan plan,
        RoofFacet facet)
    {
        plan.boundaryLines = new Vector3[
            facet.boundaryLineSegments.Count
        ];

        for (int index = 0;
            index < facet.boundaryLineSegments.Count;
            index++)
        {
            plan.boundaryLines[index] =
                facet.boundaryLineSegments[index] +
                facet.normal * 0.08f;
        }

        List<Vector3> segmentLines = new List<Vector3>();
        foreach (PlannedSegment segment in plan.segments)
        {
            AddSegmentRectangleLines(
                segmentLines,
                plan.projection,
                segment.center,
                segment.direction,
                segment.length,
                segment.width,
                0.16f +
                    supportHeight +
                    segment.elevation +
                    segment.height
            );
        }
        plan.segmentLines = segmentLines.ToArray();

        List<Vector3> terminalLines = new List<Vector3>();
        foreach (PlannedTerminal terminal in plan.terminals)
        {
            AddSegmentRectangleLines(
                terminalLines,
                plan.projection,
                terminal.position,
                terminal.direction,
                terminal.size.z,
                terminal.size.x,
                0.2f +
                    supportHeight +
                    terminal.elevation +
                    terminal.size.y
            );
        }
        plan.terminalLines = terminalLines.ToArray();
    }

    private static void AddSegmentRectangleLines(
        List<Vector3> lines,
        FacetProjection projection,
        Vector2 center,
        Vector2 direction,
        float length,
        float width,
        float heightOffset)
    {
        Vector2 side = new Vector2(-direction.y, direction.x);
        Vector2 halfForward = direction.normalized * length * 0.5f;
        Vector2 halfSide = side.normalized * width * 0.5f;

        Vector2 p0 = center - halfForward - halfSide;
        Vector2 p1 = center + halfForward - halfSide;
        Vector2 p2 = center + halfForward + halfSide;
        Vector2 p3 = center - halfForward + halfSide;

        Vector3 offset = projection.normal * heightOffset;
        Vector3 w0 = projection.ToWorld(p0) + offset;
        Vector3 w1 = projection.ToWorld(p1) + offset;
        Vector3 w2 = projection.ToWorld(p2) + offset;
        Vector3 w3 = projection.ToWorld(p3) + offset;

        lines.Add(w0); lines.Add(w1);
        lines.Add(w1); lines.Add(w2);
        lines.Add(w2); lines.Add(w3);
        lines.Add(w3); lines.Add(w0);
    }

    private void DrawScenePreview(SceneView sceneView)
    {
        if (facets.Count == 0)
        {
            return;
        }

        Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;

        if (showDetectedFacets)
        {
            for (int index = 0; index < facets.Count; index++)
            {
                RoofFacet facet = facets[index];
                if (!facet.enabled)
                {
                    continue;
                }

                Handles.color = GetFacetColor(index, 0.82f);
                Vector3[] lines = new Vector3[
                    facet.boundaryLineSegments.Count
                ];

                for (int line = 0;
                    line < facet.boundaryLineSegments.Count;
                    line++)
                {
                    lines[line] =
                        facet.boundaryLineSegments[line] +
                        facet.normal * 0.06f;
                }

                if (lines.Length >= 2)
                {
                    Handles.DrawLines(lines);
                }

                if (showFacetLabels)
                {
                    Handles.Label(
                        facet.center + facet.normal * 0.6f,
                        string.Format(
                            "Roof {0:00}\n{1:F1} m² · {2:F1}°",
                            index,
                            facet.area,
                            facet.Slope
                        )
                    );
                }
            }
        }

        if (previewDirty)
        {
            return;
        }

        foreach (FacetPlan plan in plans)
        {
            if (showNetworkPreview && plan.segmentLines.Length > 0)
            {
                Handles.color = new Color(0.25f, 0.9f, 1f, 0.95f);
                Handles.DrawLines(plan.segmentLines);
            }

            if (showTerminalPreview && plan.terminalLines.Length > 0)
            {
                Handles.color = new Color(1f, 0.62f, 0.18f, 0.95f);
                Handles.DrawLines(plan.terminalLines);
            }
        }
    }

    private void LogDetectedFacets()
    {
        Debug.Log(
            string.Format(
                "[Rooftop Mechanical] Building '{0}'\n" +
                "Bounds center {1}, size {2}\n" +
                "Scanned {3:N0}; accepted {4:N0}; facets {5}",
                buildingRoot.name,
                buildingBounds.center,
                buildingBounds.size,
                scannedTriangles,
                acceptedTriangles,
                facets.Count
            ),
            buildingRoot
        );
    }

    private Transform GetOrCreateOutputFolder()
    {
        if (siteRoot == null)
        {
            siteRoot = FindLikelySiteRoot(buildingRoot.transform);
        }

        if (siteRoot == null)
        {
            EditorUtility.DisplayDialog(
                "Rooftop Mechanical",
                "Assign a site root.",
                "OK"
            );
            return null;
        }

        Transform outputRoot = FindOrCreateChild(
            siteRoot.transform,
            SafeName(outputRootName, "rooftop_mechanical")
        );

        string resolvedBuildingName =
            string.IsNullOrWhiteSpace(buildingFolderName)
                ? buildingRoot.name
                : buildingFolderName.Trim();

        return FindOrCreateChild(
            outputRoot,
            resolvedBuildingName
        );
    }

    private Transform FindOutputFolder()
    {
        if (siteRoot == null || buildingRoot == null)
        {
            return null;
        }

        Transform outputRoot = FindChild(
            siteRoot.transform,
            SafeName(outputRootName, "rooftop_mechanical")
        );

        if (outputRoot == null)
        {
            return null;
        }

        string resolvedBuildingName =
            string.IsNullOrWhiteSpace(buildingFolderName)
                ? buildingRoot.name
                : buildingFolderName.Trim();

        return FindChild(outputRoot, resolvedBuildingName);
    }

    private void ClearGeneratedMechanical()
    {
        Transform output = FindOutputFolder();
        if (output == null)
        {
            return;
        }

        Undo.DestroyObjectImmediate(output.gameObject);
        EditorSceneManager.MarkSceneDirty(buildingRoot.scene);
    }

    private static void DestroyChildren(Transform parent)
    {
        for (int index = parent.childCount - 1;
            index >= 0;
            index--)
        {
            Undo.DestroyObjectImmediate(
                parent.GetChild(index).gameObject
            );
        }
    }

    private static Transform FindOrCreateChild(
        Transform parent,
        string name)
    {
        Transform child = FindChild(parent, name);
        if (child != null)
        {
            return child;
        }

        GameObject created = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(
            created,
            "Create Rooftop Mechanical Folder"
        );
        created.transform.SetParent(parent, false);
        return created.transform;
    }

    private static Transform FindChild(
        Transform parent,
        string name)
    {
        for (int index = 0; index < parent.childCount; index++)
        {
            Transform child = parent.GetChild(index);
            if (child.name == name)
            {
                return child;
            }
        }

        return null;
    }

    private static GameObject FindLikelySiteRoot(Transform building)
    {
        if (building == null)
        {
            return null;
        }

        Transform cursor = building;
        GameObject last = building.gameObject;

        while (cursor.parent != null)
        {
            cursor = cursor.parent;
            last = cursor.gameObject;

            if (
                cursor.name.IndexOf(
                    "cib_site",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0
            )
            {
                return cursor.gameObject;
            }
        }

        return last;
    }

    private static string SafeName(
        string value,
        string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
    }

    private static Vector3 ToWorldDirection(
        FacetProjection projection,
        Vector2 direction)
    {
        return (
            projection.u * direction.x +
            projection.v * direction.y
        ).normalized;
    }

    private static Vector2 Rotate2D(
        Vector2 value,
        float angleDegrees)
    {
        float radians = angleDegrees * Mathf.Deg2Rad;
        float cosine = Mathf.Cos(radians);
        float sine = Mathf.Sin(radians);

        return new Vector2(
            value.x * cosine - value.y * sine,
            value.x * sine + value.y * cosine
        );
    }

    private static float DistancePointToSegment(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        Vector2 segment = end - start;
        float denominator = segment.sqrMagnitude;

        if (denominator <= 0.000001f)
        {
            return Vector2.Distance(point, start);
        }

        float t = Mathf.Clamp01(
            Vector2.Dot(point - start, segment) /
            denominator
        );

        return Vector2.Distance(
            point,
            start + segment * t
        );
    }

    private static float Range(
        System.Random random,
        float minimum,
        float maximum)
    {
        if (maximum < minimum)
        {
            float temporary = minimum;
            minimum = maximum;
            maximum = temporary;
        }

        return Mathf.Lerp(
            minimum,
            maximum,
            (float)random.NextDouble()
        );
    }

    private static int CombineSeed(int first, int second)
    {
        unchecked
        {
            return first * 486187739 + second * 16777619;
        }
    }

    private static Vector3 Abs(Vector3 value)
    {
        return new Vector3(
            Mathf.Abs(value.x),
            Mathf.Abs(value.y),
            Mathf.Abs(value.z)
        );
    }

    private static Color GetFacetColor(int index, float alpha)
    {
        float hue = Mathf.Repeat(index * 0.173f, 1f);
        Color color = Color.HSVToRGB(hue, 0.72f, 1f);
        color.a = alpha;
        return color;
    }

    private static string GetPath(Transform transform)
    {
        if (transform == null)
        {
            return "<null>";
        }

        string path = transform.name;
        Transform cursor = transform.parent;

        while (cursor != null)
        {
            path = cursor.name + "/" + path;
            cursor = cursor.parent;
        }

        return path;
    }
}

#endif
