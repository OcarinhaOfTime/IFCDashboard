using UnityEngine;

[ExecuteAlways]
public sealed class RealtimeEmissiveGIRefresh : MonoBehaviour
{
    [SerializeField]
    private Renderer[] targetRenderers;

    [ContextMenu("Refresh Realtime Emissive GI")]
    public void RefreshRealtimeGI()
    {
        if (targetRenderers == null)
            return;

        foreach (Renderer targetRenderer in targetRenderers)
        {
            if (targetRenderer == null)
                continue;

            foreach (Material material in targetRenderer.sharedMaterials)
            {
                if (material == null)
                    continue;

                material.globalIlluminationFlags =
                    MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            targetRenderer.UpdateGIMaterials();
        }

        Debug.Log(
            $"Refreshed realtime emissive GI for " +
            $"{targetRenderers.Length} renderer entries.",
            this
        );
    }
}