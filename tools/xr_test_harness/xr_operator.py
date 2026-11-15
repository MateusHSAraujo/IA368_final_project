"""Minimal Meta XR Operator MCP client (HTTP, only reachable while the XR app runs in Play mode).

Usage:
  python -u xr_operator.py list
  python -u xr_operator.py call <tool> '<json args>'
  python -u xr_operator.py batch <file.json>      # steps [tool, {args}] or ["sleep", seconds]
  python -u xr_operator.py aim <hand> '[dx,dy,dz,dw]'   # left-multiply the grip orientation by an OpenXR delta
  python -u xr_operator.py press <hand> <component> [seconds]   # set 1, sleep, set 0 (default 0.3 s)
  python -u xr_operator.py shot [out.png] [eye]   # composited capture (default: <exchange dir>/shot.png, left)

Environment: XR_OPERATOR_URL overrides the endpoint (default http://127.0.0.1:8720/mcp).
Importable: init(), call(tool, args) -> text, rpc(method, params), qmul(a, b), EXCHANGE_DIR.
"""
import base64
import json
import os
import sys
import tempfile
import time
import urllib.request

URL = os.environ.get("XR_OPERATOR_URL", "http://127.0.0.1:8720/mcp")
HDR = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
EXCHANGE_DIR = os.path.join(tempfile.gettempdir(), "xr_harness")
os.makedirs(EXCHANGE_DIR, exist_ok=True)
_sid = None
_id = 0


def _post(payload, notify=False):
    global _sid
    headers = dict(HDR)
    if _sid:
        headers["Mcp-Session-Id"] = _sid
    req = urllib.request.Request(URL, data=json.dumps(payload).encode(), headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=60) as r:
        sid = r.headers.get("Mcp-Session-Id")
        if sid:
            _sid = sid
        body = r.read().decode("utf-8", "replace")
    if notify or not body.strip():
        return None
    if body.lstrip().startswith("{"):
        return json.loads(body)
    last = None  # SSE: keep the last data line
    for line in body.splitlines():
        if line.startswith("data:"):
            last = line[5:].strip()
    return json.loads(last) if last else None


def rpc(method, params=None):
    global _id
    _id += 1
    msg = {"jsonrpc": "2.0", "id": _id, "method": method}
    if params is not None:
        msg["params"] = params
    return _post(msg)


def init(retries=12, delay=5):
    """Open an MCP session. Retries every `delay` s (~60 s): the server answers 'unavailable'
    until the XR session is FOCUSED."""
    for attempt in range(retries):
        try:
            rpc("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                               "clientInfo": {"name": "agent", "version": "1.0"}})
            _post({"jsonrpc": "2.0", "method": "notifications/initialized"}, notify=True)
            return
        except Exception as e:
            print(f"init retry {attempt}: {e}", file=sys.stderr)
            time.sleep(delay)
    raise SystemExit("XR Operator not reachable (is the XR app in Play mode?)")


def call(tool, args):
    """Call an MCP tool and return its text content (images are summarized)."""
    res = rpc("tools/call", {"name": tool, "arguments": args})
    if res is None:
        return None
    if "error" in res:
        return {"error": res["error"]}
    out = []
    for c in res.get("result", {}).get("content", []):
        if c.get("type") == "text":
            out.append(c["text"])
        else:
            out.append(f"<{c.get('type')} {len(json.dumps(c))} bytes>")
    return "\n".join(out)


def qmul(a, b):
    """Hamilton product a*b of quaternions [x, y, z, w]."""
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return [aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz]


def get_grip_pose(hand):
    return json.loads(call("openxr_get_controller_pose",
                           {"hand": hand, "pose_type": "grip", "base_space": "local_floor"}))["pose"]


def set_grip_pose(hand, position, orientation):
    return call("openxr_set_controller_pose", {"hand": hand, "pose_type": "grip", "base_space": "local_floor",
                                               "position": position, "orientation": orientation})


def press(hand, component, seconds=0.3):
    call("openxr_set_controller_input", {"hand": hand, "component": component, "value": 1})
    time.sleep(seconds)
    call("openxr_set_controller_input", {"hand": hand, "component": component, "value": 0})


def shot(out=None, eye="left"):
    out = out or os.path.join(EXCHANGE_DIR, "shot.png")
    res = rpc("tools/call", {"name": "openxr_capture_composited_image", "arguments": {"eye": eye}})
    for c in res.get("result", {}).get("content", []):
        data = None
        if c.get("type") == "image":
            data = c["data"]
        elif c.get("type") == "text":
            try:
                j = json.loads(c["text"])
                data = next((j[k] for k in ("image", "data", "png_base64", "image_base64") if k in j), None)
            except ValueError:
                pass
            if data is None:
                print(c["text"][:500])
        if data:
            with open(out, "wb") as f:
                f.write(base64.b64decode(data))
            print("saved", out)
            return out
    print("no image returned")
    return None


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    init()
    cmd = sys.argv[1]
    if cmd == "list":
        for t in rpc("tools/list")["result"]["tools"]:
            print(t["name"], "-", t.get("description", "")[:110].replace("\n", " "))
            print("   args:", json.dumps(t.get("inputSchema", {}).get("properties", {}))[:400])
    elif cmd == "call":
        print(call(sys.argv[2], json.loads(sys.argv[3]) if len(sys.argv) > 3 else {}))
    elif cmd == "batch":
        with open(sys.argv[2]) as f:
            steps = json.load(f)
        for step in steps:
            if step[0] == "sleep":
                time.sleep(step[1])
            else:
                print(f">> {step[0]}: {call(step[0], step[1])}")
    elif cmd == "aim":
        hand, d = sys.argv[2], json.loads(sys.argv[3])
        pose = get_grip_pose(hand)
        n = qmul(d, pose["orientation"])
        norm = sum(c * c for c in n) ** 0.5
        n = [c / norm for c in n]
        print(set_grip_pose(hand, pose["position"], n))
        print("new orientation", n)
    elif cmd == "press":
        press(sys.argv[2], sys.argv[3], float(sys.argv[4]) if len(sys.argv) > 4 else 0.3)
        print("pressed", sys.argv[2], sys.argv[3])
    elif cmd == "shot":
        shot(sys.argv[2] if len(sys.argv) > 2 else None, sys.argv[3] if len(sys.argv) > 3 else "left")
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main()
