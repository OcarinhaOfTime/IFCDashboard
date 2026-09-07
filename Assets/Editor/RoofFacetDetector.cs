#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared editor-side roof analysis used by rooftop placement tools.
///
/// The detector reads child MeshFilter geometry in world space, extracts
/// upward-facing triangles near the top of a building, groups approximately
/// coplanar triangles, optionally separates disconnected components, and
/// analyses each resulting facet's boundary and principal directions.
/// </summary>
public static class RoofFacetDetector
{
    public enum OrientationMode
    {
        AutoBestFit,
        PrincipalAxis,
        LongestBoundaryEdge,
        BuildingLocalX,
        WorldX
    }

    [Serializable]
    public sealed class DetectionSettings
    {
        [Range(0f, 0.95f)]
        public float minimumRelativeHeight = 0.55f;

        [Min(0.1f)]
        public float roofSearchDepth = 6f;

        [Range(1f, 80f)]
        public float maximumRoofSlope = 55f;

        [Range(0.1f, 20f)]
        public float normalMergeTolerance = 4f;

        [Min(0.001f)]
        public float planeMergeTolerance = 0.18f;

        [Min(0.001f)]
        public float minimumTriangleArea = 0.02f;

        [Min(0.01f)]
        public float minimumFacetArea = 2f;

        [Tooltip(
            "Separates coplanar but spatially disconnected roof islands. " +
            "This prevents two distant roofs at the same height from being " +
            "treated as one placement surface."
        )]
        public bool splitDisconnectedFacets = true;

        public DetectionSettings Clone()
        {
            return (DetectionSettings)MemberwiseClone();
        }
    }

    [Serializable]
    public sealed class OrientationSettings
    {
        public bool snapPrincipalAxisToBoundary = true;

        [Range(0f, 45f)]
        public float boundarySnapTolerance = 14f;

        [Range(0f, 1f)]
        public float pcaAmbiguityThreshold = 0.08f;
    }

    public sealed class Triangle
    {
        public Vector3 a;
        public Vector3 b;
        public Vector3 c;
        public Vector3 normal;
        public Vector3 centroid;
        public float area;
    }

    public sealed class Facet
    {
        public readonly List<Triangle> triangles = new List<Triangle>();
        public bool enabled = true;
        public Vector3 normal;
        public Vector3 center;
        public float planeDistance;
        public float area;
        public float minY = float.PositiveInfinity;
        public float maxY = float.NegativeInfinity;

        public Vector3 principalAxis;
        public Vector3 longestBoundaryAxis;
        public float orientationConfidence;
        public readonly List<Vector3> boundaryLineSegments =
            new List<Vector3>();

        public float Slope
        {
            get { return Vector3.Angle(normal, Vector3.up); }
        }

        public void Add(Triangle triangle)
        {
            float previousArea = area;
            float combinedArea = previousArea + triangle.area;

            if (previousArea <= 0.000001f)
            {
                normal = triangle.normal;
                center = triangle.centroid;
            }
            else
            {
                normal = (
                    normal * previousArea +
                    triangle.normal * triangle.area
                ).normalized;

                center = (
                    center * previousArea +
                    triangle.centroid * triangle.area
                ) / combinedArea;
            }

            area = combinedArea;
            planeDistance = Vector3.Dot(normal, center);
            minY = Mathf.Min(
                minY,
                triangle.a.y,
                triangle.b.y,
                triangle.c.y
            );
            maxY = Mathf.Max(
                maxY,
                triangle.a.y,
                triangle.b.y,
                triangle.c.y
            );
            triangles.Add(triangle);
        }
    }

    public struct Triangle2D
    {
        public Vector2 a;
        public Vector2 b;
        public Vector2 c;
    }

    public struct Projection
    {
        public Vector3 origin;
        public Vector3 u;
        public Vector3 v;
        public Vector3 normal;
        public Vector2 min;
        public Vector2 max;
        public List<Triangle2D> triangles;
        public string orientationSummary;
        public float resolvedRotationDegrees;

        public Vector2 Size
        {
            get { return max - min; }
        }

        public Vector3 ToWorld(Vector2 point)
        {
            return origin + u * point.x + v * point.y;
        }
    }

