using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(CanvasGroup))]
public sealed class FacilityWorldLabelUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private DashboardStatefulOrbitCamera navigationCamera;

    [SerializeField]
    private Camera facingCamera;

    [Tooltip("Usually the RectTransform on this same World Space Canvas.")]
    [SerializeField]
    private RectTransform labelRoot;

    [SerializeField]
    private TextMeshProUGUI buildingNameText;

    [Header("World Placement")]
    [Tooltip("World-space distance above the top of the selected building bounds.")]
    [SerializeField, Min(0f)]
    private float heightAboveBuilding = 4f;

    [Tooltip("Continuously follows the selected building's current bounds.")]
    [SerializeField]
    private bool followSelectedFacility = true;

    [SerializeField, Min(0.01f)]
    private float positionSmoothing = 10f;

    [Header("Billboard")]
    [SerializeField]
    private bool faceCamera = true;

    [Tooltip(
        "Scales the label with camera distance so its apparent screen size " +
        "remains approximately stable."
    )]
    [SerializeField]
    private bool maintainApproximateScreenSize = true;

    [SerializeField, Min(0.01f)]
    private float referenceDistance = 100f;

    [SerializeField, Min(0.01f)]
    private float minimumScaleMultiplier = 0.5f;

    [SerializeField, Min(0.01f)]
    private float maximumScaleMultiplier = 3f;

    [Header("Visibility")]
    [SerializeField, Min(0.01f)]
    private float fadeSpeed = 6f;

    [SerializeField]
    private bool hideAtStartup = true;

    private CanvasGroup canvasGroup;
    private Transform selectedFacility;

    private Vector3 desiredWorldPosition;
    private Vector3 baseLocalScale;

    private float targetAlpha;

    private void Awake()
    {
        canvasGroup = GetComponent<CanvasGroup>();

        if (labelRoot == null)
        {
            labelRoot = transform as RectTransform;
        }

        if (facingCamera == null)
        {
            facingCamera = Camera.main;
        }

        if (navigationCamera == null)
        {
            navigationCamera =
                FindFirstObjectByType<DashboardStatefulOrbitCamera>();
        }

        baseLocalScale =
            labelRoot != null
                ? labelRoot.localScale
                : transform.localScale;

        targetAlpha = hideAtStartup ? 0f : 1f;
        canvasGroup.alpha = targetAlpha;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;
    }

    private void OnEnable()
    {
        SubscribeToCamera();
    }

    private void Start()
    {
        /*
         * OnEnable can run before another object's Awake in some scene
         * arrangements. Try once more after all Awake calls have completed.
         */
        SubscribeToCamera();
    }

    private void OnDisable()
    {
        UnsubscribeFromCamera();
    }

    private void LateUpdate()
    {
        if (
            selectedFacility != null &&
            followSelectedFacility
        )
        {
            desiredWorldPosition =
                CalculateLabelPosition(selectedFacility);
        }

        float positionT =
            1f - Mathf.Exp(
                -positionSmoothing *
                Time.unscaledDeltaTime
            );

        transform.position =
            Vector3.Lerp(
                transform.position,
                desiredWorldPosition,
                positionT
            );

        if (
            faceCamera &&
            facingCamera != null
        )
        {
            /*
             * Matches the canvas orientation to the camera while preserving
             * the camera's current up direction.
             */
            transform.LookAt(
                transform.position +
                facingCamera.transform.rotation *
                Vector3.forward,
                facingCamera.transform.rotation *
                Vector3.up
            );
        }

        UpdateDistanceScale();

        canvasGroup.alpha =
            Mathf.MoveTowards(
                canvasGroup.alpha,
                targetAlpha,
                fadeSpeed *
                Time.unscaledDeltaTime
            );
    }

    private void SubscribeToCamera()
    {
        if (navigationCamera == null)
        {
            navigationCamera =
                FindFirstObjectByType<DashboardStatefulOrbitCamera>();
        }

        if (navigationCamera == null)
        {
            return;
        }

        /*
         * Remove first so repeated Start/OnEnable calls never create
         * duplicate subscriptions.
         */
        navigationCamera.FacilitySelected -= HandleFacilitySelected;
        navigationCamera.FacilityCleared -= HandleFacilityCleared;

        navigationCamera.FacilitySelected += HandleFacilitySelected;
        navigationCamera.FacilityCleared += HandleFacilityCleared;
    }

    private void UnsubscribeFromCamera()
    {
        if (navigationCamera == null)
        {
            return;
        }

        navigationCamera.FacilitySelected -= HandleFacilitySelected;
        navigationCamera.FacilityCleared -= HandleFacilityCleared;
    }

    private void HandleFacilitySelected(Transform facility)
    {
        if (facility == null)
        {
            HandleFacilityCleared();
            return;
        }

        selectedFacility = facility;

        FacilityFocusTarget target =
            facility.GetComponent<FacilityFocusTarget>();

        string displayName =
            target != null
                ? target.DisplayName
                : facility.name;

        if (buildingNameText != null)
        {
            buildingNameText.text = displayName;
        }

        desiredWorldPosition =
            CalculateLabelPosition(facility);

        /*
         * On the first selection, place it immediately at the building.
         * Later selections can smoothly move between facilities.
         */
        if (canvasGroup.alpha <= 0.001f)
        {
            transform.position = desiredWorldPosition;
        }

        targetAlpha = 1f;
    }

    private void HandleFacilityCleared()
    {
        selectedFacility = null;
        targetAlpha = 0f;
    }

    private Vector3 CalculateLabelPosition(
        Transform facility)
    {
        if (
            TryCalculateFacilityBounds(
                facility,
                out Bounds bounds
            )
        )
        {
            return new Vector3(
                bounds.center.x,
                bounds.max.y + heightAboveBuilding,
                bounds.center.z
            );
        }

        return facility.position +
            Vector3.up * heightAboveBuilding;
    }

    private void UpdateDistanceScale()
    {
        if (
            labelRoot == null ||
            facingCamera == null
        )
        {
            return;
        }

        if (!maintainApproximateScreenSize)
        {
            labelRoot.localScale = baseLocalScale;
            return;
        }

        float cameraDistance =
            Vector3.Distance(
                facingCamera.transform.position,
                transform.position
            );

        float scaleMultiplier =
            cameraDistance /
            Mathf.Max(referenceDistance, 0.01f);

        scaleMultiplier =
            Mathf.Clamp(
                scaleMultiplier,
                minimumScaleMultiplier,
                maximumScaleMultiplier
            );

        labelRoot.localScale =
            baseLocalScale * scaleMultiplier;
    }

    private static bool TryCalculateFacilityBounds(
        Transform facility,
        out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;

        Collider[] colliders =
            facility.GetComponentsInChildren<Collider>(
                true
            );

        foreach (Collider item in colliders)
        {
            if (
                item == null ||
                !item.enabled ||
                !item.gameObject.activeInHierarchy
            )
            {
                continue;
            }

            Bounds itemBounds = item.bounds;

            if (itemBounds.size.sqrMagnitude <= 0f)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = itemBounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(itemBounds);
            }
        }

        if (hasBounds)
        {
            return true;
        }

        Renderer[] renderers =
            facility.GetComponentsInChildren<Renderer>(
                true
            );

        foreach (Renderer item in renderers)
        {
            if (
                item == null ||
                !item.enabled ||
                !item.gameObject.activeInHierarchy
            )
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = item.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(item.bounds);
            }
        }

        return hasBounds;
    }
}
