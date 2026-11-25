# Unity — Layer 1: CoppeliaSim scene reconstruction (Stage 1, no VR)

## How to test (Play mode on the PC)
1. Open CoppeliaSim 4.10 with `scene_house/scene_house.ttt`.
2. Install the streamer: `cd coppelia_scripts` and `python -u install_streamer.py`. Optionally validate with `python -u test_zmq_client.py --expect-house`.
3. In Unity, open `Assets/_Project/Scenes/SimWorld_Desktop.unity` and press Play.
4. The HUD (top-left corner, F1 toggles it) shows state, progress, counts, stream and FPS. The **Reload scene** button rebuilds everything.
5. Camera: hold the **right mouse button** and use **WASD** to move, **Q/E** to go down/up, **Shift** to speed up and the mouse wheel to change the speed.

The order does not matter. If the streamer is not running, the builder retries every 2 s.

## Structure
```
Assets/_Project/
├── Plugins/NetMQ/        NetMQ 4.0.1.13 + AsyncIO 0.1.69 + NaCl.Net 0.1.13 (netstandard) + link.xml
├── Scripts/Core/         VRRobot.Core: CoordinateConverter (RH Z-up ↔ LH Y-up)
├── Scripts/SimWorld/     VRRobot.SimWorld
│   ├── Protocol/         Protocol v1 DTOs + FrameReader (binary frames)
│   ├── Transport/        ZmqSceneClient (REQ 23005), ZmqPoseStream (SUB 23006), NetMQLifecycle
│   ├── Building/         ShapeMeshBuilder (welding/conversion/submeshes), MaterialCache (URP Lit)
│   ├── CoppeliaSceneBuilder.cs   info → list → hierarchy → meshes; resyncs on "scene" events
│   ├── CoppeliaPoseSubscriber.cs applies local poses on the main thread
│   ├── SimWorldRegistry.cs       EXTENSION POINT
│   └── SimObjectRef.cs           handle/path on every rebuilt GameObject
├── Scripts/Desktop/      VRRobot.Desktop: DesktopFlyCamera + SimWorldDebugHud (replaceable by the XR Origin)
├── Tests/EditMode/       converter, welding, material grouping, frame reading (16 tests)
├── Materials/CoppeliaLitTemplate.mat   reference to the URP/Lit shader (keeps the shader in builds)
└── Scenes/SimWorld_Desktop.unity
```

The protocol is documented in `coppelia_scripts/README_COPPELIA_SETUP.md`.

## Where to plug in VR (next agent)
- **Remove the `DesktopRig`** (free camera + HUD) and put the **XR Origin** in its place. `SimWorld` (Registry + Builder + PoseSubscriber) does not depend on anything in Desktop.
- The rebuilt world lives under `SimWorld/CoppeliaScene`, with the **same coordinates** as CoppeliaSim, converted (same origin, meters, Y up). For ROS: `x_ros = z_u, y_ros = -x_u, z_ros = y_u`.
- Consume only `SimWorldRegistry` (`SimWorldRegistry.Instance`):
  - `TryGet(handle, out SimObject)`, `TryGetByPath("/myRobot", out SimObject)`, `Find(path)` → `Transform`.
  - `Objects`, `Root`, `IsReady`, `SceneVersion`.
  - Events (on the main thread): `SceneBuilt`, `ObjectSpawned`, `ObjectRemoved`, `MeshReady`, `PosesApplied`.
- When `/myRobot` (Pioneer P3-DX) is inserted into CoppeliaSim, the streamer emits `scene`, the builder creates its subtree and the stream starts moving the robot and its wheels (poses are local, so the wheels under the joints follow). Use `ObjectSpawned` or `TryGetByPath("/myRobot")` to attach the camera, HUD, etc.
- Shadows: `CoppeliaSceneBuilder.castShadows` (off by default, like CoppeliaSim). The VR rig lights should also have shadows off, otherwise the shadow map pass comes back (even if the house objects cast nothing).
- Colliders: `CoppeliaSceneBuilder.addMeshColliders` (off by default). Turn it on if VR locomotion/teleport needs collision with the house.

