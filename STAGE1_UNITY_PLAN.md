# Plan: CoppeliaSim → Unity Converter (Stage 1, no VR)

## Context
Scope set by the user: implement only the reconstruction of the CoppeliaSim scene in Unity, testable in Play mode on the PC (no XR, no ROS). The architecture must leave a clear extension point so another agent can add VR later. It follows the original plan's decision: a dedicated Lua script with simZMQ (REP 23005 / PUB 23006). The Remote API (23000) is used only to install the script and for tests.

## Real `scene_house.ttt` data (inspected inside CoppeliaSim 4.10.0)
- 117 objects: 52 shapes, 45 dummies, 9 joints, 7 cameras, 4 lights (recounted with `sim.getObjectType`; an earlier version said "7 lights, 4 scripts"). Roots: `ResizableFloorLarge`, `house`, `scene_setup`, `objects`, cameras and lights. No `/myRobot`.
- 3 invisible shapes (`/house/roof` and the `ResizableFloorLarge` elements) → not rendered, but their nodes stay in the hierarchy.
- **944 mesh components**: large compounds such as `kitchnet` (482), `kitchen_drawer` (209), `tv_stand` and `hood` (31 each).
- **246,748 vertices / 1,344,471 indices** (~448k triangles). `coffee_maker` alone has 124k vertices and 229k triangles → 32-bit indices are mandatory.
- Normals **always per triangle corner** (n = number of indices). UVs also per corner. Indices start at 0, none out of range.
- **16 unique textures** by id (30 textured components), up to 512×512 RGBA (~10 MB in total), shared by id. (The first inspection said 17; counting by id gives 16.) `applyMode` 0 (modulate) and 1 (decal); `options` bit0/bit1 repeat U/V, bit2 interpolation.
- 9 color values per component (diffuse, specular, emission); 4 transparent components; 2 with backface culling.
- **All 52 shapes have the `static` flag**, including doors and cupboard boards attached to joints → the flag does not tell what moves. Motion is detected by pose changes.
- Some shapes are children of joints and dummies (`/house/bedroom/Cupboard/CupboardJointLeft/LeftBoard`).
- Operating lessons: `getShapeViz(h, i)` does not fail for out-of-range indices (use `#sim.getIntArrayProperty(h,'meshes')`). Transferring meshes through the Remote API freezes the add-on (CBOR serialization in Lua). Extraction inside CoppeliaSim takes < 1 s. `sim.createScript(sim.scripttype_customization, ...)` works and runs immediately → automatic installation validated.

## Design decisions
1. **Faithful hierarchy**: `list` returns **all objects** (shapes, dummies, joints) with parent, type and local pose. Unity recreates the tree with empty GameObjects for non-shapes. The stream sends **local poses** only for objects that changed: a rotating door only changes its subtree. This also serves the robot later (wheels under joints). CoppeliaSim cameras and lights are left out.
2. **One GameObject per shape** (52), not per component (944): components become **submeshes grouped by material** (color + transparency + texture). This reduces GameObjects and draw calls.
3. **Vertex welding**: since normals and UVs come per corner, Unity "de-indexes" and welds equal corners (position + normal + UV) with a hash. This preserves the original smoothing and reduces size.
4. **Compact transport**: `mesh <h>` returns 1 JSON header + 4 binary frames concatenating all components of the shape (offsets in the header), instead of 4 frames per component. Textures through `texture <id>`, cached by id on both sides.
5. **Incremental build**: Unity requests one shape at a time in a coroutine with a per-frame budget. The HUD shows progress. The whole scene (~25 MB of meshes) arrives in a few seconds over localhost.

## ZMQ Protocol v1
- **REP :23005** (text requests):
  - `info` → `{protocol, scene, objectCount, sceneObjectCount, shapeCount, sceneVersion, posePort, simulationState}`
  - `list` → `{sceneVersion, objects:[{h, name, path, type, parent, localPose[7], visible, isShape, components}]}`
  - `mesh <h>` → `[header JSON, vertices f32, indices i32, normals f32, uvs f32]`; header: `{h, components:[{vOff, vCount, iOff, iCount, nOff, nCount, uvOff, uvCount, colors[9], transparency, options, shadingAngle, tex, texOptions, texApplyMode}]}`
  - `texture <id>` → `[{id, w, h}, RGBA bytes]`
  - Error → `[{"error": "..."}]`
