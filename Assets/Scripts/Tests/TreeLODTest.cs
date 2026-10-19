using UnityEngine;

public class TreeLODTest : MonoBehaviour
{
    [Range(0, 4)]
    public int forcedLOD = 2;

    [ContextMenu("Apply")]
    public void Apply()
    {
        foreach (LODGroup group in GetComponentsInChildren<LODGroup>(true))
            group.ForceLOD(forcedLOD);
    }

    [ContextMenu("Reset LOD")]
    public void ResetLOD()
    {
        foreach (LODGroup group in GetComponentsInChildren<LODGroup>(true))
            group.ForceLOD(-1);
    }
}