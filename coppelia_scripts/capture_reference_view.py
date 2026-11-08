"""Renders a CoppeliaSim reference image to compare against Unity.

Usage:
    python -u capture_reference_view.py UX UY UZ TX TY TZ out.png [--hide /house/roof ...]

Takes the camera position and target in **Unity** coordinates and creates a temporary vision
sensor (perspective, 60° vertical FOV like the Unity camera, 1280x720) at the equivalent
CoppeliaSim pose. By default it hides /house/roof (invisible in the main camera, but rendered
by vision sensors). The sensor is removed and the layers are restored in the finally block.
"""
import argparse
import math

import numpy as np
import zmq
from coppeliasim_zmqremoteapi_client import RemoteAPIClient
from PIL import Image


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('coords', nargs=6, type=float)
    ap.add_argument('out')
    ap.add_argument('--hide', nargs='*', default=['/house/roof'])
    ap.add_argument('--width', type=int, default=1280)
    ap.add_argument('--height', type=int, default=720)
    args = ap.parse_args()

    c = RemoteAPIClient()
    c.socket.setsockopt(zmq.RCVTIMEO, 20000)
    sim = c.require('sim')

    ux, uy, uz, tx, ty, tz = args.coords
    to_c = lambda x, y, z: np.array([z, -x, y])  # Unity -> CoppeliaSim
    p, t = to_c(ux, uy, uz), to_c(tx, ty, tz)
    zax = (t - p) / np.linalg.norm(t - p)
    xax = np.cross([0, 0, 1.0], zax)
    xax /= np.linalg.norm(xax)
    yax = np.cross(zax, xax)
    m = [xax[0], yax[0], zax[0], p[0], xax[1], yax[1], zax[1], p[1], xax[2], yax[2], zax[2], p[2]]

    W, H = args.width, args.height
    fov = 2 * math.atan(math.tan(math.radians(30)) * max(W, H) / min(W, H))  # angle along the larger side
    hidden = []
    for path in args.hide:
        h = sim.getObject(path, {'noError': True})
        if h != -1:
            hidden.append((h, sim.getObjectInt32Param(h, sim.objintparam_visibility_layer)))
            sim.setObjectInt32Param(h, sim.objintparam_visibility_layer, 0)
    # options: bit0 = explicit handling, bit1 = perspective
    vs = sim.createVisionSensor(3, [W, H, 0, 0], [0.05, 200, fov, 0.1, 0, 0, 0, 0, 0, 0, 0])
    try:
        sim.setObjectMatrix(vs, m, sim.handle_world)
        sim.handleVisionSensor(vs)
        img, res = sim.getVisionSensorImg(vs)
        a = np.frombuffer(img, dtype=np.uint8).reshape(res[1], res[0], 3)[::-1]
        Image.fromarray(a).save(args.out)
        print('saved', args.out, res)
    finally:
        sim.removeObjects([vs])
        for h, layer in hidden:
            sim.setObjectInt32Param(h, sim.objintparam_visibility_layer, layer)


if __name__ == '__main__':
    main()
