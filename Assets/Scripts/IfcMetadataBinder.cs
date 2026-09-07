using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

[Serializable]
public class IfcMetadataRecord
{
    public string global_id;
    public string expanded_guid;
    public string uuid;

    public string unity_body_name;
    public string unity_axis_name;
    public string unity_footprint_name;

    public string ifc_class;
    public string name;
    public string description;
    public string object_type;
    public string tag;

    public string container_global_id;
    public string container_class;
    public string container_name;
}

public class IfcMetadataBinder : MonoBehaviour
{
    [Header("Metadata")]
    [SerializeField] private string metadataFileName = "cib_site_metadata.json";

    [Header("Binding Target")]
    [SerializeField] private Transform buildingRoot;

    private Dictionary<string, IfcMetadataRecord> metadataByUnityName;

    private void Start()
    {
        if (buildingRoot == null)
        {
            buildingRoot = transform;
        }

        LoadMetadata();
        BindMetadataToGameObjects();
    }

    private void LoadMetadata()
    {
        string path = Path.Combine(Application.streamingAssetsPath, metadataFileName);

        if (!File.Exists(path))
        {
            Debug.LogError($"Metadata file not found: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        List<IfcMetadataRecord> records = JsonConvert.DeserializeObject<List<IfcMetadataRecord>>(json);

        metadataByUnityName = new Dictionary<string, IfcMetadataRecord>();

        foreach (IfcMetadataRecord record in records)
        {
            if (!string.IsNullOrWhiteSpace(record.unity_body_name))
            {
                metadataByUnityName[record.unity_body_name] = record;
            }

            if (!string.IsNullOrWhiteSpace(record.unity_axis_name))
            {
                metadataByUnityName[record.unity_axis_name] = record;
            }

            if (!string.IsNullOrWhiteSpace(record.unity_footprint_name))
            {
                metadataByUnityName[record.unity_footprint_name] = record;
            }
        }

        Debug.Log($"Loaded {records.Count} IFC metadata records.");
    }

    private void BindMetadataToGameObjects()
    {
        if (metadataByUnityName == null)
        {
            Debug.LogError("Metadata dictionary is null.");
            return;
        }

        int boundCount = 0;
        int objectCount = 0;

        foreach (Transform child in buildingRoot.GetComponentsInChildren<Transform>(true))
        {
            objectCount++;

            string objectName = child.gameObject.name;

            if (!metadataByUnityName.TryGetValue(objectName, out IfcMetadataRecord record))
            {
                continue;
            }

            IfcElementMetadata metadata = child.gameObject.GetComponent<IfcElementMetadata>();

            if (metadata == null)
            {
                metadata = child.gameObject.AddComponent<IfcElementMetadata>();
            }

            metadata.Initialize(record);
            boundCount++;
        }

        Debug.Log($"Scanned {objectCount} GameObjects. Bound IFC metadata to {boundCount} objects.");
    }
}