- **PUB :23006**: `["poses", {seq, t, full, sceneVersion, count}, int32 handles, f32 localPoses(7·n)]` with a full keyframe every 1 s; `["scene", {sceneVersion}]` when objects are added, removed or reparented (e.g. inserting `/myRobot`).
- CoppeliaSim (RH, Z-up) → Unity (LH, Y-up) conversion: position `(x,y,z) → (-y, z, x)`, quaternion `(qx,qy,qz,qw) → (qy, -qz, -qx, qw)`, winding `(i0,i1,i2) → (i0,i2,i1)`. The same rules apply to vertices and normals in the local frame.

## Files
**CoppeliaSim** (`projeto_final/coppelia_scripts/`)
- `zmq_scene_streamer.lua`: customization script. Non-blocking REP (poll 0) in `sysCall_nonSimulation`/`sysCall_sensing`, PUB with change detection (30 Hz simulating, 10 Hz stopped, so objects dragged in edit mode show up), texture cache, minimal JSON encoder and `LINGER 0`.
- `install_streamer.py`: through the Remote API, (re)installs the script in the open scene with `sim.createScript` (parented to a `ZmqSceneStreamer` dummy), optionally opens the `.ttt` and optionally saves the scene. Uses timeouts.
- `test_zmq_client.py`: validates `info`/`list`/`mesh`/`texture` and the pose stream against the real CoppeliaSim; moves an object through the Remote API and checks the event on 23006.
- `capture_reference_view.py`: renders a CoppeliaSim reference image from a Unity camera pose (temporary vision sensor) for visual comparison.
- `README_COPPELIA_SETUP.md`: protocol and usage.

**Unity** (`My project/Assets/_Project/`)
- `Plugins/NetMQ/`: NetMQ 4.0.1.13 + AsyncIO 0.1.69 + NaCl.Net 0.1.13 (netstandard) + `link.xml`. Installed by the agent, no manual step.
- `Scripts/Core/` (asmdef `VRRobot.Core`): `CoordinateConverter` (pure, testable).
- `Scripts/SimWorld/` (asmdef `VRRobot.SimWorld`):
  - `Protocol/`: DTOs (JsonUtility) and binary frame reading.
  - `Transport/`: `ZmqSceneClient` (REQ with timeout and reconnection, background thread) and `ZmqPoseStream` (SUB on a background thread, keeps the latest pose per handle, `scene` events). `AsyncIO.ForceDotNet.Force()` and `NetMQConfig.Cleanup(false)` in the lifecycle.
  - `Building/`: `ShapeMeshBuilder` (de-index → weld → convert axes → submeshes per material, UInt32) and `MaterialCache` (URP Lit, transparency, textures with wrap/filter from `options`, modulate × color).
  - `CoppeliaSceneBuilder` (MonoBehaviour): `info` → `list` → hierarchy → incremental meshes. Rebuilds what changed when a `scene` event arrives. Inspector with host/ports, hide invisible shapes, colliders, shadows.
  - `CoppeliaPoseSubscriber`: applies local poses on the main thread.
  - `SimWorldRegistry`: **extension point**. Handle/path → Transform; events `SceneBuilt`, `ObjectSpawned`, `ObjectRemoved`, `MeshReady`, `PosesApplied`. VR, ROS and the tactical table depend only on this.
- `Scripts/Desktop/` (asmdef `VRRobot.Desktop`, **replaceable by the XR Origin**): `DesktopFlyCamera` (WASD/QE + right mouse button, Input System) and `SimWorldDebugHud` (connection, progress, counts, reload button).
- `Tests/EditMode/`: converter (position, quaternion against rotation matrices, winding), welding and frame reading.
- `Scenes/SimWorld_Desktop.unity`: built by the agent through MCP (directional light, camera, `SimWorld` with builder and subscriber, HUD).
- `README_UNITY.md`: how to test and where to plug in VR (replace `Desktop/` with the XR Origin; consume `SimWorldRegistry`).

