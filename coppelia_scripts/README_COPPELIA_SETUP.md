# CoppeliaSim → Unity scene streamer (Layer 1, protocol v1)

`zmq_scene_streamer.lua` is a *customization script* (CoppeliaSim 4.10, `sim-2` API, `simZMQ`) that exposes the open scene to Unity:

| Port | Socket | Role |
|---|---|---|
| 23005 | REP | Text requests: `info`, `list`, `mesh <h>`, `texture <id>` |
| 23006 | PUB | Local poses that changed (30 Hz simulating, 10 Hz stopped), full keyframe every 1 s, `scene` event |
| 23000 | — | CoppeliaSim ZMQ Remote API: only to install the script and for tests. It does **not** transfer meshes. |

## Usage

```powershell
cd coppelia_scripts
python -u install_streamer.py                 # installs into the open scene (removes previous instances)
python -u install_streamer.py --scene ..\scene_house\scene_house.ttt --save   # opens, installs and saves
python -u install_streamer.py --remove        # uninstalls
python -u test_zmq_client.py --expect-house   # validates the protocol against scene_house.ttt
python -u capture_reference_view.py UX UY UZ TX TY TZ out.png   # CoppeliaSim reference image from a Unity camera pose
```

The installer creates the `/ZmqSceneStreamer` dummy with the script as its child. The streamer ignores this dummy. If the scene is saved with the streamer, it starts by itself when the scene is opened. After editing the `.lua`, run the installer again.

Requirements: Python 3 with `coppeliasim-zmqremoteapi-client` and `pyzmq` (plus `numpy` and `Pillow` for `capture_reference_view.py`). Always use `python -u`. If the Remote API hangs ("ZMQ remote API server@addonScript: abort execution"), click that button in CoppeliaSim.

## Exported objects

Every object in the scene **except** cameras, lights, scripts and `/ZmqSceneStreamer` itself. Each object has an **effective parent**: the closest exported ancestor, or `-1` (world).

The **local pose** is `inv(worldPose(parent)) * worldPose(object)`, in CoppeliaSim's `{x, y, z, qx, qy, qz, qw}` format. Under a joint, the joint's intrinsic rotation appears in the **child's** pose, and the joint node represents the joint's base frame. So, when a door rotates, the message carries the pose of the door shape.

## Protocol v1

REQ → REP requests. The reply is multipart. On error, the reply is a single frame `{"error": "..."}`.

### `info`
```json
{"protocol":1, "scene":"C:\\...\\scene_house.ttt", "objectCount":106, "sceneObjectCount":117,
 "shapeCount":52, "sceneVersion":1, "posePort":23006, "simulationState":0}
```
- `objectCount`: number of exported objects.
- `sceneObjectCount`: total number of objects in the scene, excluding the streamer.

### `list`
```json
{"sceneVersion":1, "objects":[
  {"h":18, "name":"door", "path":"/house/door_backyard_diningroom/door", "type":"shape",
   "parent":17, "localPose":[x,y,z,qx,qy,qz,qw], "visible":true, "isShape":true, "components":1}, ...]}
```
Objects come in tree order, parents before children. `visible` is the effective visibility of the shape (the `visible` property, which takes layers into account).

### `mesh <h>`
5 frames: `[header JSON, vertices f32, indices i32, normals f32, uvs f32]`, little-endian. The shape components are concatenated.
```json
{"h":18, "components":[{"vOff":0,"vCount":24,"iOff":0,"iCount":36,"nOff":0,"nCount":108,
  "uvOff":0,"uvCount":0,"colors":[9 floats],"transparency":0,"options":0,"shadingAngle":0.35,
  "tex":-1,"texOptions":0,"texApplyMode":0}]}
```
- Offsets and counts are in **elements** (floats or int32), not bytes. `vCount` = 3 × number of vertices.
- The indices of each component are local (they start at 0 within the component's vertex block).
- `normals`: per triangle corner (`nCount = 3·iCount`). `uvs`: per corner (`uvCount = 2·iCount`), or 0 without a texture.
- Vertices and normals are in the shape frame (RH, Z up).
- `colors`: diffuse RGB, specular RGB, emission RGB. `options` bit0 = backface culling. `texOptions` bit0/bit1 = repeat U/V, bit2 = interpolation. `texApplyMode` 0 = modulate, 1 = decal, 2 = add.

### `texture <id>`
2 frames: `[{"id":..,"w":..,"h":..}, RGBA w·h·4 bytes]`. Textures are cached by id in Lua and shared between components.

### PUB 23006
- `["poses", {"seq":n,"t":tSim,"full":bool,"sceneVersion":v,"count":k}, int32 handles[k], f32 poses[7k]]`: only objects whose local pose changed (1e-5 threshold). `full=true` every 1 s carries all objects.
- `["scene", {"sceneVersion":v}]`: an object was added, removed or reparented (checked every 0.25 s). The client must run `list` again.

### Conversion to Unity
- Position `(x,y,z) → (-y, z, x)`.
- Quaternion `(qx,qy,qz,qw) → (qy, -qz, -qx, qw)`.
- Winding `(i0,i1,i2) → (i0,i2,i1)`.
- Same origin. The same rules apply to vertices and normals.

## Data measured on `scene_house.ttt` (test_zmq_client.py)
- 117 objects in the scene: 52 shapes, 45 dummies, 9 joints, 7 cameras, 4 lights. 106 objects are exported, 52 of them shapes.
- 944 components, 246,748 vertices, 1,344,471 indices and **16 unique textures** (30 textured components). The earlier inspection said 17; counting by id gives 16.
- 3 invisible shapes: `/house/roof`, `/ResizableFloorLarge/element` and `/ResizableFloorLarge/element/visibleElement`.
- Transferring all meshes over REQ: 24.6 MB in about 7 s, dominated by the `getShapeViz` extraction inside Lua.
