using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class FacilityFocusClickRaycasterV2 : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private DashboardStatefulOrbitCamera navigationCamera;

    [Header("Raycast")]
    [Tooltip(
        "Layers considered by the raycast. Using Everything is safe because " +
        "only hierarchies containing FacilityFocusTarget can be selected."
    )]
    [SerializeField]
    private LayerMask raycastLayers = ~0;

    [SerializeField, Min(0.1f)]
    private float maximumRayDistance = 5000f;

    [Tooltip(
        "Trigger colliders are ignored by default. Change this only if your " +
        "facility selection colliders are intentionally configured as triggers."
    )]
    [SerializeField]
    private QueryTriggerInteraction triggerInteraction =
        QueryTriggerInteraction.Ignore;

    [Header("Facility Resolution")]
    [Tooltip(
        "Only colliders belonging to a hierarchy with FacilityFocusTarget " +
        "can be selected. Terrain and unrelated geometry are ignored."
    )]
    [SerializeField]
    private bool requireFacilityFocusTarget = true;

    [Tooltip(
        "Optional compatibility fallback. When enabled, the nearest hit " +
        "collider is treated as a facility even without FacilityFocusTarget. " +
        "Leave this disabled for the current dashboard."
    )]
    [SerializeField]
    private bool allowNearestColliderFallback = false;

    [Header("Click Recognition")]
    [SerializeField, Min(0f)]
    private float clickDragTolerancePixels = 10f;

    [SerializeField, Min(0.01f)]
    private float maximumClickDuration = 1.0f;

    [Tooltip(
        "Ignore clicks that begin over Unity UI. Disable temporarily when " +
        "diagnosing a full-screen Canvas that may be intercepting input."
    )]
    [SerializeField]
    private bool ignorePointerOverUi = true;

    [Header("Selection Behaviour")]
    [Tooltip(
        "Clicking the currently selected facility keeps it selected and " +
        "re-runs the focus transition."
    )]
    [SerializeField]
    private bool refocusAlreadySelectedFacility = true;

    [Header("Debug")]
    [SerializeField]
    private bool logSelections = true;

    [SerializeField]
    private bool logIgnoredHits = false;

    [SerializeField]
    private bool drawDebugRay = true;

    [SerializeField, Min(0.1f)]
    private float debugRayDuration = 2f;

    private Camera raycastCamera;

    private bool trackingClick;
    private bool clickStartedOverUi;
    private Vector2 pressPosition;
    private double pressTime;

    private void Awake()
    {
        raycastCamera = GetComponent<Camera>();

        if (navigationCamera == null)
        {
            navigationCamera =
                GetComponent<DashboardStatefulOrbitCamera>();
        }
    }

    private void OnValidate()
    {
        maximumRayDistance =
            Mathf.Max(0.1f, maximumRayDistance);

        clickDragTolerancePixels =
            Mathf.Max(0f, clickDragTolerancePixels);

        maximumClickDuration =
            Mathf.Max(0.01f, maximumClickDuration);

        debugRayDuration =
            Mathf.Max(0.1f, debugRayDuration);
    }

    private void Update()
    {
        Mouse mouse = Mouse.current;

        if (mouse == null)
        {
            return;
        }

        if (mouse.leftButton.wasPressedThisFrame)
        {
            BeginClickTracking(mouse);
        }

        if (
            trackingClick &&
            mouse.leftButton.wasReleasedThisFrame
        )
        {
            EndClickTracking(mouse);
        }
    }

    private void BeginClickTracking(Mouse mouse)
    {
        trackingClick = true;

        pressPosition =
            mouse.position.ReadValue();

        pressTime =
            Time.realtimeSinceStartupAsDouble;

        clickStartedOverUi =
            ignorePointerOverUi &&
            EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject();
    }

    private void EndClickTracking(Mouse mouse)
    {
        trackingClick = false;

        if (clickStartedOverUi)
        {
            if (logIgnoredHits)
            {
                Debug.Log(
                    "Facility click ignored because it began over Unity UI.",
                    this
                );
            }

            return;
        }

        Vector2 releasePosition =
            mouse.position.ReadValue();

        float dragDistance =
            Vector2.Distance(
                pressPosition,
                releasePosition
            );

        double clickDuration =
            Time.realtimeSinceStartupAsDouble -
            pressTime;

        if (
            dragDistance > clickDragTolerancePixels ||
            clickDuration > maximumClickDuration
        )
        {
            if (logIgnoredHits)
            {
                Debug.Log(
                    $"Facility click rejected as a drag/hold. " +
                    $"Movement={dragDistance:F1}px, " +
                    $"Duration={clickDuration:F2}s.",
                    this
                );
            }

            return;
        }

        TrySelectFacility(releasePosition);
    }

    private void TrySelectFacility(Vector2 screenPosition)
    {
        if (navigationCamera == null)
        {
            Debug.LogWarning(
                "FacilityFocusClickRaycasterV2 has no navigation camera.",
                this
            );

            return;
        }

        if (raycastCamera == null)
        {
            raycastCamera = GetComponent<Camera>();
        }

        Ray ray =
            raycastCamera.ScreenPointToRay(
                screenPosition
            );

        if (drawDebugRay)
        {
            Debug.DrawRay(
                ray.origin,
                ray.direction * maximumRayDistance,
                Color.magenta,
                debugRayDuration
            );
        }

        RaycastHit[] hits =
            Physics.RaycastAll(
                ray,
                maximumRayDistance,
                raycastLayers,
                triggerInteraction
            );

        if (hits == null || hits.Length == 0)
        {
            if (logSelections)
            {
                Debug.Log(
                    $"Facility click hit no colliders. " +
                    $"Screen={screenPosition}, " +
                    $"LayerMask={raycastLayers.value}.",
                    this
                );
            }

            return;
        }

        Array.Sort(
            hits,
            CompareHitDistance
        );

        Transform fallbackTransform = null;
        RaycastHit fallbackHit = default;

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null)
            {
                continue;
            }

            FacilityFocusTarget target =
                hit.collider.GetComponentInParent<FacilityFocusTarget>();

            if (target != null)
            {
                SelectTarget(
                    target,
                    hit
                );

                return;
            }

            if (
                fallbackTransform == null &&
                allowNearestColliderFallback
            )
            {
                fallbackTransform =
                    hit.collider.transform;

                fallbackHit = hit;
            }

            if (logIgnoredHits)
            {
                Debug.Log(
                    $"Ignoring collider '{hit.collider.name}' on layer " +
                    $"'{LayerMask.LayerToName(hit.collider.gameObject.layer)}' " +
                    $"because no FacilityFocusTarget exists in its parent hierarchy.",
                    hit.collider
                );
            }
        }

        if (
            !requireFacilityFocusTarget &&
            allowNearestColliderFallback &&
            fallbackTransform != null
        )
        {
            navigationCamera.FocusOnFacility(
                fallbackTransform
            );

            if (logSelections)
            {
                Debug.Log(
                    $"Selected fallback collider '{fallbackHit.collider.name}' " +
                    $"on layer " +
                    $"'{LayerMask.LayerToName(fallbackHit.collider.gameObject.layer)}'.",
                    fallbackHit.collider
                );
            }

            return;
        }

        if (logSelections)
        {
            Debug.Log(
                $"The click intersected {hits.Length} collider(s), but none " +
                $"belonged to a FacilityFocusTarget hierarchy.",
                this
            );
        }
    }

    private void SelectTarget(
        FacilityFocusTarget target,
        RaycastHit hit)
    {
        if (target == null)
        {
            return;
        }

        if (
            !refocusAlreadySelectedFacility &&
            navigationCamera.SelectedFacility ==
            target.transform
        )
        {
            return;
        }

        navigationCamera.FocusOnFacility(
            target.transform,
            target
        );

        if (logSelections)
        {
            Debug.Log(
                $"Selected facility '{target.DisplayName}' through collider " +
                $"'{hit.collider.name}' on layer " +
                $"'{LayerMask.LayerToName(hit.collider.gameObject.layer)}'.",
                target
            );
        }
    }

    private static int CompareHitDistance(
        RaycastHit left,
        RaycastHit right)
    {
        return left.distance.CompareTo(
            right.distance
        );
    }
}
