using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace SurSolver
{
    /// <summary>One HTTP request handed to an <see cref="ITransport"/>.</summary>
    public sealed class TransportRequest
    {
        internal TransportRequest(string method, string url, IReadOnlyDictionary<string, string> headers, byte[]? body, TimeSpan timeout)
        {
            Method = method;
            Url = url;
            Headers = headers;
            Body = body;
            Timeout = timeout;
        }

        /// <summary><c>GET</c> or <c>POST</c>.</summary>
        public string Method { get; }

        /// <summary>Absolute URL.</summary>
        public string Url { get; }

        /// <summary>Request headers (<c>Accept</c>, <c>User-Agent</c>, and <c>Content-Type</c> with a body).</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>UTF-8 JSON body, or <c>null</c>.</summary>
        public byte[]? Body { get; }

        /// <summary>How long the request may take in total.</summary>
        public TimeSpan Timeout { get; }
    }

    /// <summary>What an <see cref="ITransport"/> returns for any HTTP status.</summary>
    public sealed class TransportResponse
    {
        /// <summary>Creates a response.</summary>
        public TransportResponse(int status, byte[] body)
        {
            Status = status;
            Body = body;
        }

        /// <summary>HTTP status.</summary>
        public int Status { get; }

        /// <summary>Raw body.</summary>
        public byte[] Body { get; }
    }

    /// <summary>
    /// Sends one request. Return a <see cref="TransportResponse"/> for any status; throw only when no
    /// response arrived (throw <see cref="TimeoutException"/> when <see cref="TransportRequest.Timeout"/>
    /// passes). Must not follow redirects and must honour the cancellation token.
    /// </summary>
    public interface ITransport
    {
        /// <summary>Sends <paramref name="request"/>.</summary>
        Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The default transport: <see cref="HttpClient"/> over an <see cref="HttpClientHandler"/> that never
    /// follows redirects, so a request body carrying the API key is not replayed to another location.
    /// </summary>
    public sealed class HttpTransport : ITransport
    {
        internal static readonly HttpTransport Shared = new HttpTransport(new HttpClientHandler());

        private readonly HttpClient _client;

        /// <summary>
        /// Sends through <paramref name="handler"/> (configure proxies or TLS on it first). Its
        /// <see cref="HttpClientHandler.AllowAutoRedirect"/> is switched off.
        /// </summary>
        public HttpTransport(HttpClientHandler handler)
        {
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            handler.AllowAutoRedirect = false;
            _client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        }

        /// <inheritdoc />
        public async Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            if (request.Body is not null)
            {
                message.Content = new ByteArrayContent(request.Body);
            }
            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (message.Content is not null) message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
                }
                else
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            try
            {
                // The default completion option buffers the body inside SendAsync, so the timeout covers it.
                using var response = await _client.SendAsync(message, timeout.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return new TransportResponse((int)response.StatusCode, body);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"no response within {request.Timeout.TotalMilliseconds:0} ms");
            }
        }
    }

    /// <summary>Lets tests drive time without waiting.</summary>
    public interface IClock
    {
        /// <summary>The current time.</summary>
        DateTimeOffset UtcNow { get; }

        /// <summary>Waits; throws <see cref="OperationCanceledException"/> when cancelled first.</summary>
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    internal sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }
}
