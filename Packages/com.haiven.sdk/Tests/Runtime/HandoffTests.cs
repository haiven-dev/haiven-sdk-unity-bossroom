using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Haiven.Sdk.Tests
{
    public class HandoffTests
    {
        [UnityTest] public IEnumerator DeepLinkStoresTokenAndCompletesOnUnityThread()
        {
            var unityThread = Environment.CurrentManagedThreadId;
            var completedThread = -1;
            var tokenStore = new MemoryHandoffTokenStore();
            var coordinator = new HandoffCoordinator(
                new HandoffOptions(
                    new Uri("https://app.haiven.gg/connect/game"),
                    new Uri("https://api.haiven.gg/v0/auth/handoff/exchange"),
                    "unity-test",
                    new Uri("haiven-test://auth/callback")),
                new Browser(),
                new MemoryHandoffTransactionStore(),
                tokenStore,
                new Exchange(),
                new UnityHandoffDispatcher());
            var start = coordinator.BeginLogin();
            coordinator.Completed += _ => completedThread = Environment.CurrentManagedThreadId;

            var host = new GameObject("handoff-test");
            var receiver = host.AddComponent<HandoffDeepLinkReceiver>();
            receiver.Configure(coordinator);
            receiver.Receive("haiven-test://auth/callback?code=one-use&state=" + start.State);

            var deadline = Time.realtimeSinceStartup + 10;
            while (tokenStore.Credential is null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(tokenStore.Credential, Is.Not.Null, "handoff timed out");
            Assert.That(tokenStore.Credential.AccessToken, Is.EqualTo("header.eyJleHAiOjF9.signature"));
            Assert.That(tokenStore.Credential.ExpiresAt, Is.GreaterThan(DateTimeOffset.FromUnixTimeSeconds(1)));
            Assert.That(completedThread, Is.EqualTo(unityThread));
            UnityEngine.Object.Destroy(host);
        }

        [Test] public void CredentialEndReasonsClearAndStartFreshHandoff()
        {
            var browser = new Browser();
            var tokens = new MemoryHandoffTokenStore();
            var coordinator = new HandoffCoordinator(
                new HandoffOptions(
                    new Uri("https://app.haiven.gg/connect/game"),
                    new Uri("https://api.haiven.gg/v0/auth/handoff/exchange"),
                    "unity-test",
                    new Uri("haiven-test://auth/callback")),
                browser,
                new MemoryHandoffTransactionStore(),
                tokens,
                new Exchange(),
                new UnityHandoffDispatcher());

            foreach (HandoffCredentialEndReason reason in Enum.GetValues(typeof(HandoffCredentialEndReason)))
            {
                tokens.Store(new HandoffCredential("rejected", "Bearer", DateTimeOffset.UtcNow.AddMinutes(5)));
                coordinator.RestartLogin(reason);
                Assert.That(tokens.Credential, Is.Null, reason.ToString());
            }
            Assert.That(browser.Opens, Is.EqualTo(3));
        }

        private sealed class Browser : IHandoffBrowser
        {
            public int Opens { get; private set; }
            public void Open(Uri uri) => Opens++;
        }

        private sealed class Exchange : IHandoffHttpTransport
        {
            public async Task<HandoffExchangeResponse> ExchangeAsync(
                Uri exchangeUri,
                HandoffExchangeRequest request,
                CancellationToken cancellationToken)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                return new HandoffExchangeResponse("header.eyJleHAiOjF9.signature", "Bearer", 300);
            }
        }
    }
}
