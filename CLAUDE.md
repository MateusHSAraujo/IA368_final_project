# IA368 — Final Project (VR HMI + Digital Twin, Pioneer P3-DX)

Reference documents:
- `PROJECT_PROPOSAL.md`: overview.
- `IMPLEMENTATION_PLAN.md`: revised stage-by-stage plan. Section 2.4 lists the open architecture items for Stages 3–6.
- `STAGE1_UNITY_PLAN.md`: detailed plan of Stage 1 (CoppeliaSim → Unity converter, no VR), with its execution status. **It is the source of truth for the Windows-side execution**; keep it updated if the plan changes.

## Language
Everything committed to the repositories is in **English**: code, comments, log/UI strings, documentation, file and folder names, and commit messages.

## Repositories and division of work
- **Windows (this folder), repo `IA368_final_project`** (`git@github.com:MateusHSAraujo/IA368_final_project.git`): Unity project, CoppeliaSim scripts and scenes, planning documents. Agent: **Claude Code on Windows** — Unity and CoppeliaSim sides of Layer 1 (ZMQ scene converter). Stage 1 is done (no VR, tested in Play mode on the PC).
- **WSL2 (Ubuntu 24.04), repo `IA368_final_project_ros2`** (`git@github.com:MateusHSAraujo/IA368_final_project_ros2.git`) in `/home/mateus/ia368/projeto_final` (from Windows: `\\wsl.localhost\Ubuntu-24.04\home\mateus\ia368\projeto_final`): ROS 2 Jazzy, Nav2, slam_toolbox. Agent: **Claude Code on WSL**. Its planning documents are symlinks into `windows_workspace/` (this folder), so edits made there land here and are committed in this repository.
- VR (XR Origin, Quest 3) will be added later: it replaces the `Desktop/` module and consumes `SimWorldRegistry`.

## Git
- Remotes use SSH; the SSH keys work without prompting on both Windows and WSL.
- The repository root is this folder. The Unity rules live in `My project/.gitignore` and `My project/.gitattributes` (paths relative to the Unity project); the root `.gitattributes` holds the `[attr]` macros (`lfs`, `unity-json`, `unity-yaml`), because Git only accepts macros in the top-level file.
- **Git LFS** (`git lfs install --local`): `*.ttt` scenes plus the Unity binary rules (images, DLLs, ...). The first commit (`6cb7c45`) still stores them as plain blobs; LFS starts from the commit that re-enabled it. The GitHub repository setting "Include Git LFS objects in archives" must be enabled, otherwise LFS pushes are refused with "exceeded its LFS budget".
- `projeto_final/Assets/` (outside the Unity project) is obsolete and ignored.

## Locations
- Unity project: `My project/` — Unity **6000.0.44f1**, URP, Input System. Project code in `My project/Assets/_Project/` (Core / SimWorld / Desktop, each with an asmdef; HMI will come in Stages 4–6).
- CoppeliaSim scripts: `coppelia_scripts/`. Scenes: `scene_house/scene_house.ttt` (reference) and `scene_house/p3dx_nav2.ttt`.
- **Windows is the master copy** of `My project/`, `coppelia_scripts/`, `scene_house/` and the planning documents. The WSL `sync_to_windows.sh` copies only `scripts/*.ps1`.
- The Unity Editor is reachable through MCP (`unity-mcp`): create scenes/GameObjects, read the console, run C# commands.
- NetMQ: 4.0.1.13 (netstandard2.1) + AsyncIO 0.1.69 + NaCl.Net 0.1.13, downloaded from `https://www.nuget.org/api/v2/package/<id>/<version>`, in `Assets/_Project/Plugins/NetMQ/`. Requires `AsyncIO.ForceDotNet.Force()` and `NetMQConfig.Cleanup(false)` on exit (otherwise the Editor hangs).

## Layer 1 architecture (CoppeliaSim → Unity)
- Dedicated Lua script in the scene (`zmq_scene_streamer.lua`, simZMQ): **REP 23005** (`info`, `list`, `mesh <h>`, `texture <id>`) and **PUB 23006** (local poses that changed + keyframe every 1 s; `scene` event when objects are added or removed). Full protocol: `coppelia_scripts/README_COPPELIA_SETUP.md`.
- The ZMQ Remote API (**23000**) is used to install the script (`sim.createScript(sim.scripttype_customization, ...)`), for tests and for the ROS nodes. It is **not** used to transfer meshes.
- CoppeliaSim (RH, Z-up) → Unity (LH, Y-up) conversion: position `(x,y,z) → (-y, z, x)`; quaternion `(qx,qy,qz,qw) → (qy, -qz, -qx, qw)`; winding `(i0,i1,i2) → (i0,i2,i1)`. Same origin.

