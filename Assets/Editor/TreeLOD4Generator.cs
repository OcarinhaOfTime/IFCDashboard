// Place in Assets/Editor/TreeLOD4Generator.cs
// Unity 6 / URP — prefab-asset editor utility; no runtime scripts.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class TreeLOD4Generator : EditorWindow
{
    [SerializeField] private List<GameObject> prefabs = new List<GameObject>();
    [SerializeField, Range(0.015f, 0.09f)] private float lod4Start = 0.04f;
    [SerializeField, Range(0.001f, 0.014f)] private float cullBelow = 0.005f;
    [SerializeField] private Color canopyColor = new Color(.37f, .52f, .27f, 1);
    [SerializeField] private Color trunkColor = new Color(.33f, .24f, .16f, 1);
    [SerializeField] private bool enableInstancing = true;
    private Vector2 scroll;
    private const string ChildName = "__Generated_LOD4_200tris";
    private const string AssetDir = "Assets/Generated/TreeLOD4";

    [MenuItem("Tools/Optimization/Generate Tree LOD 4")]
    public static void Open() => GetWindow<TreeLOD4Generator>("Tree LOD 4 Generator");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Tree LOD 4 — 200 Triangle Proxy", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Select tree prefab ASSETS in Project, then Add Selected. Also accepts a scene prefab instance and resolves its source asset. Existing LODs stay intact; a generated fifth LOD is appended. Original prefab assets are modified, so commit/back up first.", MessageType.Info);
        if (GUILayout.Button("Add Selected Prefabs")) AddSelection();
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(220));
        for (int i = prefabs.Count - 1; i >= 0; i--)
        {
            EditorGUILayout.BeginHorizontal();
            prefabs[i] = (GameObject)EditorGUILayout.ObjectField(prefabs[i], typeof(GameObject), false);
            if (GUILayout.Button("×", GUILayout.Width(25))) prefabs.RemoveAt(i);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();
        lod4Start = EditorGUILayout.Slider(new GUIContent("LOD 4 Starts (screen %)", "At this screen-relative tree height, LOD3 changes to LOD4."), lod4Start, .015f, .09f);
        cullBelow = EditorGUILayout.Slider(new GUIContent("Cull Below (screen %)", "Below this relative height, the tree is culled."), cullBelow, .001f, .014f);
        canopyColor = EditorGUILayout.ColorField("Canopy Color", canopyColor);
        trunkColor = EditorGUILayout.ColorField("Trunk Color", trunkColor);
        enableInstancing = EditorGUILayout.Toggle("Enable Material Instancing", enableInstancing);
        EditorGUILayout.HelpBox("Default geometry: three 10-sided, three-ring canopy lobes (60 triangles each) + one 5-sided trunk (20 triangles) = 200 triangles. This is a stylized volume proxy, not decimation of the foliage mesh. Materials are opaque URP/Lit.", MessageType.None);
        using (new EditorGUI.DisabledScope(prefabs.Count == 0))
            if (GUILayout.Button($"Generate/Replace LOD 4 in {prefabs.Count} Prefab(s)", GUILayout.Height(36))) GenerateAll();
    }

    private void AddSelection()
    {
        foreach (var selected in Selection.gameObjects)
        {
            var source = PrefabUtility.IsPartOfPrefabAsset(selected) ? selected : PrefabUtility.GetCorrespondingObjectFromSource(selected);
            if (source == null) continue;
            var path = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null && !prefabs.Contains(prefab)) prefabs.Add(prefab);
        }
    }

    private void GenerateAll()
    {
        if (!EditorUtility.DisplayDialog("Generate Tree LOD 4", $"Modify {prefabs.Count} prefab asset(s), adding/replacing generated LOD 4 and mesh/material assets? Existing LOD 0–3 meshes remain unchanged.", "Generate", "Cancel")) return;
        Directory.CreateDirectory(AssetDir);
        AssetDatabase.Refresh();
        int success = 0;
        var failures = new List<string>();
        try
        {
            for (int i = 0; i < prefabs.Count; i++)
            {
                var prefab = prefabs[i];
                if (prefab == null) continue;
                EditorUtility.DisplayProgressBar("Generate Tree LOD 4", prefab.name, (float)i / prefabs.Count);
                try { GenerateOne(prefab); success++; }
                catch (Exception ex) { failures.Add(prefab.name + ": " + ex.Message); Debug.LogException(ex); }
            }
        }
        finally { EditorUtility.ClearProgressBar(); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }
        EditorUtility.DisplayDialog("Tree LOD 4", $"Generated: {success}\nFailed: {failures.Count}\n" + string.Join("\n", failures.Take(8)), "OK");
    }

    private void GenerateOne(GameObject prefab)
    {
        string path = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Not a prefab asset.");
        string guid = AssetDatabase.AssetPathToGUID(path);
        string baseName = guid + "_lod4";
        var meshPath = AssetDir + "/" + baseName + ".asset";
        var trunkPath = AssetDir + "/" + baseName + "_trunk.mat";
        var canopyPath = AssetDir + "/" + baseName + "_canopy.mat";
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            LODGroup lodGroup = root.GetComponentInChildren<LODGroup>(true);
            if (lodGroup == null) throw new InvalidOperationException("No LODGroup in prefab.");
            var lods = lodGroup.GetLODs();
            if (lods.Length < 2) throw new InvalidOperationException("Need an existing multi-LOD prefab.");

            // Existing generated child is removed, then restored in place (idempotent reruns).
            var existing = lodGroup.transform.Find(ChildName);
            if (existing != null) DestroyImmediate(existing.gameObject);

            // When rerunning, last LOD may be our previous generated LOD. Drop that entry.
            if (lods.Length >= 5) lods = lods.Take(lods.Length - 1).ToArray();
            if (lods.Length != 4) throw new InvalidOperationException("Expected four source LOD levels before adding LOD 4.");

            // Fit mesh to source LOD's total renderer bounds expressed in LODGroup-local coordinates.
            Bounds bounds = GetLocalBounds(lodGroup.transform, lods[3].renderers);
            if (bounds.size.y <= 0.01f) throw new InvalidOperationException("LOD3 renderer bounds are empty.");

            Mesh newMesh = BuildProxy(bounds);
            newMesh.name = prefab.name + "_LOD4_200tris";
            Mesh meshAsset = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (meshAsset == null) { AssetDatabase.CreateAsset(newMesh, meshPath); meshAsset = newMesh; }
            else { EditorUtility.CopySerialized(newMesh, meshAsset); DestroyImmediate(newMesh); EditorUtility.SetDirty(meshAsset); }

            var trunkMat = LoadOrCreateMaterial(trunkPath, "LOD4_Trunk", trunkColor);
            var canopyMat = LoadOrCreateMaterial(canopyPath, "LOD4_Canopy", canopyColor);
            var lodObject = new GameObject(ChildName);
            lodObject.transform.SetParent(lodGroup.transform, false);
            var filter = lodObject.AddComponent<MeshFilter>();
            filter.sharedMesh = meshAsset;
            var renderer = lodObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { trunkMat, canopyMat };
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;

            // Current LOD3 takes over at lods[2].screenRelativeTransitionHeight;
            // change the LOD3 threshold so new LOD4 takes over at the desired size.
            lods[3].screenRelativeTransitionHeight = lod4Start;
            var newLods = new LOD[5];
            Array.Copy(lods, newLods, 4);
            newLods[4] = new LOD(Mathf.Min(cullBelow, lod4Start * 0.5f), new Renderer[] { renderer });
            lodGroup.SetLODs(newLods);
            lodGroup.RecalculateBounds();
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log($"Tree LOD4 generated: {path} — {newMeshTriangleCount(meshAsset)} triangles");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    private Material LoadOrCreateMaterial(string path, string suffix, Color color)
    {
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader not found.");
            mat = new Material(shader) { name = suffix };
            AssetDatabase.CreateAsset(mat, path);
        }
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.12f);
        mat.enableInstancing = enableInstancing;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static int newMeshTriangleCount(Mesh mesh) => (int)(mesh.GetIndexCount(0) + mesh.GetIndexCount(1)) / 3;

    private static Bounds GetLocalBounds(Transform parent, Renderer[] renderers)
    {
        Bounds sum = default;
        bool any = false;
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null) continue;
            Bounds b = renderer.bounds;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3(x, y, z));
                Vector3 local = parent.InverseTransformPoint(corner);
                if (!any) { sum = new Bounds(local, Vector3.zero); any = true; }
                else sum.Encapsulate(local);
            }
        }
        if (!any) throw new InvalidOperationException("Source LOD has no usable renderers.");
        return sum;
    }

    private static Mesh BuildProxy(Bounds bounds)
    {
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var trunkTris = new List<int>();
        var canopyTris = new List<int>();
        float h = bounds.size.y;
        float wide = Mathf.Max(bounds.size.x, bounds.size.z) * .5f;
        Vector3 origin = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        AddTrunk(vertices, uvs, trunkTris, origin, h * .58f, Mathf.Max(.04f, wide * .085f));
        // Three overlapping, faceted foliage lobes: 60 triangles each.
        AddCanopy(vertices, uvs, canopyTris, origin + new Vector3(-wide * .30f, h * .64f, wide * .08f), wide * .68f, h * .37f);
        AddCanopy(vertices, uvs, canopyTris, origin + new Vector3(wide * .33f, h * .69f, -wide * .10f), wide * .68f, h * .34f);
        AddCanopy(vertices, uvs, canopyTris, origin + new Vector3(0f, h * .80f, wide * .07f), wide * .70f, h * .40f);
        var mesh = new Mesh { indexFormat = IndexFormat.UInt16 };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(trunkTris, 0);
        mesh.SetTriangles(canopyTris, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        if ((trunkTris.Count + canopyTris.Count) / 3 != 200)
            throw new InvalidOperationException("Unexpected triangle budget.");
        return mesh;
    }

    // 5-sided trunk: 10 side tris + 10 cap tris = 20.
    private static void AddTrunk(List<Vector3> v, List<Vector2> uv, List<int> tris, Vector3 bottom, float height, float radius)
    {
        const int sides = 5;
        int start = v.Count;
        for (int row = 0; row < 2; row++)
            for (int j = 0; j < sides; j++)
            {
                float a = 2 * Mathf.PI * j / sides;
                v.Add(bottom + new Vector3(Mathf.Cos(a) * radius * (row == 0 ? 1f : .72f), height * row, Mathf.Sin(a) * radius * (row == 0 ? 1f : .72f)));
                uv.Add(new Vector2((float)j / sides, row));
            }
        for (int j = 0; j < sides; j++)
        {
            int next = (j + 1) % sides;
            tris.Add(start + j); tris.Add(start + next); tris.Add(start + sides + next);
            tris.Add(start + j); tris.Add(start + sides + next); tris.Add(start + sides + j);
        }
        int bottomCenter = v.Count;
        v.Add(bottom); uv.Add(new Vector2(.5f, .5f));
        int topCenter = v.Count;
        v.Add(bottom + Vector3.up * height); uv.Add(new Vector2(.5f, .5f));
        for (int j = 0; j < sides; j++)
        {
            int next = (j + 1) % sides;
            tris.Add(bottomCenter); tris.Add(start + next); tris.Add(start + j);
            tris.Add(topCenter); tris.Add(start + sides + j); tris.Add(start + sides + next);
        }
    }

    // Each canopy: 3 rings of 10 vertices, two end poles.
    // Between rings: 2 * 10 * 2 = 40 triangles; ends = 20 => 60.
    private static void AddCanopy(List<Vector3> v, List<Vector2> uv, List<int> tris, Vector3 center, float radius, float height)
    {
        const int sides = 10;
        float[] ry = { -.70f, 0f, .70f };
        float[] rr = { .70f, 1.00f, .70f };
        int start = v.Count;
        for (int ring = 0; ring < 3; ring++)
            for (int j = 0; j < sides; j++)
            {
                float a = 2 * Mathf.PI * j / sides + .18f * ring;
                v.Add(center + new Vector3(radius * rr[ring] * Mathf.Cos(a), height * ry[ring], radius * rr[ring] * Mathf.Sin(a)));
                uv.Add(new Vector2((float)j / sides, (float)ring / 2));
            }
        for (int ring = 0; ring < 2; ring++)
            for (int j = 0; j < sides; j++)
            {
                int nxt = (j + 1) % sides;
                int a = start + ring * sides + j;
                int b = start + ring * sides + nxt;
                int c = start + (ring + 1) * sides + j;
                int d = start + (ring + 1) * sides + nxt;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
        int bottomPole = v.Count;
        v.Add(center + Vector3.down * height); uv.Add(new Vector2(.5f, 0));
        int topPole = v.Count;
        v.Add(center + Vector3.up * height); uv.Add(new Vector2(.5f, 1));
        for (int j = 0; j < sides; j++)
        {
            int nxt = (j + 1) % sides;
            tris.Add(bottomPole); tris.Add(start + j); tris.Add(start + nxt);
            tris.Add(topPole); tris.Add(start + 2 * sides + nxt); tris.Add(start + 2 * sides + j);
        }
    }
}
