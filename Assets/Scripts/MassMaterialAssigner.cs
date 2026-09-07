using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

public class MassMaterialAssigner : MonoBehaviour
{
    [Header("Material")]
    [SerializeField] private Material material;

    [Header("Scope")]
    [SerializeField] private bool includeInactiveObjects = true;
    [SerializeField] private bool includeRootObject = true;

    [Header("Assignment")]
    [SerializeField] private bool replaceAllMaterialSlots = true;

    [ContextMenu("Assign Material To Children")]
    public void AssignMaterialToChildren()
    {
        if (material == null)
        {
            Debug.LogWarning($"No material assigned on {gameObject.name}.");
            return;
        }

        MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>(includeInactiveObjects);

        int changedCount = 0;

        foreach (MeshRenderer renderer in renderers)
        {
            if (!includeRootObject && renderer.gameObject == gameObject)
            {
                continue;
            }

            if (replaceAllMaterialSlots)
            {
                Material[] replacementMaterials = new Material[renderer.sharedMaterials.Length];

                if (replacementMaterials.Length == 0)
                {
                    replacementMaterials = new Material[1];
                }

                for (int i = 0; i < replacementMaterials.Length; i++)
                {
                    replacementMaterials[i] = material;
                }

                renderer.sharedMaterials = replacementMaterials;
            }
            else
            {
                renderer.sharedMaterial = material;
            }

            changedCount++;

#if UNITY_EDITOR
            EditorUtility.SetDirty(renderer);
#endif
        }

#if UNITY_EDITOR
        EditorUtility.SetDirty(gameObject);
        EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif

        Debug.Log(
            $"Assigned material '{material.name}' to {changedCount} MeshRenderers under '{gameObject.name}'."
        );
    }
}