## Conversion details
- One GameObject per CoppeliaSim object (shapes, dummies, joints; cameras and lights are left out). One Mesh per shape, with one submesh per material (color + transparency + texture + culling).
- Normals and UVs arrive per triangle corner. `ShapeMeshBuilder` welds equal corners (quantized vertex + normal + UV), converts axes and flips the winding. UInt32 indices when there are more than 65,535 vertices.
- Materials: diffuse → `_BaseColor`. Modulate texture → color × texture; decal → texture only. Transparency → Transparent surface. No backface culling in CoppeliaSim → Render Face Both.
- Shapes invisible in CoppeliaSim (roof, `ResizableFloorLarge`) get their node and mesh created, with the renderer disabled (`hideInvisibleShapes`).

## Measurements (scene_house.ttt, Play mode in the Editor, PC)
- Full load: **10.6 s** (34.1 MB over localhost). The bottleneck is the `getShapeViz` extraction in Lua: the REQ alone takes about 7 s with the Python client.
- 106 objects, 52 shapes, 134 submeshes, 90 materials, 16 textures.
- 1,344,471 corners → **538,964 vertices** after welding; **448,157 triangles**.
- **Shadows off** (CoppeliaSim does not render shadows): `CoppeliaSceneBuilder.castShadows = false` (default; renderers neither cast nor receive shadows) and the scene's Directional Light with `shadows = None`.
- Overview, A/B in the same session (1280×720 render, MSAA 4×): **2.3 ms per render without shadows vs 4.1 ms with shadows** (−45%). Game view: 267 draw calls / 30 SetPass / ~0.9M triangles without shadows, vs 446 / 37 / ~1.5M with shadows (measured in an earlier session).
- For the Quest: the 448k triangles (229k in `coffee_maker` alone) and the 267 draw calls will still require decimation/LOD on the heavy objects and possibly merging static shapes.

## VR scene (Meta XR)
- Scene `Assets/_Project/Scenes/VrScene.unity`, built from Meta Building Blocks: Camera Rig, `OVRComprehensiveInteractionRig` (Locomotor / LocomotionSettings) and a Teleport Interactable used as a blocker. `SimWorld` is a shared prefab, `Assets/_Project/Prefabs/SimWorld.prefab` (the Desktop scene uses it too); the VR instance overrides `addMeshColliders = true`.

### How to run
1. CoppeliaSim with `scene_house.ttt` and the streamer running (see above).
2. Meta XR Simulator active (menu Meta > Meta XR Simulator; v207.1, `XR_RUNTIME_JSON`).
3. Open `VrScene.unity` and press Play.
4. The app opens on the main menu: point the right ray at **Load CoppeliaSim scene** and pull the trigger. The scene is built while you watch it from above, then you are spawned inside it.
- Controls: left stick = smooth walk; right stick left/right = snap turn 45°; right stick forward = teleport arc, release to teleport.
- Locomotion is disabled until the house is loaded and the NavMesh is baked (~6–16 s load, NavMesh ~150–200 ms).

### Start flow
- `AppFlowController` (object `Start Menu`, prefab `Assets/_Project/Prefabs/StartMenu.prefab`): `MainMenu → Loading → Spawning → Playing`.
  - **Main menu**: world-space panel with the streamer endpoint and the "Load CoppeliaSim scene" button (Meta ray canvas interaction, like `VrMenu`). Locomotion and the in-game menu are disabled. Free space for future features.
  - **Loading**: `CoppeliaSceneBuilder.Build()` starts only now (the VrScene instance overrides `buildOnStart = false`). On `SimWorldRegistry.HierarchyApplied` (poses known, no meshes yet) the screen fades (Meta `OVRScreenFade` on `CenterEyeAnchor`) and the rig moves to an overview computed by `OverviewFraming` (Core): bounds of the visible shapes, eye at 20° elevation, bounding sphere within a 20° half angle (the head looks horizontally, so the scene spans 0–40° below the horizon), facing the rig's initial forward. A small panel (status, progress bar, last error while retrying) sits slightly above eye level, so it does not cover the scene.
  - **Spawning**: on `SceneBuilt` + `NavMeshReady`, fade, `VrPlayerBootstrap.RequestSpawn()` (same spawn logic as before), then locomotion and the in-game menu are enabled.
  - **Fallback**: if the NavMesh cannot be built (`SimWorldNavMeshBaker.NavMeshFailed`, baker disabled, 15 s timeout) or the player cannot be placed (`VrPlayerBootstrap.SpawnFailed`, 5 s timeout), the player starts at the CoppeliaSim origin (feet on z = 0) in fly mode, turned towards a direction where the panel fits, and the panel shows "Could not place the player" with the reason and an OK button.
  - Resynchronizations during play (`scene` events) do not return to the overview.
