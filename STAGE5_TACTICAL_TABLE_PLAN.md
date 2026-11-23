# Plan: Stage 4 Spatial Menu + Stage 5 Unity side (Holographic Tactical Table)


## Context
Stages 1–3, Stage 4 part 1 (VR locomotion, menu shell, start flow) and the Stage 5 ROS side (`mission_supervisor`) are done. The operator still cannot control the robot from VR. This task adds the HMI. The VR menu gains Mission and Map pages. A grabbable Holographic Tactical Table shows the SLAM map, the robot, the plan and the no-go zones. Three tools (point-and-click goal, path painting, no-go brush) work on the table **and** directly on the floor of the 3D twin. All of it talks only to `mission_supervisor` through ROS-TCP-Connector, following the contract in the WSL repo `ros2_ws/src/vr_robot_pkg/README.md`.

User decisions (2026-11-15):
- Table: ray grab on the frame (move + yaw only), two-hand grab scales it.
- Goal: click only. The quaternion is all zeros, so the robot faces the direction of travel.
- World feedback: `/plan` and the goal marker on the floor, keepout zones on the floor, and the tools also work when aiming at the real floor.
- Map UI:
  - Load list from `/hmi/map_state`.
  - Save as `map_YYYYMMDD_HHMM`, or overwrite the current map.
  - Reset with confirmation.
  - Clear no-go zones with confirmation.
- Keepout gap: I edit the WSL repo (new volatile `/hmi/keepout_mask`) and do not commit it there.

## Verified facts used by the design
- ROS-TCP-Connector 0.7.0:
  - `ROSConnection.GetOrCreateInstance()`, `Subscribe<T>` (callbacks on the main thread, dispatched in `Update`), `RegisterPublisher<T>(topic, queue, latch)`, `Publish`.
  - `ShowHud` defaults to true; set it false in VR.
  - `HeaderMsg.seq` is compiled out under `ROS2`.
  - Asmdefs: `Unity.Robotics.ROSTCPConnector`, `Unity.Robotics.ROSTCPConnector.Messages`.
  - No ROSConnection exists in the project yet.
- Meta ISDK 207:
  - `QuickActionsAPI.AddRayGrabInteraction(GameObject)`.
  - `Grabbable` takes a one-grab and a two-grab transformer. `TwoGrabFreeTransformer` has min/max scale constraints.
  - `RayInteractor.CollisionInfo` / `SurfaceHit`.
  - The Camera Rig is `OVRComprehensiveInteractionRig`.
- The supervisor publishes `/keepout_filter_mask` only on change (`mission_supervisor_node.py:433`). The endpoint subscribes volatile, so Unity would miss zones that already exist when it connects.
- Saved map `scene_house`: resolution 0.05, origin (−1.52, −3.58). Load it with `ros2 launch vr_robot_pkg vr_robot_bringup.launch.py map:=scene_house`, ready ~8 s after start.
- Reuse:
  - `CoordinateConverter.PositionToUnity` / `PositionFromUnity` (`Scripts/Core/CoordinateConverter.cs`).
  - The `VrMenuController` page pattern and `VrSettingsPage` (`Scripts/XR/`).
  - `LocomotionInputGate.Acquire/Release`.
  - `VrFlyMode.IsFlying` / `FlyingChanged`.
  - `SimWorldRegistry` (`/myRobot` transform as a fallback avatar pose, `Root` to recognise floor hits).

## Architecture (new module `Scripts/HMI/`, asmdef `VRRobot.HMI`)
References: VRRobot.Core, VRRobot.SimWorld, VRRobot.XR, Oculus.Interaction, Oculus.VR, UnityEngine.UI, Unity.TextMeshPro, Unity.Robotics.ROSTCPConnector(.Messages). The Tests asmdef gains VRRobot.HMI.

