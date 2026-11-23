# Plan: Stage 6 Unity side (first-person teleoperation and camera panel)

## Context
The Stage 6 ROS side is done and was validated live on 2026-11-16 (WSL `5dc5edc`). `twist_mux` routes `/cmd_vel_teleop` with priority 100, above Nav2's `cmd_vel_nav_safe`, into `/cmd_vel`. A nonzero teleop Twist auto-pauses the mission (`paused (teleop)`). `/hmi/mission_state` gained a `teleop` flag, and the bridge publishes `/camera/image_raw/compressed` (JPEG 320x240, about 8 Hz, top-down rows) only while `/camera/enable` is true.

The VR operator still cannot drive the robot or see its camera. This task adds both:
- a teleop mode in Unity, toggled with **X** or a menu button,
- a floating **camera panel** that behaves like the operations panel,
- stick driving that publishes `/cmd_vel_teleop`.

The task gets its own `STAGE6_TELEOP_PLAN.md` in the Windows repo root, created from this plan in step 0. It is the source of truth, with an execution status section.

User decisions (2026-11-23):
1. **Activation:** X (left controller, free today) toggles teleop mode, and a "Teleop" button on the Mission page does the same. Teleop mode = camera panel open + driving enabled. Closing the panel leaves teleop.
2. **Controls, sticks only:**
   - The left stick Y drives forward and back (`linear.x`).
   - The right stick X rotates the robot: left is counter-clockwise (`angular.z > 0`), right is clockwise.
   - There is **no grip deadman**. The spring-centred sticks act as the deadman: commands are published only while a stick is deflected, and one zero Twist is sent when both return to centre.
   - This deviates from the README wording "while the deadman (grip) is held". ROS behaviour is unaffected, since the ROS side only sees Twists.
3. **Player locomotion is blocked for the whole teleop mode.** `LocomotionInputGate` is held from open to close, so the Meta slide, turn and teleport interactors are off and fly mode stops reading the sticks.
4. **Camera panel works like the operations panel:**
   - It opens upright in front of the head and then stays fixed in the world.
   - A trigger ray grab on the frame moves, yaws and pitches it (reuse `PanelGrabTransformer`).
   - It shows the 4:3 image, a teleop and mission status line, the commanded speed, and Resume mission and Close buttons.

## Verified facts used by the design
- **Contract** (WSL `ros2_ws/src/vr_robot_pkg/README.md`):
  - `/cmd_vel_teleop` is a `geometry_msgs/Twist` at >= 10 Hz while driving, followed by one zero Twist.
  - Unity clamps `|linear.x| <= 0.5` m/s and `|angular.z| <= 1.5` rad/s. There is no ROS-side clamp, and Unity never publishes `/cmd_vel`.
  - `twist_mux` times out after 0.5 s and the bridge watchdog stops the robot 0.5 s after the last `/cmd_vel`.
  - `/camera/enable` is a `std_msgs/Bool`, true on open and false on close; the camera is off by default.
  - `/hmi/mission_state` JSON is `{state, mode, remaining, message, teleop}`.
- **Bridge:** it flips CoppeliaSim's bottom-up rows before encoding (`bridge_math.flip_rows`), so the JPEG is standard top-down and `ImageConversion.LoadImage` shows it the right way up.
- **`RosHmiClient`** (`Scripts/HMI/RosHmiClient.cs`) is the single ROS I/O owner:
  - It registers its publishers and subscriptions in `Start` and gates sends with `CanSend`.
  - It raises its events on the main thread.
  - It has no camera or teleop handling yet.
- **`MissionState`** (`Scripts/HMI/HmiJson.cs`) is parsed with `JsonUtility`. Adding `public bool teleop;` is enough, and old payloads without the field parse as false.
- **Button bindings in code:**
  - Left menu: VR menu.
  - Y (`Button.Four`): `OperationsPanel`.
  - Right trigger: `HmiPointer`.
  - Sticks: Meta locomotion (left slide, right turn and teleport) and `VrFlyMode`.
  - X (`Button.Three`) and the grips are free.
  - No `DistanceGrabInteractable` or `HandGrabInteractable` exists in `_Project` prefabs or scenes.
