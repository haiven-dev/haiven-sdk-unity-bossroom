#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;

namespace Haiven.Demo.BossRoom.P2P.Tests
{
    /// <summary>
    /// Two NetworkManagers set up as Boss Room sets them up (Unity Transport
    /// holding the Direct IP address and port, then <see cref="HaivenP2PBoot.Install"/>),
    /// signaling over a real TCP socket.
    /// </summary>
    public sealed class TcpSignalingTests
    {
        private const float Timeout = 20f;

        private readonly List<GameObject> objects = new List<GameObject>();

        [TearDown]
        public void Destroy()
        {
            foreach (var created in objects)
            {
                var manager = created.GetComponent<NetworkManager>();
                if (manager != null && manager.IsListening) manager.Shutdown();
                UnityEngine.Object.Destroy(created);
            }
            objects.Clear();
        }

        [UnityTest]
        public IEnumerator BossRoomDirectIpRunsOverTheHaivenTransport()
        {
            var port = FreePort();
            var host = Manager("host", port);
            var guest = Manager("guest", port);

            Assert.That(host.StartHost(), Is.True);
            Assert.That(guest.StartClient(), Is.True);
            yield return Until(() => guest.IsConnectedClient && host.ConnectedClientsIds.Count == 2);

            var hostTransport = (HaivenNetworkTransport)host.NetworkConfig.NetworkTransport;
            Assert.That(hostTransport.GetUserId(1), Is.EqualTo("guest-1"), "the host names the guest");
            Assert.That(((TcpPeerSignaling)hostTransport.Signaling!).LocalUserId, Is.EqualTo(TcpPeerSignaling.HostId));
            var guestTransport = (HaivenNetworkTransport)guest.NetworkConfig.NetworkTransport;
            Assert.That(((TcpPeerSignaling)guestTransport.Signaling!).LocalUserId, Is.EqualTo("guest-1"));

            guest.Shutdown();
            yield return Until(() => host.ConnectedClientsIds.Count == 1);

            // Off and on again: a second guest joins the same host.
            var second = Manager("second guest", port);
            Assert.That(second.StartClient(), Is.True);
            yield return Until(() => second.IsConnectedClient && host.ConnectedClientsIds.Count == 2);
            Assert.That(hostTransport.GetUserId(2), Is.EqualTo("guest-2"));
        }

        [UnityTest]
        public IEnumerator HostStopsListeningOnShutdown()
        {
            var port = FreePort();
            var host = Manager("host", port);
            Assert.That(host.StartHost(), Is.True);
            yield return null;
            host.Shutdown();
            yield return null;

            // The port is free again, so a new host can start on it.
            var again = Manager("host again", port);
            Assert.That(again.StartHost(), Is.True);
        }

        [UnityTest]
        public IEnumerator GuestThatCannotReachTheHostIsDisconnected()
        {
            // Nothing listens on the port, so the TCP connect is refused.
            var guest = Manager("lonely guest", FreePort());
            var disconnected = false;
            guest.OnClientDisconnectCallback += _ => disconnected = true;
            Assert.That(guest.StartClient(), Is.True, "the guest starts and waits for signaling");
            yield return Until(() => disconnected);
            Assert.That(guest.IsConnectedClient, Is.False);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("signaling could not reach"));
        }

        private NetworkManager Manager(string name, ushort port)
        {
            var created = new GameObject(name);
            objects.Add(created);
            var unityTransport = created.AddComponent<UnityTransport>();
            unityTransport.SetConnectionData("127.0.0.1", port);
            var manager = created.AddComponent<NetworkManager>();
            manager.NetworkConfig = new NetworkConfig { NetworkTransport = unityTransport, EnableSceneManagement = false };
            HaivenP2PBoot.Install(manager);
            return manager;
        }

        private static ushort FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = (ushort)((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartup + Timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("timed out");
                yield return null;
            }
        }
    }
}
