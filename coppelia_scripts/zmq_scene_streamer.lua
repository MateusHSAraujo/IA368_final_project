-- zmq_scene_streamer.lua
-- Customization script (CoppeliaSim 4.10, sim-2 API) that exposes the scene to Unity.
--   REP :23005  text requests: info | list | mesh <h> | texture <id>
--   PUB :23006  ["poses", header, int32 handles, f32 local poses (7 per object)]
--               ["scene", {sceneVersion}] when objects are added/removed/reparented
-- Full protocol: README_COPPELIA_SETUP.md
-- Automatic installation: install_streamer.py (via the ZMQ Remote API, port 23000).

sim = require 'sim'
simZMQ = require 'simZMQ'

local PROTOCOL = 1
local REP_PORT = 23005
local PUB_PORT = 23006
local STREAMER_ALIAS = 'ZmqSceneStreamer'
local PUB_PERIOD_RUNNING = 1.0 / 30.0
local PUB_PERIOD_STOPPED = 1.0 / 10.0
local KEYFRAME_PERIOD = 1.0
local SCENE_CHECK_PERIOD = 0.25
local MAX_REQUESTS_PER_CALL = 8
local POS_EPS = 1e-5
local QUAT_EPS = 1e-5

local ctx, rep, pub
local sceneVersion = 0
local objects = {}      -- ordered list of exported handles
local info = {}         -- h -> {parent=effParent, type=, ...}
local lastPose = {}     -- h -> last pose[7] sent
local signature = ''
local seq = 0
local lastPub, lastKey, lastSceneCheck = -1e9, -1e9, -1e9
local textureCache = {} -- id -> {w=, h=, data=}
local textureOwner = {} -- id -> handle of a shape that uses the texture

