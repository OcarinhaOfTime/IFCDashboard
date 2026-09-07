using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[DisallowMultipleComponent]
public sealed class RendererFeatureController : MonoBehaviour
{
    [Header("Renderer")]

    [Tooltip(
        "The Renderer Data asset that contains the custom screen pass."
    )]
    [SerializeField]
    private ScriptableRendererData rendererData;

    [Tooltip(
        "The Renderer Feature asset name or its C# class name."
    )]
    [SerializeField]
    private string featureName = "ScreenSpaceHeightFogFeature";

    [Header("Scene State")]

    [Tooltip(
        "Whether the feature should be active when this scene starts."
    )]
    [SerializeField]
    private bool activeAtStart = false;

    [Header("Debug")]

    [SerializeField]
    private bool logChanges = true;

    private ScriptableRendererFeature feature;

    private void Awake()
    {
        FindFeature();
        SetFeatureActive(activeAtStart);
    }

    public void EnableFeature()
    {
        SetFeatureActive(true);
    }

    public void DisableFeature()
    {
        SetFeatureActive(false);
    }

    public void ToggleFeature()
    {
        if (feature == null && !FindFeature())
        {
            return;
        }

        SetFeatureActive(!feature.isActive);
    }

    public void SetFeatureActive(bool active)
    {
        if (feature == null && !FindFeature())
        {
            return;
        }

        feature.SetActive(active);

        if (logChanges)
        {
            Debug.Log(
                $"Renderer Feature '{feature.name}' " +
                $"set to {(active ? "active" : "inactive")}.",
                this
            );
        }
    }

    private bool FindFeature()
    {
        if (rendererData == null)
        {
            Debug.LogError(
                "RendererFeatureController has no Renderer Data assigned.",
                this
            );

            return false;
        }

        foreach (
            ScriptableRendererFeature candidate
            in rendererData.rendererFeatures
        )
        {
            if (candidate == null)
            {
                continue;
            }

            bool matchesAssetName =
                string.Equals(
                    candidate.name,
                    featureName,
                    StringComparison.OrdinalIgnoreCase
                );

            bool matchesClassName =
                string.Equals(
                    candidate.GetType().Name,
                    featureName,
                    StringComparison.OrdinalIgnoreCase
                );

            if (matchesAssetName || matchesClassName)
            {
                feature = candidate;
                return true;
            }
        }

        Debug.LogError(
            $"Could not find Renderer Feature '{featureName}' " +
            $"inside Renderer Data '{rendererData.name}'.",
            this
        );

        return false;
    }
}