    public struct ProjectionScore : IComparable<ProjectionScore>
    {
        public int primary;
        public int secondary;

        public ProjectionScore(int primaryScore, int secondaryScore)
        {
            primary = primaryScore;
            secondary = secondaryScore;
        }

        public int CompareTo(ProjectionScore other)
        {
            int primaryComparison = primary.CompareTo(other.primary);
            return primaryComparison != 0
                ? primaryComparison
                : secondary.CompareTo(other.secondary);
        }
    }

    public sealed class DetectionResult
    {
        public Bounds buildingBounds;
        public int scannedTriangles;
        public int acceptedTriangles;
        public readonly List<Facet> facets = new List<Facet>();
        public string status;
        public bool succeeded;
    }

    private readonly struct QuantizedPoint :
        IEquatable<QuantizedPoint>,
        IComparable<QuantizedPoint>
    {
        private const float Quantization = 10000f;

        public readonly long x;
        public readonly long y;
        public readonly long z;

        public QuantizedPoint(Vector3 point)
        {
            x = Mathf.RoundToInt(point.x * Quantization);
            y = Mathf.RoundToInt(point.y * Quantization);
            z = Mathf.RoundToInt(point.z * Quantization);
        }

        public bool Equals(QuantizedPoint other)
        {
            return x == other.x && y == other.y && z == other.z;
        }

        public override bool Equals(object obj)
        {
            return obj is QuantizedPoint &&
                Equals((QuantizedPoint)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = x.GetHashCode();
                hash = (hash * 397) ^ y.GetHashCode();
                hash = (hash * 397) ^ z.GetHashCode();
                return hash;
            }
        }

        public int CompareTo(QuantizedPoint other)
        {
            int compareX = x.CompareTo(other.x);
            if (compareX != 0)
            {
                return compareX;
            }

            int compareY = y.CompareTo(other.y);
            return compareY != 0
                ? compareY
                : z.CompareTo(other.z);
        }
    }

    private readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        public readonly QuantizedPoint a;
        public readonly QuantizedPoint b;

        public EdgeKey(Vector3 first, Vector3 second)
        {
            QuantizedPoint p0 = new QuantizedPoint(first);
            QuantizedPoint p1 = new QuantizedPoint(second);

            if (p0.CompareTo(p1) <= 0)
            {
                a = p0;
                b = p1;
            }
            else
            {
                a = p1;
                b = p0;
            }
        }

        public bool Equals(EdgeKey other)
        {
            return a.Equals(other.a) && b.Equals(other.b);
        }

        public override bool Equals(object obj)
        {
            return obj is EdgeKey && Equals((EdgeKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (a.GetHashCode() * 397) ^ b.GetHashCode();
            }
        }
    }

    private sealed class EdgeRecord
    {
        public Vector3 a;
        public Vector3 b;
        public int count;
    }

    public static DetectionResult Detect(
        GameObject buildingRoot,
        DetectionSettings settings)
    {
        DetectionResult result = new DetectionResult();
        settings = settings ?? new DetectionSettings();

        if (buildingRoot == null)
        {
            result.status = "No building root was assigned.";
            return result;
        }

        Bounds buildingBounds;
        if (!TryGetBuildingBounds(buildingRoot, out buildingBounds))
        {
            result.status =
                "No usable MeshFilter geometry was found under the " +
                "selected building.";
            return result;
        }

        result.buildingBounds = buildingBounds;

        float threshold = Mathf.Max(
            buildingBounds.min.y +
                buildingBounds.size.y *
                Mathf.Clamp01(settings.minimumRelativeHeight),
            buildingBounds.max.y -
                Mathf.Max(0.1f, settings.roofSearchDepth)
        );

        float minUpDot = Mathf.Cos(
            Mathf.Clamp(settings.maximumRoofSlope, 1f, 89f) *
            Mathf.Deg2Rad
        );

        List<Facet> planeGroups = new List<Facet>();

        MeshFilter[] filters =
            buildingRoot.GetComponentsInChildren<MeshFilter>(true);

        foreach (MeshFilter filter in filters)
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null)
            {
                continue;
            }

            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            if (renderer != null && !renderer.enabled)
            {
                continue;
            }

            Vector3[] vertices;

