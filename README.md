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
├── CLAUDE.md                 # Instructions for the Windows-side agent
├── coppelia_scripts/         # ZMQ scene streamer (Lua) + installer, protocol test, reference capture
├── scene_house/              # CoppeliaSim scenes
├── scripts/                  # PowerShell helpers (Stage 2)
└── My project/               # Unity 6000.0.44f1 project (URP); code in Assets/_Project/
```

## Quick start (Stage 1)

1. Open `scene_house/scene_house.ttt` in CoppeliaSim 4.10.
2. `cd coppelia_scripts` and `python -u install_streamer.py`.
3. Open `My project/Assets/_Project/Scenes/SimWorld_Desktop.unity` in Unity and press Play.

Details: [`coppelia_scripts/README_COPPELIA_SETUP.md`](coppelia_scripts/README_COPPELIA_SETUP.md) and [`My project/Assets/_Project/README_UNITY.md`](My%20project/Assets/_Project/README_UNITY.md).
