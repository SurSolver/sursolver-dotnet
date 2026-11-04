// Behaviour the language-neutral scenarios cannot cover: the real HttpClient transport against a
// local server, CancellationToken handling and ToString redaction.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SurSolver.Tests;

public sealed class RuntimeTests
{
    private const string TaskId = "tsk_4f7a9c1e2b8d3a6f5c0e9d21";

    private static JsonObject NewTask() => new()
    {
        ["type"] = TaskTypes.TurnstileTokenProxyLess,
        ["websiteURL"] = "https://example.com/",
        ["websiteKey"] = "k",
        ["userAgent"] = "ua",
    };

    [Fact]
    public async Task KeyTravelsOnlyInTheBodyWithTheSdkUserAgent()
    {
        await using var server = new LocalServer(_ => Task.FromResult<string?>(LocalServer.Json($"{{\"errorId\":0,\"taskId\":\"{TaskId}\"}}")));
        var client = new SurSolverClient(ConformanceTests.TestApiKey, new SurSolverClientOptions { BaseUrl = server.BaseUrl });

        Assert.Equal(TaskId, await client.CreateTaskAsync(NewTask()));

        var request = Assert.Single(server.Requests);
        Assert.Equal("POST /createTask", request.Line);
        Assert.StartsWith("sursolver-dotnet/" + SurSolverClient.Version, request.Headers["user-agent"]);
        Assert.Equal("application/json", request.Headers["content-type"]);
        Assert.DoesNotContain(request.Headers.Values, value => value.Contains(ConformanceTests.TestApiKey));
        Assert.Equal(ConformanceTests.TestApiKey, (string?)JsonNode.Parse(request.Body)!["clientKey"]);
    }

    [Fact]
    public async Task RequestTimeoutIsATransportException()
    {
        await using var server = new LocalServer(_ => Task.FromResult<string?>(null));
        // A real 100 ms wait: the behaviour under test is HttpTransport's own deadline handling.
        var client = new SurSolverClient(ConformanceTests.TestApiKey, new SurSolverClientOptions
        {
            BaseUrl = server.BaseUrl,
            RequestTimeout = TimeSpan.FromMilliseconds(100),
        });

        var error = await Assert.ThrowsAsync<TransportException>(() => client.GetBalanceAsync());

        Assert.Contains("timed out after 100 ms", error.Message);
        Assert.False(error.MayHaveCreatedTask);
        Assert.Null(error.Status);
    }

    [Fact]
    public async Task RedirectIsNotFollowed()
    {
        await using var server = new LocalServer(request => Task.FromResult<string?>(request.Line == "POST /createTask"
            ? "HTTP/1.1 307 Temporary Redirect\r\nLocation: /elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            : LocalServer.Json("{}")));
        var client = new SurSolverClient(ConformanceTests.TestApiKey, new SurSolverClientOptions { BaseUrl = server.BaseUrl });

        var error = await Assert.ThrowsAsync<TransportException>(() => client.CreateTaskAsync(NewTask()));

        Assert.Equal(307, error.Status);
        Assert.True(error.MayHaveCreatedTask);
        Assert.Contains("charged", error.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task CancellationDuringPollingKeepsTheTaskId()
    {
        using var cancellation = new CancellationTokenSource();
        await using var server = new LocalServer(request =>
        {
            if (request.Line == "POST /createTask")
            {
                return Task.FromResult<string?>(LocalServer.Json($"{{\"errorId\":0,\"taskId\":\"{TaskId}\"}}"));
            }
            cancellation.Cancel(); // cancel while the first poll is answered; SolveAsync then waits 10 s or stops
            return Task.FromResult<string?>(LocalServer.Json("{\"errorId\":0,\"status\":\"processing\"}"));
        });
        var client = new SurSolverClient(ConformanceTests.TestApiKey, new SurSolverClientOptions
        {
            BaseUrl = server.BaseUrl,
            PollInterval = TimeSpan.FromSeconds(10),
        });
        var created = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SolveAsync(NewTask(), new SolveOptions { OnTaskCreated = created.Add }, cancellation.Token));

        Assert.Equal(new[] { TaskId }, created);
    }

    [Fact]
    public async Task ApiErrorsAndToStringNeverShowTheKey()
    {
        await using var server = new LocalServer(_ => Task.FromResult<string?>(LocalServer.Json(
            "{\"errorId\":1,\"errorCode\":\"ERROR_KEY_DOES_NOT_EXIST\",\"errorDescription\":\"The clientKey does not match any active API key.\"}")));
        var client = new SurSolverClient(ConformanceTests.TestApiKey, new SurSolverClientOptions { BaseUrl = server.BaseUrl });

        var error = await Assert.ThrowsAsync<AuthenticationException>(() => client.GetBalanceAsync());

        Assert.DoesNotContain(ConformanceTests.TestApiKey, error.ToString());
        Assert.DoesNotContain(ConformanceTests.TestApiKey, client.ToString());
        Assert.Contains(server.BaseUrl, client.ToString());
    }

    private sealed record RecordedRequest(string Line, Dictionary<string, string> Headers, string Body);

    /// <summary>A minimal HTTP/1.1 server on 127.0.0.1; the handler returns a raw response, or null to never answer.</summary>
    private sealed class LocalServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<RecordedRequest, Task<string?>> _handler;
        private readonly Task _loop;

        public LocalServer(Func<RecordedRequest, Task<string?>> handler)
        {
            _handler = handler;
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(AcceptAsync);
        }

        public string BaseUrl { get; }

        public List<RecordedRequest> Requests { get; } = new();

        public static string Json(string body) =>
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient connection;
                try
                {
                    connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(connection));
            }
        }

        private async Task ServeAsync(TcpClient connection)
        {
            using (connection)
            {
                var stream = connection.GetStream();
                var reader = new StreamReader(stream, Encoding.UTF8);
                var requestLine = await reader.ReadLineAsync();
                if (requestLine is null) return;
                var headers = new Dictionary<string, string>();
                for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                {
                    var colon = line.IndexOf(':');
                    headers[line.Substring(0, colon).Trim().ToLowerInvariant()] = line.Substring(colon + 1).Trim();
                }
                var body = new char[headers.TryGetValue("content-length", out var length) ? int.Parse(length) : 0];
                for (var read = 0; read < body.Length;)
                {
                    read += await reader.ReadAsync(body, read, body.Length - read);
                }
                var request = new RecordedRequest(string.Join(" ", requestLine.Split(' ').Take(2)), headers, new string(body));
                lock (Requests) Requests.Add(request);

                var response = await _handler(request);
                if (response is null)
                {
                    try { await Task.Delay(Timeout.Infinite, _stop.Token); } catch (OperationCanceledException) { }
                    return;
                }
                var bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(bytes);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
        }
    }
}
