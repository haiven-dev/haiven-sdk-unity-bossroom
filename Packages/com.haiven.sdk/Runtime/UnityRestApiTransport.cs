#nullable enable
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Rest;
using UnityEngine.Networking;

namespace Haiven.Sdk
{
    /// <summary>
    /// UnityWebRequest transport for the generated REST client. The token
    /// callback is read for every request so account replacement cannot leave a
    /// bearer captured in a long-lived client.
    /// </summary>
    public sealed class UnityRestApiTransport : IRestApiTransport
    {
        private readonly Uri baseUri;
        private readonly Func<string?> accessToken;

        public UnityRestApiTransport(Uri baseUri, Func<string?>? accessToken = null)
        {
            if (baseUri is null) throw new ArgumentNullException(nameof(baseUri));
            if (!baseUri.IsAbsoluteUri
                || (baseUri.Scheme != Uri.UriSchemeHttps
                    && !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback))
                || string.IsNullOrEmpty(baseUri.Host)
                || !string.IsNullOrEmpty(baseUri.UserInfo)
                || !string.IsNullOrEmpty(baseUri.Query)
                || !string.IsNullOrEmpty(baseUri.Fragment))
                throw new ArgumentException(
                    "The Haiven REST base URL must be HTTPS (HTTP is allowed only for loopback development).",
                    nameof(baseUri));
            this.baseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
            this.accessToken = accessToken ?? (() => null);
        }

        public Uri Url(string path)
        {
            if (path is null) throw new ArgumentNullException(nameof(path));
            // Unity's Mono runtime classifies a root-relative path such as
            // "/motd" as an absolute file URI. Generated REST paths may carry
            // that leading slash, so normalize it before rejecting an actual
            // absolute URI.
            var relativePath = path.TrimStart('/');
            if (Uri.TryCreate(relativePath, UriKind.Absolute, out _))
                throw new ArgumentException("A generated REST path must be relative.", nameof(path));
            return new Uri(baseUri, relativePath);
        }

        public async Task<string?> SendAsync(
            RestRequest request,
            CancellationToken cancellationToken)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            using (var webRequest = new UnityWebRequest(Url(request.Path).AbsoluteUri, request.Method))
            {
                // Refuse redirects rather than risk forwarding an authenticated
                // request outside the validated Haiven origin.
                webRequest.redirectLimit = 0;
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                if (request.Body is not null)
                {
                    webRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body));
                    webRequest.SetRequestHeader(
                        "Content-Type",
                        request.ContentType ?? "application/octet-stream");
                }
                webRequest.SetRequestHeader("Accept", "application/json");
                var token = accessToken();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    if (token.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                        throw new InvalidOperationException("The bearer token contains a newline.");
                    webRequest.SetRequestHeader("Authorization", "Bearer " + token.Trim());
                }

                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var operation = webRequest.SendWebRequest();
                operation.completed += _ => completion.TrySetResult(true);
                if (operation.isDone) completion.TrySetResult(true);
                using (cancellationToken.Register(webRequest.Abort))
                    await completion.Task;
                cancellationToken.ThrowIfCancellationRequested();

                var body = string.IsNullOrEmpty(webRequest.downloadHandler.text)
                    ? null
                    : webRequest.downloadHandler.text;
                if (webRequest.result == UnityWebRequest.Result.Success)
                    return webRequest.responseCode == 204 ? null : body;
                if (webRequest.responseCode > 0)
                    throw new HaivenRestException(
                        checked((int)webRequest.responseCode), body,
                        "haiven: REST request failed with HTTP " + webRequest.responseCode + ".");
                throw new InvalidOperationException(
                    "haiven: REST transport failed: " + (webRequest.error ?? "unknown error"));
            }
        }
    }
}
