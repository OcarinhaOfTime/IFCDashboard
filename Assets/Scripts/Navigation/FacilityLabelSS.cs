using UnityEngine;
using TMPro;

public class FacilityLabelSS : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private DashboardStatefulOrbitCamera navigationCamera;

    //private CanvasGroup canvasGroup;
    private Transform selectedFacility;
    //float targetAlpha;
    private TextMeshProUGUI buildingNameText;

    //public bool hideAtStartup = true;

    private void Awake()
    {
        buildingNameText = GetComponent<TextMeshProUGUI>();

        if (navigationCamera == null)
        {
            navigationCamera =
                FindFirstObjectByType<DashboardStatefulOrbitCamera>();
        }
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

        buildingNameText.text = "HMP Five Wells";

        /*
         * Remove first so repeated Start/OnEnable calls never create
         * duplicate subscriptions.
         */
        navigationCamera.FacilitySelected -= HandleFacilitySelected;
        navigationCamera.FacilityCleared -= HandleFacilityCleared;

        navigationCamera.FacilitySelected += HandleFacilitySelected;
        navigationCamera.FacilityCleared += HandleFacilityCleared;
    }

    private void HandleFacilitySelected(Transform facility)
    {
        if (facility == null)
        {
            HandleFacilityCleared();
            return;
        }

        selectedFacility = facility;
        print("Selected bruh " + facility.name);

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
    }

    private void HandleFacilityCleared()
    {
        selectedFacility = null;
        buildingNameText.text = "HMP Five Wells";
    }
}
