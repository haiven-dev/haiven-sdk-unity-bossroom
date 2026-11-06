#nullable enable
using System;
using System.Collections;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Haiven.Demo.BossRoom.PlayModeTests
{
    /// <summary>
    /// The demo's sign-in against a real loopback listener, a browser that
    /// follows the redirect and an exchange double. A credential issued without
    /// expires_in stays signed in; one with an expiry lapses once it passes.
    /// </summary>
    public class DemoSignInExpiryTests
    {
        private const float Seconds = 20;

        [UnityTest]
        public IEnumerator ACredentialWithoutAnExpiryStaysSignedIn()
        {
            var clock = new ManualClock();
            var signIn = NewSignIn(clock, expiresInSeconds: null);
            var task = signIn.SignInAsync();
            yield return Wait(task);

            Assert.That(task.Result, Is.True, signIn.Current.Status);
            Assert.That(signIn.Current.State, Is.EqualTo(DemoSignInState.SignedIn));
            clock.UtcNow = clock.UtcNow.AddDays(365);
            Assert.That(signIn.CheckExpiry(), Is.False, "a credential without an expiry was expired");
            Assert.That(signIn.Current.State, Is.EqualTo(DemoSignInState.SignedIn));
            Assert.That(signIn.AccessToken, Is.EqualTo(Exchange.Token));
        }

        [UnityTest]
        public IEnumerator ACredentialWithAnExpiryLapsesOnceItPasses()
        {
            var clock = new ManualClock();
            var signIn = NewSignIn(clock, expiresInSeconds: 60);
            var task = signIn.SignInAsync();
            yield return Wait(task);

            Assert.That(task.Result, Is.True, signIn.Current.Status);
            clock.UtcNow = clock.UtcNow.AddSeconds(59);
            Assert.That(signIn.CheckExpiry(), Is.False);
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            Assert.That(signIn.CheckExpiry(), Is.True);
            Assert.That(signIn.Current.State, Is.EqualTo(DemoSignInState.Expired));
            Assert.That(signIn.AccessToken, Is.Empty);
        }

        private static DemoSignIn NewSignIn(ManualClock clock, long? expiresInSeconds)
        {
            var settings = new DemoSettings(
                "http://127.0.0.1:1/v0",
                "ws://127.0.0.1:1/v0/ws",
                "100000",
                "haiven-sdk-demo",
                "channel",
                "reward",
                signInUrl: "http://127.0.0.1:1/connect/game",
                redirectUri: "http://127.0.0.1:" + FreePort() + "/haiven/callback");
            return new DemoSignIn(settings, null, new CallbackBrowser(), new Exchange(expiresInSeconds), action => action(), clock)
            {
                WaitTimeout = TimeSpan.FromSeconds(Seconds),
            };
        }

        private static IEnumerator Wait(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(Seconds);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "sign-in did not finish");
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed class ManualClock : IHandoffClock
        {
            public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        }

        // Plays the sign-in page: sends the browser straight back to the loopback callback.
        private sealed class CallbackBrowser : IHandoffBrowser
        {
            public void Open(Uri uri)
            {
                string? state = null;
                string? redirect = null;
                foreach (var pair in uri.Query.TrimStart('?').Split('&'))
                {
                    var parts = pair.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    var value = Uri.UnescapeDataString(parts[1]);
                    if (parts[0] == "state") state = value;
                    if (parts[0] == "redirect_uri") redirect = value;
                }
                _ = Task.Run(async () =>
                {
                    using var http = new HttpClient();
                    using var _ = await http.GetAsync(redirect + "?code=one-use&state=" + Uri.EscapeDataString(state ?? ""));
                });
            }
        }

        private sealed class Exchange : IHandoffHttpTransport
        {
            public const string Token = "game-token";
            private readonly long? expiresInSeconds;

            public Exchange(long? expiresInSeconds) => this.expiresInSeconds = expiresInSeconds;

            public Task<HandoffExchangeResponse> ExchangeAsync(Uri exchangeUri, HandoffExchangeRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new HandoffExchangeResponse(Token, "Bearer", expiresInSeconds));
        }
    }
}
