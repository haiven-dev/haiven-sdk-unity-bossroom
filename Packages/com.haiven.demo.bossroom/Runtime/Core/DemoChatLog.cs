#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Haiven.Sdk;
using Haiven.Sdk.Contracts;
using Haiven.Sdk.Rest;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// The chat panel's lines. A message this player sent keeps one line,
    /// keyed by its client message id, that moves through sending, accepted,
    /// held, not shown or refused in place. Channel history is inserted oldest
    /// first and does not repeat a server message id already on screen.
    /// An unlock is its own line, placed by grant time among those messages.
    /// </summary>
    public sealed class DemoChatLog
    {
        public const string ConnectionUnavailable =
            "Haiven is unavailable right now. Try again shortly.";
        public const string EntitlementsUnavailable =
            "Rewards are unavailable right now. Chat is still connected.";
        public const string SignInUnavailable =
            "Haiven sign-in could not be completed. Try again.";
        public const string HistoryUnavailable =
            "Earlier messages are unavailable right now.";

        private enum HistoryPhase
        {
            NotStarted,
            Pending,
            Shown,
            Abandoned,
        }

        private struct PendingUnlock
        {
            public string Code;
            public DateTimeOffset? At;
        }

        private struct StampRow
        {
            public string Text;
            public DateTimeOffset? At;
            public string? ServerId;
        }

        private readonly List<string> lines = new List<string>();
        private readonly List<DateTimeOffset?> stamps = new List<DateTimeOffset?>();
        private readonly Dictionary<string, int> keyed = new Dictionary<string, int>();
        private readonly HashSet<string> serverIds = new HashSet<string>();
        private readonly Dictionary<string, int> plainLines = new Dictionary<string, int>();
        private readonly List<PendingUnlock> pendingUnlocks = new List<PendingUnlock>();
        private readonly HashSet<string> unlockCodes = new HashSet<string>();
        private bool historyPending;
        private int historyAt;
        private HistoryPhase phase;

        public IReadOnlyList<string> Lines => lines;

        public event Action? Changed;

        public void Note(string line)
        {
            AddLine(line, null);
            Changed?.Invoke();
        }

        /// <summary>
        /// Places <c>unlocked: code</c> by <paramref name="validFrom"/>.
        /// That field is haiven-api's min(granted_at). Message rows use createdAt,
        /// which agrees with uuidv7 id order. A grant seen before the history
        /// page is on screen waits and is merged into that page. Without a
        /// timestamp it is appended, not given a guessed slot. The same code
        /// is shown once.
        /// </summary>
        public void ShowUnlock(string code, string? validFrom)
        {
            if (string.IsNullOrEmpty(code) || !unlockCodes.Add(code)) return;
            var at = Instant(validFrom);
            if (phase == HistoryPhase.Shown)
            {
                PlaceUnlock(code, at);
                return;
            }
            if (phase == HistoryPhase.Abandoned)
            {
                AddLine("unlocked: " + code, at);
                Changed?.Invoke();
                return;
            }
            pendingUnlocks.Add(new PendingUnlock { Code = code, At = at });
        }

        public void Upsert(OutgoingChatMessage message)
        {
            var line = Describe(message);
            if (!keyed.TryGetValue(message.ClientMessageId, out var index))
            {
                index = lines.Count;
                keyed[message.ClientMessageId] = index;
                AddLine(line, null);
            }
            else
            {
                lines[index] = line;
            }

            // History has no client message id. Once the reply names the server
            // id, drop a plain copy so the outbox line stays the only one.
            var id = message.MessageId;
            if (!string.IsNullOrEmpty(id))
            {
                if (plainLines.TryGetValue(id, out var plain) && plain != index)
                {
                    RemoveAt(plain);
                    if (plain < index) index--;
                    keyed[message.ClientMessageId] = index;
                }
                serverIds.Add(id);
            }
            Changed?.Invoke();
        }

        /// <summary>Marks where history should land, above lines appended while it loads.</summary>
        public void BeginHistory()
        {
            historyPending = true;
            historyAt = lines.Count;
            phase = HistoryPhase.Pending;
        }

        /// <summary>Drops the history anchor when the read fails.</summary>
        public void AbandonHistory()
        {
            historyPending = false;
            phase = HistoryPhase.Abandoned;
            if (pendingUnlocks.Count == 0) return;
            SortPending();
            foreach (var unlock in pendingUnlocks)
                AddLine("unlocked: " + unlock.Code, unlock.At);
            pendingUnlocks.Clear();
            Changed?.Invoke();
        }

        /// <summary>
        /// Inserts one page. <paramref name="newestFirst"/> is API order; lines
        /// are shown oldest first. An id already shown is skipped, so a live
        /// line that arrived during the read stays below the page.
        /// </summary>
        public void ShowHistory(IReadOnlyList<MessageResponse>? newestFirst, string channelId)
        {
            var index = historyPending ? historyAt : lines.Count;
            historyPending = false;
            phase = HistoryPhase.Shown;

            var rows = new List<StampRow>();
            if (newestFirst != null)
            {
                for (var i = newestFirst.Count - 1; i >= 0; i--)
                {
                    var message = newestFirst[i];
                    var content = message.Content;
                    if (!OnChannel(message.ChannelId, channelId) || string.IsNullOrEmpty(content)) continue;
                    if (!Claim(message.Id)) continue;
                    rows.Add(new StampRow
                    {
                        Text = Render(message.Author != null, message.Author?.DisplayName, message.Author?.Username, content),
                        At = Instant(message.CreatedAt),
                        ServerId = message.Id,
                    });
                }
            }

            if (pendingUnlocks.Count > 0)
            {
                SortPending();
                foreach (var unlock in pendingUnlocks)
                {
                    var at = rows.Count;
                    if (unlock.At is DateTimeOffset stamp)
                    {
                        for (var i = 0; i < rows.Count; i++)
                        {
                            // Same instant: the chat line stays first. The unlock follows it.
                            if (rows[i].At is DateTimeOffset messageAt && messageAt > stamp)
                            {
                                at = i;
                                break;
                            }
                        }
                    }
                    rows.Insert(at, new StampRow
                    {
                        Text = "unlocked: " + unlock.Code,
                        At = unlock.At,
                        ServerId = null,
                    });
                }
                pendingUnlocks.Clear();
            }

            if (rows.Count == 0) return;

            var inserted = new List<KeyValuePair<string, int>>();
            for (var added = 0; added < rows.Count; added++)
            {
                var row = rows[added];
                InsertLine(index + added, row.Text, row.At);
                if (!string.IsNullOrEmpty(row.ServerId))
                    inserted.Add(new KeyValuePair<string, int>(row.ServerId, index + added));
            }
            Shift(keyed, index, rows.Count);
            Shift(plainLines, index, rows.Count);
            foreach (var pair in inserted) plainLines[pair.Key] = pair.Value;
            Changed?.Invoke();
        }

        /// <summary>Adds a channel message, unless it is one of ours already shown by its client id.</summary>
        public void Incoming(ChatMessageV0 message, string channelId)
        {
            if (message.ChannelId != channelId || string.IsNullOrEmpty(message.Content)) return;
            var own = message.ClientMessageId != null && keyed.ContainsKey(message.ClientMessageId);
            if (!Claim(message.Id) || own) return;
            var author = message.Author;
            var index = lines.Count;
            AddLine(Render(author != null, author?.DisplayName, author?.Username, message.Content), Instant(message.CreatedAt));
            if (!string.IsNullOrEmpty(message.Id)) plainLines[message.Id] = index;
            Changed?.Invoke();
        }

        public static string Describe(OutgoingChatMessage message)
        {
            switch (message.State)
            {
                case OutgoingChatState.Sending: return "you (sending): " + message.Content;
                case OutgoingChatState.Unconfirmed: return "you (resending on reconnect): " + message.Content;
                case OutgoingChatState.Accepted: return "you: " + message.Content;
                case OutgoingChatState.Held: return "you (held for review): " + message.Content;
                case OutgoingChatState.Removed: return "you (not shown: " + message.ModerationState + "): " + message.Content;
                case OutgoingChatState.Refused: return Refused(message.Error, message.Content);
                default: return message.Content;
            }
        }

        private static bool OnChannel(string? messageChannelId, string channelId) =>
            string.IsNullOrEmpty(messageChannelId) || messageChannelId == channelId;

        private bool Claim(string? id) => string.IsNullOrEmpty(id) || serverIds.Add(id);

        private static string Render(bool hasAuthor, string? displayName, string? username, string content)
        {
            var name = !hasAuthor ? "someone"
                : !string.IsNullOrEmpty(displayName) ? displayName : username;
            return name + ": " + content;
        }

        private void PlaceUnlock(string code, DateTimeOffset? at)
        {
            var line = "unlocked: " + code;
            if (at is DateTimeOffset stamp)
            {
                var index = IndexAfter(stamp);
                InsertLine(index, line, stamp);
                Shift(keyed, index, 1);
                Shift(plainLines, index, 1);
            }
            else
            {
                AddLine(line, null);
            }
            Changed?.Invoke();
        }

        private int IndexAfter(DateTimeOffset stamp)
        {
            for (var i = 0; i < stamps.Count; i++)
            {
                if (stamps[i] is DateTimeOffset existing && existing > stamp) return i;
            }
            return lines.Count;
        }

        private void SortPending()
        {
            pendingUnlocks.Sort((left, right) =>
            {
                if (left.At is DateTimeOffset leftAt && right.At is DateTimeOffset rightAt)
                    return leftAt.CompareTo(rightAt);
                if (left.At is null && right.At is null) return 0;
                return left.At is null ? 1 : -1;
            });
        }

        private static DateTimeOffset? Instant(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                return parsed;
            return null;
        }

        private void AddLine(string line, DateTimeOffset? at)
        {
            lines.Add(line);
            stamps.Add(at);
        }

        private void InsertLine(int index, string line, DateTimeOffset? at)
        {
            lines.Insert(index, line);
            stamps.Insert(index, at);
        }

        private void RemoveAt(int index)
        {
            lines.RemoveAt(index);
            stamps.RemoveAt(index);
            Drop(keyed, index);
            Drop(plainLines, index);
        }

        private static void Shift(Dictionary<string, int> map, int index, int delta)
        {
            if (delta == 0) return;
            var snapshot = new List<KeyValuePair<string, int>>(map);
            foreach (var pair in snapshot)
            {
                if (pair.Value >= index) map[pair.Key] = pair.Value + delta;
            }
        }

        private static void Drop(Dictionary<string, int> map, int index)
        {
            var snapshot = new List<KeyValuePair<string, int>>(map);
            foreach (var pair in snapshot)
            {
                if (pair.Value == index) map.Remove(pair.Key);
                else if (pair.Value > index) map[pair.Key] = pair.Value - 1;
            }
        }

        private static string Refused(string? error, string content)
        {
            switch (error)
            {
                case "not_channel_member":
                case "not_community_member":
                    return "you (not sent - join this chat room first): " + content;
                case "unauthorized":
                case "invalid_token":
                    return "you (not sent - sign in again): " + content;
                case "content_required":
                case "invalid_payload":
                    return "you (not sent - check the message and try again): " + content;
                default:
                    return "you (not sent - try again shortly): " + content;
            }
        }
    }
}