            try
            {
                vertices = mesh.vertices;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "Could not read mesh '" + mesh.name + "' on '" +
                    filter.name + "': " + exception.Message,
                    filter
                );
                continue;
            }

            Matrix4x4 matrix = filter.transform.localToWorldMatrix;

            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] indices;

                try
                {
                    indices = mesh.GetTriangles(subMesh);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "Could not read triangles from '" + mesh.name +
                        "': " + exception.Message,
                        filter
                    );
                    continue;
                }

                for (int index = 0;
                    index + 2 < indices.Length;
                    index += 3)
                {
                    result.scannedTriangles++;

                    int indexA = indices[index];
                    int indexB = indices[index + 1];
                    int indexC = indices[index + 2];

                    if (
                        indexA < 0 || indexA >= vertices.Length ||
                        indexB < 0 || indexB >= vertices.Length ||
                        indexC < 0 || indexC >= vertices.Length
                    )
                    {
                        continue;
                    }

                    Vector3 a = matrix.MultiplyPoint3x4(vertices[indexA]);
                    Vector3 b = matrix.MultiplyPoint3x4(vertices[indexB]);
                    Vector3 c = matrix.MultiplyPoint3x4(vertices[indexC]);

                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    float area = cross.magnitude * 0.5f;

                    if (area < settings.minimumTriangleArea)
                    {
                        continue;
                    }

                    Vector3 centroid = (a + b + c) / 3f;
                    if (centroid.y < threshold)
                    {
                        continue;
                    }

                    Vector3 normal = cross.normalized;
                    if (Vector3.Dot(normal, Vector3.up) < 0f)
                    {
                        normal = -normal;
                    }

                    if (Vector3.Dot(normal, Vector3.up) < minUpDot)
                    {
                        continue;
                    }

                    result.acceptedTriangles++;

                    Triangle triangle = new Triangle
                    {
                        a = a,
                        b = b,
                        c = c,
                        normal = normal,
                        centroid = centroid,
                        area = area
                    };

                    AddTriangleToPlaneGroup(
                        planeGroups,
                        triangle,
                        settings.normalMergeTolerance,
                        settings.planeMergeTolerance
                    );
                }
            }
        }

        List<Facet> resolvedFacets = settings.splitDisconnectedFacets
            ? SplitDisconnectedComponents(planeGroups)
            : planeGroups;

        resolvedFacets.RemoveAll(
            facet => facet.area < settings.minimumFacetArea
        );

        resolvedFacets.Sort((left, right) =>
        {
            int heightComparison =
                right.center.y.CompareTo(left.center.y);

            return heightComparison != 0
                ? heightComparison
                : right.area.CompareTo(left.area);
        });

        foreach (Facet facet in resolvedFacets)
        {
            AnalyzeFacetGeometry(facet, buildingRoot.transform);
            result.facets.Add(facet);
        }

        result.succeeded = result.facets.Count > 0;
        result.status = string.Format(
            "Scanned {0:N0} triangles; accepted {1:N0}; " +
            "detected {2} roof facets.",
            result.scannedTriangles,
            result.acceptedTriangles,
            result.facets.Count
        );

        return result;
    }

    public static Projection ResolveProjection(
        Facet facet,
        Transform buildingRoot,
        OrientationMode orientationMode,
        float rotationOffsetDegrees,
        OrientationSettings orientationSettings,
        Func<Projection, ProjectionScore> evaluator = null)
    {
        if (facet == null)
        {
            return default(Projection);
        }

        orientationSettings =
            orientationSettings ?? new OrientationSettings();

        Vector3 baseAxis = GetOrientationAxis(
            facet,
            buildingRoot,
            orientationMode,
            orientationSettings
        );

        Projection primary = ProjectFacetWithAxis(
            facet,
            RotateAxisOnPlane(
                baseAxis,
                facet.normal,
                rotationOffsetDegrees
            ),
            DescribeOrientation(
                facet,
                orientationMode,
                orientationSettings,
                false
            ),
            rotationOffsetDegrees
        );

        if (orientationMode != OrientationMode.AutoBestFit)
        {
            return primary;
        }

        Projection perpendicular = ProjectFacetWithAxis(
            facet,
            RotateAxisOnPlane(
                baseAxis,
                facet.normal,
                rotationOffsetDegrees + 90f
            ),
            DescribeOrientation(
                facet,
                orientationMode,
                orientationSettings,
                true
            ),
            rotationOffsetDegrees + 90f
        );

        if (evaluator != null)
        {
            ProjectionScore primaryScore = evaluator(primary);
            ProjectionScore perpendicularScore = evaluator(perpendicular);

            int scoreComparison =
                perpendicularScore.CompareTo(primaryScore);

            if (scoreComparison > 0)
            {
                return perpendicular;
            }

            if (scoreComparison < 0)
            {
                return primary;
            }
        }

        Vector2 primarySize = primary.max - primary.min;
        Vector2 perpendicularSize =
            perpendicular.max - perpendicular.min;

        return primarySize.x >= perpendicularSize.x
            ? primary
            : perpendicular;
    }

    public static Projection ProjectFacetWithAxis(
        Facet facet,
        Vector3 axisU,
        string orientationSummary = "Custom",
        float resolvedRotationDegrees = 0f)
    {
        Vector3 normal = facet.normal.normalized;
        Vector3 u = Vector3.ProjectOnPlane(axisU, normal).normalized;

        if (u.sqrMagnitude < 0.0001f)
        {
            u = GetFallbackPlaneAxis(normal);
        }

        Vector3 v = Vector3.Cross(normal, u).normalized;
        Vector3 origin = facet.center;
        Vector2 min = new Vector2(
            float.PositiveInfinity,
            float.PositiveInfinity
        );
        Vector2 max = new Vector2(
            float.NegativeInfinity,
            float.NegativeInfinity
        );

        List<Triangle2D> projectedTriangles =
            new List<Triangle2D>(facet.triangles.Count);

        foreach (Triangle triangle in facet.triangles)
        {
            Triangle2D projected = new Triangle2D
            {
                a = Project(triangle.a, origin, u, v),
                b = Project(triangle.b, origin, u, v),
                c = Project(triangle.c, origin, u, v)
            };

            min = Vector2.Min(min, projected.a);
            min = Vector2.Min(min, projected.b);
            min = Vector2.Min(min, projected.c);
            max = Vector2.Max(max, projected.a);
            max = Vector2.Max(max, projected.b);
            max = Vector2.Max(max, projected.c);
            projectedTriangles.Add(projected);
        }

        return new Projection
        {
            origin = origin,
            u = u,
            v = v,
            normal = normal,
            min = min,
            max = max,
            triangles = projectedTriangles,
            orientationSummary = orientationSummary,
            resolvedRotationDegrees = resolvedRotationDegrees
        };
    }

    public static bool FootprintFits(
        Projection projection,
        Vector2 center,
        float width,
        float length,
        int samples = 3,
        float inset = 0f)
    {
        int resolvedSamples = Mathf.Clamp(samples, 2, 9);
        float halfWidth = Mathf.Max(0f, width * 0.5f - inset);
        float halfLength = Mathf.Max(0f, length * 0.5f - inset);

        for (int row = 0; row < resolvedSamples; row++)
        {
            float localY = Mathf.Lerp(
                -halfLength,
                halfLength,
                row / (float)(resolvedSamples - 1)
            );

            for (int column = 0;
                column < resolvedSamples;
                column++)
            {
                float localX = Mathf.Lerp(
                    -halfWidth,
                    halfWidth,
                    column / (float)(resolvedSamples - 1)
                );

                if (!PointInFacet(
                    center + new Vector2(localX, localY),
                    projection.triangles
                ))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static bool SegmentFits(
        Projection projection,
        Vector2 center,
        Vector2 direction,
        float length,
        float width,
        int longitudinalSamples = 9,
        int widthSamples = 3)
    {
        Vector2 normalizedDirection = direction.normalized;
        if (normalizedDirection.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        Vector2 perpendicular = new Vector2(
            -normalizedDirection.y,
            normalizedDirection.x
        );

        int alongCount = Mathf.Clamp(longitudinalSamples, 2, 33);
        int acrossCount = Mathf.Clamp(widthSamples, 2, 9);

        for (int along = 0; along < alongCount; along++)
        {
            float alongOffset = Mathf.Lerp(
                -length * 0.5f,
                length * 0.5f,
                along / (float)(alongCount - 1)
            );

            for (int across = 0; across < acrossCount; across++)
            {
                float acrossOffset = Mathf.Lerp(
                    -width * 0.5f,
                    width * 0.5f,
                    across / (float)(acrossCount - 1)
                );

                Vector2 point =
                    center +
                    normalizedDirection * alongOffset +
                    perpendicular * acrossOffset;

                if (!PointInFacet(point, projection.triangles))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static bool PointInFacet(
        Vector2 point,
        List<Triangle2D> triangles)
    {
        if (triangles == null)
        {
            return false;
        }

        foreach (Triangle2D triangle in triangles)
        {
            if (PointInTriangle(point, triangle))
            {
                return true;
            }
        }

        return false;
    }

    public static Vector2 Project(
        Vector3 point,
        Vector3 origin,
        Vector3 u,
        Vector3 v)
    {
        Vector3 offset = point - origin;
        return new Vector2(
            Vector3.Dot(offset, u),
            Vector3.Dot(offset, v)
        );
    }

    public static Vector3 RotateAxisOnPlane(
        Vector3 axis,
        Vector3 normal,
        float angleDegrees)
    {
        Vector3 rotated =
            Quaternion.AngleAxis(angleDegrees, normal) * axis;

        return Vector3.ProjectOnPlane(rotated, normal).normalized;
    }

    public static Vector3 GetFallbackPlaneAxis(Vector3 normal)
    {
        Vector3 axis = Vector3.ProjectOnPlane(
            Vector3.right,
            normal
        );

        if (axis.sqrMagnitude < 0.0001f)
        {
            axis = Vector3.ProjectOnPlane(
                Vector3.forward,
                normal
            );
        }

        return axis.normalized;
    }

    public static bool TryGetBuildingBounds(
        GameObject buildingRoot,
        out Bounds result)
    {
        result = default(Bounds);

        if (buildingRoot == null)
        {
            return false;
        }

        bool hasBounds = false;

        MeshFilter[] filters =
            buildingRoot.GetComponentsInChildren<MeshFilter>(true);

        foreach (MeshFilter filter in filters)
        {
            if (filter.sharedMesh == null)
            {
                continue;
            }

            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            if (renderer != null && !renderer.enabled)
            {
                continue;
            }

            Bounds worldBounds = TransformBounds(
                filter.sharedMesh.bounds,
                filter.transform.localToWorldMatrix
            );

            if (!hasBounds)
            {
                result = worldBounds;
                hasBounds = true;
            }
            else
            {
                result.Encapsulate(worldBounds);
            }
        }

        return hasBounds;
    }

    public static Bounds TransformBounds(
        Bounds bounds,
        Matrix4x4 matrix)
    {
        Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
        Vector3 extents = bounds.extents;
        Vector3 axisX = matrix.MultiplyVector(
            new Vector3(extents.x, 0f, 0f)
        );
        Vector3 axisY = matrix.MultiplyVector(
            new Vector3(0f, extents.y, 0f)
        );
        Vector3 axisZ = matrix.MultiplyVector(
            new Vector3(0f, 0f, extents.z)
        );

        Vector3 worldExtents = new Vector3(
            Mathf.Abs(axisX.x) +
                Mathf.Abs(axisY.x) +
                Mathf.Abs(axisZ.x),
            Mathf.Abs(axisX.y) +
                Mathf.Abs(axisY.y) +
                Mathf.Abs(axisZ.y),
            Mathf.Abs(axisX.z) +
                Mathf.Abs(axisY.z) +
                Mathf.Abs(axisZ.z)
        );

        return new Bounds(center, worldExtents * 2f);
    }

    private static void AddTriangleToPlaneGroup(
        List<Facet> facets,
        Triangle triangle,
        float normalMergeTolerance,
        float planeMergeTolerance)
    {
        Facet bestFacet = null;
        float bestScore = float.PositiveInfinity;

        foreach (Facet facet in facets)
        {
            float angle = Vector3.Angle(
                facet.normal,
                triangle.normal
            );

            if (angle > normalMergeTolerance)
            {
                continue;
            }

            float planeDifference = Mathf.Abs(
                Vector3.Dot(
                    facet.normal,
                    triangle.centroid
                ) - facet.planeDistance
            );

            if (planeDifference > planeMergeTolerance)
            {
                continue;
            }

            float score = angle + planeDifference * 10f;
            if (score < bestScore)
            {
                bestScore = score;
                bestFacet = facet;
            }
        }

        if (bestFacet == null)
        {
            bestFacet = new Facet();
            facets.Add(bestFacet);
        }

        bestFacet.Add(triangle);
    }

    private static List<Facet> SplitDisconnectedComponents(
        List<Facet> planeGroups)
    {
        List<Facet> result = new List<Facet>();

        foreach (Facet planeGroup in planeGroups)
        {
            if (planeGroup.triangles.Count <= 1)
            {
                result.Add(planeGroup);
                continue;
            }

            Dictionary<EdgeKey, List<int>> edgeToTriangles =
                new Dictionary<EdgeKey, List<int>>();

            for (int triangleIndex = 0;
                triangleIndex < planeGroup.triangles.Count;
                triangleIndex++)
            {
                Triangle triangle =
                    planeGroup.triangles[triangleIndex];

                AddTriangleEdge(
                    edgeToTriangles,
                    new EdgeKey(triangle.a, triangle.b),
                    triangleIndex
                );
                AddTriangleEdge(
                    edgeToTriangles,
                    new EdgeKey(triangle.b, triangle.c),
                    triangleIndex
                );
                AddTriangleEdge(
                    edgeToTriangles,
                    new EdgeKey(triangle.c, triangle.a),
                    triangleIndex
                );
            }

            List<int>[] neighbours =
                new List<int>[planeGroup.triangles.Count];

            for (int index = 0; index < neighbours.Length; index++)
            {
                neighbours[index] = new List<int>();
            }

            foreach (List<int> sharingTriangles in edgeToTriangles.Values)
            {
                for (int left = 0;
                    left < sharingTriangles.Count;
                    left++)
                {
                    for (int right = left + 1;
                        right < sharingTriangles.Count;
                        right++)
                    {
                        int leftIndex = sharingTriangles[left];
                        int rightIndex = sharingTriangles[right];
                        neighbours[leftIndex].Add(rightIndex);
                        neighbours[rightIndex].Add(leftIndex);
                    }
                }
            }

            bool[] visited = new bool[planeGroup.triangles.Count];
            Queue<int> queue = new Queue<int>();

            for (int seed = 0; seed < visited.Length; seed++)
            {
                if (visited[seed])
                {
                    continue;
                }

                Facet component = new Facet
                {
                    enabled = planeGroup.enabled
                };

                queue.Enqueue(seed);
                visited[seed] = true;

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    component.Add(planeGroup.triangles[current]);

                    foreach (int neighbour in neighbours[current])
                    {
                        if (visited[neighbour])
                        {
                            continue;
                        }

                        visited[neighbour] = true;
                        queue.Enqueue(neighbour);
                    }
                }

                result.Add(component);
            }
        }

        return result;
    }

    private static void AddTriangleEdge(
        Dictionary<EdgeKey, List<int>> edgeToTriangles,
        EdgeKey edge,
        int triangleIndex)
    {
        List<int> indices;

        if (!edgeToTriangles.TryGetValue(edge, out indices))
        {
            indices = new List<int>();
            edgeToTriangles.Add(edge, indices);
        }

        indices.Add(triangleIndex);
    }

    private static void AnalyzeFacetGeometry(
        Facet facet,
        Transform buildingRoot)
    {
        facet.boundaryLineSegments.Clear();

        Dictionary<EdgeKey, EdgeRecord> edges =
            new Dictionary<EdgeKey, EdgeRecord>();

        Dictionary<QuantizedPoint, Vector3> uniqueVertices =
            new Dictionary<QuantizedPoint, Vector3>();

        foreach (Triangle triangle in facet.triangles)
        {
            AddUniqueVertex(uniqueVertices, triangle.a);
            AddUniqueVertex(uniqueVertices, triangle.b);
            AddUniqueVertex(uniqueVertices, triangle.c);

            AddEdge(edges, triangle.a, triangle.b);
            AddEdge(edges, triangle.b, triangle.c);
            AddEdge(edges, triangle.c, triangle.a);
        }

        float longestEdgeLength = 0f;
        Vector3 longestAxis = Vector3.zero;

        foreach (EdgeRecord edge in edges.Values)
        {
            if (edge.count != 1)
            {
                continue;
            }

            facet.boundaryLineSegments.Add(edge.a);
            facet.boundaryLineSegments.Add(edge.b);

            Vector3 edgeDirection = Vector3.ProjectOnPlane(
                edge.b - edge.a,
                facet.normal
            );

            float length = edgeDirection.magnitude;
            if (length > longestEdgeLength)
            {
                longestEdgeLength = length;
                longestAxis = edgeDirection / length;
            }
        }

        facet.longestBoundaryAxis =
            longestAxis.sqrMagnitude > 0.0001f
                ? StabilizeAxisSign(
                    longestAxis,
                    facet.normal,
                    buildingRoot
                )
                : GetFallbackPlaneAxis(facet.normal);

        Vector3 principalAxis;
        float confidence;

        CalculatePrincipalAxis(
            facet,
            uniqueVertices.Values,
            out principalAxis,
            out confidence
        );

        facet.principalAxis = StabilizeAxisSign(
            principalAxis,
            facet.normal,
            buildingRoot
        );
        facet.orientationConfidence = confidence;
    }

    private static void AddUniqueVertex(
        Dictionary<QuantizedPoint, Vector3> vertices,
        Vector3 point)
    {
        QuantizedPoint key = new QuantizedPoint(point);
        if (!vertices.ContainsKey(key))
        {
            vertices.Add(key, point);
        }
    }

    private static void AddEdge(
        Dictionary<EdgeKey, EdgeRecord> edges,
        Vector3 a,
        Vector3 b)
    {
        EdgeKey key = new EdgeKey(a, b);
        EdgeRecord record;

        if (!edges.TryGetValue(key, out record))
        {
            record = new EdgeRecord
            {
                a = a,
                b = b,
                count = 0
            };
            edges.Add(key, record);
        }

        record.count++;
    }

    private static void CalculatePrincipalAxis(
        Facet facet,
        IEnumerable<Vector3> vertices,
        out Vector3 principalAxis,
        out float confidence)
    {
        Vector3 normal = facet.normal.normalized;
        Vector3 basisU = GetFallbackPlaneAxis(normal);
        Vector3 basisV = Vector3.Cross(normal, basisU).normalized;
        List<Vector2> projected = new List<Vector2>();
        Vector2 mean = Vector2.zero;

        foreach (Vector3 vertex in vertices)
        {
            Vector2 point = Project(
                vertex,
                facet.center,
                basisU,
                basisV
            );
            projected.Add(point);
            mean += point;
        }

        if (projected.Count < 2)
        {
            principalAxis = facet.longestBoundaryAxis;
            confidence = 0f;
            return;
        }

        mean /= projected.Count;

        float covarianceXX = 0f;
        float covarianceXY = 0f;
        float covarianceYY = 0f;

        foreach (Vector2 point in projected)
        {
            Vector2 delta = point - mean;
            covarianceXX += delta.x * delta.x;
            covarianceXY += delta.x * delta.y;
            covarianceYY += delta.y * delta.y;
        }

        float trace = covarianceXX + covarianceYY;
        float discriminant = Mathf.Sqrt(
            Mathf.Max(
                0f,
                (covarianceXX - covarianceYY) *
                    (covarianceXX - covarianceYY) +
                4f * covarianceXY * covarianceXY
            )
        );

        float largestEigenvalue =
            (trace + discriminant) * 0.5f;
        float smallestEigenvalue =
            (trace - discriminant) * 0.5f;

        confidence = largestEigenvalue > 0.000001f
            ? Mathf.Clamp01(
                (largestEigenvalue - smallestEigenvalue) /
                largestEigenvalue
            )
            : 0f;

        float angle = 0.5f * Mathf.Atan2(
            2f * covarianceXY,
            covarianceXX - covarianceYY
        );

        principalAxis =
            basisU * Mathf.Cos(angle) +
            basisV * Mathf.Sin(angle);

        if (principalAxis.sqrMagnitude < 0.0001f)
        {
            principalAxis = facet.longestBoundaryAxis;
        }

        principalAxis.Normalize();
    }

    private static Vector3 GetOrientationAxis(
        Facet facet,
        Transform buildingRoot,
        OrientationMode orientationMode,
        OrientationSettings settings)
    {
        Vector3 normal = facet.normal.normalized;
        Vector3 axis;

        switch (orientationMode)
        {
            case OrientationMode.LongestBoundaryEdge:
                axis = facet.longestBoundaryAxis;
                break;

            case OrientationMode.BuildingLocalX:
                axis = buildingRoot != null
                    ? Vector3.ProjectOnPlane(
                        buildingRoot.right,
                        normal
                    )
                    : Vector3.zero;
                break;

            case OrientationMode.WorldX:
                axis = Vector3.ProjectOnPlane(
                    Vector3.right,
                    normal
                );
                break;

            default:
                axis = facet.orientationConfidence <
                    settings.pcaAmbiguityThreshold
                        ? facet.longestBoundaryAxis
                        : facet.principalAxis;

                if (
                    settings.snapPrincipalAxisToBoundary &&
                    facet.longestBoundaryAxis.sqrMagnitude > 0.0001f
                )
                {
                    float edgeDifference = AxisAngleDifference(
                        axis,
                        facet.longestBoundaryAxis
                    );

                    if (edgeDifference <=
                        settings.boundarySnapTolerance)
                    {
                        axis = facet.longestBoundaryAxis;
                    }
                }
                break;
        }

        if (axis.sqrMagnitude < 0.0001f)
        {
            axis = Vector3.ProjectOnPlane(
                Vector3.right,
                normal
            );
        }

        if (axis.sqrMagnitude < 0.0001f)
        {
            axis = Vector3.ProjectOnPlane(
                Vector3.forward,
                normal
            );
        }

        axis.Normalize();
        return StabilizeAxisSign(axis, normal, buildingRoot);
    }

    private static string DescribeOrientation(
        Facet facet,
        OrientationMode orientationMode,
        OrientationSettings settings,
        bool perpendicular)
    {
        string source;

        switch (orientationMode)
        {
            case OrientationMode.AutoBestFit:
                source = facet.orientationConfidence <
                    settings.pcaAmbiguityThreshold
                        ? "Auto/edge"
                        : "Auto/PCA";
                break;
            case OrientationMode.PrincipalAxis:
                source = "PCA";
                break;
            case OrientationMode.LongestBoundaryEdge:
                source = "Boundary edge";
                break;
            case OrientationMode.BuildingLocalX:
                source = "Building X";
                break;
            case OrientationMode.WorldX:
                source = "World X";
                break;
            default:
                source = "Orientation";
                break;
        }

        return perpendicular
            ? source + " + 90°"
            : source;
    }

    private static Vector3 StabilizeAxisSign(
        Vector3 axis,
        Vector3 normal,
        Transform buildingRoot)
    {
        Vector3 reference = buildingRoot != null
            ? Vector3.ProjectOnPlane(
                buildingRoot.right,
                normal
            )
            : Vector3.ProjectOnPlane(
                Vector3.right,
                normal
            );

        if (
            reference.sqrMagnitude > 0.0001f &&
            Vector3.Dot(axis, reference) < 0f
        )
        {
            axis = -axis;
        }

        return axis;
    }

    private static float AxisAngleDifference(
        Vector3 first,
        Vector3 second)
    {
        float angle = Vector3.Angle(first, second);
        return Mathf.Min(angle, 180f - angle);
    }

    private static bool PointInTriangle(
        Vector2 point,
        Triangle2D triangle)
    {
        const float epsilon = 0.0005f;

        float d1 = SignedArea(point, triangle.a, triangle.b);
        float d2 = SignedArea(point, triangle.b, triangle.c);
        float d3 = SignedArea(point, triangle.c, triangle.a);

        bool negative =
            d1 < -epsilon ||
            d2 < -epsilon ||
            d3 < -epsilon;

        bool positive =
            d1 > epsilon ||
            d2 > epsilon ||
            d3 > epsilon;

        return !(negative && positive);
    }

    private static float SignedArea(
        Vector2 point,
        Vector2 a,
        Vector2 b)
    {
        return
            (point.x - b.x) * (a.y - b.y) -
            (a.x - b.x) * (point.y - b.y);
    }
}

#endif
