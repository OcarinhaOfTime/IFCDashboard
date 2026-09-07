using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

public class IfcDashboardHierarchyApplier : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private Transform buildingRoot;
    [SerializeField] private string hierarchyFileName = "cib_site_dashboard_hierarchy_mapping.json";

    [Header("Hierarchy Settings")]
    [SerializeField] private string dashboardRootName = "CIB_Site_Dashboard_Hierarchy";
    [SerializeField] private bool createDashboardRoot = true;
    [SerializeField] private bool preserveWorldPosition = true;

    [Header("Safety")]
    [SerializeField] private bool skipIfAlreadyGrouped = true;

    [Serializable]
    public class HierarchyRecord
    {
        public string original_unity_name;
        public string label;
        public string dashboard_bucket;
        public string dashboard_group;
        public string dashboard_parent_path;
        public string dashboard_layer;

        public bool is_macro_parent;
        public bool is_terrain;

        public string assignment_method;
        public string assignment_confidence;

        public string global_id;
        public string uuid;
        public string conversion_id;

        public string ifc_class;
        public string category;
        public string asset_identifier;
        public string element_name;
        public string object_type;
        public string tag;
        public string container_name;
        public string container_class;
    }

    [ContextMenu("Apply Dashboard Hierarchy")]
    public void ApplyDashboardHierarchy()
    {
        if (buildingRoot == null)
        {
            buildingRoot = transform;
        }

        string path = Path.Combine(Application.streamingAssetsPath, hierarchyFileName);

        if (!File.Exists(path))
        {
            Debug.LogError($"Hierarchy mapping file not found: {path}");
            return;
        }

        string json = File.ReadAllText(path);

        Dictionary<string, HierarchyRecord> mapping =
            JsonConvert.DeserializeObject<Dictionary<string, HierarchyRecord>>(json);

        if (mapping == null)
        {
            Debug.LogError($"Could not parse hierarchy mapping: {path}");
            return;
        }

        Transform dashboardRoot = createDashboardRoot
            ? GetOrCreateChild(buildingRoot, dashboardRootName)
            : buildingRoot;

        Dictionary<string, Transform> parentCache = new Dictionary<string, Transform>();

        Dictionary<string, Transform> objectLookup = BuildObjectLookup(buildingRoot);

        int matched = 0;
        int reparented = 0;
        int missing = 0;
        int skippedAlreadyGrouped = 0;

        foreach (KeyValuePair<string, HierarchyRecord> entry in mapping)
        {
            string readableId = entry.Key;
            HierarchyRecord record = entry.Value;

            if (string.IsNullOrWhiteSpace(readableId))
            {
                continue;
            }

            if (!objectLookup.TryGetValue(readableId, out Transform objTransform))
            {
                missing++;
                continue;
            }

            matched++;

            if (skipIfAlreadyGrouped && IsUnderDashboardRoot(objTransform, dashboardRoot))
            {
                skippedAlreadyGrouped++;
                continue;
            }

            string parentPath = record.dashboard_parent_path;

            if (string.IsNullOrWhiteSpace(parentPath))
            {
                parentPath = "03_Unassigned_Review";
            }

            Transform targetParent = GetOrCreatePath(
                dashboardRoot,
                parentPath,
                parentCache
            );

            objTransform.SetParent(targetParent, preserveWorldPosition);
            reparented++;

            DashboardIdentity identity = objTransform.GetComponent<DashboardIdentity>();

            if (identity != null)
            {
                // Keep the dashboard grouping visible in the inspector through the existing component if desired.
                // This assumes you do not need new fields for group/path yet.
                if (string.IsNullOrWhiteSpace(identity.dashboardLabel))
                {
                    identity.dashboardLabel = record.label;
                }
            }

#if UNITY_EDITOR
            EditorUtility.SetDirty(objTransform.gameObject);
#endif
        }

#if UNITY_EDITOR
        EditorUtility.SetDirty(gameObject);
        EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif

        Debug.Log(
            $"Dashboard hierarchy applied.\n" +
            $"Mapping records: {mapping.Count}\n" +
            $"Matched objects: {matched}\n" +
            $"Reparented objects: {reparented}\n" +
            $"Skipped already grouped: {skippedAlreadyGrouped}\n" +
            $"Missing objects: {missing}"
        );
    }

    private Dictionary<string, Transform> BuildObjectLookup(Transform root)
    {
        Dictionary<string, Transform> lookup = new Dictionary<string, Transform>();

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child == root)
            {
                continue;
            }

            if (!lookup.ContainsKey(child.gameObject.name))
            {
                lookup.Add(child.gameObject.name, child);
            }

            DashboardIdentity identity = child.GetComponent<DashboardIdentity>();

            if (identity != null)
            {
                if (!string.IsNullOrWhiteSpace(identity.suggestedUnityName)
                    && !lookup.ContainsKey(identity.suggestedUnityName))
                {
                    lookup.Add(identity.suggestedUnityName, child);
                }

                if (!string.IsNullOrWhiteSpace(identity.originalUnityName)
                    && !lookup.ContainsKey(identity.originalUnityName))
                {
                    lookup.Add(identity.originalUnityName, child);
                }
            }
        }

        return lookup;
    }

    private Transform GetOrCreatePath(
        Transform root,
        string path,
        Dictionary<string, Transform> cache)
    {
        if (cache.TryGetValue(path, out Transform cached))
        {
            return cached;
        }

        string[] parts = path.Split(
            new[] { '/' },
            StringSplitOptions.RemoveEmptyEntries
        );

        Transform current = root;
        string cumulativePath = "";

        foreach (string rawPart in parts)
        {
            string part = MakeUnitySafeName(rawPart.Trim());

            cumulativePath = string.IsNullOrEmpty(cumulativePath)
                ? part
                : $"{cumulativePath}/{part}";

            if (cache.TryGetValue(cumulativePath, out Transform cachedPart))
            {
                current = cachedPart;
                continue;
            }

            current = GetOrCreateChild(current, part);
            cache[cumulativePath] = current;
        }

        cache[path] = current;
        return current;
    }

    private Transform GetOrCreateChild(Transform parent, string childName)
    {
        string safeName = MakeUnitySafeName(childName);

        Transform existing = parent.Find(safeName);

        if (existing != null)
        {
            return existing;
        }

        GameObject obj = new GameObject(safeName);
        obj.transform.SetParent(parent, false);

#if UNITY_EDITOR
        EditorUtility.SetDirty(obj);
#endif

        return obj.transform;
    }

    private bool IsUnderDashboardRoot(Transform obj, Transform dashboardRoot)
    {
        Transform current = obj.parent;

        while (current != null)
        {
            if (current == dashboardRoot)
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private string MakeUnitySafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unnamed";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();

        foreach (char c in invalidChars)
        {
            value = value.Replace(c, '_');
        }

        value = value.Replace('/', '_');
        value = value.Replace('\\', '_');
        value = value.Replace(':', '_');
        value = value.Replace(';', '_');
        value = value.Replace('|', '_');

        while (value.Contains("  "))
        {
            value = value.Replace("  ", " ");
        }

        value = value.Trim();

        return value;
    }
}