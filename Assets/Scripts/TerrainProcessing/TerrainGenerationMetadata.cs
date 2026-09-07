using UnityEngine;

/// <summary>
/// Records how a generated Unity Terrain was derived from an imported mesh.
/// The valid-sample mask distinguishes genuine ray hits from values filled
/// merely to satisfy Unity's rectangular heightmap requirement.
/// </summary>
public sealed class TerrainGenerationMetadata : ScriptableObject
{
    [Header("Generator")]
    public string generatorVersion = "1.0.0";
    public string generatedUtc;

    [Header("Source")]
    public string sourceObjectName;
    public string sourceScenePath;
    public string sourceHierarchyPath;

    public string sourceMeshAssetPath;
    public string sourceMeshGuid;

    public Vector3 sourceReferenceOriginWorld;
    public Vector3 sourcePositionWorld;
    public Quaternion sourceRotationWorld;
    public Vector3 sourceLossyScale;
    public Bounds sourceWorldBounds;

    [Header("Generated Terrain")]
    public TerrainData terrainData;
    public Vector3 terrainWorldPosition;
    public Vector3 terrainWorldSize;
    public Bounds terrainWorldBounds;

    [Header("Sampling")]
    public int heightmapResolution;
    public int subsamplesPerAxis;

    public float horizontalPadding;
    public float rayStartPadding;
    public float rayEndPadding;
    public float bottomPadding;
    public float topPadding;

    public bool centeredOnSourceOrigin;
    public bool createdHolesOutsideSource;

    public int validSampleCount;
    public int totalSampleCount;

    public float minimumSampledWorldY;
    public float maximumSampledWorldY;

    /// <summary>
    /// Flattened [z * resolution + x] mask.
    /// 1 means the source mesh was hit; 0 means the height was filled.
    /// Hidden because displaying hundreds of thousands of entries in the
    /// Inspector is not useful.
    /// </summary>
    [HideInInspector]
    public byte[] validSampleMask;
}

/// <summary>
/// Added to the generated Terrain GameObject for convenient navigation
/// between the scene object, metadata asset, and original source object.
/// </summary>
public sealed class GeneratedTerrainReference : MonoBehaviour
{
    public TerrainGenerationMetadata metadata;
    public GameObject sourceTerrainObject;
}