----------------------------------------------------------------------------
-- Minimal JSON encoder
----------------------------------------------------------------------------
local json = {}
local escapes = { ['"'] = '\\"', ['\\'] = '\\\\', ['\b'] = '\\b', ['\f'] = '\\f',
    ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t' }

local function isArray(t)
    if next(t) == nil then return true end
    local n = #t
    if n == 0 then return false end
    for k in pairs(t) do
        if type(k) ~= 'number' or k < 1 or k > n or k % 1 ~= 0 then return false end
    end
    return true
end

function json.encode(v)
    local tv = type(v)
    if tv == 'nil' then return 'null'
    elseif tv == 'boolean' then return v and 'true' or 'false'
    elseif tv == 'number' then
        if v ~= v or v == math.huge or v == -math.huge then return '0' end
        if math.type and math.type(v) == 'integer' then return tostring(v) end
        if v % 1 == 0 and math.abs(v) < 1e15 then return string.format('%d', v) end
        return string.format('%.9g', v)
    elseif tv == 'string' then
        return '"' .. v:gsub('[%c"\\]', function(c)
            return escapes[c] or string.format('\\u%04x', c:byte())
        end) .. '"'
    elseif tv == 'table' then
        local parts = {}
        if isArray(v) then
            for i = 1, #v do parts[i] = json.encode(v[i]) end
            return '[' .. table.concat(parts, ',') .. ']'
        end
        for k, val in pairs(v) do
            parts[#parts + 1] = json.encode(tostring(k)) .. ':' .. json.encode(val)
        end
        return '{' .. table.concat(parts, ',') .. '}'
    end
    return 'null'
end

----------------------------------------------------------------------------
-- Scene
----------------------------------------------------------------------------
local excludedTypes = {
    [sim.sceneobject_camera] = true,
    [sim.sceneobject_light] = true,
}
if sim.sceneobject_script then excludedTypes[sim.sceneobject_script] = true end

local function typeName(t)
    if t == sim.sceneobject_shape then return 'shape'
    elseif t == sim.sceneobject_dummy then return 'dummy'
    elseif t == sim.sceneobject_joint then return 'joint'
    elseif t == sim.sceneobject_forcesensor then return 'forcesensor'
    elseif t == sim.sceneobject_visionsensor then return 'visionsensor'
    elseif t == sim.sceneobject_proximitysensor then return 'proximitysensor'
    elseif t == sim.sceneobject_graph then return 'graph'
    elseif t == sim.sceneobject_octree then return 'octree'
    elseif t == sim.sceneobject_pointcloud then return 'pointcloud'
    end
    return 'other'
end

local function isStreamerObject(h)
    -- The streamer's own dummy (and its children) is not exported.
    local p = h
    while p ~= -1 do
        if sim.getObjectAlias(p, 0) == STREAMER_ALIAS then return true end
        p = sim.getObjectParent(p)
    end
    return false
end

-- Recomputes the list of exported objects. Returns true if the structure changed.
local function scanScene()
    local all = sim.getObjectsInTree(sim.handle_scene)
    local included = {}
    local incSet = {}
    for _, h in ipairs(all) do
        local t = sim.getObjectType(h)
        if not excludedTypes[t] and not isStreamerObject(h) then
            included[#included + 1] = h
            incSet[h] = t
        end
    end
    local newInfo = {}
    local sig = {}
    for _, h in ipairs(included) do
        -- effective parent: closest exported ancestor (-1 = world)
        local p = sim.getObjectParent(h)
        while p ~= -1 and not incSet[p] do p = sim.getObjectParent(p) end
        newInfo[h] = { parent = p, type = incSet[h] }
        sig[#sig + 1] = h .. ':' .. p
    end
    local newSig = table.concat(sig, ';')
    local changed = newSig ~= signature
    objects = included
    info = newInfo
    if changed then
        signature = newSig
        for h in pairs(lastPose) do if not newInfo[h] then lastPose[h] = nil end end
    end
    return changed
end

-- Local pose = inv(parent world pose) * object world pose.
-- Computed from world poses so that, under a joint, the joint's intrinsic rotation
-- shows up in the child's pose (the joint node in Unity is the joint's base frame).
local worldCache = {}
local function worldPose(h)
    local w = worldCache[h]
    if not w then
        w = sim.getObjectPose(h, sim.handle_world)
        worldCache[h] = w
    end
    return w
end

-- inv(a) * b, poses {x,y,z,qx,qy,qz,qw}
local function relativePose(a, b)
    -- q = conj(qa) * qb
    local ax, ay, az, aw = -a[4], -a[5], -a[6], a[7]
    local bx, by, bz, bw = b[4], b[5], b[6], b[7]
    local qx = aw * bx + ax * bw + ay * bz - az * by
    local qy = aw * by - ax * bz + ay * bw + az * bx
    local qz = aw * bz + ax * by - ay * bx + az * bw
    local qw = aw * bw - ax * bx - ay * by - az * bz
    -- t = conj(qa) * (pb - pa)
    local vx, vy, vz = b[1] - a[1], b[2] - a[2], b[3] - a[3]
    -- v' = v + 2w(u×v) + 2u×(u×v), with u = (ax,ay,az), w = aw
    local cx = ay * vz - az * vy
    local cy = az * vx - ax * vz
    local cz = ax * vy - ay * vx
    local tx = vx + 2 * (aw * cx + ay * cz - az * cy)
    local ty = vy + 2 * (aw * cy + az * cx - ax * cz)
    local tz = vz + 2 * (aw * cz + ax * cy - ay * cx)
    return { tx, ty, tz, qx, qy, qz, qw }
end

local function localPose(h)
    local p = info[h].parent
    if p == -1 then return worldPose(h) end
    return relativePose(worldPose(p), worldPose(h))
end

local function clearPoseCache() worldCache = {} end

local function shapeVisible(h)
    local ok, vis = pcall(sim.getBoolProperty, h, 'visible')
    if ok then return vis end
    return sim.getObjectInt32Param(h, sim.objintparam_visible) ~= 0
end

local function componentCount(h)
    local ok, m = pcall(sim.getIntArrayProperty, h, 'meshes')
    if ok and m then return #m end
    return 0
end

----------------------------------------------------------------------------
-- Replies
----------------------------------------------------------------------------
local function bytes(b)
    if type(b) == 'string' then return b end
    return tostring(b)
end

local function packF(t) return bytes(sim.packFloatTable(t)) end
local function packI(t) return bytes(sim.packInt32Table(t)) end

local function sendFrames(sock, frames)
    for i = 1, #frames do
        simZMQ.send(sock, frames[i], i < #frames and simZMQ.SNDMORE or 0)
    end
end

local function errorReply(msg)
    return { json.encode({ error = msg }) }
end

local function cmdInfo()
    local shapes = 0
    for _, h in ipairs(objects) do
        if info[h].type == sim.sceneobject_shape then shapes = shapes + 1 end
    end
    local all = sim.getObjectsInTree(sim.handle_scene)
    local sceneCount = 0
    for _, h in ipairs(all) do
        if not isStreamerObject(h) then sceneCount = sceneCount + 1 end
    end
    local ok, path = pcall(sim.getStringProperty, sim.handle_scene, 'scenePath')
    return { json.encode({
        protocol = PROTOCOL,
        scene = ok and path or '',
        objectCount = #objects,
        sceneObjectCount = sceneCount,
        shapeCount = shapes,
        sceneVersion = sceneVersion,
        posePort = PUB_PORT,
        simulationState = sim.getSimulationState(),
    }) }
end

local function cmdList()
    local list = {}
    for i, h in ipairs(objects) do
        local inf = info[h]
        local isShape = inf.type == sim.sceneobject_shape
        list[i] = {
            h = h,
            name = sim.getObjectAlias(h, 0),
            path = sim.getObjectAlias(h, 2),
            type = typeName(inf.type),
            parent = inf.parent,
            localPose = localPose(h),
            visible = isShape and shapeVisible(h) or false,
            isShape = isShape,
            components = isShape and componentCount(h) or 0,
        }
    end
    return { json.encode({ sceneVersion = sceneVersion, objects = list }) }
end

local function cmdMesh(h)
    if not info[h] or info[h].type ~= sim.sceneobject_shape then
        return errorReply('not a shape: ' .. tostring(h))
    end
    local n = componentCount(h)
    local comps = {}
    local vParts, iParts, nParts, uParts = {}, {}, {}, {}
    local vOff, iOff, nOff, uOff = 0, 0, 0, 0
    for c = 0, n - 1 do
        local viz = sim.getShapeViz(h, c)
        if viz and viz.vertices then
            local verts, inds, norms = viz.vertices, viz.indices, viz.normals or {}
            local comp = {
                vOff = vOff, vCount = #verts,
                iOff = iOff, iCount = #inds,
                nOff = nOff, nCount = #norms,
                uvOff = uOff, uvCount = 0,
                colors = viz.colors or {},
                transparency = viz.transparency or 0,
                options = viz.options or 0,
                shadingAngle = viz.shadingAngle or 0,
                tex = -1, texOptions = 0, texApplyMode = 0,
            }
            vParts[#vParts + 1] = packF(verts)
            iParts[#iParts + 1] = packI(inds)
            if #norms > 0 then nParts[#nParts + 1] = packF(norms) end
            local tex = viz.texture
            if tex and tex.coordinates and #tex.coordinates > 0 then
                comp.tex = tex.id or -1
                comp.texOptions = tex.options or 0
                comp.texApplyMode = tex.applyMode or 0
                comp.uvCount = #tex.coordinates
                uParts[#uParts + 1] = packF(tex.coordinates)
                uOff = uOff + #tex.coordinates
                if tex.id and tex.texture and tex.resolution and not textureCache[tex.id] then
                    textureCache[tex.id] = { w = tex.resolution[1], h = tex.resolution[2], data = bytes(tex.texture) }
                end
                if tex.id then textureOwner[tex.id] = h end
            end
            vOff = vOff + #verts
            iOff = iOff + #inds
            nOff = nOff + #norms
            comps[#comps + 1] = comp
        end
    end
    local header = json.encode({ h = h, components = comps })
    return { header, table.concat(vParts), table.concat(iParts), table.concat(nParts), table.concat(uParts) }
end

local function findTexture(id)
    if textureCache[id] then return textureCache[id] end
    local candidates = {}
    if textureOwner[id] then candidates[1] = textureOwner[id] end
    for _, h in ipairs(objects) do
        if info[h].type == sim.sceneobject_shape then candidates[#candidates + 1] = h end
    end
    for _, h in ipairs(candidates) do
        if sim.isHandle(h) then
            for c = 0, componentCount(h) - 1 do
                local viz = sim.getShapeViz(h, c)
                local tex = viz and viz.texture
                if tex and tex.id and tex.texture and tex.resolution then
                    if not textureCache[tex.id] then
                        textureCache[tex.id] = { w = tex.resolution[1], h = tex.resolution[2], data = bytes(tex.texture) }
                    end
                    if tex.id == id then return textureCache[id] end
                end
            end
        end
    end
    return nil
end

local function cmdTexture(id)
    local t = findTexture(id)
    if not t then return errorReply('texture not found: ' .. tostring(id)) end
    return { json.encode({ id = id, w = t.w, h = t.h }), t.data }
end

local function handleRequest(req)
    local cmd, arg = req:match('^%s*(%S+)%s*(%S*)')
    if cmd == 'info' then return cmdInfo()
    elseif cmd == 'list' then return cmdList()
    elseif cmd == 'mesh' then return cmdMesh(tonumber(arg) or -1)
    elseif cmd == 'texture' then return cmdTexture(tonumber(arg) or -1)
    end
    return errorReply('unknown command: ' .. tostring(req))
end

local function serviceRequests()
    for _ = 1, MAX_REQUESTS_PER_CALL do
        local rc, revents = simZMQ.poll({ rep }, { simZMQ.POLLIN }, 0)
        if rc <= 0 then break end
        local _, data = simZMQ.recv(rep, 0)
        local ok, frames = pcall(handleRequest, tostring(data))
        if not ok then
            sim.addLog(sim.verbosity_scripterrors, 'ZmqSceneStreamer: ' .. tostring(frames))
            frames = errorReply(tostring(frames))
        end
        sendFrames(rep, frames)
    end
end

----------------------------------------------------------------------------
-- Pose stream
----------------------------------------------------------------------------
local function poseChanged(a, b)
    if not b then return true end
    for i = 1, 3 do if math.abs(a[i] - b[i]) > POS_EPS then return true end end
    -- q and -q represent the same rotation
    local dot = a[4] * b[4] + a[5] * b[5] + a[6] * b[6] + a[7] * b[7]
    return (1 - math.abs(dot)) > QUAT_EPS
end

local function publishScene()
    sendFrames(pub, { 'scene', json.encode({ sceneVersion = sceneVersion }) })
end

local function publishPoses(now, full)
    local handles, poses = {}, {}
    for _, h in ipairs(objects) do
        local p = localPose(h)
        if full or poseChanged(p, lastPose[h]) then
            handles[#handles + 1] = h
            for i = 1, 7 do poses[#poses + 1] = p[i] end
            lastPose[h] = p
        end
    end
    if #handles == 0 then return end
    seq = seq + 1
    local header = json.encode({
        seq = seq, t = sim.getSimulationTime(), full = full,
        sceneVersion = sceneVersion, count = #handles,
    })
    sendFrames(pub, { 'poses', header, packI(handles), packF(poses) })
end

local function tick()
    if not rep then return end
    clearPoseCache()
    local now = sim.getSystemTime()
    if now - lastSceneCheck >= SCENE_CHECK_PERIOD then
        lastSceneCheck = now
        if scanScene() then
            sceneVersion = sceneVersion + 1
            publishScene()
            lastKey = -1e9 -- force a keyframe for the new objects
        end
    end
    serviceRequests()
    local running = sim.getSimulationState() ~= sim.simulation_stopped
    local period = running and PUB_PERIOD_RUNNING or PUB_PERIOD_STOPPED
    if now - lastPub >= period then
        lastPub = now
        local full = (now - lastKey) >= KEYFRAME_PERIOD
        if full then lastKey = now end
        publishPoses(now, full)
    end
end

local function safeTick()
    local ok, err = pcall(tick)
    if not ok then sim.addLog(sim.verbosity_scripterrors, 'ZmqSceneStreamer: ' .. tostring(err)) end
end

----------------------------------------------------------------------------
-- Callbacks
----------------------------------------------------------------------------
function sysCall_init()
    ctx = simZMQ.ctx_new()
    rep = simZMQ.socket(ctx, simZMQ.REP)
    pub = simZMQ.socket(ctx, simZMQ.PUB)
    local zero = sim.packInt32Table({ 0 })
    simZMQ.setsockopt(rep, simZMQ.LINGER, zero)
    simZMQ.setsockopt(pub, simZMQ.LINGER, zero)
    local r1 = simZMQ.bind(rep, string.format('tcp://*:%d', REP_PORT))
    local r2 = simZMQ.bind(pub, string.format('tcp://*:%d', PUB_PORT))
    if r1 == -1 or r2 == -1 then
        sim.addLog(sim.verbosity_scripterrors,
            string.format('ZmqSceneStreamer: bind failed (%d/%d). Is another instance running?', REP_PORT, PUB_PORT))
        simZMQ.close(rep); simZMQ.close(pub); simZMQ.ctx_term(ctx)
        rep, pub, ctx = nil, nil, nil
        return
    end
    scanScene()
    sceneVersion = 1
    sim.addLog(sim.verbosity_scriptinfos,
        string.format('ZmqSceneStreamer: REP :%d, PUB :%d, %d objects', REP_PORT, PUB_PORT, #objects))
end

function sysCall_nonSimulation() safeTick() end
function sysCall_sensing() safeTick() end
function sysCall_suspended() safeTick() end

function sysCall_cleanup()
    if rep then simZMQ.close(rep) end
    if pub then simZMQ.close(pub) end
    if ctx then simZMQ.ctx_term(ctx) end
    rep, pub, ctx = nil, nil, nil
end
