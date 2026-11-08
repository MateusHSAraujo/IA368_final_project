# Project Proposal: Mixed Reality Human-Machine Interface and Digital Twin for Mobile Robot Control, Navigation and Teleoperation

**Course:** Design and Development of Intelligent Autonomous Robots (IA368)

---

## 1. Proposal Summary

The project proposes an **immersive, futuristic Human-Machine Interface (HMI) in Virtual/Mixed Reality (Meta Quest 3, via Unity)** for tactical planning, monitoring and first-person teleoperation of a **Pioneer P3-DX differential-drive mobile robot** simulated in CoppeliaSim EDU and integrated with the **ROS 2 (Jazzy)** and **Nav2** stack.

The core of the research and development is **Spatial Interaction Design**. The system runs on a Digital Twin architecture split into two decoupled layers:

1. **"Simulated Physical World" layer (via ZeroMQ):** Procedurally rebuilds any scene open in CoppeliaSim (meshes, colors and textures) and updates the poses of moving objects in real time. This layer simulates the environment that the *Passthrough* cameras would show in a real application.
2. **"Mixed Reality HMI and Control" layer (via ROS 2):** Talks exclusively to ROS 2 topics and actions. In line with the approach validated in the course labs, coordinates are synchronized deterministically through Ground Truth: the virtual world in Unity and the ROS 2 environment share the same origin $(0,0,0)$, which gives perfect, immediate spatial alignment without manual calibration or additional fiducial markers.

The operator interface is centered on a **VR Spatial Menu**, from which the user can open and switch between the following features on demand:
* **On-Demand Holographic Tactical Table:** When selected in the Menu, it projects in front of the user a floating 3D minimap that can be manipulated with hands/controllers (*XRGrabInteractable* to move, rotate and zoom), rendered from the SLAM map (`slam_toolbox`). On it, the user has:
  * **Holographic Point-and-Click:** Sends single goals (`/goal_pose`) by clicking on the hologram.
  * **Path Painting (free-hand trajectory drawing):** The user "paints" a continuous path with the controller laser over the minimap; it is sent to the Nav2 `NavigateThroughPoses` Action as an ordered chain of waypoints.
  * **No-Go Zone Painting:** The user draws restricted areas with a virtual brush over the minimap; these areas are serialized as an occupancy mask and injected directly into the **Nav2 Costmap Filter (Keepout Zone)**, so the robot immediately avoids them or replans its path.
* **First-Person Rescue Teleoperation:** Floating panel with on-demand video from the Pioneer P3-DX front camera and priority manual control through a joystick with command multiplexing (`twist_mux`).
* **Mission Lifecycle Control:** **Pause**, **Resume** and **Cancel** commands for the robot's active routes, managed by a dedicated Supervisor Node.

---

## 2. Target Robotic Platform: Pioneer P3-DX

To stay fully compatible with the ecosystem and kinematics already validated in the course, the robot used in the CoppeliaSim scene is the **Pioneer P3-DX (`/myRobot`)** differential-drive platform:
* **Differential kinematics:**
  * Wheel radius: $r = 0.0975\text{ m}$ (diameter $0.195\text{ m}$).
  * Wheel base: $L = 0.331\text{ m}$.
  * CoppeliaSim actuators: revolute joints `/leftMotor` and `/rightMotor`.
* **On-board sensors:**
  * **2D LiDAR:** Planar sensor publishing on `/myRobot/scan`.
  * **Front camera (Vision Sensor):** Camera mounted on the front of the robot streaming RGB images (`sensor_msgs/Image`) on demand on `/camera/image_raw`.
  * **Odometry / Ground Truth:** Odometry node publishing on `/myRobot/odom` and providing the `odom -> base_link` TF, synchronized with the simulator origin.
* **CoppeliaSim reference scene (`scene_house`):** The base scene for the digital twin reconstruction is [`scene_house/scene_house.ttt`](scene_house/scene_house.ttt) (Windows: `C:\Users\mateu\unicamp\IA368\projeto_final\scene_house\scene_house.ttt`). It is a complete residential environment with several rooms, walls, doors, floors and furniture. The procedural ZMQ parser in Unity uses this scene as the initial static reference for procedural mesh generation, and supports later insertion of the Pioneer P3-DX (`/myRobot`) for dynamic pose streaming and teleoperation.

