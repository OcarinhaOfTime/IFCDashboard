using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(IfcGeometryIndexExporter))]
public class IfcGeometryIndexExporterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        IfcGeometryIndexExporter exporter = (IfcGeometryIndexExporter)target;

        if (GUILayout.Button("Export IFC Geometry Index"))
        {
            exporter.Export();
        }
    }
}