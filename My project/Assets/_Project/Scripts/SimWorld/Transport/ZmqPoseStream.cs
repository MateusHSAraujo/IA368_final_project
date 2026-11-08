using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;
using UnityEngine;
using VRRobot.SimWorld.Protocol;

namespace VRRobot.SimWorld.Transport
{
    /// <summary>
    /// SUB on port 23006 in a background thread. Keeps only the latest local pose of each handle
    /// (intermediate ones are dropped) and queues the "scene" events.
    /// </summary>
    public sealed class ZmqPoseStream : IDisposable
    {
        readonly string _endpoint;
        readonly Thread _thread;
        readonly object _lock = new object();
        readonly Dictionary<int, float[]> _latest = new Dictionary<int, float[]>();
        readonly ConcurrentQueue<int> _sceneEvents = new ConcurrentQueue<int>();
        volatile bool _stop;
        bool _disposed;

        public long MessagesReceived => Interlocked.Read(ref _messages);
        long _messages;
        /// <summary>Time (Stopwatch, s) of the last message; &lt; 0 if none.</summary>
        public double LastMessageTime { get; private set; } = -1;
        public int LastSceneVersion { get; private set; } = -1;
        public double LastSimTime { get; private set; }
        public string LastError { get; private set; }

        static readonly System.Diagnostics.Stopwatch s_clock = System.Diagnostics.Stopwatch.StartNew();
        public static double Now => s_clock.Elapsed.TotalSeconds;

        public ZmqPoseStream(string host, int port)
        {
            _endpoint = $"tcp://{host}:{port}";
            NetMQLifecycle.Acquire();
            _thread = new Thread(Run) { IsBackground = true, Name = "ZmqPoseStream" };
            _thread.Start();
        }

        void Run()
        {
            using var sock = new SubscriberSocket();
            sock.Options.Linger = TimeSpan.Zero;
            sock.Options.ReceiveHighWatermark = 100;
            sock.Connect(_endpoint);
            sock.SubscribeToAnyTopic();
            var frames = new List<byte[]>();
            var wait = TimeSpan.FromMilliseconds(100);
            while (!_stop)
            {
                try
                {
                    frames.Clear();
                    if (!sock.TryReceiveMultipartBytes(wait, ref frames)) continue;
                    Interlocked.Increment(ref _messages);
                    LastMessageTime = Now;
                    Handle(frames);
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                }
            }
        }

        void Handle(List<byte[]> frames)
        {
            if (frames.Count < 2) return;
            var topic = FrameReader.Text(frames[0]);
            if (topic == "poses" && frames.Count == 4)
            {
                var head = JsonUtility.FromJson<PosesHeader>(FrameReader.Text(frames[1]));
                var handles = FrameReader.ToInts(frames[2]);
                var poses = FrameReader.ToFloats(frames[3]);
                if (poses.Length != handles.Length * 7) return;
                LastSimTime = head.t;
                LastSceneVersion = head.sceneVersion;
                lock (_lock)
                {
                    for (int i = 0; i < handles.Length; i++)
                    {
                        var p = new float[7];
                        Array.Copy(poses, i * 7, p, 0, 7);
                        _latest[handles[i]] = p;
                    }
                }
            }
            else if (topic == "scene")
            {
                var ev = JsonUtility.FromJson<SceneEvent>(FrameReader.Text(frames[1]));
                LastSceneVersion = ev.sceneVersion;
                _sceneEvents.Enqueue(ev.sceneVersion);
            }
        }

        /// <summary>Hands over (on the calling thread) the poses received since the last call.</summary>
        public int Drain(Dictionary<int, float[]> into)
        {
            lock (_lock)
            {
                foreach (var kv in _latest) into[kv.Key] = kv.Value;
                int n = _latest.Count;
                _latest.Clear();
                return n;
            }
        }

        public bool TryDequeueSceneEvent(out int sceneVersion) => _sceneEvents.TryDequeue(out sceneVersion);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop = true;
            _thread.Join(2000);
            NetMQLifecycle.Release();
        }
    }
}
