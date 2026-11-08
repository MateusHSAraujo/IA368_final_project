"""Validates the zmq_scene_streamer protocol against the real CoppeliaSim.

Usage:
    python -u test_zmq_client.py [--host localhost] [--expect-house]

Steps: info, list, mesh of every shape, texture of every id, pose stream (keyframe),
moving an object through the Remote API (pose changes on 23006) and creating/removing
a cube ("scene" event).
"""
import argparse
import json
import struct
import sys
import time

import zmq
from coppeliasim_zmqremoteapi_client import RemoteAPIClient

FAILS = []


def check(cond, msg):
    print(('  OK   ' if cond else '  FAIL ') + msg)
    if not cond:
        FAILS.append(msg)


class Req:
    def __init__(self, ctx, host, port, timeout=20000):
        self.s = ctx.socket(zmq.REQ)
        self.s.setsockopt(zmq.RCVTIMEO, timeout)
        self.s.setsockopt(zmq.LINGER, 0)
        self.s.connect('tcp://%s:%d' % (host, port))

    def call(self, cmd):
        self.s.send_string(cmd)
        frames = self.s.recv_multipart()
        head = json.loads(frames[0].decode('utf-8'))
        return head, frames[1:]


def recv_until(sub, pred, timeout):
    end = time.time() + timeout
    while time.time() < end:
        if sub.poll(100):
            frames = sub.recv_multipart()
            topic = frames[0].decode()
            head = json.loads(frames[1].decode())
            r = pred(topic, head, frames[2:])
            if r is not None:
                return r
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--host', default='localhost')
    ap.add_argument('--expect-house', action='store_true',
                    help='check the scene_house.ttt counts (52 shapes, 944 components, 16 textures)')
    args = ap.parse_args()

    ctx = zmq.Context()
    req = Req(ctx, args.host, 23005)
    sub = ctx.socket(zmq.SUB)
    sub.setsockopt(zmq.LINGER, 0)
    sub.setsockopt(zmq.SUBSCRIBE, b'')
    sub.connect('tcp://%s:23006' % args.host)

    print('[info]')
    try:
        info, _ = req.call('info')
    except zmq.error.Again:
        print('ERROR: no reply from the streamer on 23005. Run install_streamer.py.')
        return 2
    print('  ', info)
    check(info.get('protocol') == 1, 'protocol == 1')

    print('[list]')
    t0 = time.time()
    lst, _ = req.call('list')
    objs = lst['objects']
    by_h = {o['h']: o for o in objs}
    shapes = [o for o in objs if o['isShape']]
    print('   %d objects, %d shapes (%.0f ms)' % (len(objs), len(shapes), 1000 * (time.time() - t0)))
    check(len(objs) == info['objectCount'], 'list matches info.objectCount')
    check(all(o['parent'] == -1 or o['parent'] in by_h for o in objs), 'every parent is in the list')
    check(all(len(o['localPose']) == 7 for o in objs), 'poses have 7 values')
    client = RemoteAPIClient(host=args.host)
    client.socket.setsockopt(zmq.RCVTIMEO, 10000)
    sim = client.require('sim')
    worst = 0.0
    for o in objs:
        p = o['parent']
        if p != -1 and by_h[p]['type'] != 'joint':
            ref = sim.getObjectPose(o['h'], p)
            lp = o['localPose']
            dq = abs(sum(a * b for a, b in zip(ref[3:], lp[3:])))
            worst = max(worst, max(abs(a - b) for a, b in zip(ref[:3], lp[:3])), 1 - dq)
    check(worst < 1e-4, 'local pose = sim.getObjectPose(h, parent) (max error %.2e)' % worst)
    if args.expect_house:
        check(info['sceneObjectCount'] == 117, 'scene has 117 objects (excluding the streamer)')
        check(len(shapes) == 52, '52 shapes')
        invisible = sorted(o['path'] for o in shapes if not o['visible'])
        print('   invisible:', invisible)
        check(len(invisible) == 3, '3 invisible shapes')

    print('[mesh]')
    t0 = time.time()
    total_comp = total_v = total_i = 0
    tex_ids = set()
    nbytes = 0
    for o in shapes:
        head, fr = req.call('mesh %d' % o['h'])
        if 'error' in head:
            check(False, 'mesh %s: %s' % (o['path'], head['error']))
            continue
        v, i, n, uv = fr
        nbytes += sum(len(f) for f in fr)
        comps = head['components']
        nv, ni, nn, nuv = len(v) // 4, len(i) // 4, len(n) // 4, len(uv) // 4
        ok = True
        for c in comps:
            ok &= c['vOff'] + c['vCount'] <= nv and c['iOff'] + c['iCount'] <= ni
            ok &= c['nOff'] + c['nCount'] <= nn and c['uvOff'] + c['uvCount'] <= nuv
            ok &= c['nCount'] in (0, 3 * c['iCount'])
            ok &= c['uvCount'] in (0, 2 * c['iCount'])
            ok &= len(c['colors']) == 9
            if c['tex'] >= 0:
                tex_ids.add(c['tex'])
        if ok and comps:
            idx = struct.unpack('<%di' % ni, i)
            for c in comps:
                seg = idx[c['iOff']:c['iOff'] + c['iCount']]
                if seg and (min(seg) < 0 or max(seg) >= c['vCount'] // 3):
                    ok = False
        if not ok:
            check(False, 'mesh %s is consistent' % o['path'])
        if len(comps) != o['components']:
            check(False, 'component count of %s (%d, list says %d)' % (o['path'], len(comps), o['components']))
        total_comp += len(comps)
        total_v += nv // 3
        total_i += ni
    dt = time.time() - t0
    print('   %d components, %d vertices, %d indices, %d textures, %.1f MB in %.2f s'
          % (total_comp, total_v, total_i, len(tex_ids), nbytes / 1e6, dt))
    if args.expect_house:
        check(total_comp == 944, '944 components')
        check(total_v == 246748, '246748 vertices')
        check(total_i == 1344471, '1344471 indices')
        check(len(tex_ids) == 16, '16 textures')

    print('[texture]')
    for tid in sorted(tex_ids):
        head, fr = req.call('texture %d' % tid)
        good = 'error' not in head and len(fr) == 1 and len(fr[0]) == head['w'] * head['h'] * 4
        if not good:
            check(False, 'texture %d (%sx%s)' % (tid, head.get('w'), head.get('h')))
    print('   %d textures checked' % len(tex_ids))
    head, _ = req.call('texture 999999')
    check('error' in head, 'unknown texture returns an error')
    head, _ = req.call('bogus')
    check('error' in head, 'unknown command returns an error')

    print('[poses: keyframe]')
    time.sleep(0.3)

    def is_key(topic, head, rest):
        if topic == 'poses' and head['full']:
            return head, rest
    r = recv_until(sub, is_key, 3.0)
    check(r is not None, 'keyframe received within 3 s')
    if r:
        head, (hs, ps) = r
        check(head['count'] == len(objs) and len(hs) == 4 * len(objs) and len(ps) == 28 * len(objs),
              'keyframe contains all %d objects' % len(objs))

    print('[poses: motion through the Remote API]')
    target = next((o for o in objs if o['type'] == 'joint'), None) or shapes[0]
    h = target['h']
    print('   target:', target['path'], target['type'])
    if target['type'] == 'joint':
        old = sim.getJointPosition(h)
        cyclic, (lo, rng) = sim.getJointInterval(h)
        new = old + 0.5 if (cyclic or old + 0.5 <= lo + rng) else old - 0.5
        sim.setJointPosition(h, new)
        # joints: the child's pose changes (the joint node itself does not move)
        moved = {o['h'] for o in objs if o['parent'] == h}
    else:
        old = sim.getObjectPosition(h, sim.handle_parent)
        sim.setObjectPosition(h, [old[0], old[1], old[2] + 0.1], sim.handle_parent)
        moved = {h}
    try:
        def is_delta(topic, head, rest):
            if topic == 'poses' and not head['full']:
                hs = set(struct.unpack('<%di' % head['count'], rest[0]))
                if hs & moved:
                    return hs
        r = recv_until(sub, is_delta, 3.0)
        check(r is not None, 'pose delta of the moved object received')
    finally:
        if target['type'] == 'joint':
            sim.setJointPosition(h, old)
        else:
            sim.setObjectPosition(h, old, sim.handle_parent)

    print('[scene: create/remove a cube]')
    v0 = info['sceneVersion']
    cube = sim.createPrimitiveShape(sim.primitiveshape_cuboid, [0.2, 0.2, 0.2])
    try:
        def is_scene(topic, head, rest):
            if topic == 'scene':
                return head
        r = recv_until(sub, is_scene, 3.0)
        check(r is not None and r['sceneVersion'] > v0, 'scene event after creating the cube')
        lst2, _ = req.call('list')
        check(any(o['h'] == cube for o in lst2['objects']), 'cube shows up in list')
        head, fr = req.call('mesh %d' % cube)
        check('error' not in head and len(head['components']) == 1, 'cube mesh')
    finally:
        sim.removeObjects([cube])
    r = recv_until(sub, is_scene, 3.0)
    check(r is not None, 'scene event after removing the cube')
    lst3, _ = req.call('list')
    check(not any(o['h'] == cube for o in lst3['objects']), 'cube is gone from list')

    print()
    print('RESULT:', 'PASSED' if not FAILS else 'FAILED (%d)' % len(FAILS))
    for f in FAILS:
        print('  -', f)
    ctx.destroy(0)
    return 0 if not FAILS else 1


if __name__ == '__main__':
    sys.exit(main())
