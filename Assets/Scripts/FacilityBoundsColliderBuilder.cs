using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

public class FacilityBoundsColliderBuilder : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private Transform facilitiesRoot;

    [Header("Collider Settings")]
    [SerializeField] private bool setAsTrigger = true;
    [SerializeField] private bool replaceExistingColliders = true;

    [Header("Padding")]
    [SerializeField] private Vector3 padding = new Vector3(1.0f, 2.0f, 1.0f);

    [Header("Filtering")]
    [SerializeField] private bool ignoreInactiveRenderers = true;

    [ContextMenu("Build Facility Bounding Colliders")]
    public void BuildFacilityColliders()
    {
        if (facilitiesRoot == null)
        {
            facilitiesRoot = transform;
        }

        int facilityCount = 0;
        int colliderCount = 0;
        int skippedCount = 0;

        foreach (Transform facility in facilitiesRoot)
        {
            if (!facility.gameObject.activeInHierarchy && ignoreInactiveRenderers)
            {
                skippedCount++;
                continue;
            }

            Bounds? bounds = ComputeRendererBounds(facility);

            if (!bounds.HasValue)
            {
                skippedCount++;
                continue;
            }

            CreateOrUpdateColliderOnFacility(facility, bounds.Value);

            facilityCount++;
            colliderCount++;
        }

#if UNITY_EDITOR
        EditorUtility.SetDirty(gameObject);
        EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif

        Debug.Log(
            $"Facility collider build complete.\n" +
            $"Facilities processed: {facilityCount}\n" +
            $"Colliders created/updated: {colliderCount}\n" +
            $"Skipped: {skippedCount}"
        );
    }

    private Bounds? ComputeRendererBounds(Transform root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

        bool hasBounds = false;
        Bounds combinedBounds = new Bounds();

        foreach (Renderer renderer in renderers)
        {
            if (ignoreInactiveRenderers && !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (!hasBounds)
            {
                combinedBounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                combinedBounds.Encapsulate(renderer.bounds);
            }
        }

        if (!hasBounds)
        {
            return null;
        }

        combinedBounds.Expand(padding);

        return combinedBounds;
    }

    private void CreateOrUpdateColliderOnFacility(Transform facility, Bounds worldBounds)
    {
        BoxCollider box = facility.gameObject.GetComponent<BoxCollider>();

        if (box == null)
        {
            box = facility.gameObject.AddComponent<BoxCollider>();
        }

        box.isTrigger = setAsTrigger;

        Bounds localBounds = WorldBoundsToLocalBounds(facility, worldBounds);

        box.center = localBounds.center;
        box.size = localBounds.size;

        FacilitySelectionTarget target = facility.gameObject.GetComponent<FacilitySelectionTarget>();

        if (target == null)
        {
            target = facility.gameObject.AddComponent<FacilitySelectionTarget>();
        }

        target.facilityRoot = facility;
        target.facilityLabel = facility.name;

#if UNITY_EDITOR
        EditorUtility.SetDirty(facility.gameObject);
#endif
    }


    private Bounds WorldBoundsToLocalBounds(Transform target, Bounds worldBounds)
    {
        Vector3 center = worldBounds.center;
        Vector3 extents = worldBounds.extents;

        Vector3[] worldCorners =
        {
        center + new Vector3(-extents.x, -extents.y, -extents.z),
        center + new Vector3(-extents.x, -extents.y,  extents.z),
        center + new Vector3(-extents.x,  extents.y, -extents.z),
        center + new Vector3(-extents.x,  extents.y,  extents.z),
        center + new Vector3( extents.x, -extents.y, -extents.z),
        center + new Vector3( extents.x, -extents.y,  extents.z),
        center + new Vector3( extents.x,  extents.y, -extents.z),
        center + new Vector3( extents.x,  extents.y,  extents.z),
    };

        Bounds localBounds = new Bounds(
            target.InverseTransformPoint(worldCorners[0]),
            Vector3.zero
        );

        for (int i = 1; i < worldCorners.Length; i++)
        {
            localBounds.Encapsulate(target.InverseTransformPoint(worldCorners[i]));
        }

        return localBounds;
    }
    private string MakeSafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unnamed";
        }

        value = value.Trim();

        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        value = value.Replace('/', '_');
        value = value.Replace('\\', '_');
        value = value.Replace(':', '_');

        return value;
    }
}