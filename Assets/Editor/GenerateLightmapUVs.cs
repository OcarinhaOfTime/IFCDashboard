using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Editor utilities for generating:
/// 1. A secondary lightmap UV layout, copied into UV0 for GI/meta-pass
///    compatibility with otherwise UV-less triplanar meshes.
/// 2. Per-facade metric coordinates for procedural wall/window layouts.
/// 3. A combined operation that generates UV0, lightmap UVs, and wall-grid
///    UVs in one pass for every selected mesh hierarchy.
///
/// Place this script inside an Editor folder.
/// </summary>
public static class GenerateLightmapUVs
{
    private const string LightmapOutputFolder =
        "Assets/Generated/LightmapMeshes";

    private const string WallGridOutputFolder =
        "Assets/Generated/WallGridMeshes";

    private const string AllUvsOutputFolder =
        "Assets/Generated/AllUvMeshes";

    // Unity C# channel 3 corresponds to Shader Graph's UV3 node and
    // TEXCOORD3 in the generated shader. UV2 remains available for
    // realtime-GI data.
    private const int WallGridUvChannel = 3;

    // A triangle is treated as a wall when its normal is within this
    // angle of being horizontal. In other words, its plane is close
    // to vertical.
    private const float MaximumWallTiltDegrees = 15f;

    // Connected wall triangles are merged into one facade island when
    // their normals and planes are sufficiently similar.
    private const float CoplanarNormalToleranceDegrees = 4f;
    private const float CoplanarPlaneDistanceTolerance = 0.03f;

    // IFC/imported meshes commonly duplicate vertices along normal or
    // material seams. Positions are welded only for adjacency detection;
    // the output mesh still preserves the original vertex attributes.
    private const float AdjacencyWeldTolerance = 0.001f;

    // -----------------------------------------------------------------
    // Lightmap UV generation
    // -----------------------------------------------------------------

