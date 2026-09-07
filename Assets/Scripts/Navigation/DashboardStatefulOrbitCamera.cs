using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class DashboardStatefulOrbitCamera : MonoBehaviour
{
    public enum NavigationState
    {
        SiteOverview,
        FacilityFocused
    }

    [Serializable]
    public sealed class TransformEvent : UnityEvent<Transform> { }

    private struct CameraViewState
    {
        public Vector3 pivot;
        public float yaw;
        public float pitch;
        public float distance;

        public CameraViewState(
            Vector3 savedPivot,
            float savedYaw,
            float savedPitch,
            float savedDistance)
        {
            pivot = savedPivot;
            yaw = savedYaw;
            pitch = savedPitch;
            distance = savedDistance;
        }
    }

    [Header("Overview View")]
    [SerializeField] private Vector3 overviewPivot = Vector3.zero;
    [SerializeField] private float overviewYaw = 45f;
    [SerializeField] private float overviewPitch = 45f;
    [SerializeField] private float overviewDistance = 250f;

    [Header("Orbit")]
    [SerializeField] private float minPitch = 15f;
    [SerializeField] private float maxPitch = 80f;
    [SerializeField] private float orbitSensitivity = 0.2f;
    [SerializeField, Min(0f)] private float orbitDragThresholdPixels = 5f;

    [Header("Overview Zoom")]
    [SerializeField] private float overviewMinDistance = 40f;
    [SerializeField] private float overviewMaxDistance = 700f;
    [SerializeField] private float zoomSpeed = 80f;

    [Header("Facility Focus")]
    [Tooltip("Optional vertical offset added to automatically calculated building centers.")]
    [SerializeField] private float automaticFocusHeightOffset = 0f;

    [Tooltip("Extra distance around a building when calculating a framing distance.")]
    [SerializeField, Min(1f)] private float focusFramingPadding = 1.25f;

    [Tooltip("Smallest permitted distance relative to the automatic framing distance.")]
    [SerializeField, Min(0.05f)] private float focusMinDistanceMultiplier = 0.45f;

    [Tooltip("Largest permitted distance relative to the automatic framing distance.")]
    [SerializeField, Min(1f)] private float focusMaxDistanceMultiplier = 3f;

    [SerializeField] private bool preserveYawWhenFocusing = true;
    [SerializeField] private bool preservePitchWhenFocusing = false;
    [SerializeField] private float focusedPitch = 35f;
    [SerializeField] private bool allowPanWhileFocused = false;

    [Header("Pan")]
    [SerializeField] private float panSpeed = 0.9f;
    [SerializeField] private Vector2 minBoundsXZ = new Vector2(-250f, -260f);
    [SerializeField] private Vector2 maxBoundsXZ = new Vector2(180f, 260f);

    [Header("Input")]
    [Tooltip(
        "While a facility is selected, right-click returns to the exact " +
        "camera view that existed before the first facility focus."
    )]
    [SerializeField] private bool rightClickClearsFocus = true;

    [Tooltip(
        "When no facility is selected, right-click resets only the zoom " +
        "while preserving the current pivot and orbit angles."
    )]
    [SerializeField] private bool rightClickResetsOverviewZoom = true;

    [SerializeField] private bool ignorePointerOverUi = true;

    [Header("Smoothing")]
    [SerializeField, Min(0.01f)] private float pivotSmoothing = 6f;
    [SerializeField, Min(0.01f)] private float rotationSmoothing = 7f;
    [SerializeField, Min(0.01f)] private float zoomSmoothing = 7f;

    [Header("Selection Events")]
    [SerializeField] private TransformEvent facilitySelected = new TransformEvent();
    [SerializeField] private UnityEvent facilityCleared = new UnityEvent();

    [Header("Debug")]
    [SerializeField] private bool drawBoundsGizmo = true;
    [SerializeField] private bool logStateChanges = true;

    private Camera controlledCamera;

    private NavigationState state = NavigationState.SiteOverview;
    private Transform selectedFacility;
    private Bounds selectedFacilityBounds;
    private bool hasSelectedBounds;

    private Vector3 pivot;
    private Vector3 desiredPivot;
    private float yaw;
    private float desiredYaw;
    private float pitch;
    private float desiredPitch;
    private float distance;
    private float desiredDistance;
    private float activeMinDistance;
    private float activeMaxDistance;

    private CameraViewState returnViewState;
    private bool hasReturnViewState;
    private float focusedDefaultDistance;

    private bool trackingLeftButton;
    private bool orbitDragging;
    private Vector2 leftPressPosition;

    public NavigationState State => state;
    public Transform SelectedFacility => selectedFacility;
    public bool HasSelectedFacility => selectedFacility != null;
    public bool IsOrbitDragging => orbitDragging;

    public event Action<Transform> FacilitySelected;
    public event Action FacilityCleared;

    private void Awake()
    {
        controlledCamera = GetComponent<Camera>();

        pivot = overviewPivot;
        desiredPivot = overviewPivot;
        yaw = overviewYaw;
        desiredYaw = overviewYaw;
        pitch = Mathf.Clamp(overviewPitch, minPitch, maxPitch);
        desiredPitch = pitch;
        distance = Mathf.Clamp(overviewDistance, overviewMinDistance, overviewMaxDistance);
        desiredDistance = distance;
        activeMinDistance = overviewMinDistance;
        activeMaxDistance = overviewMaxDistance;
    }

    private void Start()
    {
        ApplyCameraTransform();
    }

    private void LateUpdate()
    {
        Mouse mouse = Mouse.current;

        if (mouse != null)
        {
            HandleMouse(mouse);
        }

        if (state == NavigationState.SiteOverview)
        {
            ClampOverviewPivot();
        }

        float pivotT = DampFactor(pivotSmoothing);
        float rotationT = DampFactor(rotationSmoothing);
        float zoomT = DampFactor(zoomSmoothing);

        pivot = Vector3.Lerp(pivot, desiredPivot, pivotT);
        yaw = Mathf.LerpAngle(yaw, desiredYaw, rotationT);
        pitch = Mathf.Lerp(pitch, desiredPitch, rotationT);
        distance = Mathf.Lerp(distance, desiredDistance, zoomT);

        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        distance = Mathf.Clamp(distance, activeMinDistance, activeMaxDistance);

        ApplyCameraTransform();
    }

    public void FocusOnFacility(Transform facilityRoot)
    {
        FocusOnFacility(facilityRoot, null);
    }

    public void FocusOnFacility(
        Transform facilityRoot,
        FacilityFocusTarget explicitTarget)
    {
        if (facilityRoot == null)
        {
            Debug.LogWarning(
                "Cannot focus the camera because the facility root is null.",
                this
            );
            return;
        }

        bool wasAlreadyFocused =
            state == NavigationState.FacilityFocused;

        bool isSameFacility =
            wasAlreadyFocused &&
            selectedFacility == facilityRoot;

        /*
         * Clicking the currently focused facility again should not alter
         * its pivot or orbit. It only restores the original framing zoom.
         */
        if (isSameFacility)
        {
            ResetFocusedZoom();
            return;
        }

        /*
         * Save the exact visible camera state only when entering facility
         * focus from the site view. Switching directly between facilities
         * keeps the original return destination.
         */
        if (!wasAlreadyFocused)
        {
            CaptureReturnViewState();
        }

        Bounds bounds;
        bool hasBounds = TryCalculateFacilityBounds(facilityRoot, out bounds);

        Vector3 focusPoint;

        if (explicitTarget != null && explicitTarget.CustomFocusPoint != null)
        {
            focusPoint = explicitTarget.CustomFocusPoint.position;
        }
        else if (hasBounds)
        {
            focusPoint = bounds.center + Vector3.up * automaticFocusHeightOffset;
        }
        else
        {
            focusPoint = facilityRoot.position + Vector3.up * automaticFocusHeightOffset;
        }

        float framingDistance = hasBounds
            ? CalculateFramingDistance(bounds)
            : Mathf.Max(overviewMinDistance, 50f);

        if (explicitTarget != null && explicitTarget.OverrideFocusDistance)
        {
            framingDistance = Mathf.Max(0.1f, explicitTarget.FocusDistance);
        }

        selectedFacility = facilityRoot;
        selectedFacilityBounds = bounds;
        hasSelectedBounds = hasBounds;
        state = NavigationState.FacilityFocused;

        desiredPivot = focusPoint;

        if (explicitTarget != null && explicitTarget.OverrideViewAngles)
        {
            desiredYaw = explicitTarget.FocusYaw;
            desiredPitch = Mathf.Clamp(
                explicitTarget.FocusPitch,
                minPitch,
                maxPitch
            );
        }
        else
        {
            if (!preserveYawWhenFocusing)
            {
                desiredYaw = overviewYaw;
            }

            if (!preservePitchWhenFocusing)
            {
                desiredPitch = Mathf.Clamp(
                    focusedPitch,
                    minPitch,
                    maxPitch
                );
            }
        }

        activeMinDistance = Mathf.Max(
            0.1f,
            framingDistance * focusMinDistanceMultiplier
        );

        activeMaxDistance = Mathf.Max(
            activeMinDistance,
            framingDistance * focusMaxDistanceMultiplier
        );

        focusedDefaultDistance = Mathf.Clamp(
            framingDistance,
            activeMinDistance,
            activeMaxDistance
        );

        desiredDistance = focusedDefaultDistance;

        facilitySelected.Invoke(selectedFacility);
        FacilitySelected?.Invoke(selectedFacility);

        if (logStateChanges)
        {
            Debug.Log(
                $"Camera focused on '{selectedFacility.name}'. " +
                $"Pivot={desiredPivot}, Distance={desiredDistance:F1}.",
                selectedFacility
            );
        }
    }

    public void ClearFacilityFocus()
    {
        bool hadFacility = selectedFacility != null;

        selectedFacility = null;
        hasSelectedBounds = false;
        state = NavigationState.SiteOverview;

        activeMinDistance = overviewMinDistance;
        activeMaxDistance = overviewMaxDistance;

        if (hasReturnViewState)
        {
            desiredPivot = returnViewState.pivot;
            desiredYaw = returnViewState.yaw;
            desiredPitch = Mathf.Clamp(
                returnViewState.pitch,
                minPitch,
                maxPitch
            );
            desiredDistance = Mathf.Clamp(
                returnViewState.distance,
                activeMinDistance,
                activeMaxDistance
            );

            hasReturnViewState = false;
        }
        else
        {
            SetConfiguredOverviewTarget();
        }

        if (hadFacility)
        {
            facilityCleared.Invoke();
            FacilityCleared?.Invoke();
        }

        if (logStateChanges)
        {
            Debug.Log(
                "Camera facility focus cleared; restoring the pre-focus view.",
                this
            );
        }
    }

    public void ReturnToOverview()
    {
        bool hadFacility = selectedFacility != null;

        selectedFacility = null;
        hasSelectedBounds = false;
        hasReturnViewState = false;
        state = NavigationState.SiteOverview;

        activeMinDistance = overviewMinDistance;
        activeMaxDistance = overviewMaxDistance;

        SetConfiguredOverviewTarget();

        if (hadFacility)
        {
            facilityCleared.Invoke();
            FacilityCleared?.Invoke();
        }
    }

    public void ResetFocusedZoom()
    {
        if (state != NavigationState.FacilityFocused)
        {
            return;
        }

        desiredDistance = Mathf.Clamp(
            focusedDefaultDistance,
            activeMinDistance,
            activeMaxDistance
        );

        if (logStateChanges)
        {
            Debug.Log(
                $"Focused zoom reset to {desiredDistance:F1}.",
                this
            );
        }
    }

    public void ResetOverviewZoom()
    {
        if (state != NavigationState.SiteOverview)
        {
            return;
        }

        desiredDistance = Mathf.Clamp(
            overviewDistance,
            overviewMinDistance,
            overviewMaxDistance
        );

        if (logStateChanges)
        {
            Debug.Log(
                $"Overview zoom reset to {desiredDistance:F1}; " +
                "pivot and orbit were preserved.",
                this
            );
        }
    }

    private void CaptureReturnViewState()
    {
        /*
         * Capture the currently rendered values instead of the targets.
         * This makes the return destination match exactly what the user
         * was looking at at the moment of selection.
         */
        returnViewState = new CameraViewState(
            pivot,
            yaw,
            pitch,
            distance
        );

        hasReturnViewState = true;
    }

    private void SetConfiguredOverviewTarget()
    {
        desiredPivot = overviewPivot;
        desiredYaw = overviewYaw;
        desiredPitch = Mathf.Clamp(
            overviewPitch,
            minPitch,
            maxPitch
        );
        desiredDistance = Mathf.Clamp(
            overviewDistance,
            overviewMinDistance,
            overviewMaxDistance
        );
    }

    [ContextMenu("Starting View/Capture Current Transform As Overview")]
    private void CaptureCurrentTransformAsOverview()
    {
#if UNITY_EDITOR
        Undo.RecordObject(
            this,
            "Capture Camera Starting Overview"
        );
#endif

        if (Application.isPlaying)
        {
            /*
             * During Play Mode, the orbit state is the authoritative view.
             */
            overviewPivot = pivot;
            overviewYaw = yaw;
            overviewPitch = pitch;
            overviewDistance = distance;
        }
        else
        {
            /*
             * In Edit Mode, infer an orbit representation from the camera's
             * manually configured Transform. An orbit camera needs a pivot
             * and a distance in addition to position and rotation.
             *
             * We preserve the current camera position and orientation exactly
             * and use the existing overview pivot only to estimate a useful
             * orbit radius. The new pivot is then placed directly in front of
             * the camera at that radius.
             */
            float capturedDistance = Vector3.Distance(
                transform.position,
                overviewPivot
            );

            if (capturedDistance < 0.01f)
            {
                capturedDistance = overviewDistance;
            }

            overviewDistance = Mathf.Clamp(
                capturedDistance,
                overviewMinDistance,
                overviewMaxDistance
            );

            Vector3 eulerAngles = transform.rotation.eulerAngles;

            overviewYaw = NormalizeSignedAngle(eulerAngles.y);
            overviewPitch = Mathf.Clamp(
                NormalizeSignedAngle(eulerAngles.x),
                minPitch,
                maxPitch
            );

            Quaternion capturedRotation = Quaternion.Euler(
                overviewPitch,
                overviewYaw,
                0f
            );

            overviewPivot =
                transform.position +
                capturedRotation * Vector3.forward * overviewDistance;
        }

#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
        PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        SceneView.RepaintAll();
#endif

        Debug.Log(
            $"Captured starting overview from the current camera view: " +
            $"Pivot={overviewPivot}, Yaw={overviewYaw:F1}, " +
            $"Pitch={overviewPitch:F1}, " +
            $"Distance={overviewDistance:F1}.",
            this
        );
    }

    [ContextMenu("Apply Overview Immediately")]
    private void ApplyOverviewImmediately()
    {
        ReturnToOverview();
        pivot = desiredPivot;
        yaw = desiredYaw;
        pitch = desiredPitch;
        distance = desiredDistance;
        ApplyCameraTransform();
    }

    private void HandleMouse(Mouse mouse)
    {
        bool pointerOverUi =
            ignorePointerOverUi &&
            EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject();

        HandleRightClick(mouse, pointerOverUi);
        HandleOrbitInput(mouse, pointerOverUi);

        if (
            mouse.middleButton.isPressed &&
            !pointerOverUi &&
            (
                state == NavigationState.SiteOverview ||
                allowPanWhileFocused
            )
        )
        {
            Pan(mouse.delta.ReadValue());
        }

        if (!pointerOverUi)
        {
            float scroll = mouse.scroll.ReadValue().y;

            if (Mathf.Abs(scroll) > 0.01f)
            {
                desiredDistance -=
                    scroll *
                    zoomSpeed *
                    Time.unscaledDeltaTime;

                desiredDistance = Mathf.Clamp(
                    desiredDistance,
                    activeMinDistance,
                    activeMaxDistance
                );
            }
        }
    }

    private void HandleRightClick(Mouse mouse, bool pointerOverUi)
    {
        if (
            pointerOverUi ||
            !mouse.rightButton.wasPressedThisFrame
        )
        {
            return;
        }

        if (
            state == NavigationState.FacilityFocused &&
            rightClickClearsFocus
        )
        {
            ClearFacilityFocus();
            return;
        }

        if (
            state == NavigationState.SiteOverview &&
            rightClickResetsOverviewZoom
        )
        {
            ResetOverviewZoom();
        }
    }

    private void HandleOrbitInput(Mouse mouse, bool pointerOverUi)
    {
        if (mouse.leftButton.wasPressedThisFrame)
        {
            trackingLeftButton = !pointerOverUi;
            orbitDragging = false;
            leftPressPosition = mouse.position.ReadValue();
        }

        if (trackingLeftButton && mouse.leftButton.isPressed)
        {
            Vector2 currentPosition = mouse.position.ReadValue();

            if (!orbitDragging)
            {
                float dragDistance = Vector2.Distance(
                    leftPressPosition,
                    currentPosition
                );

                if (dragDistance >= orbitDragThresholdPixels)
                {
                    orbitDragging = true;
                }
            }

            if (orbitDragging)
            {
                Vector2 mouseDelta = mouse.delta.ReadValue();

                desiredYaw += mouseDelta.x * orbitSensitivity;
                desiredPitch -= mouseDelta.y * orbitSensitivity;
                desiredPitch = Mathf.Clamp(
                    desiredPitch,
                    minPitch,
                    maxPitch
                );
            }
        }

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            trackingLeftButton = false;
            orbitDragging = false;
        }
    }

    private void Pan(Vector2 mouseDelta)
    {
        Quaternion targetRotation = Quaternion.Euler(
            desiredPitch,
            desiredYaw,
            0f
        );

        Vector3 cameraRight = targetRotation * Vector3.right;
        Vector3 cameraForward = Vector3.ProjectOnPlane(
            targetRotation * Vector3.forward,
            Vector3.up
        ).normalized;

        if (cameraForward.sqrMagnitude < 0.001f)
        {
            cameraForward = Vector3.forward;
        }

        float distanceFactor =
            Mathf.Max(desiredDistance, 1f) *
            0.0025f;

        Vector3 panOffset =
            (
                -cameraRight * mouseDelta.x -
                cameraForward * mouseDelta.y
            ) *
            panSpeed *
            distanceFactor;

        desiredPivot += panOffset;
    }

    private void ClampOverviewPivot()
    {
        desiredPivot.x = Mathf.Clamp(
            desiredPivot.x,
            minBoundsXZ.x,
            maxBoundsXZ.x
        );

        desiredPivot.z = Mathf.Clamp(
            desiredPivot.z,
            minBoundsXZ.y,
            maxBoundsXZ.y
        );
    }

    private float CalculateFramingDistance(Bounds bounds)
    {
        float radius = Mathf.Max(
            0.1f,
            bounds.extents.magnitude
        );

        if (controlledCamera.orthographic)
        {
            return Mathf.Max(radius, overviewMinDistance);
        }

        float verticalHalfFov =
            controlledCamera.fieldOfView *
            0.5f *
            Mathf.Deg2Rad;

        float horizontalHalfFov =
            Mathf.Atan(
                Mathf.Tan(verticalHalfFov) *
                Mathf.Max(controlledCamera.aspect, 0.01f)
            );

        float limitingHalfFov = Mathf.Min(
            verticalHalfFov,
            horizontalHalfFov
        );

        float distanceToFitSphere =
            radius /
            Mathf.Max(
                Mathf.Sin(limitingHalfFov),
                0.01f
            );

        return distanceToFitSphere * focusFramingPadding;
    }

    private static bool TryCalculateFacilityBounds(
        Transform facilityRoot,
        out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;

        Collider[] colliders =
            facilityRoot.GetComponentsInChildren<Collider>(true);

        foreach (Collider facilityCollider in colliders)
        {
            if (
                facilityCollider == null ||
                !facilityCollider.enabled ||
                !facilityCollider.gameObject.activeInHierarchy
            )
            {
                continue;
            }

            Bounds colliderBounds = facilityCollider.bounds;

            if (colliderBounds.size.sqrMagnitude <= 0f)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = colliderBounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(colliderBounds);
            }
        }

        if (hasBounds)
        {
            return true;
        }

        Renderer[] renderers =
            facilityRoot.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer renderer in renderers)
        {
            if (
                renderer == null ||
                !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy
            )
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private void ApplyCameraTransform()
    {
        Quaternion rotation = Quaternion.Euler(
            pitch,
            yaw,
            0f
        );

        Vector3 cameraPosition =
            pivot +
            rotation *
            new Vector3(0f, 0f, -distance);

        transform.SetPositionAndRotation(
            cameraPosition,
            rotation
        );
    }

    private static float NormalizeSignedAngle(float angle)
    {
        angle %= 360f;

        if (angle > 180f)
        {
            angle -= 360f;
        }

        return angle;
    }

    private float DampFactor(float smoothing)
    {
        return 1f -
            Mathf.Exp(
                -smoothing *
                Time.unscaledDeltaTime
            );
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawBoundsGizmo)
        {
            return;
        }

        Vector3 overviewCenter =
            new Vector3(
                (minBoundsXZ.x + maxBoundsXZ.x) * 0.5f,
                0f,
                (minBoundsXZ.y + maxBoundsXZ.y) * 0.5f
            );

        Vector3 overviewSize =
            new Vector3(
                maxBoundsXZ.x - minBoundsXZ.x,
                2f,
                maxBoundsXZ.y - minBoundsXZ.y
            );

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(overviewCenter, overviewSize);

        Gizmos.color =
            state == NavigationState.FacilityFocused
                ? Color.green
                : Color.yellow;

        Gizmos.DrawSphere(
            Application.isPlaying ? pivot : overviewPivot,
            3f
        );

        if (Application.isPlaying && hasSelectedBounds)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(
                selectedFacilityBounds.center,
                selectedFacilityBounds.size
            );
        }
    }
}