---

## 3. System Architecture and Network Topology

The system integrates the **Windows host** (CoppeliaSim EDU + ADB), the **WSL2 guest** (ROS 2 Jazzy, Nav2, SLAM Toolbox) and the **Meta Quest 3** (Unity APK) over a USB-C cable with port forwarding (`adb reverse`):

```
+---------------------------------------------------------------------------------------------------------+
|                                              HOST: WINDOWS                                              |
|                                                                                                         |
|  +--------------------------------------------+       +----------------------------------------------+  |
|  |       CoppeliaSim EDU (Pioneer P3-DX)      |       |             ADB Server (adb.exe)             |  |
|  |                                            |       |       Port forwarding over USB-C cable:      |  |
|  | - Custom scene with /myRobot               |       |       - tcp:23005 (ZMQ Meshes REP)           |  |
|  | - 2D LiDAR + front camera                  |       |       - tcp:23006 (ZMQ Poses PUB)            |  |
|  | - Lua simZMQ server:                       |       |       - tcp:10000 (ROS-TCP-Endpoint)         |  |
|  |   * Port 23005 (REP: scene 3D meshes)      |<----->|       +----------------------+---------------+  |
|  |   * Port 23006 (PUB: moving poses)         |       +----------------------+-----------------------+  |
|  | - ZMQ Remote API (port 23000)              |                              |                          |
|  +---------------------+----------------------+                              | USB-C cable              |
|                        | ZMQ (localhost)                                     v                          |
|  +---------------------+-----------------------------------------------------+-----------------------+  |
|  |                                  GUEST: WSL2 (Linux / ROS 2 Jazzy)        |                       |  |
|  |                                                                           |                       |  |
|  |  +----------------------------+             +-----------------------------v--------------------+  |  |
|  |  |  Coppelia-ROS 2 bridge node|             |                 ROS-TCP-Endpoint                 |  |  |
|  |  |   (Python + ZMQ client)    |             |                   (port 10000)                   |  |  |
|  |  |   - /myRobot/scan, odom, tf|             +--------+--------------------+--------------------+  |  |
|  |  |   - /camera/image_raw      |                      |                    ^                       |  |
|  |  |   - /cmd_vel               |                      v                    |                       |  |
|  |  +-------------+--------------+             +--------+--------------------+--------------------+  |  |
|  |                |                            |      Mission & HMI Supervisor node (Python)      |  |  |
|  |                v                            |      - Action client: NavigateToPose             |  |  |
|  |  +-------------+--------------+             |      - Action client: NavigateThroughPoses       |  |  |
|  |  |      slam_toolbox          |             |      - Publishes: /keepout_filter_mask           |  |  |
|  |  |   (online dynamic SLAM)    |             |      - Commands: PAUSE, RESUME, CANCEL           |  |  |
|  |  +-------------+--------------+             +--------+--------------------+--------------------+  |  |
|  |                | /map                                |                    |                       |  |
|  |                v                                     |                    |                       |  |
|  |  +-------------+--------------+                      |                    |                       |  |
|  |  |            Nav2            |<---------------------+                    |                       |  |
|  |  |  - Costmap Keepout Filter  |                                           |                       |  |
|  |  |  - Global & Local Planner  |                                           |                       |  |
|  |  +-------------+--------------+                                           |                       |  |
|  |                | /cmd_vel_nav                                             | /cmd_vel_teleop       |  |
|  |                +------------------------------+---------------------------+                       |  |
|  |                                               v                                                   |  |
|  |                                     +---------+----------+                                        |  |
|  |                                     |     twist_mux      |--> /cmd_vel                            |  |
|  |                                     +--------------------+                                        |  |
|  +---------------------------------------------------------------------------------------------------+  |
+---------------------------------------------------------------------------------------------------------+
                                                                               ^
                                                                               | USB-C cable (127.0.0.1)
                                                                               v
+---------------------------------------------------------------------------------------------------------+
|                                   CLIENT: META QUEST 3 (Unity APK)                                      |
|                                                                                                         |
|  +---------------------------------------+  +--------------------------------------------------------+  |
|  |  Layer 1: Simulated Physical World    |  |  Layer 2: Mixed Reality HMI (ROS 2)                    |  |
|  |  - NetMQ REQ (23005): builds the scene|  |  1. VR Spatial Menu:                                   |  |
|  |    and the Pioneer P3-DX at runtime   |  |     * Button to open/close the Holographic Table       |  |
|  |  - NetMQ SUB (23006): updates poses   |  |     * Tool selection (Point, Paint, No-Go)             |  |
|  |    of moving entities                 |  |     * First-person teleoperation mode                  |  |
|  |  - Instant deterministic alignment    |  |     * Mission control (Pause / Resume / Cancel)        |  |
|  |    (origin 0,0,0)                     |  |  2. Holographic Tactical Table (on demand):            |  |
|  +---------------------------------------+  |     * Point-and-Click (UV raycast -> /goal_pose)       |  |
|                                             |     * Path Painting (free stroke -> waypoints)         |  |
|                                             |     * No-Go Zone Brush (cost mask painting)            |  |
|                                             |     * Spatial manipulation (grab, rotate, scale)       |  |
|                                             |  3. First-person virtual monitor + joystick driving    |  |
|                                             +--------------------------------------------------------+  |
+---------------------------------------------------------------------------------------------------------+
```

