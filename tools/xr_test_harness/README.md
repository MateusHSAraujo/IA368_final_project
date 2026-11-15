# XR test harness

Drives the Meta XR Simulator from an agent (or a shell) for runtime HMI tests: aim the right controller ray at
a UI button, a point on the tactical table or the real floor, click, paint strokes, distance-grab, two-hand
scale, press the menu button, and take screenshots. This folder is outside the Unity project on purpose, so
Unity never compiles the C# snippets.

## Prerequisites
- Play mode in `VrScene` with the Meta XR Simulator active.
- The XR Operator MCP server (`http://127.0.0.1:8720/mcp`, override with `XR_OPERATOR_URL`) exists **only while
  the XR app runs**. Wait ~10 s after entering Play mode; `init()` retries every 5 s for ~60 s.
- Python 3.10, standard library only. Run every script with `python -u` (any working directory).
- Exchange directory: `<system temp>/xr_harness/` (`aim_target.txt`, `aim_state.json`, `shot.png`, `render.png`,
  `editmode_result.txt`).

## Per-session setup
1. Enter Play mode.
2. Paste `unity/AimHelper.RunCommand.cs` through `Unity_RunCommand` (re-paste after every domain reload).
3. Start the app flow (press the Load button, e.g. `python -u aim_tools.py click "button:Load"`).
4. `python -u aim_tools.py menu`, then aim/click/stroke as needed.

## Spec syntax
| Spec | Target |
|---|---|
| `button:<exact GameObject name>` | active `UI.Button` with that name |
| `map:x;y` | ROS map point on the tactical table |
| `floor:x;y` | ROS map point on the real floor |
| `bar:offset;up` | nearest table frame bar + `offset` m along the table right axis (+ `up` m) |
| `world:x;y;z` | Unity world point |

## Commands
`xr_operator.py`: `list`, `call <tool> '<json>'`, `batch <file.json>` (steps `[tool, args]` or `["sleep", s]`),
`aim <hand> '[dx,dy,dz,dw]'`, `press <hand> <component> [seconds]`, `shot [out.png] [eye]`.

`aim_tools.py`: `aim`, `click`, `stroke`, `state`, `grab`, `scale`, `menu`.

```
python -u xr_operator.py press left Menu
python -u xr_operator.py shot
python -u aim_tools.py click "button:Table Button"
python -u aim_tools.py click "map:2.5;0.6"
python -u aim_tools.py stroke "map:2.4;0.5" "map:2.4;-1.0"
python -u aim_tools.py grab "bar:0.3;0" 0.15 -0.1 -25      # dx dz yaw_deg (OpenXR local_floor)
python -u aim_tools.py scale 0.15                           # left hand must already hover the target
python -u aim_tools.py scale 0.15 "bar:0.3;0"               # aim the right hand first
```

Other files:
- `unity/RenderCamera.RunCommand.cs`: renders `CenterEyeAnchor` (or a temporary camera) to `render.png`; the
  reliable alternative when the composited capture is stale.
- `unity/RunEditModeTests.cs.txt`: temporary `[MenuItem]` script to run EditMode tests (procedure in its header;
  delete it afterwards). `.txt` so nothing compiles if the folder is ever moved under `Assets/`.
- `wait_ros_stack.sh [log] [pattern] [timeout]`: waits for the ROS stack log (exit 0 on match, 1 on timeout).
  From Windows use PowerShell: `wsl.exe -d Ubuntu-24.04 -- bash /mnt/c/Users/<user>/.../tools/xr_test_harness/wait_ros_stack.sh`
  (Git Bash mangles `/mnt` paths).

## Pitfalls
- The ISDK ray and the distance-grab frustum follow the **grip** pose in this simulator setup; setting the aim
  pose has no effect.
- Hold inputs (Grip, Trigger) across frames; the simulator applies a 60 deg pitch between the grip pose and the
  hand anchor (the helper's closed loop compensates).
- A missing target (`dxr == [0,0,0,1]`) must not count as aimed (the menu may still be opening).
- The menu toggles on each Menu press: check the state (`aim_tools.py state`, `table=open|closed`) before pressing again.
- `openxr_capture_composited_image` can return a stale frame; use `RenderCamera.RunCommand.cs`.
- Play mode stops ticking when the Editor is unfocused unless the helper queues player-loop updates (it does).
- Input names are case-sensitive: `Trigger`, `Grip`, `Menu`, `A`, `B`, `Thumbstick` with `sub_component` `X`/`Y`.
- Do not edit C# during Play mode (domain reload kills the helper).
