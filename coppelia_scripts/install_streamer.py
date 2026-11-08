"""(Re)installs zmq_scene_streamer.lua into the scene open in CoppeliaSim via the ZMQ Remote API.

Usage:
    python -u install_streamer.py [--scene PATH.ttt] [--save] [--remove] [--host H] [--port 23000]

- Removes previous instances (the /ZmqSceneStreamer dummy and its scripts).
- Creates the ZmqSceneStreamer dummy and a customization script with the .lua contents.
- --scene opens the scene before installing; --save saves the scene with the streamer embedded.
- --remove only uninstalls.
"""
import argparse
import os
import sys
import time

import zmq
from coppeliasim_zmqremoteapi_client import RemoteAPIClient

HERE = os.path.dirname(os.path.abspath(__file__))
ALIAS = 'ZmqSceneStreamer'


def connect(host, port, timeout_ms):
    client = RemoteAPIClient(host=host, port=port)
    client.socket.setsockopt(zmq.RCVTIMEO, timeout_ms)
    client.socket.setsockopt(zmq.SNDTIMEO, timeout_ms)
    sim = client.require('sim')
    return client, sim


def find_streamers(sim):
    found = []
    for h in sim.getObjectsInTree(sim.handle_scene):
        if sim.getObjectAlias(h, 0) == ALIAS:
            found.append(h)
    return found


def remove_streamers(sim):
    old = find_streamers(sim)
    if old:
        tree = []
        for h in old:
            tree += sim.getObjectsInTree(h)
        sim.removeObjects(list(dict.fromkeys(tree)))
    return len(old)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--host', default='localhost')
    ap.add_argument('--port', type=int, default=23000)
    ap.add_argument('--scene', help='open this .ttt before installing')
    ap.add_argument('--save', action='store_true', help='save the scene after installing')
    ap.add_argument('--remove', action='store_true', help='only remove the streamer')
    ap.add_argument('--lua', default=os.path.join(HERE, 'zmq_scene_streamer.lua'))
    args = ap.parse_args()

    try:
        _, sim = connect(args.host, args.port, 20000)
        sim.getSimulationState()
    except zmq.error.Again:
        print('ERROR: the Remote API (port %d) did not reply. Is CoppeliaSim open? '
              'If "ZMQ remote API server@addonScript: abort execution" shows up, click it.' % args.port)
        return 2

    if args.scene:
        print('Opening', args.scene)
        sim.loadScene(os.path.abspath(args.scene))

    n = remove_streamers(sim)
    if n:
        print('Removed %d previous instance(s).' % n)
        time.sleep(0.3)  # let sysCall_cleanup release the ports
    if args.remove:
        return 0

    with open(args.lua, encoding='utf-8') as f:
        code = f.read()

    dummy = sim.createDummy(0.01)
    sim.setObjectAlias(dummy, ALIAS)
    script = sim.createScript(sim.scripttype_customization, code)
    sim.setObjectParent(script, dummy, True)
    sim.setObjectAlias(script, ALIAS + '_script')
    print('Streamer installed: dummy %d, script %d.' % (dummy, script))

    if args.save:
        path = sim.getStringProperty(sim.handle_scene, 'scenePath', {'noError': True})
        if path:
            sim.saveScene(path)
            print('Scene saved to', path)
        else:
            print('WARNING: the scene has no path; it was not saved.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
