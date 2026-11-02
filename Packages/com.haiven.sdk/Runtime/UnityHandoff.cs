#nullable enable
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Haiven.Sdk
{
    public sealed class UnityHandoffBrowser : IHandoffBrowser
    {
        public void Open(Uri uri) => Application.OpenURL(uri.AbsoluteUri);
    }

    public sealed class UnityHandoffDispatcher : IHandoffDispatcher
    {
        private readonly SynchronizationContext context;

        public UnityHandoffDispatcher()
        {
            context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("Create UnityHandoffDispatcher on Unity's main thread.");
        }

        public Task RunAsync(Action action, CancellationToken cancellationToken)
        {
            if (SynchronizationContext.Current == context)
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>();
            context.Post(_ =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    action();
                    completion.SetResult(true);
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
            }, null);
            return completion.Task;
        }
    }

    public sealed class UnityHandoffHttpTransport : IHandoffHttpTransport
    {
        public async Task<HandoffExchangeResponse> ExchangeAsync(
            Uri exchangeUri,
            HandoffExchangeRequest exchange,
            CancellationToken cancellationToken)
        {
            using (var request = new UnityWebRequest(exchangeUri.AbsoluteUri, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(exchange.ToFormBody()));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
                request.SetRequestHeader("Accept", "application/json");

                var completion = new TaskCompletionSource<bool>();
                var operation = request.SendWebRequest();
                operation.completed += _ => completion.TrySetResult(true);
                if (operation.isDone) completion.TrySetResult(true);
                using (cancellationToken.Register(request.Abort))
                    await completion.Task;
                cancellationToken.ThrowIfCancellationRequested();

                if (request.result != UnityWebRequest.Result.Success)
                    throw new HandoffException(
                        "Handoff exchange failed with HTTP " + request.responseCode + ": " + request.error);

                return HandoffExchangeResponse.ParseJson(request.downloadHandler.text);
            }
        }
    }

    public sealed class HandoffDeepLinkReceiver : MonoBehaviour
    {
        private HandoffCoordinator? coordinator;

        public void Configure(HandoffCoordinator value) =>
            coordinator = value ?? throw new ArgumentNullException(nameof(value));

        public void Receive(string callbackUri)
        {
            if (coordinator is null)
                throw new InvalidOperationException("Configure the handoff coordinator before receiving a callback.");
            if (!Uri.TryCreate(callbackUri, UriKind.Absolute, out var uri))
                throw new HandoffException("The handoff callback URI is invalid.");
            _ = coordinator.HandleCallbackAsync(uri);
        }

        private void OnEnable() => Application.deepLinkActivated += Receive;
        private void OnDisable() => Application.deepLinkActivated -= Receive;
    }
}
