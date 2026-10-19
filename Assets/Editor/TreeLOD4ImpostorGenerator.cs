// Assets/Editor/TreeLOD4ImpostorGenerator.cs
// Unity 6 / URP. Editor-only, no runtime component required.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public sealed class TreeLOD4ImpostorGenerator : EditorWindow
{
    [SerializeField] private List<GameObject> prefabs = new List<GameObject>();
    [SerializeField, Range(128, 1024)] private int tileSize = 512;
    [SerializeField, Range(2, 8)] private int verticalCards = 4;
    [SerializeField] private bool includeTopCard = true;
    [SerializeField, Range(0.005f, 0.15f)] private float lod4Start = 0.04f;
    [SerializeField, Range(0.001f, 0.015f)] private float cullBelow = 0.005f;
    [SerializeField, Range(0.1f, 0.9f)] private float alphaCutoff = 0.45f;
    [SerializeField, Range(1.0f, 1.5f)] private float capturePadding = 1.12f;
    [SerializeField, Range(0.45f, 0.95f)] private float topCardHeight = 0.78f;
    [SerializeField] private bool castShadows = false;
    [SerializeField] private bool enableInstancing = true;
    private Vector2 scroll;

    private const string ChildName = "__Generated_LOD4_Impostor";
    private const string OldChildName = "__Generated_LOD4_200tris";
    private const string GeneratedFolder = "Assets/Generated/TreeImpostors";
    private const int CaptureLayer = 31; // temporary objects only, returned to original layer after capture

    [MenuItem("Tools/Optimization/Tree Cross-Plane Impostor Generator")]
    private static void Open() => GetWindow<TreeLOD4ImpostorGenerator>("Tree Impostor LOD4");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Tree LOD 4 — Captured Cross-Plane Impostors", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Select tree prefab ASSETS and Add Selected. Captures LOD 0 from multiple angles into a transparent texture atlas, creates crossed quads and optional horizontal canopy card, and adds/replaces the generated LOD 4. Existing LOD 0–3 are retained. Prefab assets are modified: use version control.", MessageType.Info);
        if (GUILayout.Button("Add Selected Prefabs")) AddSelection();
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(140));
        for (int i = prefabs.Count - 1; i >= 0; i--)
        {
            EditorGUILayout.BeginHorizontal();
            prefabs[i] = (GameObject)EditorGUILayout.ObjectField(prefabs[i], typeof(GameObject), false);
            if (GUILayout.Button("X", GUILayout.Width(25))) prefabs.RemoveAt(i);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();
        tileSize = EditorGUILayout.IntPopup("Capture Resolution / View", tileSize, new[] { "128", "256", "512", "1024" }, new[] { 128, 256, 512, 1024 });
        verticalCards = EditorGUILayout.IntSlider("Vertical Cards", verticalCards, 2, 8);
        includeTopCard = EditorGUILayout.Toggle("Top Card (elevated view)", includeTopCard);
        capturePadding = EditorGUILayout.Slider("Capture Padding", capturePadding, 1.0f, 1.5f);
        topCardHeight = EditorGUILayout.Slider("Top Card Height Ratio", topCardHeight, .45f, .95f);
        alphaCutoff = EditorGUILayout.Slider("Alpha Cutoff", alphaCutoff, .1f, .9f);
        lod4Start = EditorGUILayout.Slider("LOD 4 Starts", lod4Start, .005f, .15f);
        cullBelow = EditorGUILayout.Slider("Cull Below", cullBelow, .001f, .015f);
        castShadows = EditorGUILayout.Toggle("Cast Shadows", castShadows);
        enableInstancing = EditorGUILayout.Toggle("Enable GPU Instancing", enableInstancing);
        int frames = verticalCards + (includeTopCard ? 1 : 0);
        int columns = Mathf.CeilToInt(Mathf.Sqrt(frames));
        int rows = Mathf.CeilToInt((float)frames / columns);
        EditorGUILayout.HelpBox($"Output per prefab: {verticalCards * 2 + (includeTopCard ? 2 : 0)} triangles, one renderer, one URP/Lit alpha-clip material, {columns * tileSize}×{rows * tileSize} atlas.  Captured views are reusable across all prefab instances. Alpha-clipped cards can still cause overdraw; benchmark WebGL.", MessageType.None);
        using (new EditorGUI.DisabledScope(prefabs.Count == 0))
            if (GUILayout.Button($"Generate LOD4 for {prefabs.Count} Prefab(s)", GUILayout.Height(35))) GenerateAll();
    }

    private void AddSelection()
    {
        foreach (var obj in Selection.gameObjects)
        {
            var source = PrefabUtility.IsPartOfPrefabAsset(obj) ? obj : PrefabUtility.GetCorrespondingObjectFromSource(obj);
            if (source == null) continue;
            string path = AssetDatabase.GetAssetPath(source);
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null && !prefabs.Contains(prefab)) prefabs.Add(prefab);
        }
    }

    private void GenerateAll()
    {
        if (!EditorUtility.DisplayDialog("Generate Cross-Plane Impostors", $"Modify {prefabs.Count} prefab asset(s) and write generated textures, meshes and materials? Back up/commit first.", "Generate", "Cancel")) return;
        EnsureFolders();
        int success = 0;
        var errors = new List<string>();
        try
        {
            for (int i = 0; i < prefabs.Count; i++)
            {
                GameObject prefab = prefabs[i];
                if (prefab == null) continue;
                EditorUtility.DisplayProgressBar("Tree impostors", prefab.name, (float)i / prefabs.Count);
                try { GenerateOne(prefab); success++; }
                catch (Exception ex) { errors.Add(prefab.name + ": " + ex.Message); Debug.LogException(ex); }
            }
        }
        finally { EditorUtility.ClearProgressBar(); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }
        EditorUtility.DisplayDialog("Tree Impostors", $"Generated {success}. Failed {errors.Count}.\n" + string.Join("\n", errors.Take(6)), "OK");
    }

    private static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Generated")) AssetDatabase.CreateFolder("Assets", "Generated");
        if (!AssetDatabase.IsValidFolder(GeneratedFolder)) AssetDatabase.CreateFolder("Assets/Generated", "TreeImpostors");
    }

    private void GenerateOne(GameObject prefab)
    {
        string prefabPath = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select prefab asset, not scene-only object.");
        string id = AssetDatabase.AssetPathToGUID(prefabPath);
        string atlasPath = GeneratedFolder + "/" + id + "_atlas.png";
        string meshPath = GeneratedFolder + "/" + id + "_cards.asset";
        string matPath = GeneratedFolder + "/" + id + "_foliage.mat";

        // Stage capture takes place in an independent preview scene, so the source prefab
        // is never modified until the atlas and generated mesh are ready.
        GameObject sourceRoot = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            LODGroup sourceGroup = sourceRoot.GetComponentInChildren<LODGroup>(true);
            if (sourceGroup == null) throw new InvalidOperationException("Missing LODGroup.");
            LOD[] sourceLods = sourceGroup.GetLODs();
            if (sourceLods.Length < 4) throw new InvalidOperationException("Expected at least four source LODs.");
            if (sourceLods.Length > 5 || (sourceLods.Length == 5 &&
                !sourceLods[4].renderers.All(r => r != null && (r.name == ChildName || r.name == OldChildName))))
                throw new InvalidOperationException("Prefab contains a custom fifth LOD. Refusing to overwrite it.");

            Bounds local = GetBounds(sourceGroup.transform, sourceLods[0].renderers);
            if (local.size.y < 0.05f) throw new InvalidOperationException("LOD0 has invalid bounds.");
            byte[] atlasBytes = CaptureAtlas(sourceRoot, sourceGroup, sourceLods[0].renderers, local);
            File.WriteAllBytes(atlasPath, atlasBytes);
            AssetDatabase.ImportAsset(atlasPath, ImportAssetOptions.ForceSynchronousImport);
            SetTextureImport(atlasPath);
            Texture2D atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
            if (atlas == null) throw new InvalidOperationException("Atlas import failed.");

            Mesh cards = MakeCards(local);
            cards.name = prefab.name + "_LOD4_Impostor";
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null) { AssetDatabase.CreateAsset(cards, meshPath); mesh = cards; }
            else { EditorUtility.CopySerialized(cards, mesh); DestroyImmediate(cards); EditorUtility.SetDirty(mesh); }
            Material mat = CreateOrUpdateMaterial(matPath, atlas);

            // Mutate source prefab only after all assets are available.
            Transform groupTransform = sourceGroup.transform;
            foreach (string oldName in new[] { ChildName, OldChildName })
            {
                Transform old = groupTransform.Find(oldName);
                if (old != null) DestroyImmediate(old.gameObject);
            }
            GameObject child = new GameObject(ChildName);
            child.transform.SetParent(groupTransform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = true;

            LOD[] result = new LOD[5];
            Array.Copy(sourceLods, result, 4);
            float transition = Mathf.Clamp(lod4Start, cullBelow + .001f, result[2].screenRelativeTransitionHeight - .001f);
            result[3].screenRelativeTransitionHeight = transition;
            result[4] = new LOD(Mathf.Min(cullBelow, transition * .5f), new Renderer[] { renderer });
            sourceGroup.SetLODs(result);
            sourceGroup.RecalculateBounds();
            PrefabUtility.SaveAsPrefabAsset(sourceRoot, prefabPath);
            Debug.Log($"LOD4 cross-plane impostor: {prefabPath}, {mesh.triangles.Length / 3} triangles, atlas {atlas.width}x{atlas.height}");
        }
        finally { PrefabUtility.UnloadPrefabContents(sourceRoot); }
    }

    private byte[] CaptureAtlas(GameObject sourceRoot, LODGroup group, Renderer[] lod0Renderers, Bounds localBounds)
    {
        int frameCount = verticalCards + (includeTopCard ? 1 : 0);
        int columns = Mathf.CeilToInt(Mathf.Sqrt(frameCount));
        int rows = Mathf.CeilToInt((float)frameCount / columns);
        var atlas = new Texture2D(columns * tileSize, rows * tileSize, TextureFormat.RGBA32, false, false);
        var transparent = Enumerable.Repeat(new Color32(0, 0, 0, 0), atlas.width * atlas.height).ToArray();
        atlas.SetPixels32(transparent);
        Scene preview = EditorSceneManager.NewPreviewScene();
        GameObject clone = null, camGO = null, lightGO = null;
        RenderTexture rt = null;
        Texture2D readback = null;
        try
        {
            clone = Instantiate(sourceRoot);
            clone.hideFlags = HideFlags.HideAndDontSave;
            SceneManager.MoveGameObjectToScene(clone, preview);
            Transform sourceGroup = group.transform;
            Transform cloneGroup = FollowSiblingPath(sourceRoot.transform, clone.transform, sourceGroup);
            if (cloneGroup == null) throw new InvalidOperationException("Failed to locate cloned LODGroup.");
            LODGroup cloneLOD = cloneGroup.GetComponent<LODGroup>();
            cloneLOD.enabled = false;
            LOD[] levels = cloneLOD.GetLODs();
            var selectedRenderers = new HashSet<Renderer>(levels[0].renderers.Where(r => r != null));
            // Only source LOD0 may be rendered, and turn off helper renderer objects.
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                bool on = false;
                foreach (Renderer source in selectedRenderers)
                {
                    Transform mapped = FollowSiblingPath(sourceRoot.transform, clone.transform, source.transform);
                    if (mapped == renderer.transform) { on = true; break; }
                }
                renderer.enabled = on;
            }
            foreach (Transform tr in clone.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = CaptureLayer;
            clone.SetActive(true);
            // The prefab may have disabled LOD children. Enable only ancestor paths of LOD0 renderers.
            foreach (Renderer source in selectedRenderers)
            {
                Transform t = FollowSiblingPath(sourceRoot.transform, clone.transform, source.transform);
                while (t != null && t.IsChildOf(clone.transform))
                {
                    t.gameObject.SetActive(true);
                    if (t == clone.transform) break;
                    t = t.parent;
                }
            }
            // Center the capture at the LODGroup origin, ignoring arbitrary prefab placement.
            clone.transform.position = Vector3.zero;
            clone.transform.rotation = Quaternion.identity;
            clone.transform.localScale = Vector3.one;
            Vector3 center = cloneGroup.TransformPoint(localBounds.center);
            float halfSide = Mathf.Max(localBounds.extents.y, localBounds.extents.x, localBounds.extents.z) * capturePadding;
            float halfTop = Mathf.Max(localBounds.extents.x, localBounds.extents.z) * capturePadding;
            float dist = Mathf.Max(20f, localBounds.size.magnitude * 3f);

            camGO = new GameObject("__TreeImpostorCaptureCamera");
            SceneManager.MoveGameObjectToScene(camGO, preview);
            Camera cam = camGO.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.cullingMask = 1 << CaptureLayer;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.nearClipPlane = .01f;
            cam.farClipPlane = dist * 3f;
            cam.aspect = 1f;
            // Capture without postprocessing; in URP this extension is on CameraData.
            var additional = camGO.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            additional.renderPostProcessing = false;
            additional.renderShadows = false;
            additional.requiresColorOption = UnityEngine.Rendering.Universal.CameraOverrideOption.Off;
            additional.requiresDepthOption = UnityEngine.Rendering.Universal.CameraOverrideOption.Off;

            lightGO = new GameObject("__TreeImpostorCaptureLight");
            SceneManager.MoveGameObjectToScene(lightGO, preview);
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            light.shadows = LightShadows.None;
            lightGO.transform.rotation = Quaternion.Euler(45f, -40f, 0f);
            rt = new RenderTexture(tileSize, tileSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.antiAliasing = 1;
            rt.Create();
            cam.targetTexture = rt;
            readback = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false, false);
            for (int i = 0; i < frameCount; i++)
            {
                bool top = includeTopCard && i == verticalCards;
                Vector3 direction;
                if (top) direction = Vector3.up;
                else
                {
                    float a = Mathf.PI * i / verticalCards;
                    direction = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                }
                cam.orthographicSize = top ? halfTop : halfSide;
                cam.transform.position = center + direction * dist;
                // Top view uses +Z as image-up, matching the horizontal quad UV orientation.
                cam.transform.rotation = Quaternion.LookRotation(-direction, top ? Vector3.forward : Vector3.up);
                cam.Render();
                RenderTexture prior = RenderTexture.active;
                try
                {
                    RenderTexture.active = rt;
                    readback.ReadPixels(new Rect(0, 0, tileSize, tileSize), 0, 0);
                    readback.Apply(false);
                }
                finally { RenderTexture.active = prior; }
                atlas.SetPixels(i % columns * tileSize, i / columns * tileSize, tileSize, tileSize, readback.GetPixels());
            }
            atlas.Apply(false);
            return atlas.EncodeToPNG();
        }
        finally
        {
            if (rt != null) { rt.Release(); DestroyImmediate(rt); }
            if (readback != null) DestroyImmediate(readback);
            if (atlas != null) DestroyImmediate(atlas);
            if (camGO != null) DestroyImmediate(camGO);
            if (lightGO != null) DestroyImmediate(lightGO);
            if (clone != null) DestroyImmediate(clone);
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    private static Transform FollowSiblingPath(Transform root, Transform copyRoot, Transform node)
    {
        var path = new Stack<int>();
        Transform t = node;
        while (t != null && t != root) { path.Push(t.GetSiblingIndex()); t = t.parent; }
        if (t != root) return null;
        Transform mapped = copyRoot;
        while (path.Count > 0)
        {
            int n = path.Pop();
            if (n >= mapped.childCount) return null;
            mapped = mapped.GetChild(n);
        }
        return mapped;
    }

    private Mesh MakeCards(Bounds bounds)
    {
        int count = verticalCards + (includeTopCard ? 1 : 0);
        int columns = Mathf.CeilToInt(Mathf.Sqrt(count));
        int rows = Mathf.CeilToInt((float)count / columns);
        var verts = new List<Vector3>();
        var uv = new List<Vector2>();
        var tris = new List<int>();
        float w = Mathf.Max(bounds.size.x, bounds.size.z) * capturePadding;
        float h = bounds.size.y * capturePadding;
        Vector3 c = bounds.center;
        float margin = 1.5f / tileSize;
        for (int i = 0; i < count; i++)
        {
            bool top = includeTopCard && i == verticalCards;
            Vector3 a, b, d, e;
            if (!top)
            {
                float angle = Mathf.PI * i / verticalCards;
                Vector3 across = new Vector3(Mathf.Cos(angle), 0f, -Mathf.Sin(angle)) * (w * .5f);
                float y0 = bounds.center.y - h * .5f;
                float y1 = bounds.center.y + h * .5f;
                a = new Vector3(c.x, y0, c.z) - across;
                b = new Vector3(c.x, y0, c.z) + across;
                d = new Vector3(c.x, y1, c.z) - across;
                e = new Vector3(c.x, y1, c.z) + across;
            }
            else
            {
                float yy = bounds.min.y + bounds.size.y * topCardHeight;
                float sx = bounds.size.x * capturePadding * .5f;
                float sz = bounds.size.z * capturePadding * .5f;
                a = new Vector3(c.x - sx, yy, c.z - sz);
                b = new Vector3(c.x + sx, yy, c.z - sz);
                d = new Vector3(c.x - sx, yy, c.z + sz);
                e = new Vector3(c.x + sx, yy, c.z + sz);
            }
            int start = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(d); verts.Add(e);
            float u0 = ((i % columns) + margin) / columns;
            float u1 = ((i % columns) + 1f - margin) / columns;
            float v0 = ((i / columns) + margin) / rows;
            float v1 = ((i / columns) + 1f - margin) / rows;
            uv.Add(new Vector2(u0, v0)); uv.Add(new Vector2(u1, v0));
            uv.Add(new Vector2(u0, v1)); uv.Add(new Vector2(u1, v1));
            tris.Add(start); tris.Add(start + 2); tris.Add(start + 1);
            tris.Add(start + 1); tris.Add(start + 2); tris.Add(start + 3);
        }
        Mesh result = new Mesh { name = "LOD4_CrossPlane_Cards" };
        result.SetVertices(verts);
        result.SetUVs(0, uv);
        result.SetTriangles(tris, 0);
        result.RecalculateNormals();
        result.RecalculateBounds();
        return result;
    }

    private Material CreateOrUpdateMaterial(string path, Texture2D atlas)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) throw new InvalidOperationException("URP/Lit shader not found.");
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = "Tree_LOD4_Impostor" };
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = shader;
        material.SetTexture("_BaseMap", atlas);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Surface", 0f); // opaque
        material.SetFloat("_AlphaClip", 1f);
        material.SetFloat("_Cutoff", alphaCutoff);
        material.SetFloat("_Cull", 0f); // both sides
        material.SetFloat("_Smoothness", .05f);
        material.EnableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.AlphaTest;
        material.SetOverrideTag("RenderType", "TransparentCutout");
        material.enableInstancing = enableInstancing;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void SetTextureImport(string path)
    {
        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;
        importer.sRGBTexture = true;
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Compressed;
        importer.SaveAndReimport();
    }

    private static Bounds GetBounds(Transform basis, IEnumerable<Renderer> renderers)
    {
        Bounds result = default;
        bool any = false;
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null) continue;
            Bounds b = renderer.bounds;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 world = b.center + Vector3.Scale(b.extents, new Vector3(x, y, z));
                Vector3 p = basis.InverseTransformPoint(world);
                if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                else result.Encapsulate(p);
            }
        }
        if (!any) throw new InvalidOperationException("No renderers in LOD0.");
        return result;
    }
}
