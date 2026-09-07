using UnityEngine;

/// <summary>
/// Scene-space stamp consumed by TerrainDistributionGeneratorWindow.
/// Place, rotate, and scale this GameObject over the Terrain to paint a
/// grass, dirt, or third-layer region into the generated RGB distribution map.
/// </summary>
[DisallowMultipleComponent]
public sealed class TerrainDistributionStamp : MonoBehaviour
{
    public enum StampShape
    {
        Ellipse,
        Box,
        Corridor
    }

    public enum DistributionChannel
    {
        GrassRed,
        DirtGreen,
        ThirdLayerBlue
    }

    public enum StampBlendMode
    {
        Additive,
        Replace
    }

    [Header("Stamp")]
    public StampShape shape = StampShape.Ellipse;
    public DistributionChannel channel = DistributionChannel.DirtGreen;
    public StampBlendMode blendMode = StampBlendMode.Additive;

    [Tooltip("Controls how strongly this stamp affects its selected channel.")]
    [Range(0.0f, 3.0f)]
    public float strength = 1.0f;

    [Tooltip("Fraction of the shape used for its soft boundary. Small values create a mostly solid stamp.")]
    [Range(0.001f, 1.0f)]
    public float falloff = 0.2f;

    [Tooltip("Stamps with lower order values are evaluated first.")]
    public int order;

    [Tooltip("Allows this component to remain in the scene without affecting generation.")]
    public bool contributes = true;

    /// <summary>
    /// Returns a stable 0..1 influence for a world-space XZ position.
    /// The transform's X and Z scale define the stamp dimensions.
    /// </summary>
    public float EvaluateInfluence(Vector3 worldPosition)
    {
        if (!contributes || strength <= 0.0f)
            return 0.0f;

        Vector2 center = new Vector2(transform.position.x, transform.position.z);
        Vector2 point = new Vector2(worldPosition.x, worldPosition.z);
        Vector2 delta = point - center;

        Vector2 right = new Vector2(transform.right.x, transform.right.z);
        Vector2 forward = new Vector2(transform.forward.x, transform.forward.z);

        if (right.sqrMagnitude < 0.000001f)
            right = Vector2.right;
        else
            right.Normalize();

        if (forward.sqrMagnitude < 0.000001f)
            forward = Vector2.up;
        else
            forward.Normalize();

        float x = Vector2.Dot(delta, right);
        float z = Vector2.Dot(delta, forward);

        Vector3 scale = transform.lossyScale;
        float halfX = Mathf.Max(Mathf.Abs(scale.x) * 0.5f, 0.0001f);
        float halfZ = Mathf.Max(Mathf.Abs(scale.z) * 0.5f, 0.0001f);

        float normalizedDistance;

        switch (shape)
        {
            case StampShape.Box:
                normalizedDistance = Mathf.Max(
                    Mathf.Abs(x) / halfX,
                    Mathf.Abs(z) / halfZ
                );
                break;

            case StampShape.Corridor:
            {
                // Capsule aligned to local X. The Z scale controls corridor width.
                float radius = Mathf.Min(halfZ, halfX);
                float segmentHalfLength = Mathf.Max(halfX - radius, 0.0f);
                float closestX = Mathf.Clamp(x, -segmentHalfLength, segmentHalfLength);
                float dx = x - closestX;
                normalizedDistance = Mathf.Sqrt(dx * dx + z * z) / Mathf.Max(radius, 0.0001f);
                break;
            }

            default:
            {
                float nx = x / halfX;
                float nz = z / halfZ;
                normalizedDistance = Mathf.Sqrt(nx * nx + nz * nz);
                break;
            }
        }

        float innerEdge = Mathf.Clamp01(1.0f - falloff);
        return 1.0f - SmoothStep(innerEdge, 1.0f, normalizedDistance);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (Mathf.Abs(edge1 - edge0) < 0.000001f)
            return value < edge0 ? 0.0f : 1.0f;

        float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
        return t * t * (3.0f - 2.0f * t);
    }

    private void OnValidate()
    {
        strength = Mathf.Max(0.0f, strength);
        falloff = Mathf.Clamp(falloff, 0.001f, 1.0f);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = channel switch
        {
            DistributionChannel.GrassRed => new Color(1.0f, 0.2f, 0.2f, 0.9f),
            DistributionChannel.DirtGreen => new Color(0.2f, 1.0f, 0.2f, 0.9f),
            _ => new Color(0.2f, 0.45f, 1.0f, 0.9f)
        };

        Matrix4x4 previousMatrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;

        switch (shape)
        {
            case StampShape.Box:
                Gizmos.DrawWireCube(Vector3.zero, new Vector3(1.0f, 0.02f, 1.0f));
                break;

            case StampShape.Corridor:
                DrawLocalEllipse(40);
                Gizmos.DrawLine(new Vector3(-0.5f, 0.0f, -0.5f), new Vector3(0.5f, 0.0f, -0.5f));
                Gizmos.DrawLine(new Vector3(-0.5f, 0.0f, 0.5f), new Vector3(0.5f, 0.0f, 0.5f));
                break;

            default:
                DrawLocalEllipse(48);
                break;
        }

        Gizmos.matrix = previousMatrix;
    }

    private static void DrawLocalEllipse(int segments)
    {
        Vector3 previous = new Vector3(0.5f, 0.0f, 0.0f);

        for (int index = 1; index <= segments; index++)
        {
            float angle = index / (float)segments * Mathf.PI * 2.0f;
            Vector3 current = new Vector3(
                Mathf.Cos(angle) * 0.5f,
                0.0f,
                Mathf.Sin(angle) * 0.5f
            );

            Gizmos.DrawLine(previous, current);
            previous = current;
        }
    }
}
