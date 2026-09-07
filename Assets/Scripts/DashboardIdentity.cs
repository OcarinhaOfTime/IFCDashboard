using UnityEngine;

public class DashboardIdentity : MonoBehaviour
{
    [Header("Original Unity / IFC Identity")]
    public string originalUnityName;
    public string conversionId;
    public string globalId;
    public string uuid;

    [Header("Dashboard Label")]
    public string dashboardLabel;
    public string suggestedUnityName;

    [Header("IFC Metadata")]
    public string ifcClass;
    public string category;
    public string assetIdentifier;
    public string elementName;
    public string objectType;
    public string tag;
    public string containerName;
    public string containerClass;
}