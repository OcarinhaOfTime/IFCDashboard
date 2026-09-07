Dashboard Stateful Orbit Camera — Starting View Capture
========================================================

This is a drop-in replacement for DashboardStatefulOrbitCamera.cs.

New context menu
----------------

Select the Main Camera component, open the component's three-dot menu, then:

    Starting View
    → Capture Current Transform As Overview

Edit Mode behavior
------------------

You may manually position and rotate the camera in the Scene view, then run
the context-menu command. The script converts that Transform into its orbit
representation:

- Overview Pivot
- Overview Yaw
- Overview Pitch
- Overview Distance

The current camera position and viewing angle are preserved when Play Mode
starts.

Because an orbit camera needs a pivot and radius, the command uses the
previous Overview Pivot to estimate an orbit distance, then places the new
pivot directly along the camera's viewing direction.

Play Mode behavior
------------------

When invoked during Play Mode, the command captures the live orbit state.

Persistence
-----------

The Editor implementation records Undo, marks the component dirty, and
records prefab-instance property changes so the captured values are saved
with the scene.
