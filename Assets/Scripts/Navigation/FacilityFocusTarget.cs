using UnityEngine;

[DisallowMultipleComponent]
public sealed class FacilityFocusTarget : MonoBehaviour
{
    [Header("Identity")]
    [SerializeField] private string facilityId;
    [SerializeField] private string displayName;
    [SerializeField] private string websiteRoute;

    [Header("Optional Camera Overrides")]
    [SerializeField] private Transform customFocusPoint;

    [SerializeField] private bool overrideFocusDistance;
    [SerializeField, Min(0.1f)] private float focusDistance = 80f;

    [SerializeField] private bool overrideViewAngles;
    [SerializeField] private float focusYaw = 45f;
    [SerializeField] private float focusPitch = 35f;

    public string FacilityId =>
        string.IsNullOrWhiteSpace(facilityId)
            ? gameObject.name
            : facilityId;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(displayName)
            ? gameObject.name
            : displayName;

    public string WebsiteRoute => websiteRoute;
    public Transform CustomFocusPoint => customFocusPoint;
    public bool OverrideFocusDistance => overrideFocusDistance;
    public float FocusDistance => focusDistance;
    public bool OverrideViewAngles => overrideViewAngles;
    public float FocusYaw => focusYaw;
    public float FocusPitch => focusPitch;

    [ContextMenu("Use GameObject Name As Identity")]
    private void UseGameObjectNameAsIdentity()
    {
        facilityId = gameObject.name;
        displayName = gameObject.name;
    }
}