| File | Role |
|---|---|
| `Core/MapGridMath.cs` (pure, tested) | Converts between ROS map xy, Unity world, grid cell (row 0 = lowest y, row-major) and table local. Also: occupancy → color bytes, stroke resampling (min spacing, max points), map-bounds checks. |
| `HMI/HmiJson.cs` (pure, tested) | `MissionState` / `MapState` parsed with `JsonUtility` (null `mode`/`map` handled). |
| `HMI/RosHmiClient.cs` | Single owner of ROS I/O. Holds the `ROS Connection` scene object (127.0.0.1:10000, ROS2, HUD off, connect on start). Registers all `/hmi/*` publishers and subscribes to `/map`, `/hmi/robot_pose`, `/plan`, `/hmi/mission_state`, `/hmi/map_state`, `/hmi/keepout_mask`, `/myRobot/bridge_status`. Exposes C# events and `SendGoal`, `SendPath`, `SendMissionCmd`, `SendStroke(points, radius)`, `ClearKeepout`, `SendMapCmd`, plus a connection state. All conversions go through `CoordinateConverter`. |
| `HMI/OccupancyGridTexture.cs` | `/map` → Texture2D (RGBA32, point filter; reused when the size is unchanged, recreated otherwise). Holographic palette: unknown = transparent, free = dim cyan, occupied = bright cyan. Same class for the keepout mask (red, alpha). |
| `HMI/HolographicTacticalTable.cs` + prefab `TacticalTable.prefab` | Hologram base + frame (ray-grab colliders, `Grabbable` with a yaw-only one-grab transformer and `TwoGrabFreeTransformer` scale 0.5–3×). Contents: map surface (thin BoxCollider + `TableSurface` marker) and keepout overlay; mini-avatar (simple robot body + heading arrow); `/plan` line; goal marker; stroke preview; small status line (ROS / bridge / mission state). The table is a true miniature of the world: house orientation, map longest side = 1.0 m by default, rescaled when `/map` grows. `Open()` places it ~0.7 m in front of the head and ~0.45 m below eye level, facing the user. `Close()` hides it. |
| `HMI/HmiPointer.cs` | Raycast from the right ISDK controller ray origin, active only while a tool is selected. Not active while the menu ray is hovering UI, while fly mode is on, or while the table frame is grabbed. Nearest hit: `TableSurface` → table point; a collider under `SimWorldRegistry.Root` with normal.y > 0.7 and height < 0.15 m → floor point; anything else → blocked. Gives map xy, validity (inside the map; for goals, not on an occupied/unknown cell) and right trigger down/held/up (`OVRInput`). Draws its own line and reticle (white = valid, red = invalid). While a stroke is held it acquires `LocomotionInputGate` (no accidental turn or teleport). |
| `HMI/TacticalToolController.cs` | Active tool `None / Goal / Path / NoGo` and brush radius (0.15 / 0.30 / 0.50 m). **Goal**: trigger released on a valid point → `SendGoal` (zero quaternion) + marker. **Path**: hold → samples ≥ 5 cm apart (≤ 500 points), preview; release → `SendPath` if length ≥ 0.3 m. **NoGo**: hold → samples ≥ radius/2 apart, preview capsules; release → `SendStroke(z = radius)`. The preview stays until the next `/hmi/keepout_mask`. |
| `HMI/WorldOverlay.cs` | In the 3D twin, a few cm above the floor: `/plan` line, goal marker, keepout overlay (quad covering the mask grid, mask texture). Visible whether the table is open or not. |
| `HMI/VrMissionPage.cs` | New first page of `VrMenu.prefab` (the `VRModeAndMissionMenu` of the plan). Table Open/Close; tool buttons + brush size; Pause / Resume / Cancel (enabled according to `state`); mission state, `remaining`, `message`; ROS connection + bridge status. A "First-person teleop (Stage 6)" button stays disabled. |
| `HMI/VrMapPage.cs` | New page. Current map + READY/BUSY + last result; scrollable list of saved maps with Load; "Save as new" (`map_YYYYMMDD_HHMM`) / "Overwrite <current>"; "Reset map" and "Clear no-go zones", each with a two-step confirm. Buttons are disabled while BUSY. |

Menu tabs become **Mission · Map · Settings**. UI is built through MCP in the existing prefab style (uGUI + TMP, ray canvas already set up).

### WSL change (committed by the WSL agent in `3e1e3b8`)
`mission_supervisor_node.py`: add a volatile `/hmi/keepout_mask` publisher (`nav_msgs/OccupancyGrid`, same content as the mask), published on every mask change and every 2 s. Add a pytest case in `tests/test_mission_supervisor.py` and a row in the README "HMI topics" table. Then `colcon build` and run the offline suite (197 + new tests).

