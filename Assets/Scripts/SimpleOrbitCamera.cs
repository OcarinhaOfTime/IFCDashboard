using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
public class SimpleOrbitCamera : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;

    [Header("Orbit")]
    [SerializeField] private float distance = 250f;
    [SerializeField] private float minDistance = 40f;
    [SerializeField] private float maxDistance = 800f;

    [SerializeField] private float yaw = 45f;
    [SerializeField] private float pitch = 35f;
    [SerializeField] private float minPitch = 10f;
    [SerializeField] private float maxPitch = 80f;

    [Header("Input")]
    [SerializeField] private float orbitSensitivity = 0.2f;
    [SerializeField] private float zoomSensitivity = 80f;

    [Header("Presentation")]
    [SerializeField] private bool autoOrbit = false;
    [SerializeField] private float autoOrbitSpeed = 4f;
    [SerializeField] private float smoothing = 10f;

    private Vector3 desiredPosition;
    private Quaternion desiredRotation;

    private void Start()
    {
        if (target == null)
        {
            Debug.LogWarning("SimpleOrbitCamera has no target assigned.");
            return;
        }

        Vector3 offset = transform.position - target.position;
        distance = Mathf.Clamp(offset.magnitude, minDistance, maxDistance);

        Vector3 angles = transform.eulerAngles;
        pitch = NormalizeAngle(angles.x);
        yaw = angles.y;
    }

    private void LateUpdate()
    {
        if (target == null)
        {
            return;
        }

        HandleInput();
        UpdateCameraTransform();
    }

    private void HandleInput()
    {
        Mouse mouse = Mouse.current;

        if (mouse == null)
        {
            return;
        }

        bool isRightDragging = mouse.rightButton.isPressed;

        if (isRightDragging)
        {
            Vector2 delta = mouse.delta.ReadValue();

            yaw += delta.x * orbitSensitivity;
            pitch -= delta.y * orbitSensitivity;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }
        else if (autoOrbit)
        {
            yaw += autoOrbitSpeed * Time.deltaTime;
        }

        float scroll = mouse.scroll.ReadValue().y;

        if (Mathf.Abs(scroll) > 0.01f)
        {
            distance -= scroll * zoomSensitivity * Time.deltaTime;
            distance = Mathf.Clamp(distance, minDistance, maxDistance);
        }
    }

    private void UpdateCameraTransform()
    {
        desiredRotation = Quaternion.Euler(pitch, yaw, 0f);

        Vector3 direction = desiredRotation * Vector3.back;
        desiredPosition = target.position + direction * distance;

        float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);

        transform.position = Vector3.Lerp(transform.position, desiredPosition, t);
        transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, t);
    }

    private float NormalizeAngle(float angle)
    {
        while (angle > 180f)
        {
            angle -= 360f;
        }

        while (angle < -180f)
        {
            angle += 360f;
        }

        return angle;
    }
}