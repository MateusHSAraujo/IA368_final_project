# IA368 — Final Project: Mixed Reality HMI and Digital Twin for a Mobile Robot (Unity + CoppeliaSim)

**Course:** Design and Development of Intelligent Autonomous Robots (IA368) — FEEC / Unicamp
**Author:** Mateus Henrique Silva Araújo

Immersive VR/MR human-machine interface (Meta Quest 3, Unity) for tactical planning, monitoring and first-person teleoperation of a Pioneer P3-DX simulated in CoppeliaSim, integrated with ROS 2 Jazzy and Nav2.

This repository holds the Unity project and the CoppeliaSim scripts and scenes. The ROS 2 side lives in [`IA368_final_project_ros2`](https://github.com/MateusHSAraujo/IA368_final_project_ros2), included here as the `ros2/` submodule (pinned to its `v1.0.0` tag). The project was developed on Windows with ROS 2 in WSL2; it also runs on native Linux (see below).

Clone everything at once (Git LFS required for the scenes and binaries):

```bash
git lfs install
git clone --recurse-submodules git@github.com:MateusHSAraujo/IA368_final_project.git
```

In an existing clone: `git submodule update --init --recursive`.

## Structure

```text
├── CLAUDE.md                 # Instructions for the Windows-side agent
├── coppelia_scripts/         # ZMQ scene streamer (Lua) + installer, protocol test, reference capture
├── scene_house/              # CoppeliaSim scenes (Git LFS)
├── scripts/                  # PowerShell helpers (Stage 2): setup_adb_reverse.ps1 (Quest 3 port forwarding), mirrored from the ROS repo
├── ros2/                     # Submodule: IA368_final_project_ros2 (ROS 2 workspace, maps, Linux scripts)
└── My project/               # Unity 6000.0.44f1 project (URP); code in Assets/_Project/
```

## Quick start (Stage 1)

1. Open `scene_house/scene_house.ttt` in CoppeliaSim 4.10.
2. `cd coppelia_scripts` and `python -u install_streamer.py`.
3. Open `My project/Assets/_Project/Scenes/SimWorld_Desktop.unity` in Unity and press Play.

## Status

| Stage | Status |
|---|---|
| 1. CoppeliaSim → Unity scene converter (ZMQ) | Done |
| 2. Network scripts (WSL ↔ Windows ↔ Quest) | Done (ROS repo) |
| 3. P3-DX ROS 2 stack: bridge, slam_toolbox, Nav2 + Keepout Filter, ROS-TCP-Endpoint | Done (ROS repo) |
| 4. VR scene: locomotion, VR menu, start flow (`VrScene.unity`, Meta XR Simulator) | Done (locomotion, Spatial Menu with Mission / Map / Settings pages) |
| 5. Holographic Tactical Table (operations panel) + mission supervisor | Done (ROS side: supervisor and saved maps; Unity side: operations panel, goal / path / no-go tools) |
| 6. First-person teleoperation + `twist_mux` | Done (ROS side: `twist_mux`, teleop priority; Unity side: teleop mode, stick driving, camera panel) |

## VR scene

Open `My project/Assets/_Project/Scenes/VrScene.unity` and press Play with the Meta XR Simulator active (menu Meta > Meta XR Simulator); the main menu loads the CoppeliaSim scene on demand. For the robot features, start the ROS stack in WSL (`ros2 launch vr_robot_pkg vr_robot_bringup.launch.py map:=scene_house`, see the ROS repo); Unity connects to `127.0.0.1:10000`.

Details: [`coppelia_scripts/README_COPPELIA_SETUP.md`](coppelia_scripts/README_COPPELIA_SETUP.md) and [`My project/Assets/_Project/README_UNITY.md`](My%20project/Assets/_Project/README_UNITY.md).

## Running on native Linux

Tested setup: Windows + WSL2. On a native Linux machine (Ubuntu 24.04, ROS 2 Jazzy, CoppeliaSim 4.10 for Linux) everything runs on the same host:

1. Clone recursively (above), then build the ROS workspace:
   ```bash
   cd ros2
   source /opt/ros/jazzy/setup.bash
   cd ros2_ws && colcon build --symlink-install && cd ..
   ```
2. Open `scene_house/scene_house.ttt` in CoppeliaSim and install the scene streamer used by Unity: `cd coppelia_scripts && python3 -u install_streamer.py` (needs `pip install coppeliasim-zmqremoteapi-client pyzmq`).
3. Start the ROS stack from `ros2/`: `./run.sh` (or `ros2 launch vr_robot_pkg vr_robot_bringup.launch.py map:=scene_house`). Outside WSL the bridge connects to CoppeliaSim at `127.0.0.1`; use `coppelia_host:=<ip>` or `COPPELIA_HOST` for another machine.
4. Install the Quest build from the [`v1.0.0` release](https://github.com/MateusHSAraujo/IA368_final_project/releases/tag/v1.0.0) and forward the ports over USB (the Linux equivalent of `scripts/setup_adb_reverse.ps1`):
   ```bash
   adb install -r projeto_final.apk
   adb reverse tcp:10000 tcp:10000   # ROS-TCP-Endpoint
   adb reverse tcp:23005 tcp:23005   # scene streamer (REP)
   adb reverse tcp:23006 tcp:23006   # scene streamer (PUB)
   ```
5. Launch the app on the headset.

The Unity project also opens in the Unity Editor for Linux, but the Meta XR Simulator is not available there: test on the headset.
