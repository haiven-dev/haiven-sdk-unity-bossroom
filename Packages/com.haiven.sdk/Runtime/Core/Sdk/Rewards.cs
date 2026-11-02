// Vendored from sdks/dotnet/Haiven.Sdk/Rewards.cs by scripts/vendor-unity.py.
// Edit or regenerate the source, then run `make vendor-unity`.
#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Json;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk
{
    /// <summary>
    /// A non-2xx REST response, as an <see cref="IRestApiTransport"/> should
    /// report one: the status and the error body's finite <c>code</c>
    /// (<c>ErrorResponse.code</c>). <see cref="RewardClaims"/> classifies a
    /// refusal only from this; any other exception reads as a transport failure.
    /// </summary>
    public sealed class HaivenRestException : Exception
    {
        public HaivenRestException(int statusCode, string? body, string? message = null)
            : base(message ?? "haiven: the request failed with HTTP " + statusCode + ".")
        {
            StatusCode = statusCode;
            Body = body;
            Code = CodeOf(body);
        }

        /// <summary>The HTTP status.</summary>
        public int StatusCode { get; }

        /// <summary>The response body text, or null when there was none.</summary>
        public string? Body { get; }

        /// <summary>The error body's code, verbatim; null when it has none. Branch on this, never on the message.</summary>
        public string? Code { get; }

        private static string? CodeOf(string? body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var root = HaivenJson.Parse(body!);
                return root.Kind == HaivenJsonKind.Object
                    && root.TryGetProperty("code", out var code)
                    && code.Kind == HaivenJsonKind.String
                        ? code.GetString()
                        : null;
            }
            catch (HaivenJsonException)
            {
                return null;
            }
        }
    }

    /// <summary>How one reward claim, grant, or confirmation ended.</summary>
    public enum RewardClaimResult
    {
        /// <summary>
        /// Recorded: a new claim, or the claim already recorded under the same
        /// claim id (the service answers a repeat with the recorded claim).
        /// </summary>
        Claimed,

        /// <summary>The account holds no active Haiven subscription (<c>reward_not_eligible</c>).</summary>
        NotEligible,

        /// <summary>This player already claimed this reward in this period (<c>reward_already_claimed</c>).</summary>
        AlreadyClaimed,

        /// <summary>The claim id was used for a different claim (<c>reward_claim_conflict</c>).</summary>
        ClaimConflict,

        /// <summary>The period is being refunded, so it takes no new claims (<c>refund_in_progress</c>).</summary>
        RefundInProgress,

        /// <summary>The claim to confirm does not exist, or belongs to another integration.</summary>
        NotFound,

        /// <summary>Anything else: transport failure, server error, bad request, unreadable reply.</summary>
        Failed,
    }

    /// <summary>Who vouched for a claim, from <c>RewardClaimResponse.confirmation</c>.</summary>
    public enum RewardConfirmation
    {
        /// <summary>Only the player's client vouched for it.</summary>
        ClientReported,

        /// <summary>The game's server confirmed it, or granted it.</summary>
        ServerConfirmed,

        /// <summary>Absent, or a value minted after this build: never read as confirmed.</summary>
        Unrecognized,
    }

    /// <summary>One finished reward request: its result and, on success, the recorded claim.</summary>
    public sealed class RewardClaimOutcome
    {
        internal RewardClaimOutcome(
            RewardClaimResult result,
            RewardClaimResponse? claim = null,
            int? statusCode = null,
            string? errorCode = null,
            bool isRetryable = false,
            Exception? exception = null)
        {
            Result = result;
            Claim = claim;
            Confirmation = RewardClaims.ConfirmationOf(claim?.Confirmation);
            StatusCode = statusCode;
            ErrorCode = errorCode;
            IsRetryable = isRetryable;
            Exception = exception;
        }

        public RewardClaimResult Result { get; }

        /// <summary>The recorded claim, when <see cref="Result"/> is <see cref="RewardClaimResult.Claimed"/>.</summary>
        public RewardClaimResponse? Claim { get; }

        public RewardConfirmation Confirmation { get; }

        /// <summary>The HTTP status of a refusal; null on success or a transport failure.</summary>
        public int? StatusCode { get; }

        /// <summary>The refusal's finite error code, verbatim; null when none.</summary>
        public string? ErrorCode { get; }

        /// <summary>
        /// Repeating the same request may succeed. A retry through
        /// <see cref="RewardClaims.ClaimAsync"/> reuses the claim id, so it cannot claim twice.
        /// </summary>
        public bool IsRetryable { get; }

        /// <summary>What failed, for a log; never branch on its message.</summary>
        public Exception? Exception { get; }

        public bool Succeeded => Result == RewardClaimResult.Claimed;
    }

    /// <summary>
    /// Subscriber-reward claims over the generated <see cref="RestApi"/>.
    ///
    /// A player claims a reward with <c>POST /users/me/rewards/claims</c> under a
    /// claim id the client mints (a UUIDv7). The service records one claim per
    /// player, game and period and answers a repeat of the same claim id with the
    /// recorded claim, so a retry is safe only when it reuses the id. This keeps
    /// the id of every claim that has not had a final answer, keyed by
    /// integration and reward, and hands it to the next attempt; a final answer
    /// (recorded, or refused) releases it.
    ///
    /// Refusals are classified from <see cref="HaivenRestException"/>, which the
    /// transport should throw for a non-2xx response; any other exception is a
    /// retryable failure. Nothing here throws for a network or service failure:
    /// the outcome says what happened. Cancelling throws
    /// <see cref="OperationCanceledException"/> and keeps the claim id pending,
    /// because the claim may still have been recorded. Safe to use from any thread.
    /// </summary>
    public sealed class RewardClaims
    {
        private readonly RestApi api;
        private readonly Func<string> newClaimId;
        private readonly Dictionary<(string IntegrationId, string RewardRef), string> pending =
            new Dictionary<(string, string), string>();
        private readonly object gate = new object();
        private long sessionGeneration;

        /// <param name="api">A REST API whose transport sends the player's bearer token.</param>
        /// <param name="newClaimId">Mints claim ids; defaults to <see cref="NewUuidV7"/>.</param>
        public RewardClaims(RestApi api, Func<string>? newClaimId = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.newClaimId = newClaimId ?? NewUuidV7;
        }

        /// <summary>Raised, on the completing thread, for every recorded claim.</summary>
        public event Action<RewardClaimOutcome>? Claimed;

        /// <summary>Whether the signed-in account may receive subscriber rewards.</summary>
        public Task<RewardEligibilityResponse?> GetEligibilityAsync(CancellationToken cancellationToken = default) =>
            api.GetUsersMeRewardsEligibilityAsync(cancellationToken);

        /// <summary>
        /// Claims one reward for the signed-in player, as <c>client_reported</c>,
        /// reusing the claim id of an earlier attempt that had no final answer.
        /// </summary>
        public async Task<RewardClaimOutcome> ClaimAsync(
            string integrationId,
            string rewardRef,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(integrationId)) throw new ArgumentException("An integration id is required.", nameof(integrationId));
            if (string.IsNullOrEmpty(rewardRef) || UnicodeCodePointCount(rewardRef) > 128)
                throw new ArgumentException("A reward ref must be 1 to 128 characters.", nameof(rewardRef));

            var key = (integrationId, rewardRef);
            string claimId;
            long requestSessionGeneration;
            lock (gate)
            {
                requestSessionGeneration = sessionGeneration;
                if (!pending.TryGetValue(key, out claimId!))
                {
                    claimId = newClaimId();
                    pending[key] = claimId;
                }
            }

            var request = new ClaimRewardRequest { ClaimId = claimId, IntegrationId = integrationId, RewardRef = rewardRef };
            var outcome = await SendAsync(() => api.PostUsersMeRewardsClaimsAsync(request, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            lock (gate)
            {
                if (requestSessionGeneration != sessionGeneration)
                    throw new OperationCanceledException("The reward claim was invalidated because the player signed out.");
                // Only the attempt that owns the id releases it.
                if (!outcome.IsRetryable && pending.TryGetValue(key, out var current) && current == claimId) pending.Remove(key);
                if (outcome.Succeeded && Claimed is not null)
                {
                    // Keep Clear mutually exclusive with delivery, but check after
                    // every re-entrant handler: one handler may sign out player A
                    // and start player B before the next handler runs.
                    foreach (Action<RewardClaimOutcome> handler in Claimed.GetInvocationList())
                    {
                        handler(outcome);
                        if (requestSessionGeneration != sessionGeneration)
                            throw new OperationCanceledException("The reward claim was invalidated because the player signed out.");
                    }
                }
            }
            return outcome;
        }

        /// <summary>The claim id the next claim of this reward will reuse, or null when it mints a new one.</summary>
        public string? PendingClaimId(string integrationId, string rewardRef)
        {
            lock (gate) return pending.TryGetValue((integrationId, rewardRef), out var id) ? id : null;
        }

        /// <summary>
        /// Forgets every pending claim id because the player signed out. An outstanding
        /// <see cref="ClaimAsync"/> then throws <see cref="OperationCanceledException"/> instead
        /// of reporting its result to the next signed-in player's session.
        /// </summary>
        public void Clear()
        {
            lock (gate)
            {
                pending.Clear();
                sessionGeneration++;
            }
        }

        private static int UnicodeCodePointCount(string value)
        {
            var count = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
                count++;
            }
            return count;
        }

        /// <summary>
        /// A game server's grant of one reward to a player
        /// (<c>POST /integrations/rewards/claims</c>), recorded as
        /// <c>server_confirmed</c>. <paramref name="serverApi"/>'s transport must
        /// send the integration key, which never ships in a game client. Keep
        /// <paramref name="claimId"/> across your own retries.
        /// </summary>
        public static Task<RewardClaimOutcome> GrantAsync(
            RestApi serverApi,
            string userId,
            string rewardRef,
            string claimId,
            CancellationToken cancellationToken = default)
        {
            if (serverApi is null) throw new ArgumentNullException(nameof(serverApi));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentException("A user id is required.", nameof(userId));
            if (string.IsNullOrEmpty(rewardRef)) throw new ArgumentException("A reward ref is required.", nameof(rewardRef));
            if (string.IsNullOrEmpty(claimId)) throw new ArgumentException("A claim id is required.", nameof(claimId));
            var request = new ServerClaimRewardRequest { ClaimId = claimId, RewardRef = rewardRef, UserId = userId };
            return SendAsync(() => serverApi.PostIntegrationsRewardsClaimsAsync(request, cancellationToken), cancellationToken);
        }

        /// <summary>
        /// A game server's confirmation of a claim the player's client made
        /// (<c>POST /integrations/rewards/claims/{claim_id}/confirm</c>), with the
        /// integration key. Confirming twice returns the confirmed claim.
        /// </summary>
        public static Task<RewardClaimOutcome> ConfirmAsync(
            RestApi serverApi,
            string claimId,
            CancellationToken cancellationToken = default)
        {
            if (serverApi is null) throw new ArgumentNullException(nameof(serverApi));
            if (string.IsNullOrEmpty(claimId)) throw new ArgumentException("A claim id is required.", nameof(claimId));
            return SendAsync(() => serverApi.PostIntegrationsRewardsClaimsClaimIdConfirmAsync(claimId, cancellationToken), cancellationToken);
        }

        /// <summary>A UUIDv7 (RFC 9562): 48 bits of Unix milliseconds, the version, 74 random bits, the variant.</summary>
        public static string NewUuidV7()
        {
            var random = new byte[10];
            using (var generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
            return FormatUuidV7(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), random);
        }

        internal static string FormatUuidV7(long unixMilliseconds, byte[] random)
        {
            var bytes = new byte[16];
            for (var index = 0; index < 6; index++) bytes[index] = (byte)((unixMilliseconds >> (8 * (5 - index))) & 0xFF);
            bytes[6] = (byte)(0x70 | (random[0] & 0x0F));
            bytes[7] = random[1];
            bytes[8] = (byte)(0x80 | (random[2] & 0x3F));
            Array.Copy(random, 3, bytes, 9, 7);
            var text = new StringBuilder(36);
            for (var index = 0; index < 16; index++)
            {
                if (index == 4 || index == 6 || index == 8 || index == 10) text.Append('-');
                text.Append(bytes[index].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        internal static RewardConfirmation ConfirmationOf(string? confirmation)
        {
            switch (confirmation)
            {
                case "client_reported": return RewardConfirmation.ClientReported;
                case "server_confirmed": return RewardConfirmation.ServerConfirmed;
                default: return RewardConfirmation.Unrecognized;
            }
        }

        /// <summary>Any reward failure, by status and the error body's finite code.</summary>
        internal static RewardClaimOutcome Classify(Exception error)
        {
            if (!(error is HaivenRestException rest))
                return new RewardClaimOutcome(RewardClaimResult.Failed, isRetryable: true, exception: error);

            var status = rest.StatusCode;
            var code = rest.Code;
            RewardClaimResult result;
            if (status == 403 && code == "reward_not_eligible") result = RewardClaimResult.NotEligible;
            else if (status == 409 && code == "reward_already_claimed") result = RewardClaimResult.AlreadyClaimed;
            else if (status == 409 && code == "reward_claim_conflict") result = RewardClaimResult.ClaimConflict;
            else if (status == 409 && code == "refund_in_progress") result = RewardClaimResult.RefundInProgress;
            else if (status == 404) result = RewardClaimResult.NotFound;
            else
            {
                var retryable = status == 429 || status >= 500;
                return new RewardClaimOutcome(RewardClaimResult.Failed, statusCode: status, errorCode: code, isRetryable: retryable, exception: rest);
            }
            return new RewardClaimOutcome(result, statusCode: status, errorCode: code, exception: rest);
        }

        private static async Task<RewardClaimOutcome> SendAsync(
            Func<Task<RewardClaimResponse?>> send,
            CancellationToken cancellationToken)
        {
            RewardClaimResponse? claim;
            try
            {
                claim = await send().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                return Classify(error);
            }
            // A 2xx that is not a claim may still have been recorded: retry under the same id.
            if (claim is null || string.IsNullOrEmpty(claim.ClaimId))
                return new RewardClaimOutcome(
                    RewardClaimResult.Failed,
                    isRetryable: true,
                    exception: new InvalidOperationException("haiven: the reward reply carried no claim."));
            return new RewardClaimOutcome(RewardClaimResult.Claimed, claim);
        }
    }
}
