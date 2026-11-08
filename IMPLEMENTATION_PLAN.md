# Implementation Plan for Autonomous Agents (Claude Code on WSL2 / Claude Code on Windows)

> [!IMPORTANT]
> **Critical operating constraints for the implementing agents:**
> 1. **Base scene for the environment parser (`scene_house`):**
>    The reference scene for the CoppeliaSim $\rightarrow$ Unity parser of **Stage 1** is [`scene_house/scene_house.ttt`](scene_house/scene_house.ttt) (Windows: `C:\Users\mateu\unicamp\IA368\projeto_final\scene_house\scene_house.ttt`; WSL: `windows_workspace/scene_house/scene_house.ttt`). It is the complete household environment (walls, floors, rooms, furniture) and **does not contain the robot yet**. The ZMQ parser (`zmq_scene_streamer.lua` and `CoppeliaSceneBuilder.cs`) must be tested against this scene, robustly extracting simple and compound static meshes. It must work with or without the robot: if `/myRobot` is absent it builds the whole house; when `/myRobot` (Pioneer P3-DX) is later inserted into the scene, the dynamic pose stream on port `23006` starts synchronizing the robot automatically.
> 2. **Standard target robotic platform (Pioneer P3-DX):**
>    The robot to be integrated into the scene and controlled by the ROS 2 stages is the **Pioneer P3-DX (`/myRobot`)** with differential kinematics ($r = 0.0975\text{ m}, L = 0.331\text{ m}$, motors `/leftMotor` and `/rightMotor`), 2D LiDAR (publishing `/myRobot/scan`), front Vision Sensor (`/camera/image_raw`) and Ground Truth odometry (`/myRobot/odom` and the `odom -> base_link` TF).
> 3. **Deterministic spatial alignment (zero calibration):**
>    Unity and CoppeliaSim/ROS 2 share the origin $(0,0,0)$. The spatial alignment between the Unity world and the ROS 2 `/map` frame is purely geometric and constant:
>    $$x_{\text{ros}} = z_{\text{unity}}, \quad y_{\text{ros}} = -x_{\text{unity}}, \quad z_{\text{ros}} = y_{\text{unity}}$$
>    No ArUco fiducial markers or manual alignment routines are needed.
> 4. **CoppeliaSim access (revised):** CoppeliaSim EDU 4.10 runs on Windows and **can be operated by agents** through the ZMQ Remote API (port `23000`), using the Python client `coppeliasim-zmqremoteapi-client` 2.x installed in the global Windows Python. Scene converter tests (Stage 1) run **against the real CoppeliaSim**, without mock servers. Operating rules (problems observed in practice):
>    * Run scripts with `python -u` (unbuffered) and always with a timeout: the client blocks forever in `recv()` if the server does not reply.
>    * If the server stops replying and CoppeliaSim shows the **"Connectivity >> ZMQ remote API server@addonScript: abort execution"** button, the add-on is stuck. Clicking it aborts the add-on, which restarts by itself. An agent cannot click this button: it must detect the timeout and ask the user.
>    * Never leave the server in *stepping* mode: every script that calls `sim.setStepping(True)` or `sim.startSimulation()` must call `sim.stopSimulation()` and `sim.setStepping(False)` in a `finally` block.
>    * `sim.getShapeViz(h, i)` **does not fail** for out-of-range indices: the component count must come from `#sim.getIntArrayProperty(h, 'meshes')`. A loop that waits for an error/`nil` never ends and freezes the add-on.
>    * **Do not transfer large meshes/textures through the Remote API**: the reply is serialized to CBOR by Lua code, and a single `getShapeViz` of a heavy shape froze the add-on for more than 20 s. Bulk data must be extracted inside CoppeliaSim (Lua script) and sent in binary (`sim.packFloatTable`), as `zmq_scene_streamer.lua` does.
>    * To check whether the server is alive, use a simple call with a short timeout (`socket.RCVTIMEO = 5000`). The fps in the window title is not a reliable indicator.
>    * ROS 2 nodes on WSL keep tolerating the absence of CoppeliaSim (retry loop or `mock_mode:=true`) for automated tests on the ROS side.
> 5. **Unity Editor access (revised):** The Unity project lives in `C:\Users\mateu\unicamp\IA368\projeto_final\My project\` (Unity **6000.0.44f1**, URP, Input System). The Unity-side agent (Claude Code on Windows) accesses the Editor through MCP (`unity-mcp`): it creates scenes, GameObjects and assets, reads the compile console and runs tests. C# scripts are written **directly** in `My project/Assets/_Project/`. The `unity_scripts/` copy in WSL no longer exists.
> 6. **Hybrid directory architecture (WSL2 + Windows) (revised): two Git repositories, one per machine.**
>    * **WSL2 (`ext4`), repository `IA368_final_project_ros2`:** `/home/mateus/ia368/projeto_final/` hosts the ROS 2 workspace (`ros2_ws/`), the ROS-side Python tests (`tests/`) and `scripts/*.sh`. The planning documents in it are **symbolic links** into `windows_workspace/`, so there is a single copy.
>    * **Windows (`NTFS`), repository `IA368_final_project`:** `C:\Users\mateu\unicamp\IA368\projeto_final` (`/mnt/c/...`, symlink `windows_workspace` in WSL) is the **master copy** of the Unity project (`My project/`), the CoppeliaSim scripts (`coppelia_scripts/`), the scenes (`scene_house/`), `scripts/*.ps1` and the planning documents (`PROJECT_PROPOSAL.md`, `IMPLEMENTATION_PLAN.md`, `STAGE1_UNITY_PLAN.md`).
>    * **Sync rule:** `sync_to_windows.sh` copies **only** `scripts/*.ps1`. It must never copy documents, C#, Lua or scenes (it would overwrite the Windows master copy). The `projeto_final/Assets/` folder (outside the Unity project, created by the old sync) is obsolete and ignored by Git.
> 7. **Verification strategy (revised):** ROS side: `colcon build`, `pytest` and *Mock Action Servers* on WSL2. Unity/CoppeliaSim side: compilation without errors in the Unity console, EditMode tests (Unity Test Framework) and Play mode against the real CoppeliaSim.

---

## 0. Environment Context and Existing Reference Assets

Before creating any code from scratch, use the resources already installed and validated on this machine:

* **Operating system:** Windows host + WSL2 guest (Ubuntu 24.04 Linux).
* **ROS 2 distribution:** **ROS 2 Jazzy** (`/opt/ros/jazzy/setup.bash`).
* **Installed Python libraries:** `coppeliasim-zmqremoteapi-client`, `numpy`.
* **Household base scene:** [`scene_house/scene_house.ttt`](scene_house/scene_house.ttt) (rooms, walls and furniture for Stage 1).
* **Course reference repository (`/home/mateus/ia368/IA368ii/IA368_ws`):**
  Contains working code already tested with the Pioneer P3-DX (`/myRobot`) in CoppeliaSim that **must be used as a direct base**:
  * Nav2 Jazzy launch: [nav2_jazzy.launch.py](file:///home/mateus/ia368/IA368ii/IA368_ws/src/ia368_pkg/launch/nav2_jazzy.launch.py)
  * SLAM Toolbox launch: [slam_toolbox.launch.py](file:///home/mateus/ia368/IA368ii/IA368_ws/src/ia368_pkg/launch/slam_toolbox.launch.py)
  * LiDAR bridge (`/myRobot/scan`): [lidar.py](file:///home/mateus/ia368/IA368ii/IA368_ws/src/ia368_pkg/slam_toolbox/lidar.py)
  * TF bridge (`odom -> base_link`): [tf_node.py](file:///home/mateus/ia368/IA368ii/IA368_ws/src/ia368_pkg/slam_toolbox/tf_node.py)
  * Ground Truth odometry bridge (`/myRobot/odom`): [ground_truth_node.py](file:///home/mateus/ia368/IA368ii/IA368_ws/src/ia368_pkg/nav2/ground_truth_node.py)
  * P3-DX velocity bridge (`/cmd_vel` $\rightarrow$ `/leftMotor`, `/rightMotor`): [velocity_node.py](file:///home/mateus/ia368/IA368ii/IA368_ws/src/ia368_pkg/position_control/velocity_node.py)
  * Front camera bridge (`sim.getVisionSensorImg` + `sim.transformImage`): [kinect.py](file:///home/mateus/ia368/IA368ii/IA368_ws/src/ia368_pkg/rtabmap/kinect.py)
  * Validated Nav2 and SLAM parameters: [nav2_params_jazzy.yaml](file:///home/mateus/ia368/IA368ii/IA368_ws/config/nav2_params_jazzy.yaml) and [mapper_params_online_async.yaml](file:///home/mateus/ia368/IA368ii/IA368_ws/config/mapper_params_online_async.yaml)

---

## 1. Target Directory Structure (Hybrid WSL2 + Windows)

```text
# 1. WSL2 base directory (ext4 - fast for ROS 2 / colcon / pytest) — repo IA368_final_project_ros2
/home/mateus/ia368/projeto_final/
├── CLAUDE.md                           # Instructions for the WSL agent
├── README.md
├── PROJECT_PROPOSAL.md    -> windows_workspace/PROJECT_PROPOSAL.md      (symlink)
├── IMPLEMENTATION_PLAN.md -> windows_workspace/IMPLEMENTATION_PLAN.md   (symlink)
├── STAGE1_UNITY_PLAN.md   -> windows_workspace/STAGE1_UNITY_PLAN.md     (symlink)
├── windows_workspace -> /mnt/c/Users/mateu/unicamp/IA368/projeto_final  (not versioned)
├── scripts/
│   ├── sync_to_windows.sh              # Copies scripts/*.ps1 to Windows (nothing else)
│   ├── setup_adb_reverse.ps1           # PowerShell script (Windows) for adb reverse and portproxy
│   └── get_windows_host_ip.sh          # Bash helper that detects or receives the Windows host IP
├── tests/                              # Offline automated tests for the ROS side (WSL2)
│   ├── test_minimap_uv_math.py         # Validates UV -> ROS metric conversions, waypoint sampling and keepout mask
│   ├── test_mission_supervisor.py      # Tests NavigateToPose, NavigateThroughPoses, Pause, Resume, Cancel and Keepout
│   └── test_twist_mux_offline.py       # Tests twist_mux priority (/cmd_vel_teleop vs /cmd_vel_nav)
├── ros2_ws/
│   └── src/
│       ├── ROS-TCP-Endpoint/           # Official Unity-Technologies/ROS-TCP-Endpoint repository (main-ros2 branch)
│       └── vr_robot_pkg/               # Main ROS 2 package of the final project
│           ├── config/
│           │   ├── mapper_params_online_async.yaml
│           │   ├── nav2_params_jazzy.yaml  # Configured with the Costmap Filter (Keepout Zone)
│           │   └── twist_mux.yaml
│           ├── launch/
│           │   └── vr_robot_bringup.launch.py
│           ├── vr_robot_pkg/
│           │   ├── __init__.py
│           │   ├── coppelia_bridge_node.py    # P3-DX bridge (LiDAR, Odom, TF, Vel, Camera) with mock_mode
│           │   └── mission_supervisor_node.py # Supervisor: NavigateToPose, NavigateThroughPoses, Keepout Mask, Pause/Resume
│           ├── package.xml
│           └── setup.py

# 2. Windows master directory (NTFS - Unity Editor / Gradle APK / CoppeliaSim) — repo IA368_final_project
C:\Users\mateu\unicamp\IA368\projeto_final\   (/mnt/c/Users/mateu/unicamp/IA368/projeto_final)
├── CLAUDE.md                           # Instructions for the Windows agent
├── PROJECT_PROPOSAL.md
├── IMPLEMENTATION_PLAN.md
├── STAGE1_UNITY_PLAN.md
├── scene_house/                        # CoppeliaSim scenes
│   ├── scene_house.ttt                 # Base scene (open in CoppeliaSim)
│   └── p3dx_nav2.ttt                   # Second scene, used to validate the converter on other scenes
├── coppelia_scripts/                   # Stage 1 (CoppeliaSim side)
│   ├── zmq_scene_streamer.lua          # Lua customization script (simZMQ: REP 23005 / PUB 23006)
│   ├── install_streamer.py             # Installs/updates the script in the open scene via the Remote API (no manual steps)
│   ├── test_zmq_client.py              # Validates the protocol against the real CoppeliaSim
│   ├── capture_reference_view.py       # Renders a CoppeliaSim reference image to compare with Unity
│   └── README_COPPELIA_SETUP.md        # Protocol contract and instructions
├── scripts/                            # setup_adb_reverse.ps1
└── My project/                         # Unity 6000.0.44f1 project (URP)
    └── Assets/_Project/
        ├── Plugins/NetMQ/              # NetMQ 4.0.1.13 + AsyncIO + NaCl.Net (netstandard) + link.xml
        ├── Scripts/
        │   ├── Core/                   # asmdef VRRobot.Core: CoordinateConverter (pure math)
        │   ├── SimWorld/               # asmdef VRRobot.SimWorld — Layer 1 (ZMQ)
        │   │   ├── Protocol/           # DTOs + binary frame decoding
        │   │   ├── Transport/          # ZmqSceneClient (REQ), ZmqPoseStream (SUB), background threads
        │   │   ├── Building/           # ShapeMeshBuilder, MaterialCache
        │   │   ├── CoppeliaSceneBuilder.cs
        │   │   ├── CoppeliaPoseSubscriber.cs
        │   │   └── SimWorldRegistry.cs # EXTENSION POINT for VR/ROS (handle/name -> GameObject, events)
        │   ├── Desktop/                # asmdef VRRobot.Desktop: free camera + HUD (replaceable by the XR Origin)
        │   └── HMI/                    # (Stages 4–6, future) VR Menu, Tactical Table, Path/No-Go, Teleop
        ├── Tests/EditMode/             # Converter and decoder tests
        └── Scenes/SimWorld_Desktop.unity
```

---

## 2. Mathematical Modeling of Transforms and Pioneer P3-DX Kinematics

### 2.1. Pioneer P3-DX Inverse Differential Kinematics
Given the linear velocity $v_x$ and angular velocity $\omega_z$ from `/cmd_vel` (`geometry_msgs/Twist`), with wheel radius $r = 0.0975\text{ m}$ and *wheelbase* $L = 0.331\text{ m}$:
$$\omega_{\text{right}} = \frac{v_x + \frac{L}{2}\omega_z}{r}, \quad \omega_{\text{left}} = \frac{v_x - \frac{L}{2}\omega_z}{r}$$
These angular velocities in rad/s are applied directly to the `/rightMotor` and `/leftMotor` joints in CoppeliaSim.

### 2.2. Deterministic Axis Conversion: ROS 2 $\leftrightarrow$ Unity
With coincident origins at $(0,0,0)$:
* **Position:** $x_u = -y_r, \quad y_u = z_r, \quad z_u = x_r \quad \Longleftrightarrow \quad x_r = z_u, \quad y_r = -x_u, \quad z_r = y_u$
* **Quaternions:** $qx_u = qy_r, \quad qy_u = -qz_r, \quad qz_u = -qx_r, \quad qw_u = qw_r$
* **Triangle winding order:** Swap the last two indices $(i_0, i_1, i_2) \rightarrow (i_0, i_2, i_1)$.

### 2.3. Holographic Tactical Table Math (UV Interaction $\rightarrow$ ROS Metric)
The controller *Raycast* hits the flat collider of the Holographic Tactical Table and returns the normalized texture coordinates $(u, v) \in [0.0, 1.0] \times [0.0, 1.0]$:
1. **Grid coordinates (cells):**
   $$\text{col} = \lfloor u \cdot \text{width} \rfloor, \quad \text{row} = \lfloor v \cdot \text{height} \rfloor$$
2. **Metric coordinates in the ROS 2 `/map` frame:**
   $$x_{\text{map}} = \text{origin.position.x} + (\text{col} + 0.5) \cdot \text{resolution}$$
   $$y_{\text{map}} = \text{origin.position.y} + (\text{row} + 0.5) \cdot \text{resolution}$$
3. **No-Go Zone brush mapping (Keepout Filter):**
   For every painted point within the brush radius $R_{\text{brush}}$ around $(u, v)$:
   $$\text{index} = \text{col} + \text{row} \cdot \text{width}, \quad \text{mask\_data}[\text{index}] = 100$$
   Published on `/keepout_filter_mask` as `nav_msgs/OccupancyGrid`.

---

## 2.4. Open Items from the Architecture Review (affect Stages 3–6)

> Problems found while reviewing the original plan, to be solved when each stage is implemented:
> 1. **ROS-TCP-Connector is not a ROS 2 Action client.** Unity does not call `NavigateToPose`/`NavigateThroughPoses` directly: it publishes simple topics (`/hmi/goal` PoseStamped, `/hmi/path` nav_msgs/Path, `/hmi/mission_cmd` String), and `mission_supervisor_node` is the only Nav2 Action Client.
> 2. **Nav2 has no native Pause/Resume.** The supervisor implements *Pause* by canceling the action and storing the remaining waypoints (feedback `number_of_poses_remaining`), and *Resume* by sending them again.
> 3. **The Keepout Filter** needs `costmap_filter_info_server` (`/costmap_filter_info`, type=0) and a mask with `transient_local` QoS, which ROS-TCP-Endpoint does not provide. Unity sends the painted strokes (meters + radius); the supervisor rasterizes them into a fixed grid (house bounding box) and publishes the latched mask.
> 4. **`RaycastHit.textureCoord` only works with a `MeshCollider`** and an unscaled table. Convert the hit point to the table's local space (`InverseTransformPoint`) and from there to meters, continuously (no `floor(...)+0.5`).
> 5. **The slam_toolbox `/map` grows and changes `origin`** while mapping: the table texture and aspect ratio must adapt to every message.
> 6. **Camera**: raw `sensor_msgs/Image` through ROS-TCP-Endpoint saturates the bridge (~27 MB/s at 640×480/30 Hz). Use `CompressedImage` (JPEG, ~320×240, 10–15 Hz), enabled only in teleop mode.
> 7. **Table interaction**: grab only by the frame (grip); paint on the surface (trigger) according to the active tool.
> 8. **Teleop**: *deadman* (hold grip), publishing at ≥10 Hz and automatic mission pause when taking control. First-person mode as a **floating panel** (switching the whole view causes motion sickness).
> 9. **Operator feedback**: show `/plan` (nav_msgs/Path) on the table and on the floor, plus the mission state.
> 10. **Quest 3 performance**: the house has ~448k triangles; evaluate decimation or LOD for dense objects (`coffee_maker`, `fruit_basket`, `tv_stand`) when porting to the headset.
> 11. **Network**: consider `networkingMode=mirrored` in WSL2 (shared localhost) instead of `portproxy`; for development, Quest Link + Play mode before building an APK.

---

## 3. Step-by-Step Stage Details

---

### Stage 1: ZMQ Bridge and Household Scene Parser (`scene_house` $\rightarrow$ Unity) — **done** (see `STAGE1_UNITY_PLAN.md`)

#### Goal
Create the Lua script (`simZMQ`) in CoppeliaSim with a `REP` (`23005`) and a `PUB` (`23006`) socket, and the C# scripts (`NetMQ`) in Unity that procedurally instantiate the full geometry of [`scene_house.ttt`](scene_house/scene_house.ttt) (walls, floors, doors, furniture) at runtime with `new Mesh()`, adding colliders and keeping dynamic pose streaming of moving objects and of the Pioneer P3-DX ready.

> **Revised scope:** Stage 1 is implemented and tested **without VR**, in Play mode on the PC (desktop free camera). Adding VR (Stage 4) only replaces the `Desktop/` module with the XR Origin and consumes `SimWorldRegistry`.

#### Real `scene_house.ttt` data (inspected inside CoppeliaSim 4.10.0)
* 117 objects (52 shapes, 45 dummies, 9 joints, 7 cameras, 4 lights); no `/myRobot`. 3 invisible shapes (roof and the resizable floor).
* **944 mesh components** (e.g. `kitchnet` with 482, `kitchen_drawer` with 209); **246,748 vertices / 1,344,471 indices** (~448k triangles; `coffee_maker` alone has 229k) → 32-bit indices.
* Normals and UVs **per triangle corner** (not per vertex); 0-based indices.
* 16 RGBA textures (unique ids) up to 512×512 (~10 MB), shared by id; `applyMode` modulate/decal; repeat U/V flags.
* **Every shape has the `static` flag**, including doors attached to joints → moving objects are detected by **pose changes**, not by the flag.

#### Files to Create
1. `coppelia_scripts/zmq_scene_streamer.lua`
2. `coppelia_scripts/install_streamer.py` (installs the script in the open scene via the Remote API: `sim.createScript(sim.scripttype_customization, ...)`)
3. `coppelia_scripts/test_zmq_client.py`
4. `coppelia_scripts/README_COPPELIA_SETUP.md`
5. `My project/Assets/_Project/Plugins/NetMQ/` (NetMQ 4.0.1.13, AsyncIO 0.1.69, NaCl.Net 0.1.13 — netstandard — and `link.xml`)
6. `My project/Assets/_Project/Scripts/Core/CoordinateConverter.cs`
7. `My project/Assets/_Project/Scripts/SimWorld/` — `Protocol/`, `Transport/ZmqSceneClient.cs`, `Transport/ZmqPoseStream.cs`, `Building/ShapeMeshBuilder.cs`, `Building/MaterialCache.cs`, `CoppeliaSceneBuilder.cs`, `CoppeliaPoseSubscriber.cs`, `SimWorldRegistry.cs`
8. `My project/Assets/_Project/Scripts/Desktop/` — `DesktopFlyCamera.cs`, `SimWorldDebugHud.cs`
9. `My project/Assets/_Project/Tests/EditMode/` and `Scenes/SimWorld_Desktop.unity`

#### ZMQ Protocol v1
* **REP `23005`** (text requests):
  * `info` → `{protocol, scene, objectCount, sceneObjectCount, shapeCount, sceneVersion, posePort, simulationState}`
  * `list` → `{sceneVersion, objects:[{h, name, path, type, parent, localPose[7], visible, isShape, components}]}` — **all** exported objects (shapes, dummies, joints), to reproduce the hierarchy
  * `mesh <h>` → `[header JSON, vertices f32, indices i32, normals f32, uvs f32]`; the shape components are concatenated, with offsets, colors (9 values), transparency, options and texture id in the header
  * `texture <id>` → `[{id, w, h}, RGBA bytes]`
  * error → `[{"error": "..."}]`
* **PUB `23006`**: `["poses", {seq, t, full, sceneVersion, count}, int32 handles, f32 local poses (7 per handle)]` only with objects whose pose changed, plus a full keyframe every 1 s; `["scene", {sceneVersion}]` when objects are added, removed or reparented (e.g. inserting `/myRobot`).
* Little-endian binaries produced with `sim.packFloatTable`/`sim.packInt32Table` inside CoppeliaSim. **Never** through the Remote API.
* Full contract: `coppelia_scripts/README_COPPELIA_SETUP.md`.

#### Parser Specification
* Lua: the component count of each shape comes from `#sim.getIntArrayProperty(h, 'meshes')`, and each component from `sim.getShapeViz(h, i)`. The REP is non-blocking (`simZMQ.poll` with timeout 0) in `sysCall_nonSimulation`/`sysCall_sensing`. The PUB runs at 30 Hz while simulating and 10 Hz when stopped, so objects moved in edit mode show up in Unity.
* Unity: the hierarchy reproduces the CoppeliaSim tree (empty GameObjects for dummies and joints; cameras and lights ignored). Each shape becomes **one** GameObject with submeshes grouped by material (color + transparency + texture). Corners are de-indexed and welded by (position, normal, UV). Poses arrive as **local poses**: a rotating door only updates its subtree.
* Axis conversion (vertices, normals and poses): `(x,y,z) → (-y, z, x)`, quaternion `(qy, -qz, -qx, qw)`, winding `(i0,i1,i2) → (i0,i2,i1)`.
* ZMQ networking on background threads; meshes and poses applied on the main thread, with incremental per-frame construction.

#### Stage 1 Verification Criteria
* `test_zmq_client.py` against the real CoppeliaSim: 117 objects, 52 shapes, 944 components, 16 textures; moving an object through the Remote API shows up on the `23006` stream.
* Unity: console without errors and the converter EditMode tests passing.
* Play mode: the house appears with correct orientation, scale, colors and textures. Rotating a door joint or creating and removing a shape through the Remote API is reflected in Unity.
* Record load time, FPS and draw calls on the desktop (reference for the Quest optimization).

---

### Stage 2: Hybrid Network Topology Scripts (Windows $\leftrightarrow$ WSL2 $\leftrightarrow$ Quest 3 via ADB)

#### Goal
Provide scripts that automate `adb reverse` and the dynamic Windows host IP configuration for the WSL2 nodes.

#### Files to Create
1. `scripts/setup_adb_reverse.ps1`
2. `scripts/get_windows_host_ip.sh`

#### Stage 2 Autonomous Verification Criteria (offline on WSL2)
* Validate the syntax with `bash -n scripts/get_windows_host_ip.sh`, test the returned IP and run `bash scripts/sync_to_windows.sh`.

---

### Stage 3: Pioneer P3-DX Robotics Base on WSL2 (ROS 2–ZMQ bridge, `slam_toolbox`, `Nav2` and Keepout Filters)

#### Goal
Create and build the `ros2_ws` workspace with `vr_robot_pkg` and `ROS-TCP-Endpoint`, integrating the Pioneer P3-DX differential kinematics, real-time `slam_toolbox` and `Nav2` with the **Costmap Filters (Keepout Zones)** plugin.

#### Files to Create
1. `ros2_ws/src/vr_robot_pkg/package.xml` and `setup.py`
2. `ros2_ws/src/vr_robot_pkg/config/mapper_params_online_async.yaml`
3. `ros2_ws/src/vr_robot_pkg/config/nav2_params_jazzy.yaml` (including the `keepout_filter` in `global_costmap` and `local_costmap`)
4. `ros2_ws/src/vr_robot_pkg/vr_robot_pkg/coppelia_bridge_node.py` (P3-DX kinematics, LiDAR, Ground Truth Odom, Camera and `mock_mode:=true`)
5. `ros2_ws/src/vr_robot_pkg/launch/vr_robot_bringup.launch.py`

#### Stage 3 Autonomous Verification Criteria (offline on WSL2)
* `colcon build` in `ros2_ws` without errors.
* Test `coppelia_bridge_node` with `mock_mode:=true`, making sure it publishes synthetic `/myRobot/scan`, `/myRobot/odom` and `/tf`.

---

### Stage 4: VR Locomotion and Spatial Control Menu

#### Goal
Configure avatar locomotion and implement the **VR Spatial Menu** (`VRModeAndMissionMenu.cs`), from which the user can open/close the Holographic Tactical Table, select navigation tools and control the mission.

#### Files to Create
1. `My project/Assets/_Project/Scripts/HMI/VRModeAndMissionMenu.cs`
2. Unity EditMode tests for the axis conversion (already created in Stage 1 in `Tests/EditMode/`)

#### Implementation Specification
* Floating canvas in VR space, reachable through a controller button (menu or primary button).
* Interface buttons:
  * **Holographic Tactical Table:** Opens/closes the tactical table hologram in front of the operator.
  * **Tactical Tools:** Point-and-Click, Path Painting, No-Go Zone Brush.
  * **First-Person Teleoperation Mode:** Switches the view to the robot's front camera.
  * **Mission Control:** Pause, Resume and Cancel.

#### Stage 4 Autonomous Verification Criteria
* Unity EditMode tests passing and console without errors (checked through MCP).

---

### Stage 5: On-Demand Holographic Tactical Table and Multimodal Navigation

#### Goal
Implement the **Holographic Tactical Table** in Unity (invoked from the menu, manipulable with `XRGrabInteractable`, with point-and-click, path painting and the No-Go Zone brush) and `mission_supervisor_node.py` in ROS 2, managing the `NavigateToPose` and `NavigateThroughPoses` actions and the Keepout Filter mask for the Pioneer P3-DX.

#### Files to Create
1. `ros2_ws/src/vr_robot_pkg/vr_robot_pkg/mission_supervisor_node.py`
2. `My project/Assets/_Project/Scripts/HMI/HolographicTacticalTable.cs`
3. `My project/Assets/_Project/Scripts/HMI/PathPainter.cs`
4. `My project/Assets/_Project/Scripts/HMI/NoGoZonePainter.cs`
5. `tests/test_minimap_uv_math.py`
6. `tests/test_mission_supervisor.py`

#### Implementation Specification
1. **`HolographicTacticalTable.cs`:**
   * Invoked from the VR Menu. Has an `XRGrabInteractable` to grab, move, rotate and zoom.
   * Subscribes to `/map` (`nav_msgs/OccupancyGrid`) and updates the holographic texture with the Pioneer P3-DX mini-avatar.
   * Converts UV hit coordinates into `/map` meters.
2. **`PathPainter.cs`:**
   * Collects `/map` metric points while the user drags the laser over the table and sends them to `nav2_msgs/action/NavigateThroughPoses`.
3. **`NoGoZonePainter.cs`:**
   * Draws on the mask texture and publishes `nav_msgs/OccupancyGrid` on `/keepout_filter_mask`.
4. **`mission_supervisor_node.py`:**
   * Action Client for `/navigate_to_pose` and `/navigate_through_poses` with `PAUSE`, `RESUME` and `CANCEL` control.

#### Stage 5 Autonomous Verification Criteria (offline on WSL2)
* Run `pytest tests/test_minimap_uv_math.py` to validate the UV coordinate conversions, waypoints and keepout grid.
* Run `pytest tests/test_mission_supervisor.py` with *Mock Action Servers* for `NavigateToPose` and `NavigateThroughPoses`.
* Run `bash scripts/sync_to_windows.sh`.

---

### Stage 6: On-Demand First-Person Teleoperation, `twist_mux` and Setup Guides

#### Goal
Configure `twist_mux` to prioritize `/cmd_vel_teleop` over `/cmd_vel_nav`, create the C# first-person teleoperation controller (`FirstPersonTeleopController.cs`) with the Pioneer P3-DX front camera, and document the full step-by-step setup of the Unity and CoppeliaSim scenes for the user.

#### Files to Create
1. `ros2_ws/src/vr_robot_pkg/config/twist_mux.yaml`
2. `My project/Assets/_Project/Scripts/HMI/FirstPersonTeleopController.cs`
3. `My project/Assets/_Project/README_UNITY.md` (update with the XR setup)
4. `tests/test_twist_mux_offline.py`

#### Stage 6 Autonomous Verification Criteria (offline on WSL2)
* Run `pytest tests/test_twist_mux_offline.py` and the full test suite:
  ```bash
  pytest tests/
  colcon build --packages-select vr_robot_pkg
  bash scripts/sync_to_windows.sh
  ```
* Check that the PowerShell scripts are mirrored in `/mnt/c/Users/mateu/unicamp/IA368/projeto_final/scripts/` (C# and Lua already live on Windows).
