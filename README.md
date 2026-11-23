# IA368 — Final Project: Mixed Reality HMI and Digital Twin for a Mobile Robot (Unity + CoppeliaSim)

**Course:** Design and Development of Intelligent Autonomous Robots (IA368) — FEEC / Unicamp
**Author:** Mateus Henrique Silva Araújo

Immersive VR/MR human-machine interface (Meta Quest 3, Unity) for tactical planning, monitoring and first-person teleoperation of a Pioneer P3-DX simulated in CoppeliaSim, integrated with ROS 2 Jazzy and Nav2. See [`PROJECT_PROPOSAL.md`](PROJECT_PROPOSAL.md).

This repository holds the **Windows side**: the Unity project, the CoppeliaSim scripts and scenes, and the planning documents. The ROS 2 side lives in [`IA368_final_project_ros2`](https://github.com/MateusHSAraujo/IA368_final_project_ros2) (WSL2).

## Structure

```text
├── PROJECT_PROPOSAL.md       # Project proposal
├── IMPLEMENTATION_PLAN.md    # Stage-by-stage implementation plan
├── STAGE1_UNITY_PLAN.md      # Stage 1 plan and execution status (CoppeliaSim → Unity converter)
├── STAGE4_VR_LOCOMOTION_PLAN.md # Stage 4 (part 1) plan and status: VR scene + locomotion (Meta XR SDK)
├── STAGE5_TACTICAL_TABLE_PLAN.md # Stage 4 Spatial Menu + Stage 5 Unity side plan and status: operations panel and HMI tools
├── STAGE6_TELEOP_PLAN.md     # Stage 6 Unity side plan and status: teleop mode and camera panel
├── CLAUDE.md                 # Instructions for the Windows-side agent
├── coppelia_scripts/         # ZMQ scene streamer (Lua) + installer, protocol test, reference capture
├── scene_house/              # CoppeliaSim scenes (Git LFS)
├── scripts/                  # PowerShell helpers (Stage 2): setup_adb_reverse.ps1 (Quest 3 port forwarding), mirrored from the ROS repo
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

Clone with Git LFS installed (`git lfs install`) to get the scenes and binaries.
