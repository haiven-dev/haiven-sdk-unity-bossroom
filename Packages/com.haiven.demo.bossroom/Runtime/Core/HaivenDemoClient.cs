#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Json;
using Haiven.Sdk.Rest;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// Everything the Boss Room demo does with Haiven, without Unity: chat on
    /// one channel, and the player's active item entitlements as inventory.
    /// Authenticating loads one page of that channel's history. The socket is
    /// opened by the caller; every change is handed to
    /// <c>post</c> so the game applies it on its own thread.
    /// </summary>
    public sealed class HaivenDemoClient : IDisposable
    {
        private readonly DemoSettings settings;
        private readonly HaivenSessionCore session;
        private readonly HaivenChat chat = new HaivenChat();
        private readonly RestApi api;
        private readonly IRestApiTransport rest;
        private readonly RewardClaims rewards;
        private readonly Action<Action> post;
        private readonly List<string> items = new List<string>();
        private readonly WsEventRouter? motdRouter;
        private readonly Action<ReceivedFrame>? motdEventHandler;
        private readonly object rewardGate = new object();
        private readonly Dictionary<string, string> rewardStatus = new Dictionary<string, string>();
        private readonly HashSet<string> rewardInFlight = new HashSet<string>();
        private readonly HashSet<string> rewardClosed = new HashSet<string>();
        private const long HistoryPageSize = 50;
        private int membershipRecovery;

        /// <summary>The game socket's session, for the lobby's session operations and signaling.</summary>
        internal HaivenSessionCore Session => session;

        /// <summary>The signed-in REST transport.</summary>
        internal IRestApiTransport Rest => rest;

        /// <summary>The server's id for the game socket, from <c>auth.success</c>; null before it.</summary>
        internal string? ConnectionId { get; private set; }

        /// <summary>The socket closed, with the server's close code; raised on the game's thread.</summary>
        internal event Action<int>? SocketClosed;

        public HaivenDemoClient(
            DemoSettings settings,
            IHaivenSocket socket,
            IRestApiTransport rest,
            Func<string> token,
            Action<Action> post,
            DemoMotdController? motd = null)
        {
            this.settings = settings;
            this.post = post;
            this.rest = rest;
            api = new RestApi(rest);
            rewards = new RewardClaims(api);
            session = new HaivenSessionCore(socket, token);
            // The lobby binds sessions to this socket, and the server names it
            // only in auth.success, long before the lobby opens.
            session.FrameReceived += received =>
            {
                if (received.Frame.Type == "auth.success" && received.Frame.Payload != null
                    && received.Frame.Payload.TryGetProperty("connectionId", out var id)
                    && id.Kind == Haiven.Sdk.Json.HaivenJsonKind.String)
                    ConnectionId = id.GetString();
            };
            session.Authenticated += () => post(() =>
            {
                ConnectionStatus = "ONLINE · AUTHENTICATED";
                Log.Note("Haiven chat connected. Press Enter to talk.");
                PresentationChanged?.Invoke();
            });
            session.Authenticated += () => _ = RefreshEntitlementsAsync();
            session.Authenticated += () => _ = RefreshRewardClaimsAsync();
            session.Authenticated += () =>
            {
                post(() => Log.BeginHistory());
                _ = LoadChatHistoryAsync();
            };
            session.AuthenticationFailed += reason => post(() =>
            {
                ConnectionStatus = "AUTH FAILED";
                Log.Note(DemoChatLog.SignInUnavailable);
                PresentationChanged?.Invoke();
            });
            session.Closed += (code, _) => post(() => SocketClosed?.Invoke(code));
            session.Closed += (code, reason) => post(() =>
            {
                ConnectionStatus = "OFFLINE · " + code;
                Log.Note("Haiven chat offline (" + code + ").");
                PresentationChanged?.Invoke();
            });
            session.EventReceived += received =>
            {
                // The push is a refresh hint: it carries a projection version, not the entitlements.
                if (received.Frame.Type == WsTypes.EntitlementsUpdated) _ = RefreshEntitlementsAsync();
            };
            chat.OutgoingChanged += message => post(() =>
            {
                Log.Upsert(message);
                ModerationStatus = ModerationOf(message);
                PresentationChanged?.Invoke();
                if (message.State == OutgoingChatState.Refused
                    && message.Error == "not_channel_member")
                {
                    _ = RecoverMembershipAsync();
                }
            });
            chat.MessageReceived += message => post(() => Log.Incoming(message, settings.ChannelId));
            chat.Attach(session);
            if (motd is not null)
            {
                motdRouter = new WsEventRouter();
                motd.Attach(motdRouter);
                motdEventHandler = received =>
                {
                    try { motdRouter.Dispatch(received.Frame); }
                    catch (Exception) { post(() => Log.Note("Message update could not be read.")); }
                };
                session.EventReceived += motdEventHandler;
            }
        }

        public DemoChatLog Log { get; } = new DemoChatLog();

        /// <summary>Item entitlement codes granted this session, oldest first.</summary>
        public IReadOnlyList<string> Items => items;

        public string ConnectionStatus { get; private set; } = "CONNECTING";

        public string ModerationStatus { get; private set; } = "READY";

        public string RewardStatus => StatusOf(settings.RewardRef);

        /// <summary>The configured reward can be claimed. A finished claim stays disabled.</summary>
        public bool CanClaimReward => CanClaim(settings.RewardRef);

        /// <summary>Configured reward first, then the extra unclaimed demo rewards.</summary>
        public IReadOnlyList<DemoRewardOffer> RewardOffers
        {
            get
            {
                var specs = DemoRewardCatalog.For(settings.RewardRef);
                var offers = new List<DemoRewardOffer>(specs.Count);
                lock (rewardGate)
                {
                    foreach (var spec in specs)
                    {
                        offers.Add(new DemoRewardOffer(
                            spec.RewardRef,
                            spec.Title,
                            StatusOfLocked(spec.RewardRef),
                            CanClaimLocked(spec.RewardRef)));
                    }
                }
                return offers;
            }
        }

        public event Action? ItemsChanged;

        public event Action? PresentationChanged;

        /// <summary>A reward claim was recorded; raised on the game's thread with the reward ref.</summary>
        public event Action<string>? RewardClaimed;

        /// <summary>
        /// An SDK call failed, named by a <see cref="DemoOperations"/> value; raised on
        /// the game's thread. Refusals the demo presents as state are not failures.
        /// </summary>
        public event Action<string, Exception>? OperationFailed;

        /// <summary>Starts authentication; call once the socket is connected.</summary>
        public void Start() => session.Opened();

        public void Send(string content)
        {
            if (content.Trim().Length == 0) return;
            Log.Upsert(chat.Send(settings.ChannelId, content));
        }

        /// <summary>Claims the reward configured for this presenter session.</summary>
        public Task ClaimRewardAsync(CancellationToken cancellationToken = default) =>
            ClaimRewardAsync(settings.RewardRef, cancellationToken);

        /// <summary>
        /// Claims one demo reward. A terminal result (claimed, already claimed,
        /// not eligible, conflict, or a non-retryable failure) disables that
        /// reward until the next session. Retryable failures stay clickable.
        /// </summary>
        public async Task ClaimRewardAsync(string rewardRef, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(rewardRef) || !TryBeginClaim(rewardRef)) return;
            post(() =>
            {
                lock (rewardGate) rewardStatus[rewardRef] = "CLAIMING…";
                PresentationChanged?.Invoke();
            });

            RewardClaimOutcome outcome;
            try
            {
                outcome = await rewards.ClaimAsync(
                    settings.IntegrationId,
                    rewardRef,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (rewardGate) rewardInFlight.Remove(rewardRef);
                throw;
            }

            var status = RewardStatusOf(outcome);
            post(() =>
            {
                lock (rewardGate)
                {
                    rewardInFlight.Remove(rewardRef);
                    rewardStatus[rewardRef] = status;
                    if (ClosesClaim(status)) rewardClosed.Add(rewardRef);
                }
                Log.Note(outcome.Succeeded
                    ? "reward claimed: " + rewardRef
                    : "reward claim: " + status.ToLowerInvariant());
                PresentationChanged?.Invoke();
                if (outcome.Succeeded) RewardClaimed?.Invoke(rewardRef);
                else if (outcome.Result == RewardClaimResult.Failed && outcome.Exception is not null)
                    OperationFailed?.Invoke(DemoOperations.RewardClaim, outcome.Exception);
            });
        }

        /// <summary>
        /// One page of channel history, newest from the server, shown oldest first.
        /// A failure is a log note: authentication itself has already succeeded.
        /// </summary>
        private async Task LoadChatHistoryAsync()
        {
            FetchMessagesResponse? response;
            try
            {
                response = await api.GetChatChannelsChannelIdMessagesAsync(
                    settings.ChannelId,
                    limit: HistoryPageSize).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                post(() =>
                {
                    Log.AbandonHistory();
                    Log.Note(DemoChatLog.HistoryUnavailable);
                    OperationFailed?.Invoke(DemoOperations.ChatHistory, error);
                });
                return;
            }

            var messages = response?.Messages;
            post(() => Log.ShowHistory(messages, settings.ChannelId));
        }

        public async Task RefreshEntitlementsAsync()
        {
            AccountEntitlementsResponse? response;
            try
            {
                response = await api.GetUsersMeEntitlementsAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                post(() =>
                {
                    Log.Note(DemoChatLog.EntitlementsUnavailable);
                    OperationFailed?.Invoke(DemoOperations.EntitlementsRead, error);
                });
                return;
            }

            post(() =>
            {
                var granted = EntitlementGrants.NewlyActiveItems(response, items);
                if (granted.Count == 0) return;
                foreach (var grant in granted)
                {
                    items.Add(grant.Code);
                    Log.ShowUnlock(grant.Code, grant.ValidFrom);
                }
                ItemsChanged?.Invoke();
            });
        }

        /// <summary>Loads reward refs already claimed in the current subscription period.</summary>
        public async Task RefreshRewardClaimsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> claimed;
            try
            {
                var path = "/users/me/rewards/claims?integrationId="
                    + Uri.EscapeDataString(settings.IntegrationId);
                var response = await rest.SendAsync(
                    new RestRequest("GET", path, null, null),
                    cancellationToken).ConfigureAwait(false);
                claimed = ReadRewardRefs(response);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                post(() => OperationFailed?.Invoke(DemoOperations.RewardClaimsRead, error));
                return;
            }

            post(() =>
            {
                lock (rewardGate)
                {
                    foreach (var rewardRef in claimed)
                    {
                        rewardClosed.Add(rewardRef);
                        rewardStatus[rewardRef] = "CLAIMED";
                    }
                }
                PresentationChanged?.Invoke();
            });
        }

        private async Task RecoverMembershipAsync()
        {
            if (Interlocked.CompareExchange(ref membershipRecovery, 1, 0) != 0) return;

            if (settings.CommunityId.Length == 0)
            {
                Interlocked.Exchange(ref membershipRecovery, 2);
                Log.Note("This chat room requires membership. Ask the room owner for access.");
                PresentationChanged?.Invoke();
                return;
            }

            string note;
            var retryable = false;
            Exception? failure = null;
            try
            {
                await api.PostChatCommunitiesCommunityIdJoinAsync(settings.CommunityId)
                    .ConfigureAwait(false);
                note = "Joined this chat room. Send your message again.";
            }
            catch (HaivenRestException error) when (error.StatusCode == 401)
            {
                note = "Sign in again to join this chat room.";
            }
            catch (HaivenRestException error) when (error.StatusCode == 403 || error.StatusCode == 404)
            {
                note = "This chat room requires an invitation. Ask the room owner to invite you.";
            }
            catch (Exception error)
            {
                retryable = true;
                failure = error;
                note = "Could not join this chat room. Send your message again to retry.";
            }

            post(() =>
            {
                Interlocked.Exchange(ref membershipRecovery, retryable ? 0 : 2);
                Log.Note(note);
                PresentationChanged?.Invoke();
                if (failure is not null) OperationFailed?.Invoke(DemoOperations.ChatJoin, failure);
            });
        }

        public void Dispose()
        {
            if (motdEventHandler is not null) session.EventReceived -= motdEventHandler;
            chat.Dispose();
            session.Dispose();
        }

        private static string ModerationOf(OutgoingChatMessage message)
        {
            switch (message.State)
            {
                case OutgoingChatState.Sending: return "CHECKING";
                case OutgoingChatState.Accepted: return "ALLOWED";
                case OutgoingChatState.Held: return "HELD FOR REVIEW";
                case OutgoingChatState.Removed: return "NOT SHOWN";
                case OutgoingChatState.Refused: return "REFUSED";
                case OutgoingChatState.Unconfirmed: return "WAITING FOR CONNECTION";
                default: return "READY";
            }
        }

        private bool TryBeginClaim(string rewardRef)
        {
            lock (rewardGate)
            {
                if (!CanClaimLocked(rewardRef)) return false;
                rewardInFlight.Add(rewardRef);
                return true;
            }
        }

        private string StatusOf(string rewardRef)
        {
            lock (rewardGate) return StatusOfLocked(rewardRef);
        }

        private string StatusOfLocked(string rewardRef) =>
            rewardStatus.TryGetValue(rewardRef, out var status) ? status : "READY TO CLAIM";

        private bool CanClaim(string rewardRef)
        {
            lock (rewardGate) return CanClaimLocked(rewardRef);
        }

        private bool CanClaimLocked(string rewardRef) =>
            !rewardInFlight.Contains(rewardRef) && !rewardClosed.Contains(rewardRef);

        private IReadOnlyList<string> ReadRewardRefs(string? response)
        {
            if (string.IsNullOrEmpty(response)) throw new HaivenJsonException("haiven: reward claim response is empty");
            var root = api.Codec.Parse(response!);
            HaivenJsonRead.ExpectObject(root, "response");
            if (!root.TryGetProperty("rewardRefs", out var refs))
                throw HaivenJsonRead.Missing("response.rewardRefs");
            return HaivenJsonRead.List(refs, "response.rewardRefs", HaivenJsonRead.String);
        }

        /// <summary>Retryable failures stay open. Every other finished status disables the row.</summary>
        private static bool ClosesClaim(string status) => status != "RETRY CLAIM";

        private static string RewardStatusOf(RewardClaimOutcome outcome)
        {
            switch (outcome.Result)
            {
                case RewardClaimResult.Claimed: return "CLAIMED";
                case RewardClaimResult.AlreadyClaimed: return "ALREADY CLAIMED";
                case RewardClaimResult.NotEligible: return "NOT ELIGIBLE";
                case RewardClaimResult.RefundInProgress: return "TEMPORARILY UNAVAILABLE";
                case RewardClaimResult.ClaimConflict: return "CLAIM CONFLICT";
                default: return outcome.IsRetryable ? "RETRY CLAIM" : "CLAIM FAILED";
            }
        }
    }
}