## Steps (each ends with a check; delegation in brackets)
0. Create `STAGE5_TACTICAL_TABLE_PLAN.md` from this plan; mark Stage 4/5 as in progress in `IMPLEMENTATION_PLAN.md`. [haiku]
1. WSL change + tests + build. [sonnet; I review the diff]
2. `MapGridMath`, `HmiJson` + EditMode tests. [sonnet] Run them through the `TestRunnerApi` callback. [haiku]
3. `VRRobot.HMI` asmdef, `RosHmiClient`, `OccupancyGridTexture`. [sonnet] Add the `ROS Connection` + `HMI` objects to `VrScene`. [me, MCP] Check: start the stack (`map:=scene_house`) in the background; in Play mode the console logs `/map` size/origin, robot pose, map_state `scene_house`, bridge `connected`; no errors.
4. Table prefab: visuals and texture [sonnet scripts]; grab/scale wiring through `QuickActionsAPI.AddRayGrabInteraction` + transformer setup via a temporary `[MenuItem]` editor script [me]. Check in the simulator (XR Operator): menu → open table, map + avatar match the robot's position in the house (screenshot), grip-grab moves and yaws it.
5. `HmiPointer` + `TacticalToolController`. [sonnet scripts, me for wiring]
6. `WorldOverlay`. [sonnet]
7. Mission and Map pages in `VrMenu.prefab`. [scripts sonnet, UI building me via MCP]
8. End-to-end tests (below). [me; haiku for ROS-side checks/monitoring]
9. Self-review of the diff (correctness, `OnDestroy` unsubscribes, null-safety when ROS is offline); EditMode suite; Desktop scene regression (no HMI dependency). Update `README_UNITY.md` (HMI section), `CLAUDE.md` (status + lessons), the plan's status section. [haiku drafts, I check] Commit only after you approve.

## Verification (end-to-end, CoppeliaSim + ROS + Unity + Meta XR Simulator already open)
- Stack: `wsl.exe -d Ubuntu-24.04 -- bash -lc 'source …; ros2 launch vr_robot_pkg vr_robot_bringup.launch.py map:=scene_house'` in the background. Wait for `/hmi/map_state` `"map": "scene_house"`. The saved map avoids exploring the house.
- In Play mode, real input goes through the XR Operator: Menu button, controller poses aimed at computed table/floor points, trigger and grip. Check each item with data **and** screenshots:
  1. Table shows the full house; the avatar matches `/hmi/robot_pose`; the frame grab moves it; the two-hand grab scales it.
  2. Goal tool on the table at a known reachable point (e.g. ROS (2.5, 0.6)). `ros2 topic echo /hmi/goal` shows the expected xy (±0.05 m). Mission → NAVIGATING; the `/plan` line appears on the table and the floor; the robot moves in CoppeliaSim and in Unity.
  3. Pause / Resume / Cancel from the menu → states PAUSED / NAVIGATING / CANCELED.
  4. Goal tool aimed at the real floor → goal received; aiming at a wall or a furniture top → red reticle, nothing sent.
  5. Path tool: drawn path → `/hmi/path` received, mode `path`.
  6. NoGo brush: stroke → `/keepout_filter_mask` gains 100-cells in the right area. The red overlay shows on the table and the floor. Restarting Play mode still shows it (thanks to `/hmi/keepout_mask`). Clear removes it.
  7. Map page: the list shows `scene_house`. Save as new → new files in `maps/` (test files deleted afterwards). Reset → map cleared and the table texture shrinks. Load `scene_house` → full map back.
  8. ROS stack stopped → status shows disconnected, no exceptions; reconnects when the stack restarts.
- EditMode suite green; console free of errors; the WSL pytest suite green.
- Bounded retries per the `meta-xr-operator-unity-test-mechanics` skill (3 attempts, then report).

