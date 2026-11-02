using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Rest;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Haiven.Sdk.Tests
{
    public class IdentityModalTests
    {
        [UnityTest]
        public IEnumerator ModalUsesFacadeAndExplicitBrowserBoundaries()
        {
            var transport = new Transport();
            var linkBrowser = new LinkBrowser();
            var externalBrowser = new ExternalBrowser();
            using var identity = new HaivenIdentity(
                new RestApi(transport), "unity game", linkBrowser);
            var host = new GameObject("identity-modal-test");
            try
            {
                var modal = host.AddComponent<UnityIdentityModal>();
                modal.Configure(identity, false, externalBrowser);

                transport.Next(
                    "{\"id\":\"00000000-0000-4000-8000-000000000001\","
                    + "\"title\":\"Update\",\"body\":\"New season\","
                    + "\"url\":\"https://news.example.test/season\","
                    + "\"startsAt\":\"2026-10-14T00:00:00Z\"}");
                var publicRefresh = modal.RefreshAsync();
                yield return AwaitSuccess(publicRefresh);
                Assert.That(modal.Motd?.Body, Is.EqualTo("New season"));
                Assert.That(transport.Sent[0].Path, Is.EqualTo("/motd?integration_id=unity%20game"));
                modal.OpenMotdLink();
                Assert.That(externalBrowser.Opened?.AbsoluteUri,
                    Is.EqualTo("https://news.example.test/season"));

                modal.SetAuthenticated(true);
                transport.Next(
                    "{\"hasPassword\":true,\"identities\":[{\"provider\":\"google\","
                    + "\"email\":\"player@example.test\"}]}");
                var accountRefresh = modal.RefreshAsync();
                yield return AwaitSuccess(accountRefresh);
                Assert.That(modal.SignInMethods?.Identities.Count, Is.EqualTo(1));
                Assert.That(modal.CanBeginLink, Is.True);

                var sentBeforeLink = transport.Sent.Count;
                var link = modal.BeginLinkAsync("apple");
                yield return AwaitSuccess(link);
                var launch = link.Result;
                Assert.That(launch.Succeeded, Is.True);
                Assert.That(modal.StatusCode, Is.EqualTo("link_opened_waiting_for_realtime"));
                Assert.That(linkBrowser.Steps,
                    Is.EqualTo(new[] { "popup", "same_browser_post", "same_popup_redirect" }));
                Assert.That(transport.Sent.Count, Is.EqualTo(sentBeforeLink),
                    "link initiation must not use the game REST transport");

                var router = new WsEventRouter();
                identity.Attach(router);
                router.Dispatch(ServerFrame.Parse(
                    "{\"type\":\"account.identity.linked\",\"payload\":{"
                    + "\"provider\":\"apple\",\"email\":\"apple@example.test\"}}"));
                Assert.That(modal.StatusCode, Is.EqualTo("identity_linked"));
                Assert.That(modal.SignInMethods?.Identities.Count, Is.EqualTo(2));

                transport.Next(null);
                var unlink = modal.UnlinkAsync("apple");
                yield return AwaitSuccess(unlink);
                Assert.That(transport.Sent[transport.Sent.Count - 1].Path,
                    Is.EqualTo("/users/me/sign-in-methods/sso/apple"));
                Assert.That(modal.SignInMethods?.Identities.Count, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [UnityTest]
        public IEnumerator ModalDoesNotClaimLinkingWithoutBrowserContext()
        {
            var transport = new Transport();
            using var identity = new HaivenIdentity(new RestApi(transport), "unity-game");
            var host = new GameObject("identity-modal-no-browser-test");
            try
            {
                var modal = host.AddComponent<UnityIdentityModal>();
                modal.Configure(identity, true);
                Assert.That(modal.CanBeginLink, Is.False);
                var operation = modal.BeginLinkAsync("google");
                yield return AwaitCompletion(operation);
                var error = operation.Exception?.GetBaseException() as HaivenIdentityException;
                Assert.That(error, Is.Not.Null);
                Assert.That(error.Code, Is.EqualTo("browser_context_required"));
                Assert.That(transport.Sent, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [UnityTest]
        public IEnumerator ThrowingBusySubscriberDoesNotWedgeLaterOperations()
        {
            var transport = new Transport();
            using var identity = new HaivenIdentity(new RestApi(transport), "unity-game");
            var host = new GameObject("identity-modal-throwing-subscriber-test");
            try
            {
                var modal = host.AddComponent<UnityIdentityModal>();
                modal.Configure(identity, false);
                Action throwing = () =>
                {
                    if (modal.IsBusy) throw new InvalidOperationException("subscriber failed");
                };
                modal.StateChanged += throwing;

                var failed = modal.RefreshAsync();
                yield return AwaitCompletion(failed);
                Assert.That(failed.Exception?.GetBaseException().Message,
                    Is.EqualTo("subscriber failed"));
                Assert.That(modal.IsBusy, Is.False,
                    "a throwing busy notification must release the operation slot");
                Assert.That(transport.Sent, Is.Empty,
                    "the operation must not start when its busy notification fails");

                modal.StateChanged -= throwing;
                transport.Next(
                    "{\"id\":\"00000000-0000-4000-8000-000000000002\","
                    + "\"title\":\"Update\",\"body\":\"Recovered\","
                    + "\"startsAt\":\"2026-10-14T00:00:00Z\"}");
                var recovered = modal.RefreshAsync();
                yield return AwaitSuccess(recovered);
                Assert.That(modal.Motd?.Body, Is.EqualTo("Recovered"));
                Assert.That(modal.StatusCode, Is.EqualTo("ready"));
                Assert.That(modal.IsBusy, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RestTransportRequiresSecureRemoteOrigins()
        {
            Assert.Throws<ArgumentException>(() =>
                new UnityRestApiTransport(new Uri("http://api.example.test/v0/")));
            var loopback = new UnityRestApiTransport(new Uri("http://127.0.0.1:18765/v0/"));
            Assert.That(loopback.Url("/motd?integration_id=game").AbsoluteUri,
                Is.EqualTo("http://127.0.0.1:18765/v0/motd?integration_id=game"));
            Assert.Throws<ArgumentException>(() => loopback.Url("https://attacker.example/motd"));
            Assert.Throws<ArgumentException>(() => loopback.Url("/https://attacker.example/motd"));
        }

        [UnityTest]
        public IEnumerator SignOutCancelsInFlightAccountLink()
        {
            var browser = new DelayedBrowser();
            using var identity = new HaivenIdentity(
                new RestApi(new Transport()), "unity-game", browser);
            var host = new GameObject("identity-modal-cancellation-test");
            try
            {
                var modal = host.AddComponent<UnityIdentityModal>();
                modal.Configure(identity, true);
                var operation = modal.BeginLinkAsync("google");
                Assert.That(browser.Started, Is.True);

                modal.SetAuthenticated(false);
                yield return AwaitCompletion(operation);

                Assert.That(browser.Cancelled, Is.True);
                Assert.That(operation.IsCanceled
                    || operation.Exception?.GetBaseException() is OperationCanceledException,
                    Is.True);
                Assert.That(modal.Authenticated, Is.False);
                Assert.That(modal.SignInMethods, Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [UnityTest]
        public IEnumerator RealtimeOutcomeWinsOverBrowserLaunchCompletion()
        {
            var router = new WsEventRouter();
            var browser = new EventBeforeCompletionBrowser(router);
            using var identity = new HaivenIdentity(
                new RestApi(new Transport()), "unity-game", browser);
            identity.Attach(router);
            var host = new GameObject("identity-modal-outcome-order-test");
            try
            {
                var modal = host.AddComponent<UnityIdentityModal>();
                modal.Configure(identity, true);
                var operation = modal.BeginLinkAsync("google");
                Assert.That(modal.StatusCode, Is.EqualTo("identity_linked"));

                browser.Complete();
                yield return AwaitSuccess(operation);

                Assert.That(modal.StatusCode, Is.EqualTo("identity_linked"),
                    "browser launch completion must not overwrite the authoritative event");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void QueuedOldAccountEventIsDroppedAfterSignOut()
        {
            var router = new WsEventRouter();
            using var identity = new HaivenIdentity(
                new RestApi(new Transport()), "unity-game", new LinkBrowser());
            identity.Attach(router);
            var host = new GameObject("identity-modal-queued-retirement-test");
            var queued = new QueuedSynchronizationContext();
            var previous = SynchronizationContext.Current;
            var modal = host.AddComponent<UnityIdentityModal>();
            try
            {
                SynchronizationContext.SetSynchronizationContext(queued);
                modal.Configure(identity, true);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            try
            {
                var staleStatusObserved = false;
                modal.StateChanged += () =>
                    staleStatusObserved |= modal.StatusCode == "identity_linked";
                var dispatch = Task.Run(() => router.Dispatch(ServerFrame.Parse(
                    "{\"type\":\"account.identity.linked\",\"payload\":{"
                    + "\"provider\":\"google\",\"email\":\"retired@example.test\"}}")));
                Assert.That(dispatch.Wait(TimeSpan.FromSeconds(10)), Is.True);
                Assert.That(dispatch.IsFaulted, Is.False, dispatch.Exception?.ToString());
                Assert.That(queued.Count, Is.EqualTo(1));

                modal.SetAuthenticated(false);
                modal.SetAuthenticated(true);
                queued.Drain();

                Assert.That(modal.StatusCode, Is.Null);
                Assert.That(staleStatusObserved, Is.False,
                    "the queued old-account notification must not reach the next account");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [UnityTest]
        public IEnumerator RestTransportExercisesLoopbackHttpSemantics()
        {
            using var server = new LoopbackHttpServer();
            var bearer = "first-token";
            var transport = new UnityRestApiTransport(server.BaseUri, () => bearer);

            var first = transport.SendAsync(
                new RestRequest("GET", "/first", null, null), CancellationToken.None);
            yield return AwaitSuccess(first);
            Assert.That(first.Result, Is.Null, "204 must be represented as no value");

            bearer = "second-token";
            var second = transport.SendAsync(
                new RestRequest("GET", "/second", null, null), CancellationToken.None);
            yield return AwaitSuccess(second);
            Assert.That(second.Result, Is.Null);
            Assert.That(server.AuthorizationFor("/first"), Is.EqualTo("Bearer first-token"));
            Assert.That(server.AuthorizationFor("/second"), Is.EqualTo("Bearer second-token"));

            var redirect = transport.SendAsync(
                new RestRequest("GET", "/redirect", null, null), CancellationToken.None);
            yield return AwaitCompletion(redirect);
            Assert.That(redirect.IsFaulted, Is.True,
                "Unity must report redirect-limit refusal as a failed request");
            Assert.That(server.Seen("/credential-sink"), Is.False,
                "redirects must not forward the bearer to a second request");

            var failure = transport.SendAsync(
                new RestRequest("GET", "/failure", null, null), CancellationToken.None);
            yield return AwaitCompletion(failure);
            var restError = failure.Exception?.GetBaseException() as HaivenRestException;
            Assert.That(restError?.StatusCode, Is.EqualTo(429));
            Assert.That(restError?.Body, Is.EqualTo("{\"code\":\"rate_limited\"}"));
            Assert.That(restError?.Code, Is.EqualTo("rate_limited"));

            using var cancellation = new CancellationTokenSource();
            var cancelled = transport.SendAsync(
                new RestRequest("GET", "/slow", null, null), cancellation.Token);
            yield return AwaitSuccess(server.SlowSeen);
            cancellation.Cancel();
            yield return AwaitCompletion(cancelled);
            Assert.That(cancelled.IsCanceled
                || cancelled.Exception?.GetBaseException() is OperationCanceledException,
                Is.True);
        }

        [UnityTest]
        public IEnumerator ModalMarshalsRealtimeStateToUnityThread()
        {
            var unityThread = System.Environment.CurrentManagedThreadId;
            var callbackThread = -1;
            var transport = new Transport();
            var identity = new HaivenIdentity(
                new RestApi(transport), "unity-game", new LinkBrowser());
            var router = new WsEventRouter();
            identity.Attach(router);
            var host = new GameObject("identity-modal-thread-test");
            var modal = host.AddComponent<UnityIdentityModal>();
            modal.Configure(identity, true);
            modal.StateChanged += () =>
            {
                if (modal.StatusCode == "identity_linked")
                callbackThread = System.Environment.CurrentManagedThreadId;
            };

            var dispatch = Task.Run(() => router.Dispatch(ServerFrame.Parse(
                "{\"type\":\"account.identity.linked\",\"payload\":{"
                + "\"provider\":\"google\",\"email\":\"thread@example.test\"}}")));
            var deadline = Time.realtimeSinceStartup + 10;
            while (callbackThread < 0 && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.That(dispatch.IsCompleted && !dispatch.IsFaulted && !dispatch.IsCanceled, Is.True);
            Assert.That(callbackThread, Is.EqualTo(unityThread));
            UnityEngine.Object.DestroyImmediate(host);
            identity.Dispose();
        }

        private sealed class LinkBrowser : IIdentityLinkBrowserContext
        {
            public List<string> Steps { get; } = new List<string>();

            public Task<IdentityLinkLaunch> BeginLinkAsync(
                string provider,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Steps.Add("popup");
                Steps.Add("same_browser_post");
                Steps.Add("same_popup_redirect");
                return Task.FromResult(IdentityLinkLaunch.Opened());
            }
        }

        private sealed class DelayedBrowser : IIdentityLinkBrowserContext
        {
            public bool Started { get; private set; }
            public bool Cancelled { get; private set; }

            public async Task<IdentityLinkLaunch> BeginLinkAsync(
                string provider,
                CancellationToken cancellationToken)
            {
                Started = true;
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
                throw new InvalidOperationException("The cancellation test unexpectedly resumed.");
            }
        }

        private sealed class EventBeforeCompletionBrowser : IIdentityLinkBrowserContext
        {
            private readonly WsEventRouter router;
            private readonly TaskCompletionSource<IdentityLinkLaunch> completion =
                new TaskCompletionSource<IdentityLinkLaunch>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public EventBeforeCompletionBrowser(WsEventRouter router) => this.router = router;

            public Task<IdentityLinkLaunch> BeginLinkAsync(
                string provider,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                router.Dispatch(ServerFrame.Parse(
                    "{\"type\":\"account.identity.linked\",\"payload\":{"
                    + "\"provider\":\"google\",\"email\":\"order@example.test\"}}"));
                return completion.Task;
            }

            public void Complete() => completion.SetResult(IdentityLinkLaunch.Opened());
        }

        private sealed class ExternalBrowser : IUnityExternalLinkBrowser
        {
            public Uri Opened { get; private set; }
            public void Open(Uri uri) => Opened = uri;
        }

        private sealed class QueuedSynchronizationContext : SynchronizationContext
        {
            private readonly Queue<KeyValuePair<SendOrPostCallback, object>> callbacks =
                new Queue<KeyValuePair<SendOrPostCallback, object>>();

            public int Count
            {
                get { lock (callbacks) return callbacks.Count; }
            }

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (callbacks)
                    callbacks.Enqueue(
                        new KeyValuePair<SendOrPostCallback, object>(callback, state));
            }

            public void Drain()
            {
                while (true)
                {
                    KeyValuePair<SendOrPostCallback, object> item;
                    lock (callbacks)
                    {
                        if (callbacks.Count == 0) return;
                        item = callbacks.Dequeue();
                    }
                    item.Key(item.Value);
                }
            }
        }

        private sealed class LoopbackHttpServer : IDisposable
        {
            private readonly TcpListener listener;
            private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
            private readonly List<RequestObservation> requests = new List<RequestObservation>();
            private readonly TaskCompletionSource<bool> slowSeen =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Task run;

            public LoopbackHttpServer()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                BaseUri = new Uri("http://127.0.0.1:" + port + "/");
                run = Task.Run(RunAsync);
            }

            public Uri BaseUri { get; }
            public Task SlowSeen => slowSeen.Task;

            public bool Seen(string path)
            {
                lock (requests) return requests.Exists(value => value.Path == path);
            }

            public string AuthorizationFor(string path)
            {
                lock (requests)
                    return requests.Find(value => value.Path == path)?.Authorization;
            }

            private async Task RunAsync()
            {
                while (!lifetime.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (ObjectDisposedException) { return; }
                    catch (SocketException) when (lifetime.IsCancellationRequested) { return; }
                    await HandleAsync(client);
                }
            }

            private async Task HandleAsync(TcpClient client)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[1024];
                    var request = new StringBuilder();
                    while (request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                    {
                        var count = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (count == 0) return;
                        request.Append(Encoding.ASCII.GetString(buffer, 0, count));
                        if (request.Length > 32768) throw new InvalidOperationException("HTTP header too large.");
                    }

                    var lines = request.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var target = lines[0].Split(' ')[1];
                    string authorization = null;
                    foreach (var line in lines)
                        if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                            authorization = line.Substring(line.IndexOf(':') + 1).Trim();
                    lock (requests) requests.Add(new RequestObservation(target, authorization));

                    if (target == "/slow")
                    {
                        slowSeen.TrySetResult(true);
                        try { await Task.Delay(Timeout.Infinite, lifetime.Token); }
                        catch (OperationCanceledException) { }
                        return;
                    }

                    string response;
                    if (target == "/redirect")
                    {
                        response = "HTTP/1.1 302 Found\r\nLocation: "
                            + new Uri(BaseUri, "credential-sink").AbsoluteUri
                            + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    }
                    else if (target == "/failure")
                    {
                        const string body = "{\"code\":\"rate_limited\"}";
                        response = "HTTP/1.1 429 Too Many Requests\r\nContent-Type: application/json\r\n"
                            + "Content-Length: " + Encoding.UTF8.GetByteCount(body)
                            + "\r\nConnection: close\r\n\r\n" + body;
                    }
                    else
                    {
                        response = "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n"
                            + "Connection: close\r\n\r\n";
                    }
                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                }
            }

            public void Dispose()
            {
                lifetime.Cancel();
                listener.Stop();
                try { run.Wait(TimeSpan.FromSeconds(2)); }
                catch (AggregateException) { }
                lifetime.Dispose();
            }

            private sealed class RequestObservation
            {
                public RequestObservation(string path, string authorization)
                {
                    Path = path;
                    Authorization = authorization;
                }

                public string Path { get; }
                public string Authorization { get; }
            }
        }

        private sealed class Transport : IRestApiTransport
        {
            private readonly Queue<string> replies = new Queue<string>();
            public List<RestRequest> Sent { get; } = new List<RestRequest>();

            public void Next(string reply) => replies.Enqueue(reply);

            public Task<string> SendAsync(RestRequest request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Sent.Add(request);
                return Task.FromResult(replies.Dequeue());
            }

            public Uri Url(string path) => new Uri("https://api.example.test" + path);
        }

        private static IEnumerator AwaitSuccess(Task task)
        {
            yield return AwaitCompletion(task);
            Assert.That(task.IsCanceled, Is.False, "task was cancelled");
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
        }

        private static IEnumerator AwaitCompletion(Task task)
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "task did not settle within ten seconds");
        }
    }
}
