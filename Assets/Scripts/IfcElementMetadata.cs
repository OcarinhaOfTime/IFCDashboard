using UnityEngine;

public class IfcElementMetadata : MonoBehaviour
{
    public string GlobalId;
    public string Uuid;
    public string IfcClass;
    public string ElementName;
    public string ObjectType;
    public string Tag;

    public string ContainerGlobalId;
    public string ContainerClass;
    public string ContainerName;

    public void Initialize(IfcMetadataRecord record)
    {
        GlobalId = record.global_id;
        Uuid = record.uuid;
        IfcClass = record.ifc_class;
        ElementName = record.name;
        ObjectType = record.object_type;
        Tag = record.tag;

        ContainerGlobalId = record.container_global_id;
        ContainerClass = record.container_class;
        ContainerName = record.container_name;
    }
}