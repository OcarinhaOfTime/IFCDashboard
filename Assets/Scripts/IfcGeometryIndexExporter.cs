using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

public class Vector3JsonConverter : JsonConverter<Vector3>
{
    public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("x");
        writer.WriteValue(value.x);

        writer.WritePropertyName("y");
        writer.WriteValue(value.y);

        writer.WritePropertyName("z");
        writer.WriteValue(value.z);

        writer.WriteEndObject();
    }

    public override Vector3 ReadJson(
        JsonReader reader,
        System.Type objectType,
        Vector3 existingValue,
        bool hasExistingValue,
        JsonSerializer serializer)
    {
        throw new System.NotImplementedException("Reading Vector3 is not needed for this exporter.");
    }
}

[Serializable]
public class IfcGeometryRecord
{
    public string unity_name;

    public string global_id;
    public string uuid;
    public string ifc_class;
    public string element_name;
    public string object_type;
    public string tag;
    public string container_name;
    public string container_class;

    public Vector3 center;
    public Vector3 size;
    public Vector3 min;
    public Vector3 max;

    public float footprint_area;
    public float approximate_volume;

    public int renderer_count;
    public int mesh_filter_count;
    public int vertex_count;
}

public class IfcGeometryIndexExporter : MonoBehaviour
{
    [SerializeField] private Transform buildingRoot;
    [SerializeField] private string outputFileName = "cib_site_geometry_index.json";

    public void Export()
    {
        if (buildingRoot == null)
        {
            buildingRoot = transform;
        }

        List<IfcGeometryRecord> records = new List<IfcGeometryRecord>();

        foreach (IfcElementMetadata metadata in buildingRoot.GetComponentsInChildren<IfcElementMetadata>(true))
        {
            GameObject obj = metadata.gameObject;

            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
            MeshFilter[] meshFilters = obj.GetComponentsInChildren<MeshFilter>(true);

            if (renderers.Length == 0)
            {
                continue;
            }

            Bounds combinedBounds = renderers[0].bounds;

            for (int i = 1; i < renderers.Length; i++)
            {
                combinedBounds.Encapsulate(renderers[i].bounds);
            }

            int vertexCount = 0;

            foreach (MeshFilter meshFilter in meshFilters)
            {
                if (meshFilter.sharedMesh != null)
                {
                    vertexCount += meshFilter.sharedMesh.vertexCount;
                }
            }

            Vector3 size = combinedBounds.size;

            records.Add(new IfcGeometryRecord
            {
                unity_name = obj.name,

                global_id = metadata.GlobalId,
                uuid = metadata.Uuid,
                ifc_class = metadata.IfcClass,
                element_name = metadata.ElementName,
                object_type = metadata.ObjectType,
                tag = metadata.Tag,
                container_name = metadata.ContainerName,
                container_class = metadata.ContainerClass,

                center = combinedBounds.center,
                size = size,
                min = combinedBounds.min,
                max = combinedBounds.max,

                footprint_area = size.x * size.z,
                approximate_volume = size.x * size.y * size.z,

                renderer_count = renderers.Length,
                mesh_filter_count = meshFilters.Length,
                vertex_count = vertexCount
            });
        }

        JsonSerializerSettings settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new Vector3JsonConverter() }
        };
        string outputPath = Path.Combine(Application.dataPath, outputFileName);
        string json = JsonConvert.SerializeObject(records, settings);

        File.WriteAllText(outputPath, json);

        Debug.Log($"Exported {records.Count} geometry records to {outputPath}");
    }
}