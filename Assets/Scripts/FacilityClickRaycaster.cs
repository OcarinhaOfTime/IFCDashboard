using UnityEngine;
using UnityEngine.InputSystem;

public class FacilityClickRaycaster : MonoBehaviour
{
    [Header("Raycast")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] private float maxDistance = 10000f;
    [SerializeField] private LayerMask raycastMask = ~0;

    [Header("Debug")]
    [SerializeField] private bool logMisses = false;
    [SerializeField] private bool drawDebugRay = true;
    [SerializeField] private float debugRayDuration = 1.0f;

    private void Awake()
    {
        if (targetCamera == null)
        {
            targetCamera = GetComponent<Camera>();
        }

        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }
    }

    private void Update()
    {
        if (Mouse.current == null)
        {
            return;
        }

        if (!Mouse.current.leftButton.wasPressedThisFrame)
        {
            return;
        }

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = targetCamera.ScreenPointToRay(mousePosition);

        if (drawDebugRay)
        {
            Debug.DrawRay(ray.origin, ray.direction * maxDistance, Color.yellow, debugRayDuration);
        }

        bool hitSomething = Physics.Raycast(
            ray,
            out RaycastHit hit,
            maxDistance,
            raycastMask,
            QueryTriggerInteraction.Collide
        );

        if (!hitSomething)
        {
            if (logMisses)
            {
                Debug.Log("Facility raycast missed.");
            }

            return;
        }

        FacilitySelectionTarget target =
            hit.collider.GetComponent<FacilitySelectionTarget>();

        if (target == null)
        {
            target = hit.collider.GetComponentInParent<FacilitySelectionTarget>();
        }

        if (target == null)
        {
            Debug.Log($"Hit object, but it is not a facility: {hit.collider.gameObject.name}");
            return;
        }

        string facilityName = !string.IsNullOrWhiteSpace(target.facilityLabel)
            ? target.facilityLabel
            : target.facilityRoot != null
                ? target.facilityRoot.name
                : target.gameObject.name;

        Debug.Log($"Selected facility: {facilityName}");
    }
}