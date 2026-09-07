using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
public class DashboardOrbitPanCamera : MonoBehaviour
{
    [Header("Target / Pivot")]
    [SerializeField] private Vector3 pivot = Vector3.zero;

    [Header("Orbit")]
    [SerializeField] private float yaw = 45f;
    [SerializeField] private float pitch = 45f;
    [SerializeField] private float minPitch = 15f;
    [SerializeField] private float maxPitch = 80f;
    public float orbitSensitivity = 0.2f;

    [Header("Zoom")]
    [SerializeField] private float distance = 250f;
    [SerializeField] private float defaultDistance = 250f;
    [SerializeField] private float minDistance = 40f;
    [SerializeField] private float maxDistance = 700f;
    [SerializeField] private float zoomSpeed = 80f;

    [Header("Pan")]
    [SerializeField] private float panSpeed = 0.9f;
    [SerializeField] private Vector2 minBoundsXZ = new Vector2(-250f, -260f);
    [SerializeField] private Vector2 maxBoundsXZ = new Vector2(180f, 260f);

    [Header("Right Mouse Demo Orbit")]
    [SerializeField] private float autoOrbitSpeed = 8f;
    [SerializeField] private float defaultPitch = 45f;
    [SerializeField] private Vector3 defaultPivot = Vector3.zero;
    [SerializeField] private bool resetPivotOnRightClick = false;

    [Header("Smoothing")]
    [SerializeField] private float movementSmoothing = 10f;
    [SerializeField] private float zoomReturnSmoothing = 4f;

    [Header("Debug")]
    [SerializeField] private bool drawBoundsGizmo = true;

    private float desiredDistance;
    private Vector3 desiredPivot;

    private void Awake()
    {
        desiredDistance = distance;
        desiredPivot = pivot;
    }

    private void Start()
    {
        ApplyImmediate();
    }

    private void LateUpdate()
    {
        Mouse mouse = Mouse.current;

        if (mouse != null)
        {
            HandleMouse(mouse);
        }

        ClampPivot();

        float t = 1f - Mathf.Exp(-movementSmoothing * Time.deltaTime);

        pivot = Vector3.Lerp(pivot, desiredPivot, t);
        distance = Mathf.Lerp(distance, desiredDistance, t);

        UpdateCameraTransform();
    }

    private void Orbit(Vector2 mouseDelta)
    {
        yaw += mouseDelta.x * orbitSensitivity;
        pitch -= mouseDelta.y * orbitSensitivity;

        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    private void HandleMouse(Mouse mouse)
    {
        Vector2 mouseDelta = mouse.delta.ReadValue();

        // Left mouse drag: orbit around the current pivot.
        if (mouse.leftButton.isPressed)
        {
            Orbit(mouseDelta);
        }

        // Optional: middle mouse drag still does true pan.
        if (mouse.middleButton.isPressed)
        {
            Pan(mouseDelta);
        }

        // Scroll wheel: zoom in/out.
        float scroll = mouse.scroll.ReadValue().y;

        if (Mathf.Abs(scroll) > 0.01f)
        {
            desiredDistance -= scroll * zoomSpeed * Time.deltaTime;
            desiredDistance = Mathf.Clamp(desiredDistance, minDistance, maxDistance);
        }

        // Right mouse hold: return to default zoom and auto-orbit.
        if (mouse.rightButton.isPressed)
        {
            yaw += autoOrbitSpeed * Time.deltaTime;

            pitch = Mathf.Lerp(
                pitch,
                defaultPitch,
                1f - Mathf.Exp(-zoomReturnSmoothing * Time.deltaTime)
            );

            desiredDistance = Mathf.Lerp(
                desiredDistance,
                defaultDistance,
                1f - Mathf.Exp(-zoomReturnSmoothing * Time.deltaTime)
            );

            if (resetPivotOnRightClick)
            {
                desiredPivot = Vector3.Lerp(
                    desiredPivot,
                    defaultPivot,
                    1f - Mathf.Exp(-zoomReturnSmoothing * Time.deltaTime)
                );
            }
        }
    }

    private void Pan(Vector2 mouseDelta)
    {
        Vector3 right = transform.right;
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;

        if (forward.sqrMagnitude < 0.001f)
        {
            forward = Vector3.forward;
        }

        float distanceFactor = Mathf.Max(distance, 1f) * 0.0025f;

        Vector3 panOffset =
            (-right * mouseDelta.x - forward * mouseDelta.y)
            * panSpeed
            * distanceFactor;

        desiredPivot += panOffset;
    }

    private void ClampPivot()
    {
        desiredPivot.x = Mathf.Clamp(desiredPivot.x, minBoundsXZ.x, maxBoundsXZ.x);
        desiredPivot.z = Mathf.Clamp(desiredPivot.z, minBoundsXZ.y, maxBoundsXZ.y);
    }

    private void UpdateCameraTransform()
    {
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 cameraPosition = pivot + rotation * new Vector3(0f, 0f, -distance);

        transform.SetPositionAndRotation(cameraPosition, rotation);
    }

    [ContextMenu("Capture Current View As Default")]
    private void CaptureCurrentViewAsDefault()
    {
        defaultPivot = pivot;
        defaultDistance = distance;
        defaultPitch = pitch;

        Debug.Log(
            $"Captured camera default: Pivot={defaultPivot}, Distance={defaultDistance}, Pitch={defaultPitch}"
        );
    }

    [ContextMenu("Apply Immediate")]
    private void ApplyImmediate()
    {
        desiredPivot = pivot;
        desiredDistance = distance;
        ClampPivot();
        UpdateCameraTransform();
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawBoundsGizmo)
        {
            return;
        }

        Vector3 center = new Vector3(
            (minBoundsXZ.x + maxBoundsXZ.x) * 0.5f,
            0f,
            (minBoundsXZ.y + maxBoundsXZ.y) * 0.5f
        );

        Vector3 size = new Vector3(
            maxBoundsXZ.x - minBoundsXZ.x,
            2f,
            maxBoundsXZ.y - minBoundsXZ.y
        );

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(center, size);

        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(pivot, 3f);
    }
}