## Execution status
- [x] Step 0: plan document created (2026-11-15).
- [x] Step 1: WSL `/hmi/keepout_mask` (volatile copy, on change + every 2 s, parameter `keepout_republish_period`; README row; new test; offline suite 209 passed). Committed in the WSL repo as `3e1e3b8` (together with `/hmi/map`, step 8).
- [x] Step 2: `MapGridMath`, `HmiJson` + tests (EditMode 41/41). The HMI asmdef also needs `Unity.Robotics.ROSTCPConnector.MessageGeneration` (base `Message` type).
- [x] Step 3: `RosHmiClient`, `OccupancyGridTexture`; `ROS Connection` (HUD off, TF listening off) + `HMI` objects in `VrScene`. Live check: `/map` 281x180 res 0.05 origin (-1.52, -3.58), `/hmi/keepout_mask` 310x210, mission IDLE, map state READY `scene_house`.
- [x] Step 4: `TacticalTable.prefab` (visuals built at runtime under `Content`; `GridQuadMesh`, `TableSurface`, `ToolFeedbackView`; materials in `Materials/HMI/`). Grab: `QuickActionsAPI.AddDistanceGrabInteraction(ManipulateInPlace)` on the root (controller grip, `MoveAtSourceProvider`), kinematic `Rigidbody`, `GrabFreeTransformer` as one- and two-grab transformer (pitch/roll locked, uniform scale 0.5-3x); the `Surface` has its own kinematic Rigidbody so only the frame bars grab. Deviation: the ISDK controller ray selects with the trigger, so the frame is grabbed with the distance grab (grip) instead of a ray grab. Open(): near edge 0.3 m ahead; content turned 90 degrees when the map is deeper than wide (long side across the view).
- [x] Step 5: `HmiPointer` (reads the right `RayInteractor` origin/forward, yields while `HasCandidate`), `TacticalToolController`.
- [x] Step 6: `WorldOverlay`.
- [x] Step 7: `VrMissionPage`, `VrMapPage`; tabs Mission / Map / Settings; panel 640x660.
- [x] Step 8: end-to-end tests. Found: slam_toolbox publishes the transient_local `/map` only when the map changes (not while the robot is stationary), so a late Unity client got no map. WSL fix: the supervisor republishes a volatile copy `/hmi/map` (on change + every 5 s, `map_republish_period`); Unity subscribes to `/hmi/map`.
  - Menu Open table (real ray click); map 281x180, avatar matches `/hmi/robot_pose` and the 3D twin.
  - Goal on the table -> `/hmi/goal` (2.49, 0.61), NAVIGATING, goal reached; Pause/Resume/Cancel -> PAUSED, "resumed: 1 pose(s) left", CANCELED.
  - Goal on the real floor (2.01, 0.50) accepted; wall / counter top -> blocked, nothing sent; target behind a wall -> blocked.
  - Path stroke on the table -> "path with 8 waypoints"; `/plan` drawn on the table and on the floor (green line to the yellow goal ring).
  - No-go brush on the table -> 776 cells; on the floor -> 392 cells; red overlay on the table and on the floor; Clear with two-step confirm -> 0 cells.
  - Map page: Save as new -> `map_20261115_1141` files (deleted afterwards); Reset -> map from scratch (table rebuilt with 250x112); Load scene_house -> 281x180, list shows "(current)".
  - Grab: frame grip-grab moved and yawed the table (25 deg); two-hand grab scaled it (clamped at 3x). Found and fixed: `DistanceGrabInteractable` cached 0 colliders (bars were inactive at Start); a scaled table enclosing the player triggered the recovery guard -> table colliders are now triggers.
  - ROS stack stopped -> "ROS: disconnected", no exceptions; restarted -> reconnected automatically and the map/state came back. The bridge status now shows "unknown" while disconnected.
  - Observation (not HMI): after the robot moved, the Stage 4 spawn picked (-0.37, -0.10, -0.42) instead of (-0.37, 0.03, 0.33) (NavMesh islands changed 5 -> 4), leaving the view against a wall; to be looked at separately.
- [x] Step 9: review (sonnet review of `Scripts/HMI` + `MapGridMath`; fixed: no send/active marker while ROS is disconnected, stale mission mode clearing the new goal/path, raycast buffer 128 -> 512, invalid grid data ignored, stale `/plan` cleared on mission change, empty map-list rows guard, pending strokes cleared with "Clear no-go zones", `WorldOverlay` forced to the world origin; not changed: client re-resolution if `RosHmiClient` were replaced at runtime). EditMode 41/41 after the fixes; smoke test (table goal -> NAVIGATING) passed. Docs: `README_UNITY.md` (HMI section), `CLAUDE.md` (status, interface, HMI lessons). Desktop scene not re-run (it uses nothing that changed). Committed in `ba557bc`; the WSL changes (`/hmi/map`, `/hmi/keepout_mask`, tests, README) were committed in the WSL repo as `3e1e3b8` (offline suite 211 passed).

## Follow-up: operations panel (usability review, 2026-11-15)

