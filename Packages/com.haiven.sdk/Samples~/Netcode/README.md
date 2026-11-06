# Netcode for GameObjects over Haiven P2P sessions

Netcode for GameObjects (NGO) 2.4 over a Haiven P2P session: one WebRTC
connection between the host and each guest, signaled through the Haiven game
socket. Needs Unity 6 and `com.unity.netcode.gameobjects` 2.4. Not for WebGL.

- `HaivenNetworkTransport` is the NGO `NetworkTransport`. NGO client ids map
  to Haiven user ids; read one with `GetUserId(clientId)`.
- Peer attempts run in the SDK core's `HaivenPeerConnections`. The sample keeps NGO ids, delivery mapping and framing; offer/answer exchange, the 64-candidate cap and the 20-second timeout are shared with other .NET hosts.
- `HaivenNetcodeSession` sits next to the `NetworkManager`, creates or joins a
  session through `HaivenP2PSession`, sets the transport up and starts NGO.

```csharp
var p2p = networkManager.gameObject.AddComponent<HaivenNetcodeSession>();
p2p.Configure(new HaivenSessionsClient(restTransport, gameId));
p2p.Attach(session, connectionId);   // the signed-in game socket; again after every reconnect
p2p.Ended += (reason, code) => ShowMenu();

await p2p.HostAsync(new HaivenSessionCreateOptions(maxMembers: 4));
// or, on another player:
await p2p.JoinAsync(sessionId);
```

The game socket must be opened with the game credential. When the session ends
(the host closed it, the player was removed, or the socket did not come back
within 30 seconds), NGO is shut down and `Ended` says why.

This is sample code: once imported it is the game's copy, and package updates
do not change it.