    [MenuItem(
        "Tools/UV Generation/Generate Lightmap UVs + UV0 for Selected Meshes"
    )]
    private static void GenerateLightmapUvsForSelected()
    {
        HashSet<MeshFilter> meshFilters = GetSelectedMeshFilters();

        if (meshFilters.Count == 0)
        {
            Debug.LogError(
                "Select at least one GameObject containing a MeshFilter."
            );
            return;
        }

        EnsureAssetFolderExists(LightmapOutputFolder);

        int generatedCount = 0;
        int skippedCount = 0;
        int failedCount = 0;

        foreach (MeshFilter meshFilter in meshFilters)
        {
            Mesh sourceMesh = meshFilter.sharedMesh;

            if (sourceMesh == null)
            {
                Debug.LogWarning(
                    $"Skipping '{meshFilter.name}': the MeshFilter has no mesh.",
                    meshFilter
                );

                skippedCount++;
                continue;
            }

            Mesh generatedMesh = null;

            try
            {
                generatedMesh =
                    BuildLightmapUvMesh(sourceMesh);

                generatedMesh.name =
                    $"{sourceMesh.name}_LightmapUV";

                string filename = SanitizeFilename(
                    $"{meshFilter.gameObject.name}_" +
                    $"{sourceMesh.name}_LightmapUV.asset"
                );

                string assetPath =
                    AssetDatabase.GenerateUniqueAssetPath(
                        $"{LightmapOutputFolder}/{filename}"
                    );

                AssetDatabase.CreateAsset(generatedMesh, assetPath);

                AssignGeneratedMesh(
                    meshFilter,
                    generatedMesh,
                    "Assign Generated Lightmap UV Mesh"
                );

                generatedCount++;

                Debug.Log(
                    $"Generated lightmap UVs for " +
                    $"'{meshFilter.gameObject.name}'.\n" +
                    $"Asset: {assetPath}\n" +
                    $"Vertices: {generatedMesh.vertexCount:N0}\n" +
                    $"UV0 coordinates: {generatedMesh.uv.Length:N0}\n" +
                    $"Lightmap coordinates " +
                    $"(Mesh.uv2 / Shader Graph UV1): " +
                    $"{generatedMesh.uv2.Length:N0}",
                    meshFilter
                );
            }
            catch (Exception exception)
            {
                DestroyUnsavedMesh(generatedMesh);
                failedCount++;

                Debug.LogException(
                    new InvalidOperationException(
                        $"Failed to generate lightmap UVs for " +
                        $"'{meshFilter.gameObject.name}'.",
                        exception
                    ),
                    meshFilter
                );
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(
            $"Lightmap UV generation complete. " +
            $"Generated: {generatedCount}, " +
            $"Skipped: {skippedCount}, " +
            $"Failed: {failedCount}."
        );
    }

    [MenuItem(
        "Tools/UV Generation/Generate Lightmap UVs + UV0 for Selected Meshes",
        validate = true
    )]
    private static bool ValidateGenerateLightmapUvsForSelected()
    {
        return Selection.gameObjects.Length > 0;
    }

    // -----------------------------------------------------------------
    // Combined UV generation
    // -----------------------------------------------------------------

    [MenuItem(
        "Tools/UV Generation/Generate All UVs for Selected Meshes"
    )]
    private static void GenerateAllUvsForSelected()
    {
        HashSet<MeshFilter> meshFilters = GetSelectedMeshFilters();

        if (meshFilters.Count == 0)
        {
            Debug.LogError(
                "Select at least one GameObject containing a MeshFilter."
            );
            return;
        }

        EnsureAssetFolderExists(AllUvsOutputFolder);

        int generatedCount = 0;
        int skippedCount = 0;
        int failedCount = 0;

        foreach (MeshFilter meshFilter in meshFilters)
        {
            Mesh sourceMesh = meshFilter.sharedMesh;

            if (sourceMesh == null)
            {
                Debug.LogWarning(
                    $"Skipping '{meshFilter.name}': the MeshFilter has no mesh.",
                    meshFilter
                );

                skippedCount++;
                continue;
            }

            Mesh lightmapMesh = null;
            Mesh generatedMesh = null;

            try
            {
                // Generate the lightmap layout first. BuildWallGridMesh
                // then copies every existing UV channel while adding the
                // facade data to UV3, so the final mesh contains all data.
                lightmapMesh =
                    BuildLightmapUvMesh(sourceMesh);

                int facadeCount;
                int wallTriangleCount;
                int duplicatedVertexCount;

                generatedMesh = BuildWallGridMesh(
                    lightmapMesh,
                    meshFilter.transform,
                    out facadeCount,
                    out wallTriangleCount,
                    out duplicatedVertexCount
                );

                generatedMesh.name =
                    $"{sourceMesh.name}_AllUVs";

                DestroyUnsavedMesh(lightmapMesh);
                lightmapMesh = null;

                string filename = SanitizeFilename(
                    $"{meshFilter.gameObject.name}_" +
                    $"{sourceMesh.name}_AllUVs.asset"
                );

                string assetPath =
                    AssetDatabase.GenerateUniqueAssetPath(
                        $"{AllUvsOutputFolder}/{filename}"
                    );

                AssetDatabase.CreateAsset(
                    generatedMesh,
                    assetPath
                );

                AssignGeneratedMesh(
                    meshFilter,
                    generatedMesh,
                    "Assign Generated All-UV Mesh"
                );

                var wallGridData =
                    new List<Vector4>();

                generatedMesh.GetUVs(
                    WallGridUvChannel,
                    wallGridData
                );

                generatedCount++;

                Debug.Log(
                    $"Generated all UV layouts for " +
                    $"'{meshFilter.gameObject.name}'.\n" +
                    $"Asset: {assetPath}\n" +
                    $"Facade islands: {facadeCount:N0}\n" +
                    $"Wall triangles: {wallTriangleCount:N0}\n" +
                    $"Source vertices: {sourceMesh.vertexCount:N0}\n" +
                    $"Generated vertices: {generatedMesh.vertexCount:N0}\n" +
                    $"Additional facade-seam vertices: " +
                    $"{duplicatedVertexCount:N0}\n" +
                    $"UV0 coordinates: {generatedMesh.uv.Length:N0}\n" +
                    $"Lightmap coordinates " +
                    $"(Mesh.uv2 / Shader Graph UV1): " +
                    $"{generatedMesh.uv2.Length:N0}\n" +
                    $"Wall-grid coordinates " +
                    $"(C# channel {WallGridUvChannel} / " +
                    $"Shader Graph UV{WallGridUvChannel}): " +
                    $"{wallGridData.Count:N0}",
                    meshFilter
                );
            }
            catch (Exception exception)
            {
                DestroyUnsavedMesh(lightmapMesh);
                DestroyUnsavedMesh(generatedMesh);
                failedCount++;

                Debug.LogException(
                    new InvalidOperationException(
                        $"Failed to generate all UV layouts for " +
                        $"'{meshFilter.gameObject.name}'.",
                        exception
                    ),
                    meshFilter
                );
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(
            $"All-UV generation complete. " +
            $"Generated: {generatedCount}, " +
            $"Skipped: {skippedCount}, " +
            $"Failed: {failedCount}."
        );
    }

    [MenuItem(
        "Tools/UV Generation/Generate All UVs for Selected Meshes",
        validate = true
    )]
    private static bool ValidateGenerateAllUvsForSelected()
    {
        return Selection.gameObjects.Length > 0;
    }

    // -----------------------------------------------------------------
    // Wall-grid UV generation
    // -----------------------------------------------------------------

    [MenuItem(
        "Tools/UV Generation/Generate Wall Grid UVs for Selected Meshes"
    )]
    private static void GenerateWallGridUvsForSelected()
    {
        HashSet<MeshFilter> meshFilters = GetSelectedMeshFilters();

        if (meshFilters.Count == 0)
        {
            Debug.LogError(
                "Select at least one GameObject containing a MeshFilter."
            );
            return;
        }

        EnsureAssetFolderExists(WallGridOutputFolder);

        int generatedCount = 0;
        int skippedCount = 0;
        int failedCount = 0;

        foreach (MeshFilter meshFilter in meshFilters)
        {
            Mesh sourceMesh = meshFilter.sharedMesh;

            if (sourceMesh == null)
            {
                Debug.LogWarning(
                    $"Skipping '{meshFilter.name}': the MeshFilter has no mesh.",
                    meshFilter
                );

                skippedCount++;
                continue;
            }

            Mesh generatedMesh = null;

            try
            {
                int facadeCount;
                int wallTriangleCount;
                int duplicatedVertexCount;

                generatedMesh = BuildWallGridMesh(
                    sourceMesh,
                    meshFilter.transform,
                    out facadeCount,
                    out wallTriangleCount,
                    out duplicatedVertexCount
                );

                generatedMesh.name =
                    $"{sourceMesh.name}_WallGridUV";

                string filename = SanitizeFilename(
                    $"{meshFilter.gameObject.name}_" +
                    $"{sourceMesh.name}_WallGridUV.asset"
                );

                string assetPath =
                    AssetDatabase.GenerateUniqueAssetPath(
                        $"{WallGridOutputFolder}/{filename}"
                    );

                AssetDatabase.CreateAsset(generatedMesh, assetPath);

                AssignGeneratedMesh(
                    meshFilter,
                    generatedMesh,
                    "Assign Generated Wall Grid UV Mesh"
                );

                generatedCount++;

                Debug.Log(
                    $"Generated wall-grid UVs for " +
                    $"'{meshFilter.gameObject.name}'.\n" +
                    $"Asset: {assetPath}\n" +
                    $"Facade islands: {facadeCount:N0}\n" +
                    $"Wall triangles: {wallTriangleCount:N0}\n" +
                    $"Source vertices: {sourceMesh.vertexCount:N0}\n" +
                    $"Generated vertices: {generatedMesh.vertexCount:N0}\n" +
                    $"Additional seam vertices: " +
                    $"{duplicatedVertexCount:N0}\n" +
                    $"Data channel: C# UV channel {WallGridUvChannel} " +
                    $"/ Shader Graph UV{WallGridUvChannel}.\n" +
                    "Data layout: (facade U, facade V, facade width, " +
                    "facade height).",
                    meshFilter
                );
            }
            catch (Exception exception)
            {
                DestroyUnsavedMesh(generatedMesh);
                failedCount++;

                Debug.LogException(
                    new InvalidOperationException(
                        $"Failed to generate wall-grid UVs for " +
                        $"'{meshFilter.gameObject.name}'.",
                        exception
                    ),
                    meshFilter
                );
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(
            $"Wall-grid UV generation complete. " +
            $"Generated: {generatedCount}, " +
            $"Skipped: {skippedCount}, " +
            $"Failed: {failedCount}."
        );
    }

    [MenuItem(
        "Tools/UV Generation/Generate Wall Grid UVs for Selected Meshes",
        validate = true
    )]
    private static bool ValidateGenerateWallGridUvsForSelected()
    {
        return Selection.gameObjects.Length > 0;
    }

    private static Mesh BuildWallGridMesh(
        Mesh sourceMesh,
        Transform sourceTransform,
        out int facadeCount,
        out int wallTriangleCount,
        out int duplicatedVertexCount)
    {
        if (!sourceMesh.isReadable)
        {
            throw new InvalidOperationException(
                $"Mesh '{sourceMesh.name}' is not readable. Enable Read/Write " +
                "on the source mesh before generating wall-grid UVs."
            );
        }

        for (int submeshIndex = 0;
             submeshIndex < sourceMesh.subMeshCount;
             submeshIndex++)
        {
            if (sourceMesh.GetTopology(submeshIndex) !=
                MeshTopology.Triangles)
            {
                throw new NotSupportedException(
                    $"Mesh '{sourceMesh.name}' contains a non-triangle " +
                    $"submesh at index {submeshIndex}."
                );
            }
        }

        Vector3[] sourceVertices = sourceMesh.vertices;
        int sourceVertexCount = sourceVertices.Length;

        Vector3[] worldVertices =
            new Vector3[sourceVertexCount];

        Matrix4x4 localToWorld =
            sourceTransform.localToWorldMatrix;

        for (int vertexIndex = 0;
             vertexIndex < sourceVertexCount;
             vertexIndex++)
        {
            worldVertices[vertexIndex] =
                localToWorld.MultiplyPoint3x4(
                    sourceVertices[vertexIndex]
                );
        }

        List<TriangleData> triangles =
            BuildTriangleData(sourceMesh, worldVertices);

        int[] weldedVertexIds =
            BuildWeldedVertexIds(worldVertices);

        Dictionary<EdgeKey, List<int>> edgeToTriangles =
            BuildEdgeAdjacency(triangles, weldedVertexIds);

        // IMPORTANT: FindFacadeGroups receives the same welded-ID mapping
        // used to build the adjacency dictionary. This avoids the previous
        // mismatch between original vertex indices and welded edge keys.
        List<FacadeGroup> facadeGroups =
            FindFacadeGroups(
                triangles,
                edgeToTriangles,
                worldVertices,
                weldedVertexIds
            );

        facadeCount = facadeGroups.Count;
        wallTriangleCount = 0;

        foreach (TriangleData triangle in triangles)
        {
            if (triangle.FacadeGroupIndex >= 0)
            {
                wallTriangleCount++;
            }
        }

        Vector3[] sourceNormals = sourceMesh.normals;
        Vector4[] sourceTangents = sourceMesh.tangents;
        Color32[] sourceColors = sourceMesh.colors32;

        bool hasNormals =
            sourceNormals != null &&
            sourceNormals.Length == sourceVertexCount;

        bool hasTangents =
            sourceTangents != null &&
            sourceTangents.Length == sourceVertexCount;

        bool hasColors =
            sourceColors != null &&
            sourceColors.Length == sourceVertexCount;

        var sourceUvChannels = new List<Vector4>[8];
        var hasUvChannel = new bool[8];

        for (int channel = 0; channel < 8; channel++)
        {
            sourceUvChannels[channel] =
                new List<Vector4>(sourceVertexCount);

            sourceMesh.GetUVs(
                channel,
                sourceUvChannels[channel]
            );

            hasUvChannel[channel] =
                sourceUvChannels[channel].Count ==
                sourceVertexCount;
        }

        var generatedVertices =
            new List<Vector3>(sourceVertexCount);

        var generatedNormals =
            hasNormals
                ? new List<Vector3>(sourceVertexCount)
                : null;

        var generatedTangents =
            hasTangents
                ? new List<Vector4>(sourceVertexCount)
                : null;

        var generatedColors =
            hasColors
                ? new List<Color32>(sourceVertexCount)
                : null;

        var generatedUvChannels =
            new List<Vector4>[8];

        for (int channel = 0; channel < 8; channel++)
        {
            if (channel == WallGridUvChannel ||
                hasUvChannel[channel])
            {
                generatedUvChannels[channel] =
                    new List<Vector4>(sourceVertexCount);
            }
        }

        var generatedSubmeshIndices =
            new List<int>[sourceMesh.subMeshCount];

        for (int submeshIndex = 0;
             submeshIndex < sourceMesh.subMeshCount;
             submeshIndex++)
        {
            generatedSubmeshIndices[submeshIndex] =
                new List<int>();
        }

        // A corner shared by two facade islands needs two vertices because
        // one vertex cannot contain two different facade-coordinate values.
        var vertexMap =
            new Dictionary<VertexFacadeKey, int>();

        foreach (TriangleData triangle in triangles)
        {
            int generatedA = GetOrCreateGeneratedVertex(
                triangle.A,
                triangle.FacadeGroupIndex
            );

            int generatedB = GetOrCreateGeneratedVertex(
                triangle.B,
                triangle.FacadeGroupIndex
            );

            int generatedC = GetOrCreateGeneratedVertex(
                triangle.C,
                triangle.FacadeGroupIndex
            );

            List<int> submeshIndices =
                generatedSubmeshIndices[
                    triangle.SubmeshIndex
                ];

            submeshIndices.Add(generatedA);
            submeshIndices.Add(generatedB);
            submeshIndices.Add(generatedC);
        }

        int GetOrCreateGeneratedVertex(
            int sourceVertexIndex,
            int facadeGroupIndex)
        {
            var key = new VertexFacadeKey(
                sourceVertexIndex,
                facadeGroupIndex
            );

            int generatedVertexIndex;

            if (vertexMap.TryGetValue(
                    key,
                    out generatedVertexIndex))
            {
                return generatedVertexIndex;
            }

            generatedVertexIndex =
                generatedVertices.Count;

            vertexMap.Add(key, generatedVertexIndex);

            generatedVertices.Add(
                sourceVertices[sourceVertexIndex]
            );

            if (hasNormals)
            {
                generatedNormals.Add(
                    sourceNormals[sourceVertexIndex]
                );
            }

            if (hasTangents)
            {
                generatedTangents.Add(
                    sourceTangents[sourceVertexIndex]
                );
            }

            if (hasColors)
            {
                generatedColors.Add(
                    sourceColors[sourceVertexIndex]
                );
            }

            for (int channel = 0;
                 channel < 8;
                 channel++)
            {
                List<Vector4> generatedChannel =
                    generatedUvChannels[channel];

                if (generatedChannel == null)
                {
                    continue;
                }

                if (channel == WallGridUvChannel)
                {
                    generatedChannel.Add(
                        BuildWallGridData(
                            sourceVertexIndex,
                            facadeGroupIndex,
                            worldVertices,
                            facadeGroups
                        )
                    );
                }
                else
                {
                    generatedChannel.Add(
                        sourceUvChannels[channel][
                            sourceVertexIndex
                        ]
                    );
                }
            }

            return generatedVertexIndex;
        }

        var generatedMesh = new Mesh
        {
            name = $"{sourceMesh.name}_WallGridUV",
            indexFormat =
                generatedVertices.Count > ushort.MaxValue ||
                sourceMesh.indexFormat == IndexFormat.UInt32
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16
        };

        generatedMesh.SetVertices(generatedVertices);

        if (hasNormals)
        {
            generatedMesh.SetNormals(generatedNormals);
        }

        if (hasTangents)
        {
            generatedMesh.SetTangents(generatedTangents);
        }

        if (hasColors)
        {
            generatedMesh.SetColors(generatedColors);
        }

        for (int channel = 0; channel < 8; channel++)
        {
            if (generatedUvChannels[channel] != null)
            {
                generatedMesh.SetUVs(
                    channel,
                    generatedUvChannels[channel]
                );
            }
        }

        generatedMesh.subMeshCount =
            sourceMesh.subMeshCount;

        for (int submeshIndex = 0;
             submeshIndex < generatedSubmeshIndices.Length;
             submeshIndex++)
        {
            generatedMesh.SetTriangles(
                generatedSubmeshIndices[submeshIndex],
                submeshIndex,
                false
            );
        }

        if (!hasNormals)
        {
            generatedMesh.RecalculateNormals();
        }

        generatedMesh.RecalculateBounds();

        if (sourceMesh.blendShapeCount > 0)
        {
            Debug.LogWarning(
                $"Mesh '{sourceMesh.name}' contains blend shapes. " +
                "The wall-grid generator is intended for static " +
                "MeshFilter geometry and does not copy blend-shape data."
            );
        }

        duplicatedVertexCount =
            Mathf.Max(
                generatedMesh.vertexCount - sourceVertexCount,
                0
            );

        return generatedMesh;
    }

    private static List<TriangleData> BuildTriangleData(
        Mesh sourceMesh,
        Vector3[] worldVertices)
    {
        var triangles = new List<TriangleData>();

        float maximumWallNormalY =
            Mathf.Sin(
                MaximumWallTiltDegrees *
                Mathf.Deg2Rad
            );

        for (int submeshIndex = 0;
             submeshIndex < sourceMesh.subMeshCount;
             submeshIndex++)
        {
            int[] submeshTriangles =
                sourceMesh.GetTriangles(submeshIndex);

            for (int index = 0;
                 index < submeshTriangles.Length;
                 index += 3)
            {
                int a = submeshTriangles[index];
                int b = submeshTriangles[index + 1];
                int c = submeshTriangles[index + 2];

                Vector3 edgeAB =
                    worldVertices[b] -
                    worldVertices[a];

                Vector3 edgeAC =
                    worldVertices[c] -
                    worldVertices[a];

                Vector3 cross =
                    Vector3.Cross(edgeAB, edgeAC);

                float doubleArea = cross.magnitude;

                Vector3 normal =
                    doubleArea > 0.000001f
                        ? cross / doubleArea
                        : Vector3.up;

                bool isWall =
                    doubleArea > 0.000001f &&
                    Mathf.Abs(
                        Vector3.Dot(
                            normal,
                            Vector3.up
                        )
                    ) <= maximumWallNormalY;

                triangles.Add(
                    new TriangleData(
                        a,
                        b,
                        c,
                        submeshIndex,
                        normal,
                        worldVertices[a],
                        doubleArea * 0.5f,
                        isWall
                    )
                );
            }
        }

        return triangles;
    }

    private static int[] BuildWeldedVertexIds(
        Vector3[] worldVertices)
    {
        var positionToWeldedId =
            new Dictionary<QuantizedPositionKey, int>();

        var weldedVertexIds =
            new int[worldVertices.Length];

        int nextWeldedId = 0;

        for (int vertexIndex = 0;
             vertexIndex < worldVertices.Length;
             vertexIndex++)
        {
            var positionKey =
                new QuantizedPositionKey(
                    worldVertices[vertexIndex],
                    AdjacencyWeldTolerance
                );

            int weldedId;

            if (!positionToWeldedId.TryGetValue(
                    positionKey,
                    out weldedId))
            {
                weldedId = nextWeldedId++;
                positionToWeldedId.Add(
                    positionKey,
                    weldedId
                );
            }

            weldedVertexIds[vertexIndex] = weldedId;
        }

        return weldedVertexIds;
    }

    private static Dictionary<EdgeKey, List<int>>
        BuildEdgeAdjacency(
            List<TriangleData> triangles,
            int[] weldedVertexIds)
    {
        var edgeToTriangles =
            new Dictionary<EdgeKey, List<int>>();

        for (int triangleIndex = 0;
             triangleIndex < triangles.Count;
             triangleIndex++)
        {
            TriangleData triangle =
                triangles[triangleIndex];

            AddEdge(
                new EdgeKey(
                    weldedVertexIds[triangle.A],
                    weldedVertexIds[triangle.B]
                ),
                triangleIndex
            );

            AddEdge(
                new EdgeKey(
                    weldedVertexIds[triangle.B],
                    weldedVertexIds[triangle.C]
                ),
                triangleIndex
            );

            AddEdge(
                new EdgeKey(
                    weldedVertexIds[triangle.C],
                    weldedVertexIds[triangle.A]
                ),
                triangleIndex
            );
        }

        return edgeToTriangles;

        void AddEdge(EdgeKey edge, int triangleIndex)
        {
            List<int> connectedTriangles;

            if (!edgeToTriangles.TryGetValue(
                    edge,
                    out connectedTriangles))
            {
                connectedTriangles = new List<int>(2);
                edgeToTriangles.Add(
                    edge,
                    connectedTriangles
                );
            }

            connectedTriangles.Add(triangleIndex);
        }
    }

    private static List<FacadeGroup> FindFacadeGroups(
        List<TriangleData> triangles,
        Dictionary<EdgeKey, List<int>> edgeToTriangles,
        Vector3[] worldVertices,
        int[] weldedVertexIds)
    {
        var facadeGroups = new List<FacadeGroup>();

        float minimumNormalDot =
            Mathf.Cos(
                CoplanarNormalToleranceDegrees *
                Mathf.Deg2Rad
            );

        var queue = new Queue<int>();

        for (int seedTriangleIndex = 0;
             seedTriangleIndex < triangles.Count;
             seedTriangleIndex++)
        {
            TriangleData seedTriangle =
                triangles[seedTriangleIndex];

            if (!seedTriangle.IsWall ||
                seedTriangle.FacadeGroupIndex >= 0)
            {
                continue;
            }

            int facadeGroupIndex =
                facadeGroups.Count;

            var groupTriangleIndices =
                new List<int>();

            Vector3 seedNormal =
                seedTriangle.NormalWS;

            Vector3 seedPlanePoint =
                seedTriangle.PlanePointWS;

            seedTriangle.FacadeGroupIndex =
                facadeGroupIndex;

            queue.Enqueue(seedTriangleIndex);

            while (queue.Count > 0)
            {
                int triangleIndex = queue.Dequeue();
                TriangleData triangle = triangles[triangleIndex];

                groupTriangleIndices.Add(triangleIndex);

                // The adjacency dictionary is keyed by welded IDs, so all
                // edge lookups must use welded IDs as well.
                VisitEdge(
                    new EdgeKey(
                        weldedVertexIds[triangle.A],
                        weldedVertexIds[triangle.B]
                    )
                );

                VisitEdge(
                    new EdgeKey(
                        weldedVertexIds[triangle.B],
                        weldedVertexIds[triangle.C]
                    )
                );

                VisitEdge(
                    new EdgeKey(
                        weldedVertexIds[triangle.C],
                        weldedVertexIds[triangle.A]
                    )
                );
            }

            Vector3 weightedNormal = Vector3.zero;

            foreach (int triangleIndex in groupTriangleIndices)
            {
                TriangleData triangle = triangles[triangleIndex];

                weightedNormal +=
                    triangle.NormalWS *
                    Mathf.Max(triangle.Area, 0.000001f);
            }

            Vector3 facadeNormal =
                weightedNormal.sqrMagnitude > 0.000001f
                    ? weightedNormal.normalized
                    : seedNormal;

            // Project world up onto the facade plane. For vertical walls,
            // this remains world up; for slightly tilted walls it remains
            // tangent to the wall.
            Vector3 verticalAxis =
                Vector3.ProjectOnPlane(
                    Vector3.up,
                    facadeNormal
                );

            if (verticalAxis.sqrMagnitude < 0.000001f)
            {
                verticalAxis = Vector3.up;
            }
            else
            {
                verticalAxis.Normalize();
            }

            Vector3 horizontalAxis =
                Vector3.Cross(
                    verticalAxis,
                    facadeNormal
                ).normalized;

            CanonicalizeHorizontalAxis(
                ref horizontalAxis
            );

            float minU = float.PositiveInfinity;
            float maxU = float.NegativeInfinity;
            float minV = float.PositiveInfinity;
            float maxV = float.NegativeInfinity;

            foreach (int triangleIndex in groupTriangleIndices)
            {
                TriangleData triangle = triangles[triangleIndex];

                IncludeVertex(triangle.A);
                IncludeVertex(triangle.B);
                IncludeVertex(triangle.C);
            }

            facadeGroups.Add(
                new FacadeGroup(
                    horizontalAxis,
                    verticalAxis,
                    minU,
                    maxU,
                    minV,
                    maxV
                )
            );

            void VisitEdge(EdgeKey edge)
            {
                List<int> adjacentTriangles;

                if (!edgeToTriangles.TryGetValue(
                        edge,
                        out adjacentTriangles))
                {
                    return;
                }

                foreach (int adjacentTriangleIndex in
                         adjacentTriangles)
                {
                    TriangleData adjacentTriangle =
                        triangles[adjacentTriangleIndex];

                    if (!adjacentTriangle.IsWall ||
                        adjacentTriangle.FacadeGroupIndex >= 0)
                    {
                        continue;
                    }

                    float normalDot =
                        Vector3.Dot(
                            seedNormal,
                            adjacentTriangle.NormalWS
                        );

                    if (normalDot < minimumNormalDot)
                    {
                        continue;
                    }

                    float planeDistance =
                        Mathf.Abs(
                            Vector3.Dot(
                                seedNormal,
                                adjacentTriangle.PlanePointWS -
                                seedPlanePoint
                            )
                        );

                    if (planeDistance >
                        CoplanarPlaneDistanceTolerance)
                    {
                        continue;
                    }

                    adjacentTriangle.FacadeGroupIndex =
                        facadeGroupIndex;

                    queue.Enqueue(adjacentTriangleIndex);
                }
            }

            void IncludeVertex(int vertexIndex)
            {
                Vector3 worldPosition =
                    worldVertices[vertexIndex];

                float u =
                    Vector3.Dot(
                        worldPosition,
                        horizontalAxis
                    );

                float v =
                    Vector3.Dot(
                        worldPosition,
                        verticalAxis
                    );

                minU = Mathf.Min(minU, u);
                maxU = Mathf.Max(maxU, u);
                minV = Mathf.Min(minV, v);
                maxV = Mathf.Max(maxV, v);
            }
        }

        return facadeGroups;
    }

    private static Vector4 BuildWallGridData(
        int sourceVertexIndex,
        int facadeGroupIndex,
        Vector3[] worldVertices,
        List<FacadeGroup> facadeGroups)
    {
        // Roof, floor, and other non-wall triangles receive zero data.
        if (facadeGroupIndex < 0)
        {
            return Vector4.zero;
        }

        FacadeGroup facade =
            facadeGroups[facadeGroupIndex];

        Vector3 worldPosition =
            worldVertices[sourceVertexIndex];

        float projectedU =
            Vector3.Dot(
                worldPosition,
                facade.HorizontalAxisWS
            );

        float projectedV =
            Vector3.Dot(
                worldPosition,
                facade.VerticalAxisWS
            );

        float facadeU = projectedU - facade.MinU;
        float facadeV = projectedV - facade.MinV;

        return new Vector4(
            facadeU,
            facadeV,
            facade.Width,
            facade.Height
        );
    }

    private static void CanonicalizeHorizontalAxis(
        ref Vector3 horizontalAxis)
    {
        float absoluteX = Mathf.Abs(horizontalAxis.x);
        float absoluteZ = Mathf.Abs(horizontalAxis.z);

        bool shouldFlip =
            absoluteX >= absoluteZ
                ? horizontalAxis.x < 0f
                : horizontalAxis.z < 0f;

        if (shouldFlip)
        {
            horizontalAxis = -horizontalAxis;
        }
    }

    private static Mesh BuildLightmapUvMesh(
        Mesh sourceMesh)
    {
        // Work on a duplicate so the original IFC-derived asset remains
        // untouched.
        Mesh generatedMesh =
            UnityEngine.Object.Instantiate(sourceMesh);

        bool succeeded =
            Unwrapping.GenerateSecondaryUVSet(generatedMesh);

        if (!succeeded)
        {
            DestroyUnsavedMesh(generatedMesh);

            throw new InvalidOperationException(
                $"Unity could not generate lightmap UVs for " +
                $"'{sourceMesh.name}'. The mesh may be too large, " +
                "non-readable, or unsuitable for automatic unwrapping."
            );
        }

        // GenerateSecondaryUVSet writes to Mesh.uv2, which is C# channel 1
        // and Shader Graph UV1.
        Vector2[] lightmapUvs =
            generatedMesh.uv2;

        if (lightmapUvs == null ||
            lightmapUvs.Length != generatedMesh.vertexCount)
        {
            DestroyUnsavedMesh(generatedMesh);

            throw new InvalidOperationException(
                $"Unity reported successful lightmap UV generation for " +
                $"'{sourceMesh.name}', but Mesh.uv2 does not contain one " +
                "coordinate per generated vertex."
            );
        }

        // The triplanar material does not sample UV0, but Unity's GI meta
        // pass expects an albedo UV channel. Reuse the non-overlapping
        // lightmap layout to satisfy that requirement.
        generatedMesh.uv =
            (Vector2[])lightmapUvs.Clone();

        return generatedMesh;
    }

    // -----------------------------------------------------------------
    // Shared editor helpers
    // -----------------------------------------------------------------

    private static HashSet<MeshFilter> GetSelectedMeshFilters()
    {
        var meshFilters = new HashSet<MeshFilter>();

        foreach (GameObject selectedObject in Selection.gameObjects)
        {
            MeshFilter[] childFilters =
                selectedObject.GetComponentsInChildren<MeshFilter>(
                    true
                );

            foreach (MeshFilter meshFilter in childFilters)
            {
                meshFilters.Add(meshFilter);
            }
        }

        return meshFilters;
    }

    private static void AssignGeneratedMesh(
        MeshFilter meshFilter,
        Mesh generatedMesh,
        string undoLabel)
    {
        Undo.RecordObject(meshFilter, undoLabel);
        meshFilter.sharedMesh = generatedMesh;
        EditorUtility.SetDirty(meshFilter);

        if (meshFilter.gameObject.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(
                meshFilter.gameObject.scene
            );
        }
    }

    private static void DestroyUnsavedMesh(Mesh mesh)
    {
        if (mesh != null && !AssetDatabase.Contains(mesh))
        {
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    private static void EnsureAssetFolderExists(
        string folderPath)
    {
        string[] parts = folderPath.Split('/');
        string currentPath = parts[0];

        for (int index = 1;
             index < parts.Length;
             index++)
        {
            string nextPath =
                $"{currentPath}/{parts[index]}";

            if (!AssetDatabase.IsValidFolder(nextPath))
            {
                AssetDatabase.CreateFolder(
                    currentPath,
                    parts[index]
                );
            }

            currentPath = nextPath;
        }
    }

    private static string SanitizeFilename(string filename)
    {
        foreach (char invalidCharacter in
                 Path.GetInvalidFileNameChars())
        {
            filename = filename.Replace(
                invalidCharacter,
                '_'
            );
        }

        // IFC-derived names can become extremely long after several
        // processing steps. Keep generated asset names manageable.
        const int maximumLength = 160;

        if (filename.Length <= maximumLength)
        {
            return filename;
        }

        string extension = Path.GetExtension(filename);
        string nameWithoutExtension =
            Path.GetFileNameWithoutExtension(filename);

        int availableLength =
            maximumLength - extension.Length;

        return nameWithoutExtension.Substring(
                   0,
                   Mathf.Min(
                       nameWithoutExtension.Length,
                       availableLength
                   )
               ) + extension;
    }

    // -----------------------------------------------------------------
    // Internal data structures
    // -----------------------------------------------------------------

    private sealed class TriangleData
    {
        public readonly int A;
        public readonly int B;
        public readonly int C;
        public readonly int SubmeshIndex;
        public readonly Vector3 NormalWS;
        public readonly Vector3 PlanePointWS;
        public readonly float Area;
        public readonly bool IsWall;

        public int FacadeGroupIndex = -1;

        public TriangleData(
            int a,
            int b,
            int c,
            int submeshIndex,
            Vector3 normalWS,
            Vector3 planePointWS,
            float area,
            bool isWall)
        {
            A = a;
            B = b;
            C = c;
            SubmeshIndex = submeshIndex;
            NormalWS = normalWS;
            PlanePointWS = planePointWS;
            Area = area;
            IsWall = isWall;
        }
    }

    private sealed class FacadeGroup
    {
        public readonly Vector3 HorizontalAxisWS;
        public readonly Vector3 VerticalAxisWS;
        public readonly float MinU;
        public readonly float MinV;
        public readonly float Width;
        public readonly float Height;

        public FacadeGroup(
            Vector3 horizontalAxisWS,
            Vector3 verticalAxisWS,
            float minU,
            float maxU,
            float minV,
            float maxV)
        {
            HorizontalAxisWS = horizontalAxisWS;
            VerticalAxisWS = verticalAxisWS;
            MinU = minU;
            MinV = minV;
            Width = Mathf.Max(maxU - minU, 0.0001f);
            Height = Mathf.Max(maxV - minV, 0.0001f);
        }
    }

    private readonly struct VertexFacadeKey :
        IEquatable<VertexFacadeKey>
    {
        private readonly int sourceVertexIndex;
        private readonly int facadeGroupIndex;

        public VertexFacadeKey(
            int sourceVertexIndex,
            int facadeGroupIndex)
        {
            this.sourceVertexIndex = sourceVertexIndex;
            this.facadeGroupIndex = facadeGroupIndex;
        }

        public bool Equals(VertexFacadeKey other)
        {
            return sourceVertexIndex == other.sourceVertexIndex &&
                   facadeGroupIndex == other.facadeGroupIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is VertexFacadeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (sourceVertexIndex * 397) ^ facadeGroupIndex;
            }
        }
    }

    private readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        private readonly int first;
        private readonly int second;

        public EdgeKey(int vertexA, int vertexB)
        {
            if (vertexA <= vertexB)
            {
                first = vertexA;
                second = vertexB;
            }
            else
            {
                first = vertexB;
                second = vertexA;
            }
        }

        public bool Equals(EdgeKey other)
        {
            return first == other.first &&
                   second == other.second;
        }

        public override bool Equals(object obj)
        {
            return obj is EdgeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (first * 397) ^ second;
            }
        }
    }

    private readonly struct QuantizedPositionKey :
        IEquatable<QuantizedPositionKey>
    {
        private readonly long x;
        private readonly long y;
        private readonly long z;

        public QuantizedPositionKey(
            Vector3 position,
            float tolerance)
        {
            double inverseTolerance =
                1.0 / Math.Max(tolerance, 0.0000001f);

            x = (long)Math.Round(
                position.x * inverseTolerance
            );

            y = (long)Math.Round(
                position.y * inverseTolerance
            );

            z = (long)Math.Round(
                position.z * inverseTolerance
            );
        }

        public bool Equals(QuantizedPositionKey other)
        {
            return x == other.x &&
                   y == other.y &&
                   z == other.z;
        }

        public override bool Equals(object obj)
        {
            return obj is QuantizedPositionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + x.GetHashCode();
                hash = hash * 31 + y.GetHashCode();
                hash = hash * 31 + z.GetHashCode();
                return hash;
            }
        }
    }
}
