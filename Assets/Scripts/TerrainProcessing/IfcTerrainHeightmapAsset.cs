using UnityEngine;

/// <summary>
/// Persistent intermediate representation between the IFC mesh and the
/// generated Unity Terrain.
///
/// Arrays are flattened using:
///     index = z * resolution + x
/// </summary>
public sealed class IfcTerrainHeightmapAsset : ScriptableObject
{
    [Header("Format")]
    public int formatVersion = 3;
    public string datasetName;
    public string generatedUtc;

    [Header("Source")]
    public string sourceObjectName;
    public string sourceHierarchyPath;
    public string sourceScenePath;

    public string sourceMeshAssetPath;
    public string sourceMeshGuid;

    public Vector3 sourceOriginWorld;
    public Vector3 sourcePositionWorld;
    public Quaternion sourceRotationWorld;
    public Vector3 sourceLossyScale;
    public Bounds sourceWorldBounds;

    [Header("Sampling Grid")]
    public int resolution;

    public float gridMinX;
    public float gridMaxX;
    public float gridMinZ;
    public float gridMaxZ;

    public float sampleSpacingX;
    public float sampleSpacingZ;

    public float rayTopY;
    public float rayBottomY;

    public bool centeredOnSourceOrigin;
    public float horizontalPadding;
    public float rayTopPadding;
    public float rayBottomPadding;

    [Header("Sample Statistics")]
    public int validSampleCount;
    public int totalSampleCount;

    public float minimumSampledWorldY;
    public float maximumSampledWorldY;

    [Header("Repair Statistics")]
    public bool hasRepair;
    public int exteriorSampleCount;
    public int interpolatedSampleCount;

    [Header("Smoothing")]
    public bool hasSmoothing;
    public int smoothingIterations;
    public float smoothingStrength;
    public bool smoothingUsesDiagonals;

    [Header("Surface Clearance")]
    public bool hasClearance;
    public bool clearanceInputWasSmoothed;
    public int clearanceTargetCount;
    public int clearanceProcessedMeshCount;
    public int clearanceTargetHitSampleCount;
    public int clearanceAdjustedSampleCount;
    public float clearanceOffset;
    public float maximumClearanceLowering;
    public float clearanceFeatherDistance;
    public int clearanceFeatherIterations;
    public float clearanceFeatherStrength;
    public bool clearanceUsesDiagonals;
    public float maximumAppliedClearanceLowering;

    [HideInInspector]
    public string[] clearanceTargetHierarchyPaths;

    /*
     * Invalid raw-height entries contain zero. Always consult validMask
     * before reading rawHeights.
     *
     * byte masks:
     *     0 = false
     *     1 = true
     */

    [HideInInspector]
    public float[] rawHeights;

    [HideInInspector]
    public byte[] validMask;

    [HideInInspector]
    public float[] repairedHeights;

    [HideInInspector]
    public byte[] exteriorMask;

    [HideInInspector]
    public byte[] interpolatedMask;

    [HideInInspector]
    public float[] smoothedHeights;

    [HideInInspector]
    public float[] clearanceHeights;

    [HideInInspector]
    public byte[] clearanceMask;

    [HideInInspector]
    public float[] clearanceDelta;

    public int CellCount
    {
        get
        {
            return resolution > 0
                ? resolution * resolution
                : 0;
        }
    }

    public bool HasRawData
    {
        get
        {
            return resolution > 1 &&
                   rawHeights != null &&
                   validMask != null &&
                   rawHeights.Length == CellCount &&
                   validMask.Length == CellCount;
        }
    }

    public bool HasRepairedData
    {
        get
        {
            return hasRepair &&
                   repairedHeights != null &&
                   exteriorMask != null &&
                   interpolatedMask != null &&
                   repairedHeights.Length == CellCount &&
                   exteriorMask.Length == CellCount &&
                   interpolatedMask.Length == CellCount;
        }
    }

    public bool HasSmoothedData
    {
        get
        {
            return hasSmoothing &&
                   smoothedHeights != null &&
                   smoothedHeights.Length == CellCount;
        }
    }

    public bool HasClearanceData
    {
        get
        {
            return hasClearance &&
                   clearanceHeights != null &&
                   clearanceMask != null &&
                   clearanceDelta != null &&
                   clearanceHeights.Length == CellCount &&
                   clearanceMask.Length == CellCount &&
                   clearanceDelta.Length == CellCount;
        }
    }

    public int GetIndex(int x, int z)
    {
        return z * resolution + x;
    }
}

/// <summary>
/// Attached to the generated Terrain GameObject.
/// </summary>
public sealed class IfcGeneratedTerrainReference : MonoBehaviour
{
    public IfcTerrainHeightmapAsset heightmapData;
    public GameObject sourceTerrainObject;
}
