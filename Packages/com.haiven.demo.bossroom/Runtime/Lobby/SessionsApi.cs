#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Json;
using Haiven.Sdk.Rest;

namespace Haiven.Demo.BossRoom.Lobby
{
    /// <summary>Who may find and join a room, as the game service names it.</summary>
    public enum SessionVisibility
    {
        /// <summary>Listed for every player the server lets in; always relay-only.</summary>
        Public,

        /// <summary>Listed for the host's friends only.</summary>
        Friends,

        /// <summary>Never listed; joined with the six-character code.</summary>
        Private,
    }

    /// <summary>One room in the list, as <c>GET /v0/games/{game_id}/sessions</c> returns it.</summary>
    public sealed class SessionListing
    {
        public string SessionId = "";
        public string HostUserId = "";
        public string Visibility = "";
        public int MaxMembers;
        public int MemberCount;
        public string RelayPolicy = "";
        public string State = "";
        public string Name = "";

        public bool IsFull => MemberCount >= MaxMembers;
    }

    /// <summary>What create and join give the caller.</summary>
    public sealed class SessionAdmission
    {
        public string SessionId = "";
        public string ParticipationId = "";
        public string HostUserId = "";
        public string RelayPolicy = "";
        public string? JoinCode;
        public string? JoinPayload;
        public string State = "";

        /// <summary>The proof for <c>session.resume</c>, kept in memory only (SDK-P2P-2); empty for a roster read.</summary>
        public string ResumeToken = "";

        /// <summary>The socket the participation is bound to; it moves with every resume.</summary>
        public string ConnectionId = "";

        /// <summary>The members' user ids, from a roster read; empty for create and join.</summary>
        public List<string> Members = new List<string>();
    }

    /// <summary>
    /// The REST half of the game service's P2P sessions (peer-connection rules), for one game.
    ///
    /// Every call needs the game credential and the id of the game socket it
    /// authenticated, so admission is bound to the socket that will carry the
    /// session's events and signaling. A refusal surfaces as a
    /// <see cref="HaivenRestException"/> whose <c>Code</c> is the API's error code.
    /// </summary>
    public sealed class SessionsApi
    {
        private readonly IRestApiTransport rest;
        private readonly string gameId;

        public SessionsApi(IRestApiTransport rest, string gameId)
        {
            this.rest = rest;
            this.gameId = gameId;
        }

        private string Root => "/games/" + Uri.EscapeDataString(gameId) + "/sessions";

        public async Task<IReadOnlyList<SessionListing>> ListAsync(CancellationToken cancellationToken = default)
        {
            var body = await rest.SendAsync(new RestRequest("GET", Root + "?limit=50", null, null), cancellationToken)
                .ConfigureAwait(false);
            var listings = new List<SessionListing>();
            if (string.IsNullOrEmpty(body)) return listings;
            var root = HaivenJson.Parse(body!);
            if (!root.TryGetProperty("items", out var items)) return listings;
            foreach (var item in items.EnumerateArray())
            {
                var listing = new SessionListing
                {
                    SessionId = Text(item, "sessionId"),
                    HostUserId = Text(item, "hostUserId"),
                    Visibility = Text(item, "visibility"),
                    MaxMembers = Number(item, "maxMembers"),
                    MemberCount = Number(item, "memberCount"),
                    RelayPolicy = Text(item, "relayPolicy"),
                    State = Text(item, "state"),
                };
                if (item.TryGetProperty("metadata", out var metadata)) listing.Name = Text(metadata, "name");
                listings.Add(listing);
            }
            return listings;
        }

        /// <summary>
        /// Creates a room with the caller as host. Mixed ages are allowed only for
        /// friends and private rooms; the server decides the age group itself.
        /// </summary>
        public async Task<SessionAdmission> CreateAsync(
            string connectionId,
            string name,
            SessionVisibility visibility,
            int maxMembers,
            bool allowMixedAges,
            CancellationToken cancellationToken = default)
        {
            var resumeToken = NewResumeToken();
            var request = new Dictionary<string, object?>
            {
                ["connectionId"] = connectionId,
                ["admissionId"] = Guid.NewGuid().ToString("D"),
                ["resumeToken"] = resumeToken,
                ["visibility"] = Wire(visibility),
                ["maxMembers"] = maxMembers,
                ["allowMixedAges"] = allowMixedAges,
                ["metadata"] = new Dictionary<string, object?> { ["name"] = name },
            };
            var body = await Post(Root, request, cancellationToken).ConfigureAwait(false);
            return Bound(Admission(body), resumeToken, connectionId);
        }

