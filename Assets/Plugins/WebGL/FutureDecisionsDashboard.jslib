mergeInto(LibraryManager.library, {
    FD_NotifyBuildingClicked: function (
        buildingIdPointer,
        displayNamePointer,
        websiteRoutePointer
    ) {
        var buildingId =
            UTF8ToString(buildingIdPointer);

        var displayName =
            UTF8ToString(displayNamePointer);

        var websiteRoute =
            UTF8ToString(websiteRoutePointer);

        if (
            typeof window.onUnityBuildingClicked === "function"
        ) {
            window.onUnityBuildingClicked(
                buildingId,
                displayName,
                websiteRoute
            );

            return;
        }

        if (
            typeof window.CustomEvent === "function" &&
            typeof window.dispatchEvent === "function"
        ) {
            window.dispatchEvent(
                new CustomEvent(
                    "unity-building-clicked",
                    {
                        detail: {
                            buildingId: buildingId,
                            displayName: displayName,
                            websiteRoute: websiteRoute
                        }
                    }
                )
            );

            return;
        }

        console.log(
            "Unity selected building:",
            buildingId,
            displayName,
            websiteRoute
        );
    },

    FD_NotifyTemperatureClicked: function (
        formattedTemperaturePointer
    ) {
        var formattedTemperature =
            UTF8ToString(formattedTemperaturePointer);

        if (
            typeof window.onUnityTemperatureClicked === "function"
        ) {
            window.onUnityTemperatureClicked(
                formattedTemperature
            );

            return;
        }

        if (
            typeof window.CustomEvent === "function" &&
            typeof window.dispatchEvent === "function"
        ) {
            window.dispatchEvent(
                new CustomEvent(
                    "unity-temperature-clicked",
                    {
                        detail: {
                            temperature: formattedTemperature
                        }
                    }
                )
            );

            return;
        }

        console.log(
            "Unity temperature UI clicked:",
            formattedTemperature
        );
    },

    FD_NotifyInfoClicked: function (
        websiteUrlPointer
    ) {
        var websiteUrl =
            UTF8ToString(websiteUrlPointer);

        if (
            typeof window.onUnityInfoClicked === "function"
        ) {
            window.onUnityInfoClicked(websiteUrl);
            return;
        }

        if (
            typeof window.CustomEvent === "function" &&
            typeof window.dispatchEvent === "function"
        ) {
            window.dispatchEvent(
                new CustomEvent(
                    "unity-info-clicked",
                    {
                        detail: {
                            websiteUrl: websiteUrl
                        }
                    }
                )
            );

            return;
        }

        console.log(
            "Unity Info button clicked:",
            websiteUrl
        );
    }
});
