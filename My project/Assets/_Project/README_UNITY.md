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

## Known issues / notes
- **Unfocused Editor**: without focus, Unity may stop advancing frames in Play mode. With the Editor focused (normal use) this does not happen. `PlayerSettings.runInBackground` is on.
- Color/mesh changes of an existing shape are not detected (only objects being added/removed/reparented, and poses). Use **Reload scene**.
- Vision sensors and other objects created in CoppeliaSim also trigger a `scene` event and show up as empty nodes.
- Editing scripts while in Play mode reloads the domain. The components fetch their references in `OnEnable` and the builder rebuilds the scene by itself.