- **`LocomotionInputGate`** (`Scripts/XR/LocomotionInputGate.cs`) is multi-owner: `Acquire` and `Release` are per owner. `VrFlyMode` skips input while `IsHeldByOtherThan(this)`. The menu, `TacticalToolController` and `AppFlowController` already use it.
- **Reusable `OperationsPanel` patterns:**
  - Root always active, with only the content and the `_activeWhileOpen` objects toggled.
  - Frame bars are trigger `BoxCollider` + `ColliderSurface` + `RayInteractable` + `Grabbable` with `PanelGrabTransformer`.
  - Ray interactables need their `Start` while active (content active for the first frame, then closed).
  - All hologram colliders are triggers.
  - Placement with `PanelMath.FitDistance` in front of `CenterEyeAnchor`.
  - `ISDK_RayCanvasInteraction` toolbar using UI Set prefabs with `UiTextRole`.

## UI design (must follow the project's established style, Meta UI Set)
All new UI follows `README_UNITY.md`, section "UI theme (Meta UI Set)", the same way `VrMenu.prefab`, `StartMenu.prefab` and the `OperationsPanel` toolbar do. No plain uGUI buttons and no ad-hoc colours or fonts.

- **Theme:**
  - The camera panel's canvas gets Meta's `UIThemeManager` with `UIThemeQuest_Dark`.
  - The backplate is tagged `QDSUIBackplateGradient` (material `RoundedBoxGradientUIDark`) and sections are tagged `QDSUISection`.
  - Rounded corners use `RoundedBoxUI` + `RoundedBoxUIProperties`.
  - Canvas scale 0.001 (UI Set units). Pages and sections use `VerticalLayoutGroup` / `HorizontalLayoutGroup`.
- **Components**, as nested instances of the UI Set prefabs from `Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Objects/UISet`:
  - Resume mission is a Primary button.
  - Close teleop is Secondary, or Destructive if it reads better as "stop".
  - The Mission page "Start teleop / Stop teleop" button is Primary, like "Open panel".
  - Icons come from `Runtime/Sample/Textures/OCUIIconSet`.
- **Text:** every TMP text, both canvas and world-space status, gets a `UiTextRole` (Title / Heading / Body / Caption / Label from `UI/UiTextStyles.asset`). Otherwise `UIThemeManager` leaves the theme font on it.
- **State display:**
  - `StatusChip` (tagged `QDSUISharedThemeColor`, so the theme leaves it alone) for the teleop state (Driving = Ok, Idle = Neutral, No camera = Warn, ROS disconnected = Error) and the mission state.
  - The chip mapping is the same as on the Mission page.
- **Toggles:** if a toggle is needed, set it through `UiToggles.Set`, never `SetIsOnWithoutNotify`.
- **Hologram parts** (frame bars, screen quad) reuse the OperationsPanel materials and dimensions (`Materials/HMI/`, frame width 0.02 m), so both panels look like one family.
- **Editable in the Editor:** every visual (screen, frame, status, toolbar, placeholder) exists as an object in `CameraPanel.prefab`, not built only at runtime. Code only feeds textures and text.
- The "UI theme" section of `README_UNITY.md` is updated to list the camera panel among the UI Set panels.

## Architecture
New and changed files under `My project/Assets/_Project/`:

| File | Change |
|---|---|
| `Scripts/Core/TeleopMath.cs` (new) | Pure mapping: per-axis deadzone with rescale (default 0.15), optional response exponent, `ToTwist(leftY, rightX, maxLin, maxAng)` returning `(linear, angular)` in ROS convention (`angular = -rightX * maxAng`). The maxima are clamped to the contract limits 0.5 / 1.5, and `IsZero` uses a small epsilon. |
| `Tests/EditMode/TeleopMathTests.cs` (new) | Deadzone and rescale, sign convention (left = CCW = +z), clamp to contract limits even with larger serialized maxima, zero detection. |
| `Scripts/HMI/HmiJson.cs` | `MissionState.teleop` (+ a case in `HmiJsonTests`). |
| `Scripts/HMI/RosHmiClient.cs` | Publishers: `/cmd_vel_teleop` (`TwistMsg`) and `/camera/enable` (`BoolMsg`). Subscription: `/camera/image_raw/compressed` (`CompressedImageMsg`), caching the latest bytes plus a timestamp and raising `CameraFrameReceived`. New methods `SendTeleop(float lin, float ang)` and `SetCameraEnabled(bool)`; the latter remembers the requested state and **re-publishes it on reconnect**, because the endpoint respawns. Topic names are serialized fields, as with the existing topics. |
| `Scripts/HMI/FirstPersonTeleopController.cs` (new) | Teleop mode owner. Details below. |
| `Scripts/HMI/CameraPanel.cs` (new) | Camera panel view. Details below. |
| `Prefabs/CameraPanel.prefab` (new) | Built through MCP / editor script following the OperationsPanel prefab structure, editable in the Editor. |
| `Materials/HMI/CameraScreen.mat` (new) | Unlit material for the screen quad. |
| `Scripts/HMI/VrMissionPage.cs` + `Prefabs/VrMenu.prefab` | "Start teleop" / "Stop teleop" button with label (UI Set button, `UiTextRole`). |
| `Scenes/VrScene.unity` | `CameraPanel` instance and `FirstPersonTeleopController` (on the `HMI` object), with references to the gate, the panel and `CenterEyeAnchor`. |

