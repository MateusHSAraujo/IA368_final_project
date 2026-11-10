# Plan: VR Scene with Locomotion (Stage 4, part 1 — Meta XR SDK + Meta XR Simulator)

## Context
Stage 1 rebuilds the CoppeliaSim scene in Unity, on the desktop only. This step finishes the XR package setup so an XR scene compiles and runs, ports the Stage 1 world (`SimWorld`) into `Assets/_Project/Scenes/VrScene.unity` (created by the user with Meta Building Blocks), and implements **only player locomotion**:
- left stick: smooth walk;
- right stick: snap turn (45°) left/right, and teleport (push forward to show the arc, release to teleport).

Validation happens in the Editor with the **Meta XR Simulator**, against `scene_house.ttt` (open in CoppeliaSim, streamer running). Rules:
- Prefer Meta Building Blocks and Meta's design patterns (Interaction SDK) over custom code.
- Walkable areas must **not** depend on CoppeliaSim object names, so other scenes keep working.

The VR menu, tactical table and teleoperation (rest of Stages 4–6) are out of scope.

## Starting point (verified 2026-11-08)
- **Packages:**
  - `com.meta.xr.sdk.core` 201.0.0, `com.meta.xr.sdk.interaction.ovr` 207.0.1 (pulls `com.meta.xr.sdk.interaction`), `com.unity.xr.openxr` 1.19.0 (+ `xr.hands`, `xr.management`, `xr.core-utils`), `com.unity.ai.navigation` 2.0.0.
  - The **Meta Interaction SDK** replaces the XR Interaction Toolkit (`XRGrabInteractable`) mentioned in `IMPLEMENTATION_PLAN.md`.
- **OpenXR:**
  - Loader on Standalone and Android with `MetaXRFeature` and the Oculus Touch profile.
  - Single Pass Instanced.
  - `ApiLayersFeature` (Standalone) loads `Assets/XR/APILayers~/WindowsLayers/x64/XrApiLayer_METAX_operator.dll` (Meta XR Operator).
- **Active build profile:** `Assets/Settings/Build Profiles/New Meta Quest Profile.asset` (Android / Meta Quest). It does not override the global scene list. In the Editor, Play mode uses the **Standalone** XR settings, but rendering uses the Android quality level (`Mobile_RPAsset`).
- **Meta XR Simulator v207.1:** installed in `C:\Program Files\MetaXRSimulator\v207.1` and active (`XR_RUNTIME_JSON` points to its `meta_openxr_simulator.json`).
- **`VrScene`** contains:
  - `[BuildingBlock] Camera Rig`: `OVRCameraRig`, `OVRManager`, `OVRHeadsetEmulator`.
  - Controller and hand tracking blocks.
  - `[BuildingBlock] OVRComprehensiveInteractionRig`, which holds:
    - `Locomotor`: `FirstPersonLocomotor` and Meta's `CharacterController`, with `maxStep` 0.3, `maxSlope` 50° and a layer mask that includes Default.
    - `LocomotionSettings`: Moving = Slide, Turning = Snap 45°.
  - `Teleport Physics Blocker/[BuildingBlock] Teleport Interactable`: allowTeleport = false and a `PhysicsLayerSurface` on Default, so it acts as a blocker.
- **Controller mapping (Slide mode):** the left `ControllerSlideInteractor` and the right `ControllerTurnerInteractor` are active. Both `TeleportControllerInteractor`s are inactive.
- **APIs used by this plan:**
  - `FirstPersonLocomotor` disables movement by itself when no ground is found at `Start`, and exposes `EnableMovement()`, `DisableMovement()` and `HandleLocomotionEvent()`.
  - Meta's `Oculus.Interaction.Surfaces.NavMeshSurface` implements `ISurface` and can be the surface of a `TeleportInteractable`.
  - From Stage 1: `SimWorldRegistry` raises `SceneBuilt`, and `CoppeliaSceneBuilder.addMeshColliders` adds `MeshCollider`s to visible shapes.