## CoppeliaSim 4.10.0 EDU (`C:\Program Files\CoppeliaRobotics\CoppeliaSimEdu`)
- `sim-2` API: `sim = require 'sim'`, `simZMQ = require 'simZMQ'`; the shape type is `sim.sceneobject_shape`. Constants `simZMQ.REP/PUB/SNDMORE/DONTWAIT/LINGER/POLLIN`; `simZMQ.poll({sock},{simZMQ.POLLIN},0)`. Usage reference: `addOns/ZMQ remote API server.lua`.
- There is no JSON module in Lua (there are `org.conman.cbor`, `messagePack`, `base64`): use a minimal custom JSON encoder.
- `sim.getShapeViz(h, i)` **does not fail** for out-of-range indices. The component count comes from `#sim.getIntArrayProperty(h, 'meshes')`.
- In `sim-2`, `sim.packFloatTable`/`packInt32Table` and `texture.texture` are **buffer objects**, not strings: use `tostring(buf)` before `table.concat`.
- `sim.createVisionSensor(options, ...)`: bit0 = explicit handling (required for `handleVisionSensor`), bit1 = perspective (without it the sensor is orthographic). Useful to render CoppeliaSim reference images (`coppelia_scripts/capture_reference_view.py`).
- `getShapeViz` returns vertices and normals in the shape frame, `colors` with 9 values and `texture` (`texture` RGBA buffer, `id`, `resolution`, `coordinates`, `applyMode`, `options`).
- Local manual: `manual/en/regularApi/sim*.htm`, `manual/en/simZMQ.htm`, `manual/en/propertiesReference.htm`.

## Operating CoppeliaSim through the Remote API (practical lessons)
- Global Windows Python: `python` (3.10) with `coppeliasim-zmqremoteapi-client` 2.0.4 and `pyzmq`. Do not use `python -I` (the packages are in the user site). Use `python -u` (unbuffered).
- Always set a timeout: `client.socket.setsockopt(zmq.RCVTIMEO, 5000..20000)`. Without it, the client blocks forever.
- **Never** pull large meshes and textures through the Remote API: the CBOR serialization in Lua freezes the add-on. To inspect the scene, create a temporary customization script with `sim.createScript`, write the summary with `sim.setStringProperty(sim.handle_scene, 'customData.<key>', ...)`, read it with `sim.getStringProperty(..., {'noError': True})` and remove it with `sim.removeObjects([h])`.
- If the add-on hangs, CoppeliaSim shows the **"Connectivity >> ZMQ remote API server@addonScript: abort execution"** button. Only the user can click it; ask them. Check whether the server is alive with a simple call and a 5 s timeout. The fps in the window title is not reliable.
- Scripts that use `sim.setStepping(True)`/`startSimulation()` must call `stopSimulation()` and `setStepping(False)` in a `finally` block.
- `netstat`/`Get-NetTCPConnection` in this shell may not list CoppeliaSim sockets: test the port by connecting to it.

## Unity through MCP (practical lessons)
- With the Editor **unfocused**, Play mode may stop at frame 2. Temporary fix (until the next domain reload): through `Unity_RunCommand`, `EditorApplication.update += () => { if (EditorApplication.isPlaying && !EditorApplication.isPaused) EditorApplication.QueuePlayerLoopUpdate(); };`.
- `Unity_Camera_Capture` fails in Play mode. To capture: `cam.targetTexture = RenderTexture` + `cam.Render()` + `ReadPixels` → PNG in `%TEMP%`. Do not call `cam.Render()` without a `targetTexture` (it produces URP RenderPass errors).
- **Do not edit C# while in Play mode**: it reloads the domain in the middle of the test.
- `Unity_RunCommand` logs do not accept `{0:F2}`; format with `.ToString("F2")`. `System.Reflection` is blocked.
- Compile errors: `grep "error CS" %LOCALAPPDATA%/Unity/Editor/Editor.log`. EditMode tests: `TestRunnerApi` with a callback that writes the result to a file.

## `scene_house.ttt` data
117 objects (52 shapes, 45 dummies, 9 joints, 7 cameras, 4 lights), no `/myRobot`; the streamer exports 106 (no cameras/lights). 944 mesh components (`kitchnet` 482, `kitchen_drawer` 209), 246,748 vertices / 1,344,471 indices (~448k triangles; `coffee_maker` has 229k). Normals and UVs **per triangle corner**, 0-based indices. 16 textures (unique ids) up to 512×512. Roof and `ResizableFloorLarge` invisible. **Every shape has the `static` flag**, including doors attached to joints → detect motion by pose changes.