**`FirstPersonTeleopController`:**
- **Mode:** `IsActive`, `Open()`, `Close()`, `Toggle()` and the `ActiveChanged` event. X (`OVRInput.Button.Three`, serialized) toggles the mode, and is ignored while the start flow holds the gate before spawn.
- **On open:**
  - acquire the gate,
  - open the panel,
  - call `SetCameraEnabled(true)`.
- **On close:**
  - send a zero Twist if the last command sent was nonzero,
  - call `SetCameraEnabled(false)`,
  - close the panel,
  - release the gate.
- **Each frame while active:**
  - Read the left stick Y and the right stick X with `OVRInput`, then apply `TeleopMath`.
  - While the command is nonzero, publish at a fixed rate (default 15 Hz, unscaled time, and immediately on the first deflection).
  - On the transition to zero, publish exactly one zero Twist.
  - Expose `CurrentLinear`, `CurrentAngular` and `IsDriving` for the panel.
- **Safety:** treat the command as zero (and send the zero Twist) on:
  - `OnDisable` and `OnApplicationPause`,
  - loss of input focus (`OVRManager.hasInputFocus` false, or `OnApplicationFocus(false)`),
  - ROS disconnect (no send while disconnected; the ROS watchdogs stop the robot).

**`CameraPanel`:**
- `Open()` / `Close()` mirror `OperationsPanel`:
  - the content is active for the first frame, then closed,
  - all colliders are triggers,
  - it is placed upright with `PanelMath.FitDistance` at eye level,
  - a close button and the `OpenChanged` event.
- One reused `Texture2D`. The latest JPEG is decoded once per frame in `Update` with `ImageConversion.LoadImage`, dropping intermediate frames.
- "No camera image" placeholder when no frame arrived for 1.5 s while open.
- Status line from `RosHmiClient`, for example `Teleop: driving 0.32 m/s, -0.40 rad/s | Mission: PAUSED (paused (teleop))`, plus ROS and bridge state.
- The Resume button is enabled when the mission is paused. It sends `RESUME` and the user must keep the sticks centred, otherwise the mission auto-pauses again.

## Steps (each ends with a check; delegation in brackets)
0. Create `STAGE6_TELEOP_PLAN.md` from this plan.
1. `TeleopMath` + `TeleopMathTests`, and `MissionState.teleop` + its test. Check: compile and EditMode suite green (41 tests + new). [sonnet subagent for the code and tests]
2. `RosHmiClient` extension. Check: compile, then Play mode against the live stack. `ros2 topic echo /camera/enable` sees true/false from a temporary `Unity_RunCommand` call, and `CameraFrameReceived` fires (frame size logged).
3. `FirstPersonTeleopController` (no panel yet). Check in the simulator with the XR Operator:
   - X toggles the mode, and Meta locomotion is off while it is active.
   - With the left stick Y held at 1, `ros2 topic hz /cmd_vel_teleop` reads about 15 Hz and the echo shows `linear.x` 0.5.
   - With the right stick X held at -1, `angular.z` is +1.5.
   - On release, exactly one zero message.
4. `CameraPanel.prefab` + `CameraScreen.mat` + `CameraPanel.cs`, the scene instance and the wiring. The toolbar and status follow "UI design" above, starting from the OperationsPanel toolbar and the UI Set prefabs. Check:
   - In Play mode the buttons, chips and fonts match the menus (screenshot side by side with the Mission page).
   - The panel opens in front of the head and shows the robot camera image the right way up (screenshot by rendering `CenterEyeAnchor` into a RenderTexture).
   - The frame ray grab moves and yaws it.
   - Close leaves teleop.
