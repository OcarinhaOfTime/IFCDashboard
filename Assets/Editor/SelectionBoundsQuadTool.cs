#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public sealed class SelectionBoundsQuadTool : EditorWindow
{
    private const string DefaultGroupName = "generated_quads";
    private const string DefaultMeshFolder = "Assets/Generated/QuadMeshes";
    private const float GeometryEpsilon = 0.000001f;

    private enum PlaneMode
    {
        AutoThinAxis,
        LocalXY,
        LocalXZ,
        LocalYZ
    }

    [Header("Source")]
    [SerializeField] private Transform root;
    [SerializeField] private Transform source;
    [SerializeField] private bool includeChildMeshes = true;

    [Header("Geometry Fit")]
    [SerializeField] private PlaneMode planeMode = PlaneMode.AutoThinAxis;
    [SerializeField, Range(0.5f, 0.999f)]
    private float surfaceNormalAlignment = 0.85f;
    [SerializeField] private bool alignLongestEdgeToTextureX = true;
    [SerializeField] private bool forceNormalUp = true;
    [SerializeField] private bool flipTextureU;
    [SerializeField] private bool flipTextureV;
    [SerializeField] private float surfaceOffset = 0.02f;

    [Header("Output")]
    [SerializeField] private string generatedGroupName = DefaultGroupName;
    [SerializeField] private string nameSuffix = "_BoundsQuad";
    [SerializeField] private Material material;
    [SerializeField] private bool copyFirstSourceMaterial;
    [SerializeField] private bool copyLayer = true;
    [SerializeField] private bool copyTag = true;
    [SerializeField] private bool copyStaticFlags = true;
    [SerializeField] private bool deactivateSource;

    [SerializeField] private string statusMessage = string.Empty;
    [SerializeField] private MessageType statusType = MessageType.Info;

    private readonly struct TriangleSample
    {
        public readonly Vector3 A;
        public readonly Vector3 B;
        public readonly Vector3 C;
        public readonly Vector3 Normal;
        public readonly Vector3 Centroid;
        public readonly float Area;

        public TriangleSample(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 normal,
            float area)
        {
            A = a;
            B = b;
            C = c;
            Normal = normal;
            Centroid = (a + b + c) / 3.0f;
            Area = area;
        }
    }

    private sealed class GeometryData
    {
        public readonly List<Vector3> VerticesWS = new List<Vector3>();
        public readonly List<TriangleSample> Triangles = new List<TriangleSample>();
        public Bounds SourceLocalBounds;
        public bool HasLocalBounds;
    }

    private readonly struct RectangleFit
    {
        public readonly Vector3 CenterWS;
        public readonly Vector3 RightWS;
        public readonly Vector3 UpWS;
        public readonly Vector3 NormalWS;
        public readonly float Width;
        public readonly float Height;
        public readonly int HullPointCount;

        public RectangleFit(
            Vector3 centerWS,
            Vector3 rightWS,
            Vector3 upWS,
            Vector3 normalWS,
            float width,
            float height,
            int hullPointCount)
        {
            CenterWS = centerWS;
            RightWS = rightWS;
            UpWS = upWS;
            NormalWS = normalWS;
            Width = width;
            Height = height;
            HullPointCount = hullPointCount;
        }
    }

    [MenuItem("Tools/Geometry/Bounds-Matched Quad")]
    private static void OpenWindow()
    {
        SelectionBoundsQuadTool window =
            GetWindow<SelectionBoundsQuadTool>("Bounds-Matched Quad");

        window.minSize = new Vector2(460.0f, 590.0f);
        window.AssignSelection();
        window.Show();
    }

    private void OnEnable()
    {
        AssignSelection();
    }

    private void OnSelectionChange()
    {
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(
            "Geometry-Fitted Quad",
            EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "Creates or updates a four-vertex mesh under Root/generated_quads. " +
            "The orientation is calculated from the source mesh vertices: the tool " +
            "projects the dominant surface onto a plane, computes its convex hull, " +
            "and fits the minimum-area oriented rectangle.",
            MessageType.None);

        DrawSourceSection();

        EditorGUILayout.Space(8.0f);
        DrawFitSection();

        EditorGUILayout.Space(8.0f);
        DrawOutputSection();

        EditorGUILayout.Space(10.0f);

        using (new EditorGUI.DisabledScope(source == null || root == null))
        {
            if (GUILayout.Button(
                    "Create / Update Generated Quad",
                    GUILayout.Height(34.0f)))
            {
                CreateOrUpdateQuad();
            }
        }

        using (new EditorGUI.DisabledScope(source == null || root == null))
        {
            if (GUILayout.Button("Toggle Source / Generated Quad"))
            {
                ToggleSourceAndQuad();
            }
        }

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            EditorGUILayout.Space(6.0f);
            EditorGUILayout.HelpBox(statusMessage, statusType);
        }
    }

    private void DrawSourceSection()
    {
        EditorGUILayout.LabelField("Source Hierarchy", EditorStyles.boldLabel);

        root = (Transform)EditorGUILayout.ObjectField(
            new GUIContent(
                "Root",
                "Generated objects are placed under this object's generated_quads child."),
            root,
            typeof(Transform),
            true);

        using (new EditorGUILayout.HorizontalScope())
        {
            source = (Transform)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Source",
                    "The mesh hierarchy whose actual vertices define the generated rectangle."),
                source,
                typeof(Transform),
                true);

            using (new EditorGUI.DisabledScope(Selection.activeTransform == null))
            {
                if (GUILayout.Button("Use Selection", GUILayout.Width(105.0f)))
                {
                    source = Selection.activeTransform;
                    root = FindSuggestedRoot(source);

                    SetStatus(
                        source != null
                            ? $"Source set to '{source.name}'."
                            : "No GameObject is selected.",
                        source != null
                            ? MessageType.Info
                            : MessageType.Warning);
                }
            }
        }

        includeChildMeshes = EditorGUILayout.Toggle(
            new GUIContent(
                "Include Child Meshes",
                "Collect geometry from MeshFilters and SkinnedMeshRenderers in the source hierarchy."),
            includeChildMeshes);

        if (root == null)
        {
            EditorGUILayout.HelpBox(
                "Assign cib_site as Root, or select a child of cib_site and click Use Selection.",
                MessageType.Warning);
        }
        else if (source != null && source != root && !source.IsChildOf(root))
        {
            EditorGUILayout.HelpBox(
                "The Source is not inside the selected Root hierarchy.",
                MessageType.Warning);
        }
    }

    private void DrawFitSection()
    {
        EditorGUILayout.LabelField("Geometry Rectangle Fit", EditorStyles.boldLabel);

        planeMode = (PlaneMode)EditorGUILayout.EnumPopup(
            new GUIContent(
                "Plane Normal",
                "Auto finds the thinnest source-local geometry axis. The chosen axis only " +
                "defines the projection plane; the rectangle yaw comes from the actual vertices."),
            planeMode);

        surfaceNormalAlignment = EditorGUILayout.Slider(
            new GUIContent(
                "Surface Alignment",
                "Triangles whose normals align with the detected plane contribute to the footprint. " +
                "Higher values reject more side-wall geometry."),
            surfaceNormalAlignment,
            0.5f,
            0.999f);

        alignLongestEdgeToTextureX = EditorGUILayout.Toggle(
            new GUIContent(
                "Longest Edge → Texture X",
                "Maps the fitted rectangle's longest side to the texture's horizontal U axis."),
            alignLongestEdgeToTextureX);

        forceNormalUp = EditorGUILayout.Toggle(
            new GUIContent(
                "Force Front Face Up",
                "For horizontal surfaces, orient the generated front face toward world +Y."),
            forceNormalUp);

        flipTextureU = EditorGUILayout.Toggle(
            new GUIContent(
                "Flip Texture U",
                "Mirror the texture horizontally without changing geometry."),
            flipTextureU);

        flipTextureV = EditorGUILayout.Toggle(
            new GUIContent(
                "Flip Texture V",
                "Mirror the texture vertically without changing geometry."),
            flipTextureV);

        surfaceOffset = EditorGUILayout.FloatField(
            new GUIContent(
                "Surface Offset",
                "World-space displacement along the generated front normal. " +
                "A small positive value prevents z-fighting."),
            surfaceOffset);
    }

    private void DrawOutputSection()
    {
        EditorGUILayout.LabelField("Generated Output", EditorStyles.boldLabel);

        generatedGroupName = EditorGUILayout.TextField(
            new GUIContent(
                "Group Name",
                "Direct child created under Root for generated Quad objects."),
            generatedGroupName);

        nameSuffix = EditorGUILayout.TextField(
            new GUIContent(
                "Name Suffix",
                "The result is named SourceName + Suffix."),
            nameSuffix);

        material = (Material)EditorGUILayout.ObjectField(
            new GUIContent(
                "Material",
                "Optional material for the generated field."),
            material,
            typeof(Material),
            false);

        copyFirstSourceMaterial = EditorGUILayout.Toggle(
            new GUIContent(
                "Fallback to Source Material",
                "When Material is empty, use the first material found on the source hierarchy."),
            copyFirstSourceMaterial);

        copyLayer = EditorGUILayout.Toggle("Copy Layer", copyLayer);
        copyTag = EditorGUILayout.Toggle("Copy Tag", copyTag);
        copyStaticFlags = EditorGUILayout.Toggle(
            "Copy Static Flags",
            copyStaticFlags);

        deactivateSource = EditorGUILayout.Toggle(
            new GUIContent(
                "Deactivate Source",
                "Deactivate the original mesh after generating the replacement."),
            deactivateSource);
    }

    private void AssignSelection()
    {
        if (Selection.activeTransform == null)
        {
            return;
        }

        if (source == null)
        {
            source = Selection.activeTransform;
        }

        if (root == null)
        {
            root = FindSuggestedRoot(source);
        }
    }

    private static Transform FindSuggestedRoot(Transform selected)
    {
        if (selected == null)
        {
            return null;
        }

        Transform current = selected;
        Transform topmost = selected;

        while (current != null)
        {
            topmost = current;

            if (string.Equals(
                    current.name,
                    "cib_site",
                    StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            current = current.parent;
        }

        return topmost;
    }

    private void CreateOrUpdateQuad()
    {
        if (!ValidateSourceAndRoot(out string validationError))
        {
            SetStatus(validationError, MessageType.Error);
            return;
        }

        if (!TryCollectGeometry(
                source,
                includeChildMeshes,
                out GeometryData geometry,
                out string geometryError))
        {
            SetStatus(geometryError, MessageType.Error);
            return;
        }

        if (!TryFitMinimumAreaRectangle(
                source,
                geometry,
                planeMode,
                surfaceNormalAlignment,
                alignLongestEdgeToTextureX,
                forceNormalUp,
                out RectangleFit fit,
                out string fitError))
        {
            SetStatus(fitError, MessageType.Error);
            return;
        }

        string groupName = string.IsNullOrWhiteSpace(generatedGroupName)
            ? DefaultGroupName
            : generatedGroupName.Trim();

        string safeSuffix = string.IsNullOrWhiteSpace(nameSuffix)
            ? "_BoundsQuad"
            : nameSuffix;

        string outputName = source.name + safeSuffix;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName($"Generate Quad for {source.name}");

        try
        {
            Transform generatedGroup =
                GetOrCreateGeneratedGroup(root, groupName);

            GameObject output =
                FindExistingOutput(root, generatedGroup, outputName);

            if (output == null)
            {
                output = new GameObject(outputName);
                Undo.RegisterCreatedObjectUndo(
                    output,
                    "Create generated Quad");

                Undo.SetTransformParent(
                    output.transform,
                    generatedGroup,
                    "Parent generated Quad");
            }
            else if (output.transform.parent != generatedGroup)
            {
                Undo.SetTransformParent(
                    output.transform,
                    generatedGroup,
                    "Move generated Quad to group");
            }

            MeshFilter meshFilter =
                GetOrAddComponentWithUndo<MeshFilter>(output);

            MeshRenderer meshRenderer =
                GetOrAddComponentWithUndo<MeshRenderer>(output);

            Mesh mesh =
                GetOrCreateMeshAsset(source, outputName);

            Undo.RegisterCompleteObjectUndo(
                mesh,
                "Update generated Quad mesh");

            BuildQuadMesh(
                mesh,
                generatedGroup,
                fit.CenterWS + fit.NormalWS * surfaceOffset,
                fit.RightWS,
                fit.UpWS,
                fit.Width,
                fit.Height,
                flipTextureU,
                flipTextureV);

            Undo.RecordObject(
                output.transform,
                "Reset generated Quad transform");

            output.transform.localPosition = Vector3.zero;
            output.transform.localRotation = Quaternion.identity;
            output.transform.localScale = Vector3.one;

            Undo.RecordObject(
                meshFilter,
                "Assign generated Quad mesh");

            meshFilter.sharedMesh = mesh;

            ApplyOutputMaterial(meshRenderer);
            ApplyOutputMetadata(output);

            if (!output.activeSelf)
            {
                Undo.RecordObject(
                    output,
                    "Activate generated Quad");

                output.SetActive(true);
            }

            if (deactivateSource && source.gameObject.activeSelf)
            {
                Undo.RecordObject(
                    source.gameObject,
                    "Deactivate Quad source");

                source.gameObject.SetActive(false);
            }

            EditorUtility.SetDirty(mesh);
            EditorUtility.SetDirty(meshFilter);
            EditorUtility.SetDirty(meshRenderer);
            EditorUtility.SetDirty(output);

            AssetDatabase.SaveAssets();

            Selection.activeGameObject = output;
            SceneView.FrameLastActiveSceneView();

            SetStatus(
                $"Generated '{root.name}/{groupName}/{outputName}' at " +
                $"{fit.Width:0.###} × {fit.Height:0.###} world units. " +
                $"Hull points: {fit.HullPointCount}. Front normal: {fit.NormalWS}.",
                MessageType.Info);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);

            SetStatus(
                $"Could not generate the Quad: {exception.Message}",
                MessageType.Error);
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private void ToggleSourceAndQuad()
    {
        if (!ValidateSourceAndRoot(out string validationError))
        {
            SetStatus(validationError, MessageType.Error);
            return;
        }

        string groupName = string.IsNullOrWhiteSpace(generatedGroupName)
            ? DefaultGroupName
            : generatedGroupName.Trim();

        Transform generatedGroup =
            FindDirectChild(root, groupName);

        if (generatedGroup == null)
        {
            SetStatus(
                $"'{root.name}/{groupName}' does not exist yet.",
                MessageType.Warning);
            return;
        }

        string safeSuffix = string.IsNullOrWhiteSpace(nameSuffix)
            ? "_BoundsQuad"
            : nameSuffix;

        string outputName = source.name + safeSuffix;

        Transform output =
            FindDirectChild(generatedGroup, outputName);

        if (output == null)
        {
            SetStatus(
                $"Generated object '{outputName}' was not found.",
                MessageType.Warning);
            return;
        }

        bool showSource = !source.gameObject.activeSelf;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Toggle source and generated Quad");

        try
        {
            Undo.RecordObject(
                source.gameObject,
                "Toggle Quad source");

            Undo.RecordObject(
                output.gameObject,
                "Toggle generated Quad");

            source.gameObject.SetActive(showSource);
            output.gameObject.SetActive(!showSource);

            Selection.activeGameObject =
                showSource
                    ? source.gameObject
                    : output.gameObject;

            SetStatus(
                showSource
                    ? "Showing the original source."
                    : "Showing the generated Quad.",
                MessageType.Info);
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private bool ValidateSourceAndRoot(out string error)
    {
        if (source == null)
        {
            error = "Assign a Source transform first.";
            return false;
        }

        if (root == null)
        {
            error = "Assign the cib_site Root transform first.";
            return false;
        }

        if (source != root && !source.IsChildOf(root))
        {
            error =
                $"Source '{source.name}' is not inside Root '{root.name}'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static Transform GetOrCreateGeneratedGroup(
        Transform root,
        string groupName)
    {
        Transform existing =
            FindDirectChild(root, groupName);

        if (existing != null)
        {
            return existing;
        }

        GameObject groupObject =
            new GameObject(groupName);

        Undo.RegisterCreatedObjectUndo(
            groupObject,
            "Create generated Quad group");

        Undo.SetTransformParent(
            groupObject.transform,
            root,
            "Parent generated Quad group");

        groupObject.transform.localPosition = Vector3.zero;
        groupObject.transform.localRotation = Quaternion.identity;
        groupObject.transform.localScale = Vector3.one;

        return groupObject.transform;
    }

    private static GameObject FindExistingOutput(
        Transform root,
        Transform generatedGroup,
        string outputName)
    {
        Transform direct =
            FindDirectChild(generatedGroup, outputName);

        if (direct != null)
        {
            return direct.gameObject;
        }

        Transform[] hierarchy =
            root.GetComponentsInChildren<Transform>(true);

        foreach (Transform candidate in hierarchy)
        {
            if (candidate == root ||
                candidate == generatedGroup ||
                candidate.name != outputName)
            {
                continue;
            }

            return candidate.gameObject;
        }

        return null;
    }

    private static Transform FindDirectChild(
        Transform parent,
        string childName)
    {
        if (parent == null)
        {
            return null;
        }

        for (int index = 0; index < parent.childCount; index++)
        {
            Transform child = parent.GetChild(index);

            if (string.Equals(
                    child.name,
                    childName,
                    StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    private static T GetOrAddComponentWithUndo<T>(GameObject target)
        where T : Component
    {
        T existing = target.GetComponent<T>();

        return existing != null
            ? existing
            : Undo.AddComponent<T>(target);
    }

    private static Mesh GetOrCreateMeshAsset(
        Transform source,
        string outputName)
    {
        EnsureAssetFolderExists(DefaultMeshFolder);

        string hierarchyPath = GetHierarchyPath(source);
        string hash = StableHash(hierarchyPath).ToString("X8");
        string filename = SanitizeFilename($"{outputName}_{hash}.asset");
        string assetPath = $"{DefaultMeshFolder}/{filename}";

        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);

        if (existing != null)
        {
            return existing;
        }

        Mesh mesh = new Mesh
        {
            name = outputName + "_Mesh"
        };

        AssetDatabase.CreateAsset(mesh, assetPath);
        return mesh;
    }

    private static void EnsureAssetFolderExists(string folderPath)
    {
        string[] parts = folderPath.Split('/');
        string current = parts[0];

        for (int index = 1; index < parts.Length; index++)
        {
            string next = current + "/" + parts[index];

            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[index]);
            }

            current = next;
        }
    }

    private static string SanitizeFilename(string filename)
    {
        HashSet<char> invalid =
            new HashSet<char>(Path.GetInvalidFileNameChars());

        char[] characters = filename
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();

        return new string(characters);
    }

    private static string GetHierarchyPath(Transform transform)
    {
        Stack<string> parts = new Stack<string>();
        Transform current = transform;

        while (current != null)
        {
            parts.Push($"{current.name}[{current.GetSiblingIndex()}]");
            current = current.parent;
        }

        return string.Join("/", parts);
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            uint hash = offsetBasis;

            foreach (char character in value)
            {
                hash ^= character;
                hash *= prime;
            }

            return (int)hash;
        }
    }

    private static void BuildQuadMesh(
        Mesh mesh,
        Transform generatedGroup,
        Vector3 centerWS,
        Vector3 rightWS,
        Vector3 upWS,
        float width,
        float height,
        bool flipU,
        bool flipV)
    {
        Vector3 halfRight = rightWS * (width * 0.5f);
        Vector3 halfUp = upWS * (height * 0.5f);

        Vector3 bottomLeftWS = centerWS - halfRight - halfUp;
        Vector3 bottomRightWS = centerWS + halfRight - halfUp;
        Vector3 topLeftWS = centerWS - halfRight + halfUp;
        Vector3 topRightWS = centerWS + halfRight + halfUp;

        Vector3[] vertices =
        {
            generatedGroup.InverseTransformPoint(bottomLeftWS),
            generatedGroup.InverseTransformPoint(bottomRightWS),
            generatedGroup.InverseTransformPoint(topLeftWS),
            generatedGroup.InverseTransformPoint(topRightWS)
        };

        float u0 = flipU ? 1.0f : 0.0f;
        float u1 = flipU ? 0.0f : 1.0f;
        float v0 = flipV ? 1.0f : 0.0f;
        float v1 = flipV ? 0.0f : 1.0f;

        Vector2[] uv =
        {
            new Vector2(u0, v0),
            new Vector2(u1, v0),
            new Vector2(u0, v1),
            new Vector2(u1, v1)
        };

        int[] triangles =
        {
            0, 1, 2,
            2, 1, 3
        };

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
    }

    private void ApplyOutputMaterial(MeshRenderer outputRenderer)
    {
        Undo.RecordObject(
            outputRenderer,
            "Assign generated Quad material");

        Material selectedMaterial = material;

        if (selectedMaterial == null && copyFirstSourceMaterial)
        {
            Renderer[] sourceRenderers = includeChildMeshes
                ? source.GetComponentsInChildren<Renderer>(true)
                : source.GetComponents<Renderer>();

            foreach (Renderer sourceRenderer in sourceRenderers)
            {
                Material[] sharedMaterials = sourceRenderer.sharedMaterials;

                if (sharedMaterials.Length > 0 && sharedMaterials[0] != null)
                {
                    selectedMaterial = sharedMaterials[0];
                    break;
                }
            }
        }

        if (selectedMaterial != null)
        {
            outputRenderer.sharedMaterial = selectedMaterial;
        }
    }

    private void ApplyOutputMetadata(GameObject output)
    {
        Undo.RecordObject(output, "Copy generated Quad metadata");

        if (copyLayer)
        {
            output.layer = source.gameObject.layer;
        }

        if (copyTag)
        {
            try
            {
                output.tag = source.gameObject.tag;
            }
            catch (UnityException)
            {
                // Ignore missing project-specific tags.
            }
        }

        if (copyStaticFlags)
        {
            StaticEditorFlags flags =
                GameObjectUtility.GetStaticEditorFlags(source.gameObject);

            GameObjectUtility.SetStaticEditorFlags(output, flags);
        }
    }

    private static bool TryCollectGeometry(
        Transform source,
        bool includeChildren,
        out GeometryData geometry,
        out string error)
    {
        geometry = new GeometryData();
        error = string.Empty;

        MeshFilter[] meshFilters = includeChildren
            ? source.GetComponentsInChildren<MeshFilter>(true)
            : source.GetComponents<MeshFilter>();

        foreach (MeshFilter meshFilter in meshFilters)
        {
            Mesh mesh = meshFilter.sharedMesh;

            if (mesh == null)
            {
                continue;
            }

            if (!mesh.isReadable)
            {
                error =
                    $"Mesh '{mesh.name}' is not readable. Enable Read/Write on its model import settings " +
                    "before using geometry fitting.";
                return false;
            }

            AddMeshGeometry(
                mesh,
                meshFilter.transform.localToWorldMatrix,
                source,
                geometry);
        }

        SkinnedMeshRenderer[] skinnedRenderers = includeChildren
            ? source.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            : source.GetComponents<SkinnedMeshRenderer>();

        foreach (SkinnedMeshRenderer skinnedRenderer in skinnedRenderers)
        {
            Mesh bakedMesh = new Mesh();

            try
            {
                skinnedRenderer.BakeMesh(bakedMesh);

                AddMeshGeometry(
                    bakedMesh,
                    skinnedRenderer.transform.localToWorldMatrix,
                    source,
                    geometry);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bakedMesh);
            }
        }

        if (geometry.VerticesWS.Count < 3)
        {
            error =
                $"'{source.name}' does not contain enough readable mesh vertices to fit a rectangle.";
            return false;
        }

        if (!geometry.HasLocalBounds)
        {
            error = $"Could not calculate source-local geometry bounds for '{source.name}'.";
            return false;
        }

        return true;
    }

    private static void AddMeshGeometry(
        Mesh mesh,
        Matrix4x4 localToWorld,
        Transform source,
        GeometryData geometry)
    {
        Vector3[] localVertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        Vector3[] worldVertices = new Vector3[localVertices.Length];

        for (int index = 0; index < localVertices.Length; index++)
        {
            Vector3 vertexWS = localToWorld.MultiplyPoint3x4(localVertices[index]);
            worldVertices[index] = vertexWS;
            geometry.VerticesWS.Add(vertexWS);

            Vector3 vertexSourceLS = source.InverseTransformPoint(vertexWS);
            EncapsulatePoint(
                vertexSourceLS,
                ref geometry.SourceLocalBounds,
                ref geometry.HasLocalBounds);
        }

        for (int index = 0; index + 2 < triangles.Length; index += 3)
        {
            int indexA = triangles[index];
            int indexB = triangles[index + 1];
            int indexC = triangles[index + 2];

            if (indexA < 0 || indexA >= worldVertices.Length ||
                indexB < 0 || indexB >= worldVertices.Length ||
                indexC < 0 || indexC >= worldVertices.Length)
            {
                continue;
            }

            Vector3 a = worldVertices[indexA];
            Vector3 b = worldVertices[indexB];
            Vector3 c = worldVertices[indexC];
            Vector3 cross = Vector3.Cross(b - a, c - a);
            float doubleArea = cross.magnitude;

            if (doubleArea <= GeometryEpsilon)
            {
                continue;
            }

            geometry.Triangles.Add(
                new TriangleSample(
                    a,
                    b,
                    c,
                    cross / doubleArea,
                    doubleArea * 0.5f));
        }
    }

    private static void EncapsulatePoint(
        Vector3 point,
        ref Bounds bounds,
        ref bool initialized)
    {
        if (!initialized)
        {
            bounds = new Bounds(point, Vector3.zero);
            initialized = true;
            return;
        }

        bounds.Encapsulate(point);
    }

    private static bool TryFitMinimumAreaRectangle(
        Transform source,
        GeometryData geometry,
        PlaneMode mode,
        float normalAlignment,
        bool alignLongestEdgeToX,
        bool forceUp,
        out RectangleFit fit,
        out string error)
    {
        fit = default;
        error = string.Empty;

        Vector3 approximateNormal = GetApproximateWorldNormal(
            source,
            geometry.SourceLocalBounds.size,
            mode);

        if (approximateNormal.sqrMagnitude <= GeometryEpsilon)
        {
            error = "Could not determine a valid projection-plane normal.";
            return false;
        }

        Vector3 normalWS = RefineNormalFromTriangles(
            approximateNormal,
            geometry.Triangles,
            normalAlignment);

        if (forceUp && Vector3.Dot(normalWS, Vector3.up) < 0.0f)
        {
            normalWS = -normalWS;
        }

        BuildPlaneBasis(
            normalWS,
            source,
            out Vector3 planeRightWS,
            out Vector3 planeUpWS);

        Vector3 originWS = Average(geometry.VerticesWS);
        List<Vector3> footprintVertices = GetAlignedSurfaceVertices(
            geometry,
            normalWS,
            normalAlignment);

        if (footprintVertices.Count < 3)
        {
            footprintVertices = geometry.VerticesWS;
        }

        List<Vector2> projectedPoints = new List<Vector2>(footprintVertices.Count);

        foreach (Vector3 vertexWS in footprintVertices)
        {
            Vector3 relative = vertexWS - originWS;

            projectedPoints.Add(
                new Vector2(
                    Vector3.Dot(relative, planeRightWS),
                    Vector3.Dot(relative, planeUpWS)));
        }

        List<Vector2> hull = ComputeConvexHull(projectedPoints);

        if (hull.Count < 3)
        {
            error =
                "The projected geometry is degenerate and cannot produce an oriented rectangle.";
            return false;
        }

        if (!TryFindMinimumAreaRectangle(
                hull,
                out Vector2 center2D,
                out Vector2 rectangleAxisX,
                out Vector2 rectangleAxisY,
                out float width,
                out float height))
        {
            error = "Could not fit a minimum-area rectangle to the projected geometry.";
            return false;
        }

        Vector3 rightWS =
            (planeRightWS * rectangleAxisX.x +
             planeUpWS * rectangleAxisX.y).normalized;

        Vector3 upWS =
            (planeRightWS * rectangleAxisY.x +
             planeUpWS * rectangleAxisY.y).normalized;

        if (alignLongestEdgeToX && height > width)
        {
            float previousWidth = width;
            width = height;
            height = previousWidth;

            Vector3 previousRight = rightWS;
            rightWS = upWS;
            upWS = -previousRight;
        }

        StabilizeRectangleDirection(source, normalWS, ref rightWS, ref upWS);

        float topSurfaceDistance = GetTopSurfaceDistance(
            geometry,
            originWS,
            normalWS,
            normalAlignment);

        Vector3 centerWS =
            originWS +
            planeRightWS * center2D.x +
            planeUpWS * center2D.y +
            normalWS * topSurfaceDistance;

        fit = new RectangleFit(
            centerWS,
            rightWS,
            upWS,
            normalWS,
            width,
            height,
            hull.Count);

        return true;
    }

    private static Vector3 GetApproximateWorldNormal(
        Transform source,
        Vector3 localSize,
        PlaneMode mode)
    {
        int normalAxis = ResolveNormalAxis(localSize, mode);

        Vector3 localNormal;

        switch (normalAxis)
        {
            case 0:
                localNormal = Vector3.right;
                break;

            case 1:
                localNormal = Vector3.up;
                break;

            default:
                localNormal = Vector3.forward;
                break;
        }

        Matrix4x4 inverseTranspose =
            source.localToWorldMatrix.inverse.transpose;

        return inverseTranspose.MultiplyVector(localNormal).normalized;
    }

    private static int ResolveNormalAxis(Vector3 size, PlaneMode mode)
    {
        switch (mode)
        {
            case PlaneMode.LocalXY:
                return 2;

            case PlaneMode.LocalXZ:
                return 1;

            case PlaneMode.LocalYZ:
                return 0;
        }

        if (size.x <= size.y && size.x <= size.z)
        {
            return 0;
        }

        return size.y <= size.z ? 1 : 2;
    }

    private static Vector3 RefineNormalFromTriangles(
        Vector3 approximateNormal,
        List<TriangleSample> triangles,
        float alignment)
    {
        Vector3 weightedNormal = Vector3.zero;

        foreach (TriangleSample triangle in triangles)
        {
            float dot = Vector3.Dot(triangle.Normal, approximateNormal);

            if (Mathf.Abs(dot) < alignment)
            {
                continue;
            }

            weightedNormal +=
                triangle.Normal *
                Mathf.Sign(dot) *
                triangle.Area;
        }

        return weightedNormal.sqrMagnitude > GeometryEpsilon
            ? weightedNormal.normalized
            : approximateNormal.normalized;
    }

    private static void BuildPlaneBasis(
        Vector3 normalWS,
        Transform source,
        out Vector3 rightWS,
        out Vector3 upWS)
    {
        Vector3 preferred = Vector3.ProjectOnPlane(source.right, normalWS);

        if (preferred.sqrMagnitude <= GeometryEpsilon)
        {
            preferred = Vector3.ProjectOnPlane(Vector3.right, normalWS);
        }

        if (preferred.sqrMagnitude <= GeometryEpsilon)
        {
            preferred = Vector3.ProjectOnPlane(Vector3.forward, normalWS);
        }

        rightWS = preferred.normalized;
        upWS = Vector3.Cross(normalWS, rightWS).normalized;
        rightWS = Vector3.Cross(upWS, normalWS).normalized;
    }

    private static Vector3 Average(List<Vector3> values)
    {
        Vector3 sum = Vector3.zero;

        foreach (Vector3 value in values)
        {
            sum += value;
        }

        return sum / Mathf.Max(1, values.Count);
    }

    private static List<Vector3> GetAlignedSurfaceVertices(
        GeometryData geometry,
        Vector3 normalWS,
        float alignment)
    {
        List<Vector3> points = new List<Vector3>();

        foreach (TriangleSample triangle in geometry.Triangles)
        {
            if (Mathf.Abs(Vector3.Dot(triangle.Normal, normalWS)) < alignment)
            {
                continue;
            }

            points.Add(triangle.A);
            points.Add(triangle.B);
            points.Add(triangle.C);
        }

        return points;
    }

    private static float GetTopSurfaceDistance(
        GeometryData geometry,
        Vector3 originWS,
        Vector3 normalWS,
        float alignment)
    {
        float positiveArea = 0.0f;
        float positiveDistance = 0.0f;
        float negativeArea = 0.0f;
        float negativeDistance = 0.0f;

        foreach (TriangleSample triangle in geometry.Triangles)
        {
            float facing = Vector3.Dot(triangle.Normal, normalWS);

            if (Mathf.Abs(facing) < alignment)
            {
                continue;
            }

            float distance = Vector3.Dot(
                triangle.Centroid - originWS,
                normalWS);

            if (facing >= 0.0f)
            {
                positiveArea += triangle.Area;
                positiveDistance += distance * triangle.Area;
            }
            else
            {
                negativeArea += triangle.Area;
                negativeDistance += distance * triangle.Area;
            }
        }

        bool hasPositive = positiveArea > GeometryEpsilon;
        bool hasNegative = negativeArea > GeometryEpsilon;

        if (hasPositive || hasNegative)
        {
            float positiveMean = hasPositive
                ? positiveDistance / positiveArea
                : float.NegativeInfinity;

            float negativeMean = hasNegative
                ? negativeDistance / negativeArea
                : float.NegativeInfinity;

            return Mathf.Max(positiveMean, negativeMean);
        }

        float maximum = float.NegativeInfinity;

        foreach (Vector3 vertexWS in geometry.VerticesWS)
        {
            maximum = Mathf.Max(
                maximum,
                Vector3.Dot(vertexWS - originWS, normalWS));
        }

        return float.IsNegativeInfinity(maximum) ? 0.0f : maximum;
    }

    private static List<Vector2> ComputeConvexHull(List<Vector2> input)
    {
        if (input.Count <= 1)
        {
            return new List<Vector2>(input);
        }

        List<Vector2> points = input
            .OrderBy(point => point.x)
            .ThenBy(point => point.y)
            .ToList();

        List<Vector2> unique = new List<Vector2>(points.Count);

        foreach (Vector2 point in points)
        {
            if (unique.Count == 0 ||
                (point - unique[unique.Count - 1]).sqrMagnitude > GeometryEpsilon)
            {
                unique.Add(point);
            }
        }

        if (unique.Count <= 2)
        {
            return unique;
        }

        List<Vector2> lower = new List<Vector2>();

        foreach (Vector2 point in unique)
        {
            while (lower.Count >= 2 &&
                   Cross2D(
                       lower[lower.Count - 1] - lower[lower.Count - 2],
                       point - lower[lower.Count - 1]) <= 0.0f)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        List<Vector2> upper = new List<Vector2>();

        for (int index = unique.Count - 1; index >= 0; index--)
        {
            Vector2 point = unique[index];

            while (upper.Count >= 2 &&
                   Cross2D(
                       upper[upper.Count - 1] - upper[upper.Count - 2],
                       point - upper[upper.Count - 1]) <= 0.0f)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);

        return lower;
    }

    private static float Cross2D(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static bool TryFindMinimumAreaRectangle(
        List<Vector2> hull,
        out Vector2 center,
        out Vector2 axisX,
        out Vector2 axisY,
        out float width,
        out float height)
    {
        center = Vector2.zero;
        axisX = Vector2.right;
        axisY = Vector2.up;
        width = 0.0f;
        height = 0.0f;

        float bestArea = float.PositiveInfinity;
        bool found = false;

        for (int edgeIndex = 0; edgeIndex < hull.Count; edgeIndex++)
        {
            Vector2 edge =
                hull[(edgeIndex + 1) % hull.Count] -
                hull[edgeIndex];

            if (edge.sqrMagnitude <= GeometryEpsilon)
            {
                continue;
            }

            Vector2 candidateX = edge.normalized;
            Vector2 candidateY = new Vector2(-candidateX.y, candidateX.x);

            float minimumX = float.PositiveInfinity;
            float maximumX = float.NegativeInfinity;
            float minimumY = float.PositiveInfinity;
            float maximumY = float.NegativeInfinity;

            foreach (Vector2 point in hull)
            {
                float projectionX = Vector2.Dot(point, candidateX);
                float projectionY = Vector2.Dot(point, candidateY);

                minimumX = Mathf.Min(minimumX, projectionX);
                maximumX = Mathf.Max(maximumX, projectionX);
                minimumY = Mathf.Min(minimumY, projectionY);
                maximumY = Mathf.Max(maximumY, projectionY);
            }

            float candidateWidth = maximumX - minimumX;
            float candidateHeight = maximumY - minimumY;
            float candidateArea = candidateWidth * candidateHeight;

            if (candidateArea >= bestArea)
            {
                continue;
            }

            bestArea = candidateArea;
            width = candidateWidth;
            height = candidateHeight;
            axisX = candidateX;
            axisY = candidateY;
            center =
                candidateX * ((minimumX + maximumX) * 0.5f) +
                candidateY * ((minimumY + maximumY) * 0.5f);
            found = true;
        }

        return found && width > GeometryEpsilon && height > GeometryEpsilon;
    }

    private static void StabilizeRectangleDirection(
        Transform source,
        Vector3 normalWS,
        ref Vector3 rightWS,
        ref Vector3 upWS)
    {
        Vector3 preferredDirection =
            Vector3.ProjectOnPlane(source.right, normalWS);

        if (preferredDirection.sqrMagnitude <= GeometryEpsilon)
        {
            preferredDirection =
                Vector3.ProjectOnPlane(Vector3.right, normalWS);
        }

        if (preferredDirection.sqrMagnitude <= GeometryEpsilon)
        {
            return;
        }

        if (Vector3.Dot(rightWS, preferredDirection.normalized) < 0.0f)
        {
            rightWS = -rightWS;
            upWS = -upWS;
        }
    }

    private void SetStatus(string message, MessageType type)
    {
        statusMessage = message;
        statusType = type;
        Repaint();
    }
}
#endif