---

## 4. Main Innovations and Engineering Rationale

1. **Deterministic Spatial Alignment (Zero Calibration):**
   Since odometry and SLAM use the simulator world frame and Unity imports the scene at its own origin $(0,0,0)$, spatial synchronization is instantaneous through a direct geometric axis conversion ($x_{\text{ros}} = z_{\text{unity}}, y_{\text{ros}} = -x_{\text{unity}}, z_{\text{ros}} = y_{\text{unity}}$). This removes failure points and preliminary manual calibration steps.
2. **On-Demand Holographic Tactical Table:**
   The Tactical Table does not occupy the user's view continuously; it is invoked from the **Spatial Menu**, letting the operator inspect the house floor plan in 3D, rotate it, zoom with the hands and plan tactical routes without interfering with the physical floor of the scene.
3. **Decoupling through Texture UV Coordinates:**
   Pointing, route drawing and zone painting happen on the hologram surface. The *Raycast* returns normalized coordinates $(u, v) \in [0, 1] \times [0, 1]$, which are converted directly into metric `/map` coordinates from the grid resolution and origin, with no coupling to the simulation floor.
4. **Multimodal Navigation (Point-and-Click + Path Painting):**
   The operator can send single goals (`NavigateToPose`) or draw free-hand paths on the Tactical Table, executed through the Nav2 `NavigateThroughPoses` Action.
5. **Dynamic Virtual Barriers through Nav2 Costmap Filters:**
   Restricted areas ("No-Go Zones") can be outlined with a virtual brush on the minimap. The painting produces an occupancy mask sent directly to the Nav2 **Keepout Filter** plugin, which reroutes the robot automatically in real time.
6. **High-Performance Wired USB Topology via ADB:**
   `adb reverse` provides `localhost` connections on ports `23005`, `23006` (ZMQ) and `10000` (ROS), avoiding wireless network jitter and keeping a high frame rate (FPS) on the Meta Quest 3.

---

## 5. Technology Stack

### Simulation and Digital Twin (Windows)
* **CoppeliaSim EDU:** Pioneer P3-DX platform (`/myRobot`), `/leftMotor` and `/rightMotor` actuators, 2D LiDAR and front Vision Sensor in a custom scene.
* **`simZMQ` plugin (Lua):** Custom ZMQ server with a `REP` socket (port `23005`, scene meshes) and a `PUB` socket (port `23006`, moving-pose streaming).

### Robotics, Autonomy and Middleware (WSL2 / Linux)
* **ROS 2 (Jazzy):** Main robotics middleware.
* **`slam_toolbox`:** Real-time construction of the 2D occupancy map (`/map`).
* **`Nav2` (Navigation2):** Full stack with `NavigateToPose`, `NavigateThroughPoses` and `nav2_costmap_2d::CostmapFilter` (Keepout Zones).
* **`twist_mux`:** Velocity multiplexer that prioritizes manual teleoperation (`/cmd_vel_teleop`) over the autonomous planner (`/cmd_vel_nav`).
* **`ROS-TCP-Endpoint`:** Bidirectional TCP/IP bridge to Unity on port `10000`.

