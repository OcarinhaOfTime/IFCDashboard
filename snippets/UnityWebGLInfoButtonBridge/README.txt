UNITY WEBGL INFO BUTTON BRIDGE
==============================

This update keeps the existing:

- SetTemperature(value)
- building selection alert
- temperature UI alert

and adds an Info-button alert.


FILES TO REPLACE
----------------

Assets/Scripts/Web/WebGLDashboardBridge.cs

Assets/Plugins/WebGL/FutureDecisionsDashboard.jslib

Assets/WebGLTemplates/FutureDecisions/index.html


UNITY SETUP
-----------

1. Select the GameObject containing the Info icon.

2. Add a Unity UI Button component if it does not have one.

3. Ensure the icon's Image component has:

       Raycast Target: On

4. On WebGLBridge / WebGLDashboardBridge, assign:

       Info Button:
           your Info icon Button

       Info Website Url:
           https://www.futuredecisions.ai/

5. The Canvas must have GraphicRaycaster enabled and the scene must contain
   an EventSystem.

No manual Button OnClick entry is required because the bridge subscribes to
the assigned Button automatically.

You can instead wire the Button manually to:

    WebGLDashboardBridge.NotifyInfoClickedToJs


WEB CALLBACK
------------

The .jslib plug-in calls:

    window.onUnityInfoClicked(websiteUrl);

The supplied index.html shows:

    alert(
        "Future Decisions information clicked.\n" +
        websiteUrl
    );

Later, replace the alert with:

    window.open(
        websiteUrl,
        "_blank",
        "noopener,noreferrer"
    );