## Design decisions
1. **Teleport on the right stick, also in Slide mode.** The right `TeleportControllerInteractor` is added to the Slide list of `MovingSetting`. Snap turn stays at 45°.
2. **`SimWorld` becomes a shared prefab** (`Assets/_Project/Prefabs/SimWorld.prefab`) used by both scenes. The VR instance overrides `addMeshColliders = true`.
3. **Walking needs no walkable definition.** Meta's `CharacterController` handles slopes, steps and collisions against all mesh colliders.
4. **Teleport targets come from a NavMesh baked at runtime**, built from the rebuilt scene colliders with a player-sized agent. A target is accepted only if it is **reachable on foot** from the player (`NavMesh.CalculatePath` → `PathComplete`). This drops table and bed tops and needs no object names. The NavMesh is rebaked on every `SceneBuilt`.
5. **Locomotion is gated on the scene.** It stays disabled until the NavMesh is ready (the house takes ~10 s to load). Then the player is placed on the NavMesh and movement is enabled.
6. **Active Input Handling = Input System Package (New)** instead of Both, which removes the build warning.
   - No legacy `UnityEngine.Input` use in `Assets/_Project`.
   - `OVRInput` and the Interaction SDK read controllers through OVRPlugin/OpenXR.
   - `OVRHeadsetEmulator` has Input System bindings.
   - The only unguarded legacy call in the Meta packages (`LipSyncMicInput.cs`) compiles only with `META_AVATAR_SDK_DEFINED`.
7. **Runtime tests drive real input through the Meta XR Operator** (MCP over HTTP at `127.0.0.1:8720/mcp` while Play mode runs). The skills in `My project/.claude/skills/meta-xr-operator*` describe it and are committed.

## Steps
0. **Git**: a first commit with the existing XR setup:
   - packages and project settings;
   - `Assets/XR`, `Assets/Oculus`, `Assets/Resources`, `Assets/Plugins/Android`;
   - the build profile, `VrScene` and `My project/.claude/skills`.

   Commits are made only when the user asks.
1. **Finish the package and project setup:**
   - Active Input Handling → Input System Package (New). Needs an Editor restart. Then check the console and the Desktop fly camera.
   - Run the Meta Project Setup Tool for Android and Standalone, apply the required and recommended fixes, and record what changed.
   - In the Meta Quest build profile, enable **Override Global Scene List** with only `VrScene`. The global list keeps `SimWorld_Desktop`.
   - Check that the console is clean and that Play mode starts through the simulator runtime.
2. **SimWorld prefab:**
   - Create it from the Desktop scene object: Registry + Builder + PoseSubscriber, with the material template.
   - The Desktop scene keeps an instance, with no behavior change.
   - `VrScene` gets an instance with `addMeshColliders = true`, and its Directional Light has no shadows.