- The sky is Unity's default procedural skybox (`CenterEyeAnchor` clear flags = Skybox).

### Module `Assets/_Project/Scripts/XR/`
Asmdef `VRRobot.XR` (→ VRRobot.Core, VRRobot.SimWorld, Oculus.Interaction). Components live on the `XR Locomotion` object in the scene.
- `SimWorldNavMeshBaker`: on every `SimWorldRegistry.SceneBuilt`, collects the physics colliders under `SimWorldRegistry.Root` with the low-level `NavMeshBuilder` API and bakes (async) a NavMesh for agent type `VRPlayer`. Raises `NavMeshReady`; logs time, sources, triangles, area, island count and largest island areas. `SimWorldNavMeshBaker.Islands` / `TryGetIsland` expose the connected NavMesh islands.
- `ReachableTeleportSurface` (`ISurface`): wraps Meta's `NavMeshSurface` (agent index 1). Accepts a teleport target only if a complete NavMesh path exists from the player's feet **and** the player's capsule fits there (`CapsuleClearance`). This removes table/bed/counter tops, the outside ground and points behind walls without using any CoppeliaSim object name.
- `VrPlayerBootstrap`: keeps `FirstPersonLocomotor` disabled until `NavMeshReady`, then places the player at the closest clear NavMesh point to the spawn (optional `_spawnPoint`, default = rig origin = CoppeliaSim origin), searching in rings at the spawn's own level first. If that point lands on an island smaller than 4 m², the largest island at the spawn level is used. Recovery guard: if the capsule stays inside geometry (>5 cm for 3 frames) or falls >1 m below the last valid position, the player is moved back to the last valid NavMesh point (`[XRLoco] Player recovered ...` warning).
- `CapsuleClearance`: physics overlap test of the Meta CharacterController capsule at a candidate feet position (ignores the rig's own colliders). The margin against walls and furniture comes from this test, not from the agent radius.
- `VrMenu` (below) and `VrFlyMode` also live in this module.

### VR menu
- Prefab `Assets/_Project/Prefabs/VrMenu.prefab` (object `VR Menu` in `VrScene`): world-space uGUI + TextMeshPro canvas made interactive with Meta's Quick Action "Add Ray Interaction to Canvas" (`RayInteractable`, `PointableCanvas`, `PlaneSurface`/`ClippedPlaneSurface`, EventSystem with `PointableCanvasModule`). TMP Essential Resources were imported (`Assets/TextMesh Pro`).
- The left Menu button opens/closes it ~0.8 m in front of the eyes (world-locked).
- `VrMenuController` handles pages/tabs (first page: **Settings**); `VrSettingsPage` has the toggle "Fly mode (creative)" and the **Height offset** row (−, +, Reset).
- While open, the panel follows the Camera Rig's motion (translation + yaw), so it stays in front of the player after the fall that follows turning fly mode off, after recovery teleports and after height changes.
- While the menu is open, `LocomotionInputGate` deactivates the Meta locomotion interactors (left slide + slide actions, right turner, right teleport, right slide actions), so the trigger only clicks the UI.

### Fly mode
- `VrFlyMode` on `XR Locomotion`: disables `FirstPersonLocomotor` (it re-places the rig every frame), pauses the recovery guard and the locomotion interactors, and moves the Camera Rig directly, without collisions.
- Controls: left stick = horizontal movement relative to the head, right trigger = up, left trigger = down (analog), right stick X = snap turn 45°. About 2 m/s horizontal, 1.5 m/s vertical. A yellow "FLY MODE" HUD sits under the view.
- Turning it off = "fall where you are" if the surface below is a clear NavMesh point on the same island as the last valid position (`VrPlayerBootstrap.ResolveLanding`); otherwise (roof, outside ground, furniture tops) the player is teleported to the closest clear point of that island. Without any surface below and without a NavMesh (fallback start), fly mode stays on.
- A NavMesh rebake while flying does not move the player.

### Height setting
- `VrHeightSetting` (on `XR Locomotion`) owns Meta's `FirstPersonLocomotor.HeightOffset`: it raises/lowers the view relative to the character's feet, and the capsule grows by the same amount. Range −0.50…+0.25 m, 5 cm steps; above ~+0.25 m the capsule (~1.8 m + offset) no longer fits under the door lintels (~2.05 m). Saved in `PlayerPrefs` (`VRRobot.HeightOffset`).
- Meta's `StandingSetting` (standing/seated) also writes `HeightOffset` and reset it at startup; its locomotor reference is cleared in `VrScene`, so seated users use this setting instead.

### Tuned values
- NavMesh agent `VRPlayer`: radius 0.25 (matches the Meta capsule), height 1.9, climb 0.2, slope 50, cell 0.083. Height 2.0 does not fit under the door lintels (~2.05–2.09 m) and radius 0.35 closes narrow doorways; both split the house into islands.
- Resulting NavMesh: the house interior is one island (~99 m²), plus the outside ground (~33 m²) and a tiny one. All rooms are reachable by teleport.
- Meta CharacterController `maxStep` 0.3 → 0.2 (stops climbing coffee tables); `FirstPersonLocomotor` speedFactor 30 → 15 (~1.5 m/s).
- Teleport interactable tie-breaker 0 vs blocker −100.
- Logs use the `[XRLoco]` prefix (the OpenXR runtime already logs with `[XR]`).

### Known limitations
- Meta's CharacterController uses `CapsuleCast`, which ignores colliders it already overlaps. On the CoppeliaSim meshes (thin, non-convex) the capsule can occasionally slip into furniture; the recovery guard handles it.
- In `scene_house` all door joints are open (90°) and Unity matches CoppeliaSim.
- The roof is invisible, so the sky is visible from inside the house (default procedural skybox).

### Runtime testing
Meta XR Operator MCP at `http://127.0.0.1:8720/mcp` while in Play mode: `openxr_set_controller_input`, `openxr_set_controller_pose`, `openxr_capture_composited_image`, `unity_get_world_pose`. The simulated right controller needs an explicit pose for teleport tests (its default pose points 60° up from inside the player's right side).
- The simulator applies a fixed 60° pitch between the OpenXR `grip` pose and `RightHandAnchor`, and the ISDK ray follows the grip (setting the `aim` pose has no effect). To aim the ray at a UI element, compute grip = desiredPointer × inverse(pointerOffset) × inverse(Euler(60,0,0)) in tracking space and convert to OpenXR with (x, y, −z) / quaternion (−x, −y, z, w).
- Left Menu button: `openxr_set_controller_input` with component "Menu". Trigger: component "Trigger" (no `sub_component`); sticks: "Thumbstick" with `sub_component` "X"/"Y".
- Simpler ray aiming: read the right `ControllerRayInteractor`'s `_rayOrigin` (position/forward) in Unity, compute the world rotation that turns that forward toward the target, convert it to OpenXR and pre-multiply it into the current grip orientation (`openxr_get_controller_pose`); one refinement step lands within a few pixels.

## HMI (Stage 4 Spatial Menu, Stage 5 operations panel, Stage 6 teleop)
### Module and ROS link
- Module `Assets/_Project/Scripts/HMI/`, asmdef `VRRobot.HMI` (refs: VRRobot.Core, VRRobot.SimWorld, VRRobot.XR, Oculus.Interaction, Oculus.VR, UnityEngine.UI, Unity.TextMeshPro, Unity.Robotics.ROSTCPConnector, `.Messages`, `.MessageGeneration`).
- Pure math in `Scripts/Core/MapGridMath.cs` and `Scripts/Core/PanelMath.cs` (EditMode tests `MapGridMathTests`, `HmiJsonTests`, `PanelMathTests`; suite 51/51).
- Scene objects in `VrScene`:
  - `ROS Connection`: `ROSConnection` at 127.0.0.1:10000, protocol ROS2 (`ROS2` define), HUD off, TF listening off.
  - `HMI`: `RosHmiClient`, the only owner of ROS I/O. Raises its events on the main thread.
- Contract: WSL repo `ros2_ws/src/vr_robot_pkg/README.md`.
- Subscribes: `/hmi/map`, `/hmi/keepout_mask`, `/hmi/robot_pose`, `/plan`, `/hmi/mission_state`, `/hmi/map_state`, `/myRobot/bridge_status`, `/camera/image_raw/compressed`.
  - `/hmi/map` and `/hmi/keepout_mask` are volatile copies republished by the mission supervisor. ROS-TCP-Endpoint subscribes volatile and cannot receive the transient_local `/map` or `/keepout_filter_mask`; slam_toolbox publishes `/map` only when the map changes.
- Publishes: `/hmi/goal` (zero quaternion = face the direction of travel), `/hmi/path`, `/hmi/mission_cmd`, `/hmi/keepout_stroke` (`z` = brush radius, empty = clear), `/hmi/map_cmd`, `/cmd_vel_teleop` and `/camera/enable` (Stage 6). Unity never publishes `/cmd_vel`.
- Logs use the `[HMI]` prefix.

### Operations panel
Formerly the "holographic tactical table" (renamed after the `todos.md` usability review, 2026-11-15).
- Prefab `Assets/_Project/Prefabs/OperationsPanel.prefab`, component `OperationsPanel`. A miniature of the world (map frame = Unity world); longest map side `_panelSize` = 0.5 m at scale 1.
- Hierarchy: the root is upright (its rotation is the grab's yaw and pitch, pitch 0 = vertical, 90 = lying face up). `Pivot` (rotated -90° about X) holds `Content`, `Toolbar` and `Handles`, so the miniature lies in the pivot's XZ plane (+Y towards the viewer, +Z up).
- Visuals are **prefab objects** under `Content` (editable in Prefab Mode): `Surface` (map mesh, trigger `BoxCollider`, kinematic `Rigidbody`, `PanelSurface`), `Backdrop`, `Keepout`, `Frame/Near|Far|Left|Right`, `Avatar/Body|Heading`, `Status` (TMP). Only the map and keepout meshes, materials/textures and the tool feedback (plan line, goal ring, previews) are created at runtime. The component context menu **Apply Default Layout** lays frame, backdrop, status, toolbar and handles out around the default size (`_panelSize` × 0.64) in the Editor; `Awake` runs it too (an empty panel, before the first map or without ROS, can be grabbed and used), and every `/hmi/map` re-lays them out to the map. Content is turned 90° when the map is deeper than wide.
- Open / close: **Y** (left controller, `_toggleButton`), the toolbar's X, or the Mission page button. `Open()` places it upright in front of the head, at the distance where frame + status + toolbar fit in 55° × 45° (`PanelMath.FitDistance`), centre 0.1 m below the eyes. It stays fixed in the world (does not follow locomotion). Scale resets to 1 on every open.
- Toolbar (`Pivot/Toolbar`): a copy of the VR menu canvas (UI Set backplate, `UIThemeManager`, ISDK ray canvas) with a `ToolSelector`: tools None / Goal / Path / No-go, Close, and with No-go the brush row plus a full-width "Clear no-go zones" row (two-step confirm; `ToolSelector._clearRow`). Laid out under the near frame bar in `LayoutParts` (also in `Awake`, so it faces the viewer before the first map).
- Frame grab: **ray grab with the trigger** on a frame bar. Each bar has a `ColliderSurface` + `RayInteractable` (in the prefab) pointing at the root `Grabbable`, movement `MoveAtSourceProvider` on `Frame Grab` (the panel follows the hand's translation and rotation). Transformer `PanelGrabTransformer` (one or two hands): yaw about world up + pitch about the panel's horizontal axis accumulated as angles and clamped to 0..90°, never roll, never scale.
- Resize: four corner handles (Meta `PanelWithManipulators` pattern), each with `GrabInteractable` (direct grip) and `RayInteractable` (trigger; hover surface = the handle sphere, select surface = `Handles/Scale Plane`, an infinite plane in the panel plane), pointing at the `Scaler` `Grabbable` with `UniformGrabScaleTransformer`: absolute scale = start scale × in-plane distance from the centre now / at grab start, clamped 0.5-2.5×. Handles keep a constant world size (`LateUpdate`).
- Why not distance grab: the rig's `BestHoverInteractorGroup` ranks `ControllerDistanceGrabInteractor` above `ControllerRayInteractor`, and `DistantCandidateComputer` keeps a sticky `_stableCandidate` without re-checking interactor filters, so a distance-grabbable frame next to the toolbar kept the ray disabled. Two-hand scaling with distance grab (Stage 5) also saturated: both `MoveAtSourceProvider` grab points start at the object origin and `GrabFreeTransformer` multiplies per-frame distance ratios, so the first ratio was huge and the scale stuck at the maximum.
- All panel colliders are triggers, so the CharacterController, the recovery guard and `CapsuleClearance` ignore them. The grab interaction, toolbar and handles are active only while the panel is open (`_activeWhileOpen`).

### Tools and pointer
- `HMI/Tools` = `TacticalToolController`; `HMI/Pointer` = `HmiPointer`.
- Pointer: right controller ray read from the `ControllerRayInteractor`'s ray-origin transform (`_rayOrigin`, wired in the scene; `RayInteractor.Origin` goes stale while its interactor group keeps it disabled), right trigger (hysteresis 0.55 / 0.35). It yields while the ISDK ray hovers UI or the panel frame/handles and is off while flying.
- Hit resolution: panel surface (`PanelSurface`) → map point; SimWorld collider with up normal and height < 0.15 m → floor point; anything else → blocked (red).
- Goal: click on a free cell (panel or floor).
- Path: hold and drag; samples ≥ 5 cm apart; sent if length ≥ 0.3 m.
- No-go brush: radius 0.15 / 0.30 / 0.50 m; samples every radius/2.
- While a stroke is held, `LocomotionInputGate` is held.

### World overlay
- `HMI/World Overlay` (`WorldOverlay`): `/plan`, goal ring, active path and keepout mask drawn on the house floor (y 0.02-0.03).

### Teleop and camera panel (Stage 6)
- `HMI` object: `FirstPersonTeleopController`, the owner of the teleop mode. **X** (left controller, `_toggleButton`) or the Mission page button toggles it; it only opens in the `Playing` phase of `AppFlowController`.
- While the mode is open:
  - `LocomotionInputGate` is held, so Meta's slide / turn / teleport interactors are off and fly mode ignores the sticks;
  - the robot camera is on (`/camera/enable` true; false on close);
  - the **sticks drive the robot**: left stick Y = forward / backward (`linear.x`), right stick X = rotation (left = counter-clockwise, `angular.z > 0`).
- No grip deadman (user decision, 2026-11-23): the spring-centred sticks are the deadman. Commands go out at 15 Hz (accumulated schedule, so the average holds at low frame rates) only while a stick is deflected, and exactly one zero Twist is sent when both sticks return to the centre, when the mode closes, on pause and on focus loss. Without ROS nothing is sent; twist_mux and the bridge watchdog stop the robot (0.5 s each).
- Speeds: `Scripts/Core/TeleopMath.cs` (EditMode `TeleopMathTests`). Raw stick axes (`OVRInput.RawAxis2D`), deadzone 0.15 with rescale, response exponent 1.5, clamped to the contract limits 0.5 m/s and 1.5 rad/s whatever the serialized maxima. In the Meta XR Simulator a thumbstick input of 0.6 reads ~0.3 (raw and virtual axes alike; 1 reads 1), so partial speeds look lower there than on the headset.
- A nonzero teleop command auto-pauses an active mission on the ROS side (`paused (teleop)`); Resume is on the camera panel and the Mission page.
- `RosHmiClient.SetCameraEnabled` remembers the requested state and publishes it on every (re)connection, false included: the "off" sent when Play mode exits rarely reaches ROS, which used to leave the camera on into the next session.
- Camera panel: prefab `Prefabs/CameraPanel.prefab`, component `CameraPanel` (same family as the operations panel: same `Pivot` convention, frame bars with trigger ray grab through `PanelGrabTransformer`, pitch 0..60°, all colliders triggers, opens upright in front of the head and stays fixed in the world). It follows `FirstPersonTeleopController.ActiveChanged`; its Close ends the teleop mode.
  - `Content`: `Screen` (quad, `Materials/HMI/CameraScreen.mat`, 0.64 m × 4:3), `Backdrop`, `Frame/Bottom|Top|Left|Right`, `Status` (ROS / bridge / mission line) and `Placeholder` ("Waiting for the camera image" / "Camera image lost" after 1.5 s without frames / "ROS disconnected").
  - `Toolbar` (UI Set canvas copied from the operations panel): Teleop chip (Driving / Idle / No camera / Disconnected) and Mission chip with captions, the commanded speeds (or the controls hint while idle), "Resume mission" (UI Set Primary, enabled while the mission is paused) and Close.
  - Every visual is a prefab object; context menu **Apply Layout** places them around `_screenWidth` (toolbar as wide as the frame). Only the texture and the texts are set at runtime: the newest JPEG is decoded once per frame (`ImageConversion.LoadImage`; the bridge already flips the rows to top-down).

### VR menu tabs
- Mission (`VrMissionPage` + `ToolSelector`): open/close panel, tool and brush for the real floor (mirrors the panel toolbar; "Clear no-go zones" in its own row below the brush), Pause / Resume / Cancel enabled by mission state, mission and ROS/bridge status, "Start teleop / Stop teleop" (Stage 6).
- Map (`VrMapPage`): saved maps list with Load; Save as new `map_YYYYMMDD_HHMM`; Overwrite current; Reset map and Clear no-go zones, each with a two-step confirm. Buttons are disabled while BUSY or disconnected.
- Settings: as described in the VR menu section above.

### UI theme (Meta UI Set)
- Both menus (`StartMenu.prefab`, `VrMenu.prefab`) and the toolbars of `OperationsPanel.prefab` and `CameraPanel.prefab` follow Meta's Interaction SDK **UI Set** (`Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Objects/UISet`), the reference for every new panel.
- Each menu root has Meta's `UIThemeManager` with `UIThemeQuest_Dark`. In `Start` it applies the theme to all children, driven by `QDSUI*` tags:
  - backplate (`QDSUIBackplateGradient`, material `RoundedBoxGradientUIDark`), section plates (`QDSUISection`), accent (`QDSUIAccentColor`);
  - button state colors through Animator controllers chosen by tag (Primary / Secondary / Destructive / ToggleButton / ToggleSwitch);
  - **every TMP text gets the theme font** (LiberationSans in the package theme) and the primary or secondary (`QDSUITextSecondaryColor`) color. `QDSUISharedThemeColor` objects are left alone (status chips).
- `UiTextRole` (`Scripts/XR`, runs after the manager) re-applies the project fonts and sizes from `Assets/_Project/UI/UiTextStyles.asset`: Title (Roboto Bold 26), Heading (Bold 18), Body (Regular 18), Caption (Regular 15), Label (Medium 18, button labels). Change sizes there; every text in both menus has a role.
- Components (nested instances of the UI Set prefabs): Primary (Open panel, Load scene, OK), Destructive (Cancel, Reset map, Clear no-go zones), Secondary (everything else), `TextTileButton_IconAndLabel_Toggle` for tabs, tools and brush (visual on state = selected), `ToggleButton_Switch` for fly mode. Icons come from `Runtime/Sample/Textures/OCUIIconSet`. Rounded corners: material `RoundedBoxUI` + `RoundedBoxUIProperties`.
- Selection uses `Toggle.isOn` (tabs, tools, brush), not `interactable = false`; `interactable` only means "unavailable". No `ToggleGroup`: the page scripts keep exactly one toggle on. Code sets toggles through `UiToggles.Set` (with notification, under a guard flag), because the UI Set highlight (`AnimatorOverrideLayerWeight`) ignores `SetIsOnWithoutNotify`; a click on the active item is undone in `LateUpdate`.
- `StatusChip` (`Scripts/XR`): rounded colored chip for the ROS, mission and map states (Ok green, Warn amber, Error red, Neutral gray).
- Layout: canvas scale 0.001 (UI Set units), pages are `VerticalLayoutGroup`s of sections; the panel height fits the tallest page (VrMenu 520×749, StartMenu 560×306).
- Editing the menu pages in the Editor: in Prefab Mode, Map Page and Settings Page are simply inactive; activate the one you want to edit (and deactivate Mission Page). The saved active state does not matter, `VrMenuController.ShowPage` sets it at runtime. Button colors only show their final look in Play mode (Animator).

### Runtime testing
- Start the stack: `ros2 launch vr_robot_pkg vr_robot_bringup.launch.py map:=scene_house`. The saved house map loads ~8 s after start; no exploration is needed.
- Aim the right ray by rotating the simulated grip: the ISDK ray and the distance-grab `SelectionFrustum` follow the grip in the simulator.
- Hold Grip across frames for the distance grab.
- The composited-image capture can return a stale frame; rendering `CenterEyeAnchor` (or a temporary camera) into a RenderTexture is reliable.

## Known issues / notes
- **Unfocused Editor**: without focus, Unity may stop advancing frames in Play mode. With the Editor focused (normal use) this does not happen. `PlayerSettings.runInBackground` is on.
- Color/mesh changes of an existing shape are not detected (only objects being added/removed/reparented, and poses). Use **Reload scene**.
- Vision sensors and other objects created in CoppeliaSim also trigger a `scene` event and show up as empty nodes.
- Editing scripts while in Play mode reloads the domain. The components fetch their references in `OnEnable` and the builder rebuilds the scene by itself.
