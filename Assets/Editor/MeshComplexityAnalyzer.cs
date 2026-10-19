using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public class MeshComplexityAnalyzer : EditorWindow
{
    // ============================================================
    // SETTINGS
    // ============================================================

    private GameObject rootObject;

    private bool includeInactive = true;

    // Useful for your current IFC workflow.
    private bool onlyActiveInHierarchy = false;
    private bool onlyEnabledRenderers = false;

    private int maxRows = 100;

    // Number of name tokens used for category grouping.
    // Example:
    // PEV-site-fence-HalfConcApron-StraightSection...
    //
    // with 4 tokens:
    // PEV-site-fence-HalfConcApron
    private int groupTokenCount = 4;

    private Vector2 scrollPosition;

    private bool showSummary = true;
    private bool showObjects = true;
    private bool showSharedMeshes = true;
    private bool showCategories = true;


    // ============================================================
    // DATA
    // ============================================================

    private readonly List<ObjectStats> objectStats = new();
    private readonly List<MeshStats> meshStats = new();
    private readonly List<CategoryStats> categoryStats = new();

    private long totalTriangles;
    private long totalVertices;
    private long totalMeshInstances;
    private long totalRenderers;
    private long totalMaterialSlots;


    private enum SortMode
    {
        Triangles,
        Vertices,
        Instances,
        Materials
    }

    private SortMode objectSort = SortMode.Triangles;
    private SortMode meshSort = SortMode.Triangles;
    private SortMode categorySort = SortMode.Triangles;


    // ============================================================
    // WINDOW
    // ============================================================

    [MenuItem("Tools/Optimization/Mesh Complexity Analyzer")]
    public static void ShowWindow()
    {
        GetWindow<MeshComplexityAnalyzer>("Mesh Complexity Analyzer");
    }


    private void OnGUI()
    {
        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField(
            "Mesh Complexity Analyzer",
            EditorStyles.boldLabel
        );

        EditorGUILayout.HelpBox(
            "Analyzes MeshFilters and SkinnedMeshRenderers under a hierarchy root. " +
            "Useful for finding expensive IFC/BIM geometry and repeated asset categories.",
            MessageType.Info
        );

        EditorGUILayout.Space(5);

        rootObject = (GameObject)EditorGUILayout.ObjectField(
            "Root Object",
            rootObject,
            typeof(GameObject),
            true
        );


        // ========================================================
        // FILTERS
        // ========================================================

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Filters", EditorStyles.boldLabel);

        includeInactive = EditorGUILayout.Toggle(
            new GUIContent(
                "Search Inactive Objects",
                "Allows inactive GameObjects to be discovered during the hierarchy scan."
            ),
            includeInactive
        );

        onlyActiveInHierarchy = EditorGUILayout.Toggle(
            new GUIContent(
                "Only Active In Hierarchy",
                "Ignore GameObjects that are currently disabled, or whose parent is disabled."
            ),
            onlyActiveInHierarchy
        );

        onlyEnabledRenderers = EditorGUILayout.Toggle(
            new GUIContent(
                "Only Enabled Renderers",
                "Ignore MeshRenderers / SkinnedMeshRenderers whose Enabled checkbox is off."
            ),
            onlyEnabledRenderers
        );

        EditorGUILayout.Space(4);

        maxRows = EditorGUILayout.IntSlider(
            "Maximum Rows",
            maxRows,
            10,
            500
        );


        // ========================================================
        // GROUPING
        // ========================================================

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField(
            "IFC Category Grouping",
            EditorStyles.boldLabel
        );

        groupTokenCount = EditorGUILayout.IntSlider(
            new GUIContent(
                "Name Tokens",
                "Number of '-' / '_' separated portions of the object name used to define a category."
            ),
            groupTokenCount,
            1,
            10
        );

        EditorGUILayout.HelpBox(
            "Example:\n" +
            "PEV-site-fence-HalfConcApron-StraightSection-...\n\n" +
            "Using 4 tokens becomes:\n" +
            "PEV-site-fence-HalfConcApron",
            MessageType.None
        );


        // ========================================================
        // ANALYZE BUTTON
        // ========================================================

        EditorGUILayout.Space(8);

        GUI.enabled = rootObject != null;

        if (GUILayout.Button("Analyze", GUILayout.Height(32)))
        {
            Analyze();
        }

        GUI.enabled = true;

        if (objectStats.Count == 0)
            return;


        // ========================================================
        // RESULTS
        // ========================================================

        EditorGUILayout.Space(10);

        scrollPosition =
            EditorGUILayout.BeginScrollView(scrollPosition);

        DrawSummary();

        EditorGUILayout.Space(16);

        DrawCategorySection();

        EditorGUILayout.Space(20);

        DrawObjectSection();

        EditorGUILayout.Space(20);

        DrawMeshSection();

        EditorGUILayout.EndScrollView();
    }


    // ============================================================
    // ANALYSIS
    // ============================================================

    private void Analyze()
    {
        objectStats.Clear();
        meshStats.Clear();
        categoryStats.Clear();

        totalTriangles = 0;
        totalVertices = 0;
        totalMeshInstances = 0;
        totalRenderers = 0;
        totalMaterialSlots = 0;

        Dictionary<Mesh, MeshStats> meshDictionary = new();
        Dictionary<string, CategoryStats> categoryDictionary = new();

        AnalyzeMeshFilters(meshDictionary, categoryDictionary);
        AnalyzeSkinnedMeshes(meshDictionary, categoryDictionary);

        meshStats.AddRange(
            meshDictionary.Values
                .OrderByDescending(x => x.TotalTriangles)
        );

        categoryStats.AddRange(
            categoryDictionary.Values
                .OrderByDescending(x => x.triangles)
        );

        objectStats.Sort(
            (a, b) => b.triangles.CompareTo(a.triangles)
        );

        Repaint();

        Debug.Log(
            $"Mesh analysis complete for '{rootObject.name}':\n" +
            $"{totalTriangles:N0} triangles\n" +
            $"{totalVertices:N0} vertices\n" +
            $"{totalMeshInstances:N0} mesh instances\n" +
            $"{totalRenderers:N0} renderers\n" +
            $"{categoryStats.Count:N0} categories."
        );
    }


    private void AnalyzeMeshFilters(
        Dictionary<Mesh, MeshStats> meshDictionary,
        Dictionary<string, CategoryStats> categoryDictionary)
    {
        MeshFilter[] filters =
            rootObject.GetComponentsInChildren<MeshFilter>(
                includeInactive
            );

        foreach (MeshFilter filter in filters)
        {
            if (!ShouldProcessGameObject(filter.gameObject))
                continue;

            Mesh mesh = filter.sharedMesh;

            if (mesh == null)
                continue;

            MeshRenderer renderer =
                filter.GetComponent<MeshRenderer>();

            if (onlyEnabledRenderers)
            {
                if (renderer == null || !renderer.enabled)
                    continue;
            }

            AddMeshInstance(
                filter.gameObject,
                mesh,
                renderer,
                meshDictionary,
                categoryDictionary
            );
        }
    }


    private void AnalyzeSkinnedMeshes(
        Dictionary<Mesh, MeshStats> meshDictionary,
        Dictionary<string, CategoryStats> categoryDictionary)
    {
        SkinnedMeshRenderer[] renderers =
            rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(
                includeInactive
            );

        foreach (SkinnedMeshRenderer renderer in renderers)
        {
            if (!ShouldProcessGameObject(renderer.gameObject))
                continue;

            if (onlyEnabledRenderers && !renderer.enabled)
                continue;

            Mesh mesh = renderer.sharedMesh;

            if (mesh == null)
                continue;

            AddMeshInstance(
                renderer.gameObject,
                mesh,
                renderer,
                meshDictionary,
                categoryDictionary
            );
        }
    }


    private bool ShouldProcessGameObject(GameObject go)
    {
        if (onlyActiveInHierarchy && !go.activeInHierarchy)
            return false;

        return true;
    }


    private void AddMeshInstance(
        GameObject go,
        Mesh mesh,
        Renderer renderer,
        Dictionary<Mesh, MeshStats> meshDictionary,
        Dictionary<string, CategoryStats> categoryDictionary)
    {
        long triangles = GetTriangleCount(mesh);
        long vertices = mesh.vertexCount;

        int materialCount =
            renderer != null
                ? renderer.sharedMaterials.Length
                : 0;

        ObjectStats objectStat = new()
        {
            gameObject = go,
            mesh = mesh,
            triangles = triangles,
            vertices = vertices,
            subMeshes = mesh.subMeshCount,
            materials = materialCount
        };

        objectStats.Add(objectStat);


        // --------------------------------------------------------
        // Global totals
        // --------------------------------------------------------

        totalTriangles += triangles;
        totalVertices += vertices;
        totalMeshInstances++;

        if (renderer != null)
        {
            totalRenderers++;
            totalMaterialSlots += materialCount;
        }


        // --------------------------------------------------------
        // Shared mesh statistics
        // --------------------------------------------------------

        if (!meshDictionary.TryGetValue(mesh, out MeshStats meshStat))
        {
            meshStat = new MeshStats
            {
                mesh = mesh,
                verticesPerInstance = vertices,
                trianglesPerInstance = triangles,
                subMeshes = mesh.subMeshCount
            };

            meshDictionary.Add(mesh, meshStat);
        }

        meshStat.instances++;
        meshStat.objects.Add(go);


        // --------------------------------------------------------
        // IFC/category statistics
        // --------------------------------------------------------

        string category =
            GenerateCategoryName(go.name);

        if (!categoryDictionary.TryGetValue(
                category,
                out CategoryStats categoryStat))
        {
            categoryStat = new CategoryStats
            {
                categoryName = category
            };

            categoryDictionary.Add(category, categoryStat);
        }

        categoryStat.instances++;
        categoryStat.triangles += triangles;
        categoryStat.vertices += vertices;
        categoryStat.materialSlots += materialCount;

        categoryStat.uniqueMeshes.Add(mesh);
        categoryStat.objects.Add(go);
    }


    // ============================================================
    // CATEGORY GENERATION
    // ============================================================

    private string GenerateCategoryName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return "<Unnamed>";

        // Split IFC-style names on common delimiters.
        string[] tokens = Regex.Split(
            objectName,
            @"[-_\s]+"
        );

        tokens = tokens
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (tokens.Length == 0)
            return objectName;

        int count = Mathf.Min(
            groupTokenCount,
            tokens.Length
        );

        return string.Join(
            "-",
            tokens.Take(count)
        );
    }


    // ============================================================
    // TRIANGLE COUNT
    // ============================================================

    private static long GetTriangleCount(Mesh mesh)
    {
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


    // ============================================================
    // SUMMARY
    // ============================================================

    private void DrawSummary()
    {
        showSummary = EditorGUILayout.Foldout(
            showSummary,
            "Summary",
            true
        );

        if (!showSummary)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        DrawStat("Triangles", totalTriangles);
        DrawStat("Vertices", totalVertices);
        DrawStat("Mesh Instances", totalMeshInstances);
        DrawStat("Unique Meshes", meshStats.Count);
        DrawStat("Renderers", totalRenderers);
        DrawStat("Material Slots", totalMaterialSlots);
        DrawStat("Name Categories", categoryStats.Count);

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
            GUILayout.Width(130)
        );

        EditorGUILayout.EndHorizontal();
    }


    // ============================================================
    // CATEGORY TABLE
    // ============================================================

    private void DrawCategorySection()
    {
        showCategories = EditorGUILayout.Foldout(
            showCategories,
            "Optimization Candidates — Grouped by Name",
            true
        );

        if (!showCategories)
            return;

        EditorGUILayout.HelpBox(
            "This table is usually the most useful one for IFC data. " +
            "It shows the total cost of similar objects rather than only individual meshes.",
            MessageType.Info
        );

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField(
            "Sort by:",
            GUILayout.Width(50)
        );

        categorySort = (SortMode)EditorGUILayout.EnumPopup(
            categorySort,
            GUILayout.Width(120)
        );

        GUILayout.FlexibleSpace();

        if (GUILayout.Button("CSV", GUILayout.Width(60)))
        {
            ExportCategoriesCSV();
        }

        EditorGUILayout.EndHorizontal();

        List<CategoryStats> sorted =
            SortCategories(categoryStats, categorySort);

        EditorGUILayout.BeginHorizontal(
            EditorStyles.toolbar
        );

        GUILayout.Label("#", GUILayout.Width(30));
        GUILayout.Label("Category", GUILayout.Width(280));
        GUILayout.Label("Count", GUILayout.Width(65));
        GUILayout.Label("Total Tris", GUILayout.Width(100));
        GUILayout.Label("Avg Tris", GUILayout.Width(85));
        GUILayout.Label("Total Verts", GUILayout.Width(100));
        GUILayout.Label("Meshes", GUILayout.Width(60));
        GUILayout.Label("Mats", GUILayout.Width(60));

        EditorGUILayout.EndHorizontal();

        int count =
            Mathf.Min(maxRows, sorted.Count);

        for (int i = 0; i < count; i++)
        {
            CategoryStats stat = sorted[i];

            EditorGUILayout.BeginHorizontal();

            GUILayout.Label(
                (i + 1).ToString(),
                GUILayout.Width(30)
            );

            if (GUILayout.Button(
                    stat.categoryName,
                    EditorStyles.linkLabel,
                    GUILayout.Width(280)))
            {
                SelectCategory(stat);
            }

            GUILayout.Label(
                stat.instances.ToString("N0"),
                GUILayout.Width(65)
            );

            GUILayout.Label(
                stat.triangles.ToString("N0"),
                GUILayout.Width(100)
            );

            GUILayout.Label(
                stat.AverageTriangles.ToString("N0"),
                GUILayout.Width(85)
            );

            GUILayout.Label(
                stat.vertices.ToString("N0"),
                GUILayout.Width(100)
            );

            GUILayout.Label(
                stat.uniqueMeshes.Count.ToString("N0"),
                GUILayout.Width(60)
            );

            GUILayout.Label(
                stat.materialSlots.ToString("N0"),
                GUILayout.Width(60)
            );

            EditorGUILayout.EndHorizontal();
        }
    }


    private void SelectCategory(CategoryStats category)
    {
        Selection.objects =
            category.objects
                .Where(x => x != null)
                .Cast<UnityEngine.Object>()
                .ToArray();

        if (category.objects.Count > 0 &&
            category.objects[0] != null)
        {
            EditorGUIUtility.PingObject(
                category.objects[0]
            );
        }
    }


    // ============================================================
    // OBJECT TABLE
    // ============================================================

    private void DrawObjectSection()
    {
        showObjects = EditorGUILayout.Foldout(
            showObjects,
            "Worst GameObjects",
            true
        );

        if (!showObjects)
            return;

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField(
            "Sort by:",
            GUILayout.Width(50)
        );

        objectSort = (SortMode)EditorGUILayout.EnumPopup(
            objectSort,
            GUILayout.Width(120)
        );

        GUILayout.FlexibleSpace();

        if (GUILayout.Button("CSV", GUILayout.Width(60)))
        {
            ExportObjectsCSV();
        }

        EditorGUILayout.EndHorizontal();


        List<ObjectStats> sorted =
            SortObjects(objectStats, objectSort);


        EditorGUILayout.BeginHorizontal(
            EditorStyles.toolbar
        );

        GUILayout.Label("#", GUILayout.Width(30));
        GUILayout.Label("GameObject", GUILayout.Width(300));
        GUILayout.Label("Tris", GUILayout.Width(90));
        GUILayout.Label("Verts", GUILayout.Width(90));
        GUILayout.Label("Sub", GUILayout.Width(45));
        GUILayout.Label("Mats", GUILayout.Width(45));

        EditorGUILayout.EndHorizontal();


        int count =
            Mathf.Min(maxRows, sorted.Count);

        for (int i = 0; i < count; i++)
        {
            ObjectStats stat = sorted[i];

            EditorGUILayout.BeginHorizontal();

            GUILayout.Label(
                (i + 1).ToString(),
                GUILayout.Width(30)
            );

            if (GUILayout.Button(
                    stat.gameObject.name,
                    EditorStyles.linkLabel,
                    GUILayout.Width(300)))
            {
                Selection.activeGameObject =
                    stat.gameObject;

                EditorGUIUtility.PingObject(
                    stat.gameObject
                );
            }

            GUILayout.Label(
                stat.triangles.ToString("N0"),
                GUILayout.Width(90)
            );

            GUILayout.Label(
                stat.vertices.ToString("N0"),
                GUILayout.Width(90)
            );

            GUILayout.Label(
                stat.subMeshes.ToString(),
                GUILayout.Width(45)
            );

            GUILayout.Label(
                stat.materials.ToString(),
                GUILayout.Width(45)
            );

            EditorGUILayout.EndHorizontal();
        }
    }


    // ============================================================
    // SHARED MESH TABLE
    // ============================================================

    private void DrawMeshSection()
    {
        showSharedMeshes = EditorGUILayout.Foldout(
            showSharedMeshes,
            "Worst Shared Meshes",
            true
        );

        if (!showSharedMeshes)
            return;

        EditorGUILayout.HelpBox(
            "Useful for finding inexpensive meshes that become expensive " +
            "because they are instantiated hundreds or thousands of times.",
            MessageType.None
        );

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField(
            "Sort by:",
            GUILayout.Width(50)
        );

        meshSort = (SortMode)EditorGUILayout.EnumPopup(
            meshSort,
            GUILayout.Width(120)
        );

        EditorGUILayout.EndHorizontal();


        List<MeshStats> sorted =
            SortMeshes(meshStats, meshSort);


        EditorGUILayout.BeginHorizontal(
            EditorStyles.toolbar
        );

        GUILayout.Label("#", GUILayout.Width(30));
        GUILayout.Label("Mesh", GUILayout.Width(260));
        GUILayout.Label("Instances", GUILayout.Width(75));
        GUILayout.Label("Tris Each", GUILayout.Width(85));
        GUILayout.Label("Total Tris", GUILayout.Width(100));
        GUILayout.Label("Verts Each", GUILayout.Width(85));

        EditorGUILayout.EndHorizontal();


        int count =
            Mathf.Min(maxRows, sorted.Count);

        for (int i = 0; i < count; i++)
        {
            MeshStats stat = sorted[i];

            EditorGUILayout.BeginHorizontal();

            GUILayout.Label(
                (i + 1).ToString(),
                GUILayout.Width(30)
            );

            if (GUILayout.Button(
                    stat.mesh.name,
                    EditorStyles.linkLabel,
                    GUILayout.Width(260)))
            {
                EditorGUIUtility.PingObject(stat.mesh);
            }

            GUILayout.Label(
                stat.instances.ToString("N0"),
                GUILayout.Width(75)
            );

            GUILayout.Label(
                stat.trianglesPerInstance.ToString("N0"),
                GUILayout.Width(85)
            );

            GUILayout.Label(
                stat.TotalTriangles.ToString("N0"),
                GUILayout.Width(100)
            );

            GUILayout.Label(
                stat.verticesPerInstance.ToString("N0"),
                GUILayout.Width(85)
            );

            EditorGUILayout.EndHorizontal();
        }
    }


    // ============================================================
    // SORTING
    // ============================================================

    private static List<ObjectStats> SortObjects(
        List<ObjectStats> stats,
        SortMode mode)
    {
        return mode switch
        {
            SortMode.Vertices =>
                stats.OrderByDescending(
                    x => x.vertices
                ).ToList(),

            SortMode.Materials =>
                stats.OrderByDescending(
                    x => x.materials
                ).ToList(),

            _ =>
                stats.OrderByDescending(
                    x => x.triangles
                ).ToList()
        };
    }


    private static List<MeshStats> SortMeshes(
        List<MeshStats> stats,
        SortMode mode)
    {
        return mode switch
        {
            SortMode.Vertices =>
                stats.OrderByDescending(
                    x => x.TotalVertices
                ).ToList(),

            SortMode.Instances =>
                stats.OrderByDescending(
                    x => x.instances
                ).ToList(),

            _ =>
                stats.OrderByDescending(
                    x => x.TotalTriangles
                ).ToList()
        };
    }


    private static List<CategoryStats> SortCategories(
        List<CategoryStats> stats,
        SortMode mode)
    {
        return mode switch
        {
            SortMode.Vertices =>
                stats.OrderByDescending(
                    x => x.vertices
                ).ToList(),

            SortMode.Instances =>
                stats.OrderByDescending(
                    x => x.instances
                ).ToList(),

            SortMode.Materials =>
                stats.OrderByDescending(
                    x => x.materialSlots
                ).ToList(),

            _ =>
                stats.OrderByDescending(
                    x => x.triangles
                ).ToList()
        };
    }


    // ============================================================
    // CSV EXPORT
    // ============================================================

    private void ExportObjectsCSV()
    {
        string path =
            EditorUtility.SaveFilePanel(
                "Export Object Analysis",
                "",
                "mesh_objects.csv",
                "csv"
            );

        if (string.IsNullOrEmpty(path))
            return;

        using StreamWriter writer =
            new(path);

        writer.WriteLine(
            "GameObject,Mesh,Triangles,Vertices,SubMeshes,Materials,Hierarchy"
        );

        foreach (ObjectStats stat in
                 objectStats.OrderByDescending(
                     x => x.triangles))
        {
            writer.WriteLine(
                $"\"{stat.gameObject.name}\"," +
                $"\"{stat.mesh.name}\"," +
                $"{stat.triangles}," +
                $"{stat.vertices}," +
                $"{stat.subMeshes}," +
                $"{stat.materials}," +
                $"\"{GetHierarchyPath(stat.gameObject.transform)}\""
            );
        }

        Debug.Log(
            $"Object analysis exported to:\n{path}"
        );
    }


    private void ExportCategoriesCSV()
    {
        string path =
            EditorUtility.SaveFilePanel(
                "Export Category Analysis",
                "",
                "mesh_categories.csv",
                "csv"
            );

        if (string.IsNullOrEmpty(path))
            return;

        using StreamWriter writer =
            new(path);

        writer.WriteLine(
            "Category,Instances,TotalTriangles,AverageTriangles," +
            "TotalVertices,UniqueMeshes,MaterialSlots"
        );

        foreach (CategoryStats stat in
                 categoryStats.OrderByDescending(
                     x => x.triangles))
        {
            writer.WriteLine(
                $"\"{stat.categoryName}\"," +
                $"{stat.instances}," +
                $"{stat.triangles}," +
                $"{stat.AverageTriangles}," +
                $"{stat.vertices}," +
                $"{stat.uniqueMeshes.Count}," +
                $"{stat.materialSlots}"
            );
        }

        Debug.Log(
            $"Category analysis exported to:\n{path}"
        );
    }


    private static string GetHierarchyPath(
        Transform transform)
    {
        string path = transform.name;

        while (transform.parent != null)
        {
            transform = transform.parent;

            path =
                transform.name +
                "/" +
                path;
        }

        return path;
    }


    // ============================================================
    // DATA CLASSES
    // ============================================================

    private class ObjectStats
    {
        public GameObject gameObject;
        public Mesh mesh;

        public long triangles;
        public long vertices;

        public int subMeshes;
        public int materials;
    }


    private class MeshStats
    {
        public Mesh mesh;

        public long trianglesPerInstance;
        public long verticesPerInstance;

        public int instances;
        public int subMeshes;

        public readonly List<GameObject> objects = new();

        public long TotalTriangles =>
            trianglesPerInstance * instances;

        public long TotalVertices =>
            verticesPerInstance * instances;
    }


    private class CategoryStats
    {
        public string categoryName;

        public int instances;

        public long triangles;
        public long vertices;
        public long materialSlots;

        public readonly HashSet<Mesh> uniqueMeshes = new();
        public readonly List<GameObject> objects = new();

        public long AverageTriangles =>
            instances > 0
                ? triangles / instances
                : 0;
    }
}