5. Mission page button in `VrMenu.prefab` + `VrMissionPage`. It is a UI Set Primary button nested from the package, with a `UiTextRole` Label, placed in the existing panel section next to "Open panel"; the menu height is re-fitted if needed. Check: the button label follows the mode and toggles it, and the page still looks themed in Play mode.
6. End-to-end validation (below), then review [sonnet review of the new HMI code], fixes, docs:
   - `README_UNITY.md`: teleop section, controls, `CameraPanel`.
   - `CLAUDE.md`: status and lessons.
   - `IMPLEMENTATION_PLAN.md` and `README.md`: Stage 6 done.
   - WSL README row for `/cmd_vel_teleop`: deadman wording becomes "while a stick is deflected". Edited from Windows; the WSL agent or the user commits it there.

   Then commit in the Windows repo.

## Verification (end-to-end; CoppeliaSim with `scene_house.ttt` + `ros2 launch vr_robot_pkg vr_robot_bringup.launch.py map:=scene_house` + Unity Play mode in the Meta XR Simulator)
Read `tools/xr_test_harness/README.md` and the `meta-xr-operator` skills first. Use the 3-attempt policy.
1. EditMode tests all pass, and the console has no errors after entering Play mode.
2. **Camera on demand:**
   - Before opening, `ros2 topic hz /camera/image_raw/compressed` shows no messages.
   - After X, about 8 Hz and the panel shows the image.
   - After close, it stops again.
3. **Driving:**
   - Hold the left stick Y with `openxr_set_controller_input` (`Thumbstick`, `sub_component` `Y`).
   - The robot moves forward in CoppeliaSim and the twin (`/hmi/robot_pose` changes), and `/hmi/mission_state` reports `teleop: true`.
   - The right stick X rotates the robot: X = -1 turns it counter-clockwise (yaw increases in ROS).
   - On release the robot stops within 0.5 s and the mission state reports `teleop: false` within about 0.6 s.
4. **Auto-pause:**
   - Send a goal from the operations panel (NAVIGATING).
   - Open teleop and push a stick: the state becomes PAUSED with `paused (teleop)`.
   - Release, then Resume on the camera panel: the state returns to NAVIGATING and the robot reaches the goal.
5. **Locomotion lock:** while teleop is open, the left stick does not move the player and the right stick neither snap-turns nor teleports. After close, locomotion works again.
6. **Safety:**
   - Stop the ROS stack while teleop is open: no exceptions, status shows disconnected.
   - Restart it: it reconnects and the camera is re-enabled automatically.
   - Leaving Play mode with the panel open leaves the robot stopped (watchdog) and the camera off on the next start.

## Execution status
- [x] Step 0: plan document created (2026-11-23).
- [x] Step 1: `TeleopMath` + `TeleopMathTests` (9 tests), `MissionState.teleop` + 2 tests (sonnet subagent). EditMode 62/62.
- [x] Step 2: `RosHmiClient`: `/cmd_vel_teleop`, `/camera/enable` (re-sent on reconnect, off on destroy), `/camera/image_raw/compressed` (`LastCameraFrame`, `CameraFrameReceived`). Live: bridge logged "Camera enabled"/"Camera disabled", Unity received JPEG frames (~6 KB), the topic stops after disable. Observation: camera ~2 Hz instead of ~8 Hz; the bridge cycle runs at ~5 Hz (mean exchange 189 ms) with the camera on, so the limit is on the CoppeliaSim side, not Unity.
- [x] Step 3: `FirstPersonTeleopController` on the `HMI` object (gate + `AppFlowController` refs). Live (XR Operator): X opens/closes; left stick Y=1 -> 45 x `linear.x` 0.5 then exactly one zero, odometry advanced 0.34 m; right stick X=-1 -> `angular.z` +1.5; with teleop open the rig neither moved nor turned, after close it walked 2.7 m and snap-turned 45 deg. Measured 11 Hz at ~20 fps (unfocused Editor) with `now + period` scheduling: changed to an accumulated schedule so the average holds 15 Hz at low frame rates.
- [x] Step 4: `CameraPanel.cs`, `Prefabs/CameraPanel.prefab` (built from a copy of `OperationsPanel.prefab`: frame ray grab, UI Set toolbar with `UIThemeManager`, nested UI Set buttons; map parts, handles and scaler removed; `Screen` quad + `Materials/HMI/CameraScreen.mat`, `Placeholder`, Teleop / Mission chips with captions copied from the Mission page, command line, "Resume mission" as a UI Set Primary with the same overrides as "Open operation panel", Close), scene instance with `_head` and `_teleop`. Live: X opened it 0.86 m ahead, the robot image upright; chips Driving (green) / Paused (amber); frame ray grab moved it 0.15 m and yawed it 25 deg (pitch 0); Close ended teleop (gate released, camera off). Layout fixes after the first screenshots: captions on the chips (two "Idle" chips were ambiguous), screen 0.48 -> 0.64 m, command line in its own row, toolbar as wide as the frame (`ApplyLayout`).
- [x] Step 5: Mission page button wired (`VrMissionPage._teleopButton/_teleopLabel`, "Start teleop" / "Stop teleop"). Live: ray click on it closed teleop; the label followed the mode.
- [x] Step 6: end-to-end validation, review, docs.
  - Auto-pause: goal (8.0, 2.2) NAVIGATING -> teleop stick -> `PAUSED` "paused (teleop)", `teleop: true` -> release -> `teleop: false` -> ray click on the panel's Resume -> `NAVIGATING` "resumed: 1 pose(s) left".
  - Rate after the accumulator fix: 16.3-16.8 Hz, 60 x `linear.x` 0.5 then one zero.
  - Endpoint restart: Unity stayed connected and camera frames kept arriving.
  - Found and fixed: the "camera off" sent when Play mode exits never reached ROS (the camera stayed on into the next session). `RosHmiClient` now publishes the requested camera state on every connection, false included; verified: camera on, leave Play mode, enter again -> bridge logged "Camera disabled" at connect.
  - Review (sonnet): no serious bugs. Fixed: teleop closes when the start flow leaves `Playing`; driving state dropped while ROS is disconnected; focus/pause suspension in player builds (skipped in the Editor, whose focus loss does not stop the simulator). Not changed: driving while the VR menu is open (the menu does not use the sticks); Resume leaves teleop open (keep the sticks centred, or the mission pauses again).
  - EditMode 62/62. Console: only Meta's AgentBridge port error (unrelated).
  - Docs: `README_UNITY.md` (teleop section, UI theme), `CLAUDE.md`, `IMPLEMENTATION_PLAN.md`, `README.md`; WSL README `/cmd_vel_teleop` row reworded (stick deadman), uncommitted in the WSL repo.

## Deviations and observations
- Mission page: the existing (disabled, Stage 5) Secondary "First-person teleop" button with the gamepad icon was wired instead of adding a new Primary button next to "Open panel".
- Stick axes: `OVRInput.RawAxis2D` (TeleopMath alone owns the deadzone and curve). In the Meta XR Simulator a thumbstick input of 0.6 reads ~0.3 with both the raw and the virtual axes (1 reads 1), so partial speeds look lower in the simulator.
- Camera rate: ~2 Hz instead of ~8 Hz in this session; the bridge cycle ran at 5-8 Hz (mean exchange 120-190 ms), so the limit is on the CoppeliaSim side.
- Nav2 goals failing with error code 102 (2026-11-23 investigation; first attributed to a slow simulation, which was wrong):
  - Cause: the WSL2 VM clock ran slow (91-97 % of real time, measured against a Windows `Stopwatch`) and `systemd-timesyncd`, stuck at its 32 s minimum poll, corrected it with forward steps of 2.3-3.6 s. After each step the slam_toolbox `map -> odom` stamp (scan time + `transform_timeout` 1.0 s) was 2-3 s old, so `controller_server` aborted with "Transform data too old when converting from map to odom" (`FollowPath` error 102 = `TF_ERROR`); `collision_monitor` reported the scan as old as the step size right before each failure.
  - Not the teleop commits: the failure reproduced with Unity stopped and no `/cmd_vel_teleop` traffic, and the velocity chain (controller -> smoother -> collision_monitor -> `twist_mux` -> `/cmd_vel`) passed every command 1:1 at 20 Hz. The WSL journal shows no clock steps in the 2026-11-15 boots and steps from the 2026-11-16 boot on.
  - Not fixed by `wsl --shutdown`, the WSL 3.0.5 update (kernel 6.18) or `hwclock -s` (they do not change the clock rate).
  - Fix (WSL side, done by the user): `chrony` instead of `systemd-timesyncd` (`sudo apt install -y chrony`, `/etc/chrony/conf.d/wsl.conf` = `makestep 1 3`). It follows the host through the Hyper-V PHC with no steps after start-up. Afterwards two goals across the house (4.9 m and 4.8 m) succeeded with zero TF errors and a `map -> odom` age never above -0.1 s.
- Unity in Play mode loads CoppeliaSim: the bridge drops from ~10-17 Hz to ~5-10 Hz, and to 3.6 Hz (exchanges up to 1.9 s) while the scene is being loaded. This limits the camera rate; it did not cause the Nav2 failures.
