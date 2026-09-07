using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Renderer))]
public sealed class BuildingShaderBoundsProvider : MonoBehaviour
{
    [Header("Target")]
    [SerializeField]
    private Renderer targetRenderer;

    [Header("Shader Property References")]
    [Tooltip("Shader Graph Reference name, not the display name.")]
    [SerializeField]
    private string buildingMinYProperty = "_BuildingMinY";

    [Tooltip("Shader Graph Reference name, not the display name.")]
    [SerializeField]
    private string buildingMaxYProperty = "_BuildingMaxY";

    [Header("Updating")]
    [Tooltip(
        "Reapply the values whenever the renderer's world bounds change. " +
        "Useful while positioning buildings in the Editor and at runtime."
    )]
    [SerializeField]
    private bool monitorBoundsChanges = true;

    [SerializeField, Min(0f)]
    private float changeTolerance = 0.0001f;

    private MaterialPropertyBlock propertyBlock;

    private int buildingMinYId;
    private int buildingMaxYId;

    private float previousMinY;
    private float previousMaxY;
    private bool hasPreviousBounds;

    private void Reset()
    {
        targetRenderer = GetComponent<Renderer>();
        CachePropertyIds();
        ApplyBounds(force: true);
    }

    private void OnEnable()
    {
        EnsureRenderer();
        CachePropertyIds();
        ApplyBounds(force: true);
    }

    private void OnValidate()
    {
        EnsureRenderer();
        CachePropertyIds();
        ApplyBounds(force: true);
    }

    private void LateUpdate()
    {
        if (monitorBoundsChanges)
        {
            ApplyBounds(force: false);
        }
    }

    private void OnTransformParentChanged()
    {
        ApplyBounds(force: true);
    }

    private void OnDidApplyAnimationProperties()
    {
        ApplyBounds(force: true);
    }

    [ContextMenu("Apply Building Bounds")]
    public void ApplyBuildingBounds()
    {
        EnsureRenderer();
        CachePropertyIds();
        ApplyBounds(force: true);
    }

    private void EnsureRenderer()
    {
        if (targetRenderer == null)
        {
            targetRenderer = GetComponent<Renderer>();
        }
    }

    private void CachePropertyIds()
    {
        buildingMinYId = Shader.PropertyToID(buildingMinYProperty);
        buildingMaxYId = Shader.PropertyToID(buildingMaxYProperty);
    }

    private void ApplyBounds(bool force)
    {
        if (targetRenderer == null)
        {
            return;
        }

        Bounds worldBounds = targetRenderer.bounds;

        float minY = worldBounds.min.y;
        float maxY = worldBounds.max.y;

        if (!force &&
            hasPreviousBounds &&
            Mathf.Abs(minY - previousMinY) <= changeTolerance &&
            Mathf.Abs(maxY - previousMaxY) <= changeTolerance)
        {
            return;
        }

        if (propertyBlock == null)
        {
            propertyBlock = new MaterialPropertyBlock();
        }

        // Preserve any other per-renderer overrides that may already exist.
        targetRenderer.GetPropertyBlock(propertyBlock);

        propertyBlock.SetFloat(buildingMinYId, minY);
        propertyBlock.SetFloat(buildingMaxYId, maxY);

        targetRenderer.SetPropertyBlock(propertyBlock);

        previousMinY = minY;
        previousMaxY = maxY;
        hasPreviousBounds = true;
    }
}