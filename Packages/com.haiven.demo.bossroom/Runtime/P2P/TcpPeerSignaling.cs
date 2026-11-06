#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Haiven.Sdk;
using UnityEngine;

namespace Haiven.Demo.BossRoom.P2P
{
    /// <summary>
    /// Signaling between Boss Room processes over one TCP connection per guest,
    /// on the address and port typed into Boss Room's IP window, for Direct IP
    /// play without a Haiven session. A Haiven room signals through
    /// <c>p2p.signal</c> on the game socket instead.
    ///
    /// The host names every guest, the way haiven-api sets <c>fromUserId</c>, so
    /// a guest cannot claim another guest's id. Messages are one JSON object per
    /// line. Sockets are read on background threads and handed to the main
    /// thread through <see cref="Poll"/>.
    /// </summary>
    public sealed class TcpPeerSignaling : IHaivenPeerSignaling, IHaivenPeerSignalingLifecycle
    {
        public const string HostId = "host";

        private readonly Func<(string Address, ushort Port)> endpoint;
        private readonly ConcurrentQueue<Action> inbox = new ConcurrentQueue<Action>();
        private readonly Dictionary<string, Link> guests = new Dictionary<string, Link>();
        private TcpListener? listener;
        private Link? toHost;
        private int nextGuest;
        private volatile bool running;

        public TcpPeerSignaling(Func<(string Address, ushort Port)> endpoint)
        {
            this.endpoint = endpoint;
        }

        public string LocalUserId { get; private set; } = "";

        public string? HostUserId { get; private set; }

        public event Action<string, PeerSignal>? Received;

        public event Action<string>? HostUnreachable;

        public bool Start(bool asHost)
        {
            var (address, port) = endpoint();
            running = true;
            try
            {
                if (asHost)
                {
                    LocalUserId = HostId;
                    listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    new Thread(Accept) { IsBackground = true, Name = "haiven-p2p-accept" }.Start();
                    Debug.Log($"[haiven-p2p] signaling listens on TCP {port}");
                }
                else
                {
                    HostUserId = HostId;
                    var link = new Link(new TcpClient());
                    toHost = link;
                    new Thread(() => ConnectToHost(link, address, port)) { IsBackground = true, Name = "haiven-p2p-guest" }.Start();
                }
                return true;
            }
            catch (SocketException error)
            {
                Debug.LogError($"[haiven-p2p] signaling could not start on {address}:{port}: {error.Message}");
                Stop();
                return false;
            }
        }

        public void Poll()
        {
            while (inbox.TryDequeue(out var action)) action();
        }

        public void Send(string toUserId, PeerSignal signal)
        {
            var wire = new Wire
            {
                type = "signal",
                kind = signal.Kind.ToString(),
                sdp = signal.Sdp ?? "",
                candidate = signal.Candidate?.Candidate ?? "",
                sdpMid = signal.Candidate?.SdpMid ?? "",
                sdpMLineIndex = signal.Candidate?.SdpMLineIndex ?? -1,
            };
            if (toHost != null)
            {
                if (toUserId == HostId) toHost.Write(wire);
            }
            else if (guests.TryGetValue(toUserId, out var guest))
            {
                guest.Write(wire);
            }
        }

        public void Stop()
        {
            running = false;
            listener?.Stop();
            listener = null;
            toHost?.Close();
            toHost = null;
            foreach (var guest in guests.Values) guest.Close();
            guests.Clear();
            while (inbox.TryDequeue(out _)) { }
        }

        private void Accept()
        {
            var accepting = listener;
            while (running && accepting != null)
            {
                TcpClient client;
                try
                {
                    client = accepting.AcceptTcpClient();
                }
                catch (Exception) when (!running)
                {
                    return;
                }
                catch (SocketException error)
                {
                    Debug.LogWarning($"[haiven-p2p] signaling accept failed: {error.Message}");
                    return;
                }

                var id = "guest-" + Interlocked.Increment(ref nextGuest);
                var link = new Link(client);
                inbox.Enqueue(() =>
                {
                    if (!running)
                    {
                        link.Close();
                        return;
                    }
                    guests[id] = link;
                    link.Write(new Wire { type = "welcome", id = id });
                });
                // Each guest is read on its own thread, so the next one is
                // accepted while this one stays connected.
                new Thread(() => Read(link, id)) { IsBackground = true, Name = "haiven-p2p-" + id }.Start();
            }
        }

