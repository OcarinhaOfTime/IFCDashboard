using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Renderer))]
public sealed class BuildingGIEmissionController : MonoBehaviour
{
    [Header("Target")]
    [SerializeField]
    private Renderer targetRenderer;

    [Tooltip("The Reference name of the Scalar property in Shader Graph.")]
    [SerializeField]
    private string scalarProperty = "_Scalar";

    [Header("Emission")]
    [Range(0f, 1f)]
    [SerializeField]
    private float scalar = 1f;

    private void Reset()
    {
        targetRenderer = GetComponent<Renderer>();
    }

    [ContextMenu("Apply Scalar And Update GI")]
    public void ApplyScalarAndUpdateGI()
    {
        if (targetRenderer == null)
        {
            targetRenderer = GetComponent<Renderer>();
        }

        if (targetRenderer == null)
        {
            Debug.LogError(
                $"{nameof(BuildingGIEmissionController)} requires a Renderer.",
                this
            );
            return;
        }

        // In Play mode, use a local material instance so this renderer
        // can be controlled independently.
        // In Edit mode, modify the assigned material asset directly.
        Material material = Application.isPlaying
            ? targetRenderer.material
            : targetRenderer.sharedMaterial;

        if (material == null)
        {
            Debug.LogError(
                $"'{targetRenderer.name}' has no material assigned.",
                targetRenderer
            );
            return;
        }

        int propertyId = Shader.PropertyToID(scalarProperty);

        if (!material.HasProperty(propertyId))
        {
            Debug.LogError(
                $"Material '{material.name}' does not contain the shader " +
                $"property '{scalarProperty}'. Check the property's Reference " +
                "name in Shader Graph.",
                material
            );
            return;
        }

        material.SetFloat(propertyId, scalar);

        // Schedules the renderer's albedo and emissive data
        // for a realtime-GI update.
        targetRenderer.UpdateGIMaterials();

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.EditorUtility.SetDirty(material);
        }
#endif

        Debug.Log(
            $"Applied {scalarProperty} = {scalar:0.00} to " +
            $"'{targetRenderer.name}' and scheduled a GI update.",
            targetRenderer
        );
    }
}