        /// <summary>Joins a listed room, or a private or mixed room by its code.</summary>
        public async Task<SessionAdmission> JoinAsync(
            string connectionId,
            string sessionId,
            string? joinCode,
            CancellationToken cancellationToken = default)
        {
            var resumeToken = NewResumeToken();
            var request = new Dictionary<string, object?>
            {
                ["connectionId"] = connectionId,
                ["admissionId"] = Guid.NewGuid().ToString("D"),
                ["resumeToken"] = resumeToken,
                ["joinCode"] = string.IsNullOrEmpty(joinCode) ? null : joinCode,
            };
            var body = await Post(Root + "/" + Uri.EscapeDataString(sessionId) + "/members", request, cancellationToken)
                .ConfigureAwait(false);
            return Bound(Admission(body), resumeToken, connectionId);
        }

        private static SessionAdmission Bound(SessionAdmission admission, string resumeToken, string connectionId)
        {
            admission.ResumeToken = resumeToken;
            admission.ConnectionId = connectionId;
            return admission;
        }

        /// <summary>Reads the room as a member sees it, which a create does not return in full.</summary>
        public async Task<SessionAdmission> RosterAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var body = await rest.SendAsync(
                new RestRequest("GET", Root + "/" + Uri.EscapeDataString(sessionId), null, null),
                cancellationToken).ConfigureAwait(false);
            return Admission(body);
        }

        /// <summary>Leaves the room; the host leaving closes it for everyone.</summary>
        public Task LeaveAsync(string sessionId, CancellationToken cancellationToken = default) =>
            rest.SendAsync(
                new RestRequest("DELETE", Root + "/" + Uri.EscapeDataString(sessionId) + "/members/me", null, null),
                cancellationToken);

        private Task<string?> Post(string path, Dictionary<string, object?> request, CancellationToken cancellationToken) =>
            rest.SendAsync(new RestRequest("POST", path, HaivenJson.Serialize(request), "application/json"), cancellationToken);

        private static SessionAdmission Admission(string? body)
        {
            if (string.IsNullOrEmpty(body)) throw new HaivenJsonException("haiven: the session response is empty");
            var root = HaivenJson.Parse(body!);
            var members = new List<string>();
            if (root.TryGetProperty("members", out var list))
                foreach (var member in list.EnumerateArray())
                {
                    var userId = Text(member, "userId");
                    if (userId.Length > 0) members.Add(userId);
                }
            return new SessionAdmission
            {
                Members = members,
                SessionId = Text(root, "sessionId"),
                ParticipationId = Text(root, "participationId"),
                HostUserId = Text(root, "hostUserId"),
                RelayPolicy = Text(root, "relayPolicy"),
                JoinCode = NullableText(root, "joinCode"),
                JoinPayload = NullableText(root, "joinPayload"),
                State = Text(root, "state"),
            };
        }

        internal static string Wire(SessionVisibility visibility) => visibility switch
        {
            SessionVisibility.Public => "public",
            SessionVisibility.Friends => "friends",
            _ => "private",
        };

        // peer-connection rules point 4: 32 bytes from the platform CSPRNG, unpadded base64url.
        // It proves a later session.resume; this demo does not resume yet, but
        // the server requires one.
        private static string NewResumeToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static string Text(IHaivenJsonNode node, string name) =>
            node.TryGetProperty(name, out var value) && value.Kind == HaivenJsonKind.String ? value.GetString() : "";

        internal static string? NullableText(IHaivenJsonNode node, string name) =>
            node.TryGetProperty(name, out var value) && value.Kind == HaivenJsonKind.String ? value.GetString() : null;

        internal static int Number(IHaivenJsonNode node, string name) =>
            node.TryGetProperty(name, out var value) && value.Kind == HaivenJsonKind.Number
                && int.TryParse(value.GetNumberText(), out var number) ? number : 0;
    }
}