## Execution order
1. `zmq_scene_streamer.lua` + `install_streamer.py` + `test_zmq_client.py`, validated against `scene_house.ttt` (counts match the inspection above).
2. NetMQ + `Core` + EditMode tests passing.
3. `SimWorld`: transport → hierarchy → meshes/materials → poses → registry, in Play mode against CoppeliaSim.
4. `SimWorld_Desktop` scene + camera/HUD + READMEs.

## Verification
1. `test_zmq_client.py` passes: 117 objects, 52 shapes, 944 components, 16 textures.
2. Unity console without errors; EditMode tests green.
3. Play mode: Game view captures compared with CoppeliaSim for orientation, scale, colors, textures and compounds (`kitchnet`, `coffee_maker`). Roof and resizable floor hidden.
4. Dynamics: with the simulation stopped, rotating a door/joint through the Remote API → the door rotates in Unity. Creating a cube through the Remote API → it appears in Unity; removing it → it disappears.
5. Performance measured in Play mode (load time, FPS, draw calls) and recorded for the future Quest optimization (448k triangles may require decimation or LOD there).

## Execution status (2026-11-08)
Stage executed and verified against the real CoppeliaSim and the Unity Editor.

1. **CoppeliaSim**: `zmq_scene_streamer.lua`, `install_streamer.py`, `test_zmq_client.py` and `README_COPPELIA_SETUP.md` are done. `test_zmq_client.py --expect-house` **passes**: 117 objects (106 exported), 52 shapes, 3 invisible, 944 components, 246,748 vertices, 1,344,471 indices and 16 textures. The local pose matches `sim.getObjectPose(h, parent)` (error 3.5e-8). The pose delta of a joint arrives, and creating/removing a cube produces `scene` events.
2. **Unity**: NetMQ installed; asmdefs `VRRobot.Core`, `VRRobot.SimWorld`, `VRRobot.Desktop` and `VRRobot.Tests.EditMode`; **16/16 EditMode tests green**; console without errors.
3. **Play mode** (`SimWorld_Desktop.unity`): 106 objects, 52 shapes, 134 submeshes, 538,964 welded vertices (from 1.34M corners), 448,157 triangles, 90 materials and 16 textures in **10.6 s**.
   - Side-by-side comparison with CoppeliaSim vision sensor images (same camera pose) matches in the overview, the kitchen (`kitchnet`, `coffee_maker`), the picture frame and the TV: geometry, scale, orientation, colors and texture orientation (no flip). Roof and resizable floor are hidden.
4. **Dynamics**: door rotated through the Remote API → same world pose in Unity (error 0). Cube created → appears at the converted position; moved → follows; removed → disappears.
5. **Performance** (Editor, PC, shadows off): overview render 2.3 ms, 267 draw calls, ~0.9M rendered triangles, ~159 fps. The Quest will need decimation/LOD.
6. **Second scene**: the user validated the converter with `scene_house/p3dx_nav2.ttt` as well.

### Deviations from the plan
- `HMI/` was not created yet (Stages 4–6).
- The local pose is computed as `inv(world(parent))·world(obj)` in plain Lua, so the joint's intrinsic rotation shows up in the child (`sim.multiplyPoses` rejected the `getObjectPose` tables).
- Added `SimObjectRef` (handle/path on every GameObject), the `MeshReady` registry event and `Materials/CoppeliaLitTemplate.mat` (keeps the shader in builds).
- **Shadows off** at the user's request (CoppeliaSim does not render them): `castShadows` option (default false) in the builder and Directional Light with `shadows = None`. Overview render: 4.1 → 2.3 ms; 446 → 267 draw calls.
- `install_streamer.py` installs the streamer in the open scene without saving it. To embed it in the `.ttt`: `install_streamer.py --save`.