        private void ConnectToHost(Link link, string address, ushort port)
        {
            try
            {
                link.Client.Connect(address, port);
            }
            catch (SocketException error)
            {
                var reason = $"signaling could not reach {address}:{port}: {error.Message}";
                inbox.Enqueue(() => { if (running) HostUnreachable?.Invoke(reason); });
                return;
            }
            link.Connected();
            Read(link, HostId);
        }

        private void Read(Link link, string from)
        {
            try
            {
                using var reader = new StreamReader(link.Client.GetStream(), Encoding.UTF8);
                string? line;
                while (running && (line = reader.ReadLine()) != null)
                {
                    var wire = JsonUtility.FromJson<Wire>(line);
                    inbox.Enqueue(() => Handle(from, wire));
                }
            }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is InvalidOperationException)
            {
                // The peer went away; WebRTC notices on its own.
            }
            catch (ArgumentException error)
            {
                Debug.LogWarning($"[haiven-p2p] signaling from {from} was not JSON: {error.Message}");
            }
            if (from != HostId) inbox.Enqueue(() => guests.Remove(from));
            else inbox.Enqueue(() => { if (running) HostUnreachable?.Invoke("signaling to the host closed"); });
        }

        private void Handle(string from, Wire wire)
        {
            if (!running) return;
            if (wire.type == "welcome")
            {
                // Only the host names guests.
                if (from == HostId) LocalUserId = wire.id;
                return;
            }
            if (wire.type != "signal" || !Enum.TryParse<PeerSignalKind>(wire.kind, out var kind)) return;

            var signal = kind switch
            {
                PeerSignalKind.Offer => PeerSignal.Offer(wire.sdp),
                PeerSignalKind.Answer => PeerSignal.Answer(wire.sdp),
                _ => PeerSignal.ForCandidate(new PeerIceCandidate(
                    wire.candidate,
                    wire.sdpMid == "" ? null : wire.sdpMid,
                    wire.sdpMLineIndex < 0 ? (int?)null : wire.sdpMLineIndex)),
            };
            Received?.Invoke(from, signal);
        }

        [Serializable]
        private sealed class Wire
        {
            public string type = "";
            public string id = "";
            public string kind = "";
            public string sdp = "";
            public string candidate = "";
            public string sdpMid = "";
            public int sdpMLineIndex = -1;
        }

        /// <summary>One TCP connection. Writes made before a guest connects wait for it.</summary>
        private sealed class Link
        {
            private readonly object gate = new object();
            private readonly List<string> waiting = new List<string>();
            private bool connected;

            public Link(TcpClient client)
            {
                Client = client;
                connected = client.Connected;
            }

            public TcpClient Client { get; }

            public void Write(Wire wire)
            {
                var line = JsonUtility.ToJson(wire) + "\n";
                lock (gate)
                {
                    if (!connected)
                    {
                        waiting.Add(line);
                        return;
                    }
                }
                WriteNow(line);
            }

            public void Connected()
            {
                List<string> pending;
                lock (gate)
                {
                    connected = true;
                    pending = new List<string>(waiting);
                    waiting.Clear();
                }
                foreach (var line in pending) WriteNow(line);
            }

            public void Close()
            {
                lock (gate) connected = false;
                Client.Close();
            }

            private void WriteNow(string line)
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                try
                {
                    lock (gate) Client.GetStream().Write(bytes, 0, bytes.Length);
                }
                catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is InvalidOperationException)
                {
                    // Closed; the reader reports it.
                }
            }
        }
    }
}
