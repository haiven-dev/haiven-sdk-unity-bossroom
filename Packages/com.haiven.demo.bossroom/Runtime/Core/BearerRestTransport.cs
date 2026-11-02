#nullable enable
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Rest;

namespace Haiven.Demo.BossRoom
{
    /// <summary>
    /// The REST transport the SDK leaves to the game: HttpClient with the
    /// optional player's bearer token and the app id header the API expects.
    /// Public requests use <see cref="Anonymous"/> so an account token cannot be
    /// attached. Desktop and editor only; WebGL uses UnityWebRequest instead.
    /// </summary>
    public sealed class BearerRestTransport : IRestApiTransport, IDisposable
    {
        private readonly HttpClient http;
        private readonly string baseUrl;
        private readonly string appId;
        private readonly Func<string>? token;

        public BearerRestTransport(string baseUrl, string appId, Func<string> token, HttpMessageHandler? handler = null)
            : this(baseUrl, appId, handler, token ?? throw new ArgumentNullException(nameof(token)))
        {
        }

        private BearerRestTransport(
            string baseUrl,
            string appId,
            HttpMessageHandler? handler,
            Func<string>? token)
        {
            this.baseUrl = baseUrl.TrimEnd('/');
            this.appId = appId;
            this.token = token;
            http = handler == null ? new HttpClient() : new HttpClient(handler);
        }

        /// <summary>Creates a transport that can never attach an account credential.</summary>
        public static BearerRestTransport Anonymous(
            string baseUrl,
            string appId,
            HttpMessageHandler? handler = null) => new BearerRestTransport(baseUrl, appId, handler, null);

        public Uri Url(string path) => new Uri(baseUrl + path);

        public async Task<string?> SendAsync(RestRequest request, CancellationToken cancellationToken)
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), Url(request.Path));
            var accessToken = token?.Invoke();
            if (!string.IsNullOrWhiteSpace(accessToken))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            message.Headers.Add("X-Haiven-App-ID", appId);
            message.Headers.Add("X-Haiven-SDK", "unity-bossroom-demo/0.0.0");
            if (request.Body != null)
            {
                message.Content = new StringContent(request.Body);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType ?? "application/json");
            }

            using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Preserve the finite API error code so reward refusals are presentation state,
                // while generated operations continue to decode success bodies only.
                throw new HaivenRestException(
                    (int)response.StatusCode,
                    body,
                    "haiven: " + request.Method + " " + request.Path + " returned " + (int)response.StatusCode);
            }
            return body.Length == 0 ? null : body;
        }

        public void Dispose() => http.Dispose();
    }
}
