using System.Globalization;
using System.Runtime.InteropServices;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class WebGLDashboardBridge : MonoBehaviour
{
    [Header("Navigation")]
    [SerializeField]
    private DashboardStatefulOrbitCamera navigationCamera;

    [Header("Site Temperature UI")]
    [Tooltip(
        "TextMeshPro label that displays the temperature for the entire site."
    )]
    [SerializeField]
    private TMP_Text temperatureText;

    [Tooltip(
        "Button attached to the temperature UI. It may be placed on the same " +
        "GameObject as the TMP text."
    )]
    [SerializeField]
    private Button temperatureButton;

    [SerializeField]
    private string unavailableTemperatureText = "-- °C";

    [SerializeField]
    private string temperatureFormat = "{0:0.0} °C";

    [Header("Info UI")]
    [Tooltip(
        "Button that represents the information / website action."
    )]
    [SerializeField]
    private Button infoButton;

    [Tooltip(
        "Website address forwarded to browser JavaScript when Info is clicked."
    )]
    [SerializeField]
    private string infoWebsiteUrl =
        "https://www.futuredecisions.ai/";

    [Header("Browser Notifications")]
    [Tooltip(
        "Notify browser JavaScript whenever the camera focuses on a facility."
    )]
    [SerializeField]
    private bool notifyBrowserOnFacilitySelection = true;

    [Tooltip(
        "Notify browser JavaScript whenever the Unity temperature UI is clicked."
    )]
    [SerializeField]
    private bool notifyBrowserOnTemperatureClick = true;

    [Tooltip(
        "Notify browser JavaScript whenever the Unity Info button is clicked."
    )]
    [SerializeField]
    private bool notifyBrowserOnInfoClick = true;

    [Header("Debug")]
    [SerializeField]
    private bool logMessages = true;

    private FacilityFocusTarget selectedTarget;

    private float siteTemperature;
    private bool hasSiteTemperature;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void FD_NotifyBuildingClicked(
        string buildingId,
        string displayName,
        string websiteRoute
    );

    [DllImport("__Internal")]
    private static extern void FD_NotifyTemperatureClicked(
        string formattedTemperature
    );

    [DllImport("__Internal")]
    private static extern void FD_NotifyInfoClicked(
        string websiteUrl
    );
#endif

    private void Awake()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        /*
         * The surrounding website owns keyboard input. Unity still receives
         * mouse interaction over the canvas.
         */
        WebGLInput.captureAllKeyboardInput = false;
#endif

        if (navigationCamera == null)
        {
            navigationCamera =
                FindFirstObjectByType<DashboardStatefulOrbitCamera>();
        }

        /*
         * For the simple setup, the Button can live directly on the same
         * GameObject as the TMP temperature text.
         */
        if (
            temperatureButton == null &&
            temperatureText != null
        )
        {
            temperatureButton =
                temperatureText.GetComponent<Button>();
        }

        RefreshTemperatureText();
    }

    private void OnEnable()
    {
        SubscribeToNavigation();
        SubscribeToTemperatureButton();
        SubscribeToInfoButton();
    }

    private void Start()
    {
        /*
         * Repeat after scene initialization. The subscription methods remove
         * their listener first, so this never creates duplicates.
         */
        SubscribeToNavigation();
        SubscribeToTemperatureButton();
        SubscribeToInfoButton();
    }

    private void OnDisable()
    {
        UnsubscribeFromNavigation();
        UnsubscribeFromTemperatureButton();
        UnsubscribeFromInfoButton();
    }

    /// <summary>
    /// Browser entry point.
    ///
    /// JavaScript calls:
    ///
    /// unityInstance.SendMessage(
    ///     "WebGLBridge",
    ///     "SetTemperatureFromJs",
    ///     "21.5"
    /// );
    ///
    /// This is one whole-site value; there is no building ID.
    /// </summary>
    public void SetTemperatureFromJs(string valueText)
    {
        if (string.IsNullOrWhiteSpace(valueText))
        {
            Debug.LogWarning(
                "SetTemperatureFromJs received an empty value.",
                this
            );

            return;
        }

        string normalizedValue =
            valueText.Trim().Replace(',', '.');

        bool parsed =
            float.TryParse(
                normalizedValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float value
            );

        if (!parsed)
        {
            Debug.LogWarning(
                $"Could not parse site temperature '{valueText}'.",
                this
            );

            return;
        }

        siteTemperature = value;
        hasSiteTemperature = true;

        RefreshTemperatureText();

        if (logMessages)
        {
            Debug.Log(
                $"Received whole-site temperature from JavaScript: " +
                $"{siteTemperature:0.0} °C.",
                this
            );
        }
    }

    /// <summary>
    /// Public method that can also be connected manually to a uGUI Button.
    /// </summary>
    public void NotifyTemperatureClickedToJs()
    {
        if (!notifyBrowserOnTemperatureClick)
        {
            return;
        }

        string formattedTemperature =
            hasSiteTemperature
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    temperatureFormat,
                    siteTemperature
                )
                : unavailableTemperatureText;

