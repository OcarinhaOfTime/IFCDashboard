using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class IfcObjectRenamer : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private Transform buildingRoot;
    [SerializeField] private string mappingFileName = "cib_site_id_to_label_rich.json";

    [Header("Options")]
    [SerializeField] private bool renameObjects = true;
    [SerializeField] private bool addDashboardIdentity = true;
    [SerializeField] private bool ensureUniqueNames = true;
    [SerializeField] private int maxNameLength = 96;

    [Serializable]
    public class MappingRecord
    {
        public string label;
        public string suggested_unity_name;
        public string conversion_id;

        public string global_id;
        public string uuid;

        public string ifc_class;
        public string category;
        public string asset_identifier;
        public string element_name;
        public string object_type;
        public string tag;
        public string container_name;
        public string container_class;
    }

    [ContextMenu("Rename IFC Objects From Mapping")]
    public void RenameFromMapping()
    {
        if (buildingRoot == null)
        {
            buildingRoot = transform;
        }

        string mappingPath = Path.Combine(Application.streamingAssetsPath, mappingFileName);

        if (!File.Exists(mappingPath))
        {
            Debug.LogError($"Mapping file not found: {mappingPath}");
            return;
        }

        string json = File.ReadAllText(mappingPath);

        Dictionary<string, MappingRecord> mapping =
            JsonConvert.DeserializeObject<Dictionary<string, MappingRecord>>(json);

        if (mapping == null)
        {
            Debug.LogError($"Failed to parse mapping file: {mappingPath}");
            return;
        }

        int scanned = 0;
        int matched = 0;
        int renamed = 0;
        int identityAddedOrUpdated = 0;

        HashSet<string> usedNames = new HashSet<string>();

        foreach (Transform child in buildingRoot.GetComponentsInChildren<Transform>(true))
        {
            scanned++;

            GameObject obj = child.gameObject;

            DashboardIdentity existingIdentity = obj.GetComponent<DashboardIdentity>();

            string lookupName = null;

            if (existingIdentity != null && !string.IsNullOrWhiteSpace(existingIdentity.originalUnityName))
            {
                lookupName = existingIdentity.originalUnityName;
            }
            else
            {
                lookupName = obj.name;
            }

            if (!mapping.TryGetValue(lookupName, out MappingRecord record))
            {
                continue;
            }

            matched++;


            if (addDashboardIdentity)
            {
                DashboardIdentity identity = obj.GetComponent<DashboardIdentity>();

                if (identity == null)
                {
                    identity = obj.AddComponent<DashboardIdentity>();
                }

                ApplyIdentity(identity, lookupName, record);
                identityAddedOrUpdated++;
            }

            if (renameObjects)
            {
                string newName = BuildObjectName(record, lookupName);
                newName = MakeNameUnique(newName, usedNames);

                if (obj.name != newName)
                {
                    obj.name = newName;
                    renamed++;
                }
            }

#if UNITY_EDITOR
            EditorUtility.SetDirty(obj);
#endif
        }

#if UNITY_EDITOR
        EditorUtility.SetDirty(gameObject);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif

        Debug.Log(
            $"IFC rename complete.\n" +
            $"Scanned: {scanned}\n" +
            $"Matched: {matched}\n" +
            $"Renamed: {renamed}\n" +
            $"Identity components added/updated: {identityAddedOrUpdated}"
        );
    }

    private void ApplyIdentity(
        DashboardIdentity identity,
        string originalUnityName,
        MappingRecord record)
    {
        identity.originalUnityName = originalUnityName;
        identity.conversionId = record.conversion_id;
        identity.globalId = record.global_id;
        identity.uuid = record.uuid;

        identity.dashboardLabel = record.label;
        identity.suggestedUnityName = record.suggested_unity_name;

        identity.ifcClass = record.ifc_class;
        identity.category = record.category;
        identity.assetIdentifier = record.asset_identifier;
        identity.elementName = record.element_name;
        identity.objectType = record.object_type;
        identity.tag = record.tag;
        identity.containerName = record.container_name;
        identity.containerClass = record.container_class;
    }

    private string BuildObjectName(MappingRecord record, string fallbackOriginalName)
    {
        string suggested = Clean(record.suggested_unity_name);

        if (!string.IsNullOrWhiteSpace(suggested))
        {
            return TrimName(suggested);
        }

        string label = Clean(record.label);
        string ifcClass = Clean(record.ifc_class);
        string conversionId = Clean(record.conversion_id);

        if (string.IsNullOrWhiteSpace(label))
        {
            label = "IFC_Object";
        }

        if (string.IsNullOrWhiteSpace(ifcClass))
        {
            ifcClass = "IfcObject";
        }

        string shortId = "unknown";

        if (!string.IsNullOrWhiteSpace(conversionId))
        {
            shortId = conversionId.Length >= 8 ? conversionId.Substring(0, 8) : conversionId;
        }
        else if (!string.IsNullOrWhiteSpace(fallbackOriginalName))
        {
            shortId = fallbackOriginalName.Length >= 8 ? fallbackOriginalName.Substring(0, 8) : fallbackOriginalName;
        }

        string name = $"{label}__{ifcClass}__{shortId}";

        return TrimName(MakeUnitySafeName(name));
    }

    private string MakeNameUnique(string baseName, HashSet<string> usedNames)
    {
        if (!ensureUniqueNames)
        {
            return baseName;
        }

        if (!usedNames.Contains(baseName))
        {
            usedNames.Add(baseName);
            return baseName;
        }

        int index = 2;

        while (true)
        {
            string candidate = $"{baseName}__{index:000}";

            if (!usedNames.Contains(candidate))
            {
                usedNames.Add(candidate);
                return candidate;
            }

            index++;
        }
    }

    private string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        string lower = value.ToLowerInvariant();

        if (
            lower == "n/a" ||
            lower == "na" ||
            lower == "none" ||
            lower == "null" ||
            lower == "nan"
        )
        {
            return null;
        }

        return MakeUnitySafeName(value);
    }

    private string MakeUnitySafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
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

        value = value.Replace(' ', '_');

        while (value.Contains("__"))
        {
            value = value.Replace("__", "_");
        }

        return value.Trim('_');
    }

    private string TrimName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "IFC_Object";
        }

        if (maxNameLength <= 0 || value.Length <= maxNameLength)
        {
            return value;
        }

        return value.Substring(0, maxNameLength).TrimEnd('_');
    }
}