User feedback (`todos.md`): the table opened too big (only part of the map in view), could only yaw, the two-hand "zoom" jumped to 3x and stuck, removing no-go zones was not discoverable, and the table and its tools were cumbersome to reach through the main menu. Decisions taken with the user: the table becomes an **operations panel** that opens vertical in front of the head (like the menus) and can be pitched down to horizontal; it fits the whole map in view; resize follows Meta's `PanelWithManipulators` pattern; Y (left controller) opens/closes it; it carries its own toolbar (the Mission page keeps a compact copy for the floor); it stays fixed in the world; no ROS change ("Clear no-go zones" just moves where it is easy to find); everything renamed.

### Execution status
- [x] Rename: `HolographicTacticalTable` → `OperationsPanel`, `TableSurface` → `PanelSurface`, `TacticalTable.prefab` → `OperationsPanel.prefab` (`.meta` GUIDs kept), `HitKind.Table` → `Panel`, `MapGridMath.WorldToTableLocal/TableLocalToWorld` → `…PanelLocal…`, UI strings and the test harness.
- [x] Vertical panel: upright root + `Pivot` (-90° about X) holding content, toolbar and handles; opens at the distance that fits frame + status + toolbar in 55° × 45° (`PanelMath.FitDistance`), longest map side 0.5 m; world-fixed; Y toggles it.
- [x] Pitch: `PanelGrabTransformer` (yaw + pitch 0..90° accumulated as angles, no roll, no scale). Deviation: Meta's `GrabFreeTransformer` rotation constraints clamp Euler angles, which flip yaw/roll by 180° past 90° (measured: yaw jumped to 160°).
- [x] Resize: corner handles + `UniformGrabScaleTransformer` (absolute, non-accumulating; 0.5-2.5×). Root cause of the old bug (from the ISDK sources): distance-grab `MoveAtSourceProvider` grab points start coincident and `GrabFreeTransformer` multiplies per-frame distance ratios, so the first ratio was huge and the scale stayed pinned at the clamp.
- [x] Toolbar on the panel (`ToolSelector`, shared with the Mission page): tools, brush row, "Clear no-go zones" (two-step confirm), Close. Mission page: tool picker moved to `ToolSelector`. In both, "Clear no-go zones" sits in its own full-width row below the brush row (visible with No-go): inside the brush row the label overflowed and the icon was squeezed (user feedback).
- [x] Deviation (found in runtime tests): the frame/handles use **ray grab (trigger)**, not distance grab. The rig's `BestHoverInteractorGroup` ranks the controller distance grab above the ray, and `DistantCandidateComputer`'s sticky `_stableCandidate` ignores interactor filters, so the distance-grabbable frame kept the ray disabled over the toolbar (an `IGameObjectFilter` attempt did not help). With ray interactables the ray picks the nearest hit among toolbar, frame and handles. Handles also keep a direct grip grab.
- [x] Latent Stage 5 bug fixed: `HmiPointer` read `RayInteractor.Origin/Forward`, which go stale while the interactor group disables the ray; it now reads the ray-origin transform (`_rayOrigin`, wired in `VrScene`).
- [x] Removed the orphan `ISDK_SnapZone (OperationsPanel)` (snap target of the old distance-grab `SnapInteractor`).
- [x] Verification (Meta XR Simulator + live stack `map:=scene_house`): Y opens the panel upright with the whole map in view, Y/X close it; toolbar Goal → goal on the vertical panel → `/hmi/goal` (2.38, 0.57), NAVIGATING → SUCCEEDED; frame trigger-grab pitch 45° then clamped at 90° with yaw −20°, roll 0; handle drag 1.0 → 1.47× → 0.62× (both ways); No-go stroke on the panel → 529 cells, toolbar "Clear" (two clicks) → 0 cells; tool state mirrored on the Mission page; menu clicks still work with the panel behind it. EditMode 51/51 (`PanelMathTests` added).
- [x] Editable in the Editor (user request): the visuals (surface, backdrop, keepout, frame bars with their ray grab, avatar, status) are prefab objects instead of being created in `Awake`; `ApplyDefaultLayout` (context menu, also run in `Awake`) lays the panel out at its default size. Fixes "the panel cannot be moved" seen in a user test without ROS: the frame bars only got a size from the first map. Verified without ROS: frame trigger-grab moved, yawed and pitched the empty panel.
- Not verified: snap turn with the panel open (the simulated thumbstick did not turn the rig in this session; the panel is not parented to the rig and has no tracking code). Physical headset not tested.
- Observed (WSL side, not changed): ROS-TCP-Endpoint sometimes dies when Unity leaves Play mode (`[Errno 9] Bad file descriptor`, process exit 1) while the rest of the stack keeps running.
