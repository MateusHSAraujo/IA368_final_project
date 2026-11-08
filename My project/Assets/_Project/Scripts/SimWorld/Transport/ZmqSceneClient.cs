using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetMQ;
using NetMQ.Sockets;

namespace VRRobot.SimWorld.Transport
{
    /// <summary>
    /// REQ client for port 23005 in a background thread. Requests are queued and answered in
    /// order. On timeout the socket is recreated ("lazy pirate"), since a REQ socket that got
    /// no reply is unusable.
    /// </summary>
    public sealed class ZmqSceneClient : IDisposable
    {
        sealed class Pending
        {
            public string Command;
            public TaskCompletionSource<byte[][]> Tcs;
        }

        readonly string _endpoint;
        readonly TimeSpan _timeout;
        readonly BlockingCollection<Pending> _queue = new BlockingCollection<Pending>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly Thread _thread;
        bool _disposed;

        public bool LastRequestOk { get; private set; }
        public string LastError { get; private set; }
        public long BytesReceived => Interlocked.Read(ref _bytes);
        long _bytes;

        public ZmqSceneClient(string host, int port, int timeoutMs = 15000)
        {
            _endpoint = $"tcp://{host}:{port}";
            _timeout = TimeSpan.FromMilliseconds(timeoutMs);
            NetMQLifecycle.Acquire();
            _thread = new Thread(Run) { IsBackground = true, Name = "ZmqSceneClient" };
            _thread.Start();
        }

        public Task<byte[][]> Request(string command)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ZmqSceneClient));
            var p = new Pending
            {
                Command = command,
                Tcs = new TaskCompletionSource<byte[][]>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            _queue.Add(p);
            return p.Tcs.Task;
        }

        RequestSocket CreateSocket()
        {
            var s = new RequestSocket();
            s.Options.Linger = TimeSpan.Zero;
            s.Connect(_endpoint);
            return s;
        }

        void Run()
        {
            RequestSocket sock = null;
            var frames = new List<byte[]>();
            try
            {
                sock = CreateSocket();
                foreach (var p in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    try
                    {
                        sock.SendFrame(p.Command);
                        frames.Clear();
                        if (!sock.TryReceiveMultipartBytes(_timeout, ref frames))
                        {
                            sock.Dispose();
                            sock = CreateSocket();
                            LastRequestOk = false;
                            LastError = $"timeout on '{p.Command}' ({_endpoint})";
                            p.Tcs.TrySetException(new TimeoutException(LastError));
                            continue;
                        }
                        long n = 0;
                        foreach (var f in frames) n += f.Length;
                        Interlocked.Add(ref _bytes, n);
                        LastRequestOk = true;
                        p.Tcs.TrySetResult(frames.ToArray());
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        LastRequestOk = false;
                        LastError = e.Message;
                        p.Tcs.TrySetException(e);
                        try { sock?.Dispose(); } catch { }
                        sock = CreateSocket();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                try { sock?.Dispose(); } catch { }
                while (_queue.TryTake(out var left)) left.Tcs.TrySetCanceled();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _thread.Join(2000);
            _queue.Dispose();
            _cts.Dispose();
            NetMQLifecycle.Release();
        }
    }
}