3. **NavMesh agent type `VRPlayer`**: radius 0.25, height ~1.6, step 0.3, slope 50°, matching Meta's `CharacterController`.
4. **New module `Scripts/XR/`** (asmdef `VRRobot.XR` → `VRRobot.Core`, `VRRobot.SimWorld`, `Oculus.Interaction`, `Unity.AI.Navigation`). It replaces `Desktop/` in the VR scene and contains:
   - `SimWorldNavMeshBaker`:
     - Unity `NavMeshSurface` with agent `VRPlayer`, collecting the children of `SimWorldRegistry.Root` and using physics colliders as geometry.
     - Baked on `SceneBuilt`. Raises `NavMeshReady` and logs the bake time and area.
   - `ReachableTeleportSurface : MonoBehaviour, ISurface`: delegates the raycast to Meta's `NavMeshSurface` and rejects hits that have no complete path from the player's feet.
   - `VrPlayerBootstrap`:
     - Keeps the locomotor disabled until `NavMeshReady`.
     - Then places the player at the NavMesh point closest to an optional `spawnPoint` (default: the rig's start position), using a Meta `LocomotionEvent` with absolute translation, and calls `EnableMovement()`.
     - Repeats this after a rebuild if the player is no longer on the NavMesh.
5. **Rig wiring in `VrScene`**, using Meta components and no custom input code:
   - New `Walkable Teleport Interactable`, based on the Teleport Interactable building block where possible:
     - `TeleportInteractable` with allowTeleport = true and surface = `ReachableTeleportSurface` (+ Meta `NavMeshSurface` with agent `VRPlayer`).
     - `ReticleDataTeleport`.
     - The existing blocker stays: walls, furniture and doors stop the arc.
   - `LocomotionSettings/Moving`: add the right `TeleportControllerInteractor` to the Slide list. Turning stays Snap 45°.
6. **Docs:**
   - `README_UNITY.md`: VR section (running with the simulator, locomotion design, NavMesh walkable logic).
   - `IMPLEMENTATION_PLAN.md` Stage 4: Meta Interaction SDK instead of XRI; locomotion done.
   - `CLAUDE.md`: lessons learned.

## Verification
1. Console without errors; EditMode tests still 16/16 green.
2. Desktop regression: `SimWorld_Desktop` still loads the house through the prefab.
3. `VrScene` in Play mode with the simulator, against `scene_house.ttt`:
   - the house loads;
   - the NavMesh is baked, with time and area logged;
   - the player spawns grounded inside the house;
   - a capture with `openxr_capture_composited_image` looks right.
4. Programmatic checks:
   - `ReachableTeleportSurface.Raycast` accepts floor points and rejects table/bed tops and points behind walls.
   - Slide input stops at walls.
5. Real input through the Meta XR Operator: `openxr_set_controller_input` for the thumbsticks, then compare player poses (`unity_get_world_pose`) and captures before and after each of these: walk, snap turn, teleport, wall collision.
6. Record the load time, NavMesh bake time and FPS in the simulator (`Mobile_RPAsset`), and compare visuals with the desktop scene (colors, transparency, textures).

## Execution status
- [x] Step 1: Active Input Handling = Input System Package (New) (`activeInputHandler: 1`), Editor restarted, console clean. Meta Project Setup Tool (Android): only 2 Recommended items left, both about the Platform SDK (Data Use Checkup, App ID), which is not used. Meta Quest build profile overrides the scene list with `VrScene` only.
- [x] Step 2: `Assets/_Project/Prefabs/SimWorld.prefab` created from the Desktop scene (Desktop keeps an instance); `VrScene` instance with `addMeshColliders = true`; VR Directional Light without shadows.
- [x] Step 3: NavMesh agent type `VRPlayer` (index 1): radius 0.25, height 1.6, climb 0.3, slope 50, cell size 0.083.
- [x] Step 4: `Scripts/XR/` (`VRRobot.XR`). Deviation: the baker uses the low-level `NavMeshBuilder` API (`CollectSources` under `SimWorldRegistry.Root` + `UpdateNavMeshDataAsync`) instead of the `NavMeshSurface` component, so the asmdef does not need `Unity.AI.Navigation`.
- [x] Step 5: `XR Locomotion` root (baker + bootstrap) with `Walkable Teleport Interactable` (Meta `NavMeshSurface` agent index 1, voxel 0.1, snap 0.05 → `ReachableTeleportSurface` → `TeleportInteractable` allowTeleport, tie-breaker 0; `ReticleDataTeleport`). Right `TeleportControllerInteractor` added to the Slide list of `MovingSetting` and activated.
- First simulator run (XR Operator MCP): house loads in 12.5–16.3 s; NavMesh bakes in ~150 ms. Walk, wall stop and snap turn 45° worked, but the player sometimes slipped into furniture/walls (Meta `CharacterController` uses `CapsuleCast`, which ignores colliders it already overlaps) and fell out of the house; slide speed ~3 m/s.
- [x] Fixes (agreed with the user: margin + guard, ~1.5 m/s):
  - `VRPlayer` agent: radius 0.35, height 2.0 (covers the Meta capsule, ~1.8 m, which follows the head), climb 0.2; Meta `CharacterController` maxStep 0.3 → 0.2 (it climbed a 0.28 m coffee table); `FirstPersonLocomotor` speedFactor 30 → 15.
  - New `CapsuleClearance` (capsule overlap test at a candidate feet position): the NavMesh is approximate near obstacles (a spawn point on the NavMesh edge was 0.155 m from a TV stand), so spawn and teleport targets are also validated against the real colliders.
  - `VrPlayerBootstrap`: spawn = closest clear NavMesh point searched in rings at the spawn's level (default spawn = rig origin; a global `SamplePosition` had picked the outside ground at y −0.66); recovery guard (inside geometry > 5 cm for 3 frames, or > 1 m below the last valid point → back to the last valid point).
  - Log prefix `[XRLoco]` (OpenXR already logs with `[XR]`).
- [x] Simulator validation (final run): NavMesh ~150–200 ms (105 m²); player spawned inside the house at (−1.63, 0.04, 0.35); 3 s walks in all directions stay on the floor level (the guard recovered twice: inside a dining chair 0.17 m, one fall); snap turn 45°; teleport with real input (`openxr_set_controller_pose` + right stick) moved the player from (−4.54, 3.86) to (−2.00, 2.46) with a valid (white) arc; arcs onto the kitchen aisle/island are shown invalid. Programmatic: countertop, coffee table and sofa tops, the outside ground and other rooms are rejected; floor points in the same room are accepted.
- [x] EditMode tests 16/16; Desktop regression: `SimWorld_Desktop` loads the house through the prefab (13.6 s, no errors). The Desktop fly camera was not re-tested with keyboard input.
- ~~Finding: all doors closed~~ — wrong, see Part 1b (the islands came from agent height 2.0 / radius 0.35).
- [x] Step 6: `README_UNITY.md` (VR scene section), `IMPLEMENTATION_PLAN.md` (Stage 4: Meta Interaction SDK, locomotion done), `CLAUDE.md` (status + VR locomotion lessons).
- [ ] Step 0 (commit, when the user asks).

## Part 1b — VR menu (minimal) + fly mode (approved 2026-11-08)
Context: the project's goal is the HMI to control the vacuum robot, not a perfectly realistic scene. Walking keeps the expected physics, but if the player gets stuck they can switch to a "creative" fly mode (no collisions) from a settings page of the VR menu, which will later also host the robot control table.

Correction: the earlier note that "all doors in `scene_house` are closed" was wrong. All door joints are at 90° (open) and Unity matches CoppeliaSim. The NavMesh islands came from the agent parameters: height 2.0 m does not fit under the door lintels (~2.05–2.09 m), and radius 0.35 m closes the narrower doorways. With radius 0.25 / height ≤ 1.9 all rooms are connected.

Steps:
1. NavMesh: `VRPlayer` radius 0.25, height 1.9 (the extra margin against walls/furniture comes from `CapsuleClearance`). Log the island count and areas after each bake. If the spawn lands on a tiny island, use the largest island at the spawn's level. Spawn = closest clear point to the CoppeliaSim origin.
2. VR menu: world-space uGUI canvas made interactive with Meta's Quick Action "Add Ray Interaction to Canvas" (`PointableCanvas`, `RayInteractable`, `PointableCanvasModule`). Left Menu button opens/closes it ~0.8 m in front of the eyes (world-locked while open); `VrMenuController` handles pages/tabs; first page **Settings** with a "Fly mode (creative)" toggle. While the menu is open, stick/trigger locomotion is paused so the trigger only clicks the UI. Prefab `Assets/_Project/Prefabs/VrMenu.prefab`.
3. Fly mode (`VrFlyMode`): on → `FirstPersonLocomotor.DisableMovement()`, guard and teleport off; left stick moves horizontally relative to the head, right trigger up / left trigger down (analog), snap turn unchanged, ~2 m/s, no collisions. Off → `EnableMovement()` where the player is ("fall where you are"); if inside geometry, the existing recovery guard brings the player back to the last valid point. Small "Fly mode" HUD label while active.
4. Simulator validation with the XR Operator (menu, toggle, fly up/down/through a wall, landing, teleport between rooms) + EditMode tests and Desktop regression.
5. Docs: this section's status, `README_UNITY.md`, `CLAUDE.md`.

### Part 1b status
- [x] Step 1: `VRPlayer` radius 0.25 / height 1.9 / climb 0.2. Bake: ~215 ms, 134 m², 4 islands (largest 98.7 = whole house interior, 33.4 = outside ground, 1.1). Living room, bedroom, bathroom, corridor/garage and dining are all reachable from the spawn; the outside ground is not. Spawn at (−0.37, 0.03, 0.33), next to the CoppeliaSim origin.
- [x] Step 2: `VR Menu` (prefab `Assets/_Project/Prefabs/VrMenu.prefab`): world-space uGUI + TMP canvas (TMP Essential Resources imported); Meta Quick Action "Add Ray Interaction to Canvas" applied through `QuickActionsAPI.AddRayCanvasInteraction` (added `RayInteractable`, `PointableCanvas`, plane surfaces and an EventSystem with `PointableCanvasModule`). Scripts: `VrMenuController`, `VrSettingsPage`, `LocomotionInputGate` (suspends left slide + slide actions, right turner, right teleport, right slide actions).
- [x] Step 3: `VrFlyMode` (+ `VrPlayerBootstrap.GuardPaused`), yellow "FLY MODE" HUD under the view.
- [x] Step 4 (simulator, real input through the XR Operator): left Menu button opens the menu in front of the player; right ray aimed at the toggle + trigger → fly mode on; right trigger up (y −0.01 → 3.57), left stick through the outer wall (x −0.37 → 5.68), left trigger down (→ 0.86); menu toggle again → fly mode off, the player fell onto the coffee table and walked off it to the floor without guard recoveries. EditMode 16/16. Desktop regression not re-run (nothing it uses changed).
- [x] Step 5: docs (`README_UNITY.md`, `CLAUDE.md`).
- [ ] Commit (when the user asks).

## Part 1c — Start flow (main menu + overview while building) and sky (approved 2026-11-09)
Context: the player used to stand in the middle of the scene while it was being built. Now the app opens on a main menu, the CoppeliaSim import starts only after the user confirms, the player watches the construction from an elevated viewpoint, and is then spawned at the best point. The black background outside the house is replaced by the default procedural sky (the same one the Editor shows).

Steps:
1. `CoppeliaSceneBuilder.Build()` (public; the VrScene instance overrides `buildOnStart = false`) and `SimWorldRegistry.HierarchyApplied` (raised after each `list`, before the meshes).
2. `AppFlowController` (`Scripts/XR/`): `MainMenu → Loading → Spawning → Playing`. Main menu panel (title, streamer endpoint, "Load CoppeliaSim scene" button), in-game menu and locomotion disabled. On confirm: build starts; when the hierarchy arrives, fade (Meta `OVRScreenFade` on `CenterEyeAnchor`) and move the rig to the overview; small loading panel (status, progress bar, last error while retrying) low and tilted in front of the player. When `SceneBuilt` + `NavMeshReady`: fade, `VrPlayerBootstrap.RequestSpawn()` (existing spawn logic), enable locomotion and the in-game menu. Later resynchronizations do not go back to the overview.
3. `OverviewFraming` (`Scripts/Core/`, pure function + EditMode tests): bounds of the visible shapes' positions → eye outside the scene at 35° elevation, at the distance that fits the bounding sphere in a 35° half angle, facing the center along the rig's initial forward. Computed once (no camera motion during the build).
4. Prefab `Assets/_Project/Prefabs/StartMenu.prefab` (same look and Meta ray canvas interaction as `VrMenu`).
5. Sky: `CenterEyeAnchor` clear flags Solid Color (black) → Skybox (`Default-Skybox`, procedural).
6. Validation in the simulator + EditMode tests; docs.

Deferred (agreed): "Back to main menu" in Settings; "Reconnect" button when the streamer drops during play.

### Part 1c status
- [x] Steps 1–5 implemented. `VrPlayerBootstrap` gained `_spawnOnNavMeshReady` (off in VrScene), `RequestSpawn()` and `Spawned`.
- [x] Simulator (real input through the XR Operator): main menu in front of the head over the procedural sky; right ray + trigger on "Load CoppeliaSim scene" → overview at (−1.03, 8.17, −5.00) looking at the house center (radius 7.1 m), meshes appearing with "Building meshes: n / 52" and the progress bar; scene built in 5.9 s, NavMesh 160 ms; spawn at (−0.37, 0.03, 0.33); left stick walked 2 m afterwards. Streamer unreachable (wrong port, runtime only): panel shows "Retrying connection..." with the timeout message while the builder retries. No errors in the console.
- [x] EditMode 21/21 (5 new `OverviewFramingTests`).
- [x] Docs (`README_UNITY.md`, `CLAUDE.md`).
- [ ] Commit (when the user asks).

## Part 1d — Height setting + menu follows the player (approved 2026-11-09)
Context (user test): there was no way to adjust the user's height, and after turning fly mode off from a high position the menu stayed up in the air, out of view, so the player could believe they were stuck.

Steps:
1. `VrHeightSetting` (on `XR Locomotion`): owns Meta's `FirstPersonLocomotor.HeightOffset` (view and capsule raised/lowered relative to the feet). Range −0.50…+0.25 m in 5 cm steps (above ~+0.25 m the capsule no longer fits under the door lintels), saved in `PlayerPrefs` (`VRRobot.HeightOffset`). With the locomotor disabled (fly mode, start flow) the rig is shifted directly.
2. Settings page: "Height offset" row with −, +, Reset and a label `+0.10 m (eyes 1.80 m)` (invariant culture). Applied to `VrMenu.prefab`.
3. `VrMenuController`: while open, the panel follows the Camera Rig's motion (translation + yaw around the head). Stick locomotion is gated while the menu is open, so this only reacts to the fall after fly mode, recovery teleports and height changes.
4. `VrFlyMode` landing subtracts `HeightOffset` from the rig height to get the feet.
5. Meta's `StandingSetting` (Locomotor/LocomotionSettings/Standing) also writes `HeightOffset` (0 when standing, +0.5 when seated) and reset the saved value at startup: its locomotor reference is cleared in `VrScene` (seated users use the height setting instead).

### Part 1d status
- [x] Steps 1–5 implemented.
- [x] Simulator (real input through the XR Operator): left Menu → Settings shows the height row; ray + trigger on "+" twice → offset +0.10, eyes 1.694 → 1.794 m and the panel rose with the view; fly mode on (toggle), menu closed, right trigger up to eyes 4.81 m, menu opened, toggle off → the player fell 3 m (eyes 1.79 m = 1.69 + 0.10) and the panel came down with them (y 4.71 → 1.69), still in front of the eyes. Restarted Play mode → offset +0.10 restored (eyes 1.79 m after spawn; failed before clearing `StandingSetting._locomotor`). Reset → 0.00, eyes 1.69 m.
- [x] EditMode 21/21. Console without errors.
- [x] Docs (`README_UNITY.md`, `CLAUDE.md`).
- [ ] Commit (when the user asks).

## Part 1e — Code review fixes (approved 2026-11-09)
A `/code-review medium` pass found 10 issues; all fixed. The user asked that, when the player cannot be spawned normally, an error is shown and the player starts at the CoppeliaSim origin (0, 0) in fly mode.

1. Spawn could wait forever behind a black screen → `VrPlayerBootstrap.SpawnFailed` + 5 s timeout in `AppFlowController.Spawn()` → **fallback**.
2. NavMesh failures were silent → `SimWorldNavMeshBaker.NavMeshFailed` / `FailureReason` (no sources, empty bake, missing registry/agent type) + 15 s timeout after `SceneBuilt` → **fallback**.
   - Fallback: fade, fly mode on (no collisions), rig at the CoppeliaSim origin with the feet on z = 0, turned towards the closest direction where the message panel fits (the origin is next to a wall in `scene_house`), panel "Could not place the player" with the reason and an OK button; locomotion menu enabled. `AppFlowController.UsedFallback` exposes it.
   - Turning fly mode off without any surface below and without a NavMesh keeps fly mode on (no fall into the void).
3. A rebake during fly mode re-placed the player on landing → `OnNavMeshReady` leaves the player alone while the guard is paused.
4. Landing after fly mode on the roof/outside ground/furniture → `VrPlayerBootstrap.ResolveLanding`: lands only on a clear NavMesh point of the island of the last valid position, otherwise teleports to the closest clear point of that island.
5. The overview put the scene at the bottom edge of the view → elevation 20°, half angle 20° (scene spans 0–40° below the horizon); the loading panel moved slightly above eye level (+0.12 m, tilted −10°) so it does not cover the scene. New EditMode test.
6. Recovery guard now ignores every collider of the rig (same rule as `CapsuleClearance`).
7. The small-island spawn fallback tries every larger island at the spawn level until one has a clear point.
8. Islands/area use only the triangles queryable by the `VRPlayer` agent (the triangulation contains all loaded NavMeshes); area computed once (sum of islands).
9. Loading status text is rebuilt only when phase/state/progress/error change.
10. Feet math centralized in `CapsuleClearance.FeetOf` / `CenterForFeet`; redundant registry lookup removed from `CoppeliaSceneBuilder.Build()`.

### Part 1e status
- [x] All 10 implemented.
- [x] Simulator: normal flow (overview shows the whole house with the panel above it; spawn at (−0.37, 0.03, 0.33)); rebake while flying → "player left where they are"; fly off above the outside (5.7, 2.0, 0.43) → landed inside at (2.28, 0.03, 0.81); spawn point forced 100 m away → fallback at the origin in fly mode, turned −30°, message readable; baker layers set to Nothing → "no collider sources" → same fallback; OK hides the panel; fly off without NavMesh → standing on the floor. NavMesh log back to 209 triangles / 133.9 m² after relaxing the own-triangle tolerance (5 → 20 cm).
- [x] EditMode 22/22.
- [x] Docs.
- [ ] Commit (when the user asks).