### Virtual Reality and HMI (Meta Quest 3 / Unity)
* **Unity Engine (C#):** Graphics engine for immersive rendering on the headset.
* **XR Interaction Toolkit:** Continuous locomotion (*Continuous Move*), ray casting (*Raycast*) and two-handed manipulation of the tactical table (*XRGrabInteractable*).
* **NetMQ (C#):** ZeroMQ client that consumes the scene geometry and poses in real time.
* **ROS-TCP-Connector (C#):** Official Unity Robotics Hub client for publishing and subscribing to ROS 2 topics and actions.

---

## 6. Development Stages

### Stage 1: ZMQ Bridge and Dynamic Reconstruction of the Pioneer P3-DX (CoppeliaSim $\rightarrow$ Unity)
* Create the Lua script (`simZMQ`) in CoppeliaSim with a `REP` (`23005`) and a `PUB` (`23006`) socket.
* Create the Unity C# scripts that instantiate the scene and Pioneer P3-DX geometry at runtime with `new Mesh()`, updating poses dynamically on the Main Thread with a deterministic axis conversion.

### Stage 2: Hybrid Network Topology Scripts (Windows $\leftrightarrow$ WSL2 $\leftrightarrow$ Quest 3 via ADB)
* PowerShell script `setup_adb_reverse.ps1` mapping ports `23005`, `23006` and `10000` over USB-C.
* Utility that detects the Windows host IP dynamically to configure the WSL2 nodes.

### Stage 3: Pioneer P3-DX Robotics Base in WSL2 (ROS 2–ZMQ bridge, `slam_toolbox`, `Nav2` and Keepout Filters)
* Merge the LiDAR, Ground Truth odometry, TF, differential kinematics ($r=0.0975, L=0.331$) and front camera nodes into `coppelia_bridge_node.py` (with `mock_mode:=true` for fully offline tests).
* Configure `slam_toolbox` in *online async* mode and `Nav2` with **Costmap Keepout Filters** enabled (`/keepout_filter_mask`).

### Stage 4: VR Locomotion and Spatial Control Menu
* Configure the *Continuous Move Provider* on the `XR Origin` to move around the scene.
* Build the **VR Spatial Menu**, reachable from the controller, with buttons to:
  * Open/close the **Holographic Tactical Table**.
  * Select a tool: **Point-and-Click**, **Path Painting**, **No-Go Zone Painting**.
  * Switch to **First-Person Teleoperation Mode**.
  * Mission control: **Pause**, **Resume** and **Cancel**.

### Stage 5: On-Demand Holographic Tactical Table and Multimodal Navigation
* **Holographic Tactical Table (`HolographicTacticalTable.cs`):** Floating object invoked from the menu, manipulable with hands/controllers (`XRGrabInteractable`), rendering the `/map` mesh and the Pioneer P3-DX mini-avatar.
* **Point-and-Click:** A laser click on the table converts UV into map $(X, Y)$ and sends the goal.
* **Path Painting (`PathPainter.cs`):** Continuous path drawing on the minimap, sampled into waypoints for the `NavigateThroughPoses` Action.
* **No-Go Zone Painting (`NoGoZonePainter.cs`):** Virtual brush on the minimap producing a binary mask sent to the Nav2 Costmap Filter (`/keepout_filter_mask`).
* **Mission Supervisor Node:** Manages the `NavigateToPose` and `NavigateThroughPoses` actions with `PAUSE`, `RESUME` and `CANCEL` commands.

### Stage 6: On-Demand First-Person Teleoperation, `twist_mux` and Setup Guides
* Configure `twist_mux` to prioritize `/cmd_vel_teleop` over `/cmd_vel_nav`.
* Create a Unity controller with a floating monitor for the Pioneer P3-DX front camera (`/camera/image_raw` on demand) and analog robot control through the joystick.
* Provide detailed setup guides for Unity and CoppeliaSim, kept in the Windows repository (`IA368_final_project`).
