"""Closed-loop aiming and interaction tests for the right controller in the Meta XR Simulator.

Needs the Unity-side helper (unity/AimHelper.RunCommand.cs) running in Play mode: it reads
<exchange dir>/aim_target.txt and writes <exchange dir>/aim_state.json.

Usage:
  python -u aim_tools.py aim <spec>                      # rotate the right grip until the ray hits the target
  python -u aim_tools.py click <spec>                    # aim + trigger press/release
  python -u aim_tools.py stroke <spec1> <spec2> ...      # aim at first, hold trigger, sweep through the rest
  python -u aim_tools.py state                           # print the helper state
  python -u aim_tools.py grab <spec> <dx> <dz> <yaw_deg> [pitch_deg]
                                                         # distance grab (right grip): aim, hold Grip, move
                                                         #   dx/dz metres (OpenXR local_floor), yaw and pitch
                                                         #   (Unity sense: + tilts the top away), release
  python -u aim_tools.py drag <spec> <dx> <dz> <yaw_deg> [pitch_deg] [dy]
                                                         # ray grab (right Trigger): like grab, plus dy metres;
                                                         #   operations panel frame bars and resize handles
  python -u aim_tools.py sweep <spec_from> <spec_to>     # ray grab (Trigger) at spec_from, re-aim the ray at
                                                         #   spec_to while holding (e.g. drag a resize handle)
  python -u aim_tools.py scale <spread_m> [spec]         # both grips held, hands spread along OpenXR X;
                                                         #   the LEFT hand must already hover the target
                                                         #   (optional spec aims the right hand first)
  python -u aim_tools.py menu                            # press left Menu (toggles the VR menu)

Spec syntax:
  button:<exact GameObject name>   active UI Button
  map:x;y                          ROS map point on the operations panel
  floor:x;y                        ROS map point on the real floor
  handle:i                         panel corner resize handle (0 NL, 1 NR, 2 FL, 3 FR)
  bar:offset;up                    nearest panel frame bar + offset along the panel right axis (+ up, m)
  world:x;y;z                      Unity world point
"""
import json
import math
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xr_operator as xr  # noqa: E402

TARGET = os.path.join(xr.EXCHANGE_DIR, "aim_target.txt")
STATE = os.path.join(xr.EXCHANGE_DIR, "aim_state.json")


def read_state(after=0.0):
    for _ in range(50):
        try:
            with open(STATE) as f:
                s = json.load(f)
            if s.get("t", 0) >= after:
                return s
        except Exception:
            pass
        time.sleep(0.1)
    raise SystemExit("no state from the Unity helper (paste unity/AimHelper.RunCommand.cs in Play mode)")


def aim(spec, tol=0.25, max_iter=10):
    with open(TARGET, "w") as f:
        f.write(spec)
    for _ in range(max_iter):
        time.sleep(0.35)
        s = read_state(time.time() - 0.2)
        # dxr == identity means "target not found yet" (e.g. menu still opening): keep waiting
        if s.get("spec") != spec or s["dxr"] == [0, 0, 0, 1]:
            continue
        if s["angle"] <= tol:
            break
        pose = xr.get_grip_pose("right")
        n = xr.qmul(s["dxr"], pose["orientation"])
        norm = sum(c * c for c in n) ** 0.5
        xr.set_grip_pose("right", pose["position"], [c / norm for c in n])
    time.sleep(0.3)
    s = read_state(time.time() - 0.2)
    print(f"aim {spec}: angle={s['angle']:.2f} pointer={s['pointer']}")
    return s


def trigger(v):
    xr.call("openxr_set_controller_input", {"hand": "right", "component": "Trigger", "value": v})


def grip(hand, v):
    xr.call("openxr_set_controller_input", {"hand": hand, "component": "Grip", "value": v})


def cmd_grab(spec, dx, dz, yaw_deg, pitch_deg=0.0, button="Grip", dy=0.0):
    aim(spec)
    xr.call("openxr_set_controller_input", {"hand": "right", "component": button, "value": 1})
    time.sleep(0.6)
    pose = xr.get_grip_pose("right")
    steps = 8
    for i in range(1, steps + 1):
        f = i / steps
        pos = [pose["position"][0] + dx * f, pose["position"][1] + dy * f, pose["position"][2] + dz * f]
        a = math.radians(yaw_deg * f) / 2  # yaw about OpenXR +Y
        b = -math.radians(pitch_deg * f) / 2  # Unity +X rotation = OpenXR -X rotation (handedness flip)
        q = xr.qmul([0, math.sin(a), 0, math.cos(a)], [math.sin(b), 0, 0, math.cos(b)])
        xr.set_grip_pose("right", pos, xr.qmul(q, pose["orientation"]))
        time.sleep(0.15)
    time.sleep(0.4)
    xr.call("openxr_set_controller_input", {"hand": "right", "component": button, "value": 0})
    time.sleep(0.5)
    print("after grab:", read_state(time.time() - 0.2)["pointer"])


def cmd_sweep(spec_from, spec_to):
    aim(spec_from)
    trigger(1)
    time.sleep(0.4)
    aim(spec_to, tol=0.3, max_iter=6)
    time.sleep(0.3)
    trigger(0)
    time.sleep(0.5)
    print("after sweep:", read_state(time.time() - 0.2)["pointer"])


def cmd_scale(spread, spec=None):
    if spec:
        aim(spec)
    poses = {h: xr.get_grip_pose(h) for h in ("left", "right")}
    for h in poses:
        grip(h, 1)
    time.sleep(0.8)
    steps = 10
    for i in range(1, steps + 1):
        f = i / steps
        for h, sign in (("left", -1), ("right", 1)):
            p = poses[h]["position"]
            xr.set_grip_pose(h, [p[0] + sign * spread * f, p[1], p[2]], poses[h]["orientation"])
        time.sleep(0.12)
    time.sleep(0.5)
    for h in poses:
        grip(h, 0)
    time.sleep(0.5)
    print("after scale:", read_state(time.time() - 0.2)["pointer"])


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    xr.init()
    cmd, a = sys.argv[1], sys.argv[2:]
    if cmd == "state":
        print(json.dumps(read_state(), indent=1))
    elif cmd == "aim":
        aim(a[0])
    elif cmd == "click":
        aim(a[0])
        trigger(1)
        time.sleep(0.4)
        trigger(0)
        time.sleep(0.5)
        print("after click:", read_state(time.time() - 0.2)["pointer"])
    elif cmd == "stroke":
        aim(a[0])
        trigger(1)
        time.sleep(0.3)
        for sp in a[1:]:
            aim(sp, tol=0.4, max_iter=4)
        time.sleep(0.2)
        trigger(0)
        time.sleep(0.5)
        print("after stroke:", read_state(time.time() - 0.2)["pointer"])
    elif cmd == "grab":
        cmd_grab(a[0], float(a[1]), float(a[2]), float(a[3]), float(a[4]) if len(a) > 4 else 0.0)
    elif cmd == "drag":
        cmd_grab(a[0], float(a[1]), float(a[2]), float(a[3]), float(a[4]) if len(a) > 4 else 0.0, "Trigger",
                 float(a[5]) if len(a) > 5 else 0.0)
    elif cmd == "sweep":
        cmd_sweep(a[0], a[1])
    elif cmd == "scale":
        cmd_scale(float(a[0]) if a else 0.15, a[1] if len(a) > 1 else None)
    elif cmd == "menu":
        xr.press("left", "Menu")
        time.sleep(0.5)
        print("menu pressed")
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main()
