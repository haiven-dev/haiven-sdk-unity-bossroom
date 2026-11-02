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
            Assert.That(tokenStore.Credential.AccessToken, Is.EqualTo("unity-access"));
            Assert.That(completedThread, Is.EqualTo(unityThread));
            UnityEngine.Object.Destroy(host);
        }

        private sealed class Browser : IHandoffBrowser
        {
            public void Open(Uri uri) { }
        }

        private sealed class Exchange : IHandoffHttpTransport
        {
            public async Task<HandoffExchangeResponse> ExchangeAsync(
                Uri exchangeUri,
                HandoffExchangeRequest request,
                CancellationToken cancellationToken)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                return new HandoffExchangeResponse("unity-access", "Bearer", 300);
            }
        }
    }
}