#if UNITY_WEBGL && !UNITY_EDITOR
        FD_NotifyTemperatureClicked(formattedTemperature);
#else
        if (logMessages)
        {
            Debug.Log(
                $"[Editor simulation] Unity would notify JavaScript that " +
                $"the temperature UI was clicked: " +
                $"'{formattedTemperature}'.",
                this
            );
        }
#endif
    }

    /// <summary>
    /// Public method connected to the Unity Info button.
    /// </summary>
    public void NotifyInfoClickedToJs()
    {
        if (!notifyBrowserOnInfoClick)
        {
            return;
        }

        string resolvedUrl =
            string.IsNullOrWhiteSpace(infoWebsiteUrl)
                ? "https://www.futuredecisions.ai/"
                : infoWebsiteUrl.Trim();

#if UNITY_WEBGL && !UNITY_EDITOR
        FD_NotifyInfoClicked(resolvedUrl);
#else
        if (logMessages)
        {
            Debug.Log(
                $"[Editor simulation] Unity would notify JavaScript that " +
                $"the Info button was clicked. URL='{resolvedUrl}'.",
                this
            );
        }
#endif
    }

    /// <summary>
    /// Optional manual building-notification hook for a Unity UI Button.
    /// </summary>
    public void NotifyCurrentSelectionToJs()
    {
        if (selectedTarget == null)
        {
            if (logMessages)
            {
                Debug.Log(
                    "No facility is currently selected.",
                    this
                );
            }

            return;
        }

        NotifyBuildingSelected(selectedTarget);
    }

    private void SubscribeToNavigation()
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

        navigationCamera.FacilitySelected -=
            HandleFacilitySelected;

        navigationCamera.FacilityCleared -=
            HandleFacilityCleared;

        navigationCamera.FacilitySelected +=
            HandleFacilitySelected;

        navigationCamera.FacilityCleared +=
            HandleFacilityCleared;
    }

    private void UnsubscribeFromNavigation()
    {
        if (navigationCamera == null)
        {
            return;
        }

        navigationCamera.FacilitySelected -=
            HandleFacilitySelected;

        navigationCamera.FacilityCleared -=
            HandleFacilityCleared;
    }

    private void SubscribeToTemperatureButton()
    {
        if (temperatureButton == null)
        {
            return;
        }

        temperatureButton.onClick.RemoveListener(
            NotifyTemperatureClickedToJs
        );

        temperatureButton.onClick.AddListener(
            NotifyTemperatureClickedToJs
        );
    }

    private void UnsubscribeFromTemperatureButton()
    {
        if (temperatureButton == null)
        {
            return;
        }

        temperatureButton.onClick.RemoveListener(
            NotifyTemperatureClickedToJs
        );
    }

    private void SubscribeToInfoButton()
    {
        if (infoButton == null)
        {
            return;
        }

        infoButton.onClick.RemoveListener(
            NotifyInfoClickedToJs
        );

        infoButton.onClick.AddListener(
            NotifyInfoClickedToJs
        );
    }

    private void UnsubscribeFromInfoButton()
    {
        if (infoButton == null)
        {
            return;
        }

        infoButton.onClick.RemoveListener(
            NotifyInfoClickedToJs
        );
    }

    private void HandleFacilitySelected(
        Transform facilityTransform)
    {
        if (facilityTransform == null)
        {
            HandleFacilityCleared();
            return;
        }

        selectedTarget =
            facilityTransform.GetComponent<FacilityFocusTarget>();

        if (
            notifyBrowserOnFacilitySelection &&
            selectedTarget != null
        )
        {
            NotifyBuildingSelected(selectedTarget);
        }
    }

    private void HandleFacilityCleared()
    {
        selectedTarget = null;
    }

    private void RefreshTemperatureText()
    {
        if (temperatureText == null)
        {
            return;
        }

        temperatureText.text =
            hasSiteTemperature
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    temperatureFormat,
                    siteTemperature
                )
                : unavailableTemperatureText;
    }

    private void NotifyBuildingSelected(
        FacilityFocusTarget target)
    {
        if (target == null)
        {
            return;
        }

        string buildingId =
            target.FacilityId ?? string.Empty;

        string displayName =
            target.DisplayName ?? buildingId;

        string websiteRoute =
            target.WebsiteRoute ?? string.Empty;

#if UNITY_WEBGL && !UNITY_EDITOR
        FD_NotifyBuildingClicked(
            buildingId,
            displayName,
            websiteRoute
        );
#else
        if (logMessages)
        {
            Debug.Log(
                $"[Editor simulation] Unity would notify JavaScript: " +
                $"buildingId='{buildingId}', " +
                $"displayName='{displayName}', " +
                $"websiteRoute='{websiteRoute}'.",
                target
            );
        }
#endif
    }
}
