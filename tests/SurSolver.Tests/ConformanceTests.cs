// Runs the vendored language-neutral conformance scenarios (conformance/README.md) with a
// scripted fake transport, a virtual clock and a fixed random source.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SurSolver.Tests;

public sealed class ConformanceTests
{
    internal const string TestApiKey = "sur_00000000000000000000000000000000";

    private static readonly string Root = FindRoot();

    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static IEnumerable<object[]> Scenarios() =>
        Directory.GetFiles(Path.Combine(Root, "conformance", "scenarios"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new object[] { name! });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario(string name)
    {
        var sc = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "conformance", "scenarios", name + ".json")))!.AsObject();
        var script = sc["script"]?.AsArray() ?? new JsonArray();
        var call = sc["call"]!.AsObject();
        var expect = sc["expect"]!.AsObject();
        var args = call["args"] as JsonObject ?? new JsonObject();

        using var cancellation = new CancellationTokenSource();
        var clock = new VirtualClock((int?)call["cancelAfterSleeps"] ?? 0, cancellation);
        var transport = new ScriptedTransport(script);
        var random = (double?)sc["random"] ?? 0;
        var options = new SurSolverClientOptions { Transport = transport, Clock = clock, Random = () => random };
        var apiKey = TestApiKey;
        foreach (var setting in sc["client"] as JsonObject ?? new JsonObject())
        {
            var value = setting.Value!;
            switch (setting.Key)
            {
                case "apiKey": apiKey = (string)value!; break;
                case "baseUrl": options.BaseUrl = (string)value!; break;
                case "requestTimeoutMs": options.RequestTimeout = TimeSpan.FromMilliseconds((double)value); break;
                case "pollIntervalMs": options.PollInterval = TimeSpan.FromMilliseconds((double)value); break;
                case "solveTimeoutMs": options.SolveTimeout = TimeSpan.FromMilliseconds((double)value); break;
                case "maxSubmitRetries": options.MaxSubmitRetries = (int)value; break;
                case "appId": options.AppId = (string)value!; break;
                case "userAgentSuffix": options.UserAgentSuffix = (string)value!; break;
                default: throw new InvalidOperationException("unknown client option " + setting.Key);
            }
        }

        var created = new List<string>();
        object? result = null;
        Exception? error = null;
        try
        {
            var client = new SurSolverClient(apiKey, options);
            var token = cancellation.Token;
            JsonObject Task() => args["task"]!.DeepClone().AsObject();
            switch ((string)call["method"]!)
            {
                case "construct": break;
                case "createTask": result = await client.CreateTaskAsync(Task(), token); break;
                case "getTaskResult": result = await client.GetTaskResultAsync((string)args["taskId"]!, token); break;
                case "getBalance": result = await client.GetBalanceAsync(token); break;
                case "listTaskTypes": result = await client.ListTaskTypesAsync(token); break;
                case "listServices": result = await client.ListServicesAsync(token); break;
                case "solve":
                    var solveOptions = new SolveOptions { OnTaskCreated = created.Add };
                    if (args["pollIntervalMs"] is JsonNode poll) solveOptions.PollInterval = TimeSpan.FromMilliseconds((double)poll);
                    if (args["solveTimeoutMs"] is JsonNode limit) solveOptions.SolveTimeout = TimeSpan.FromMilliseconds((double)limit);
                    result = await client.SolveAsync(Task(), solveOptions, token);
                    break;
                default: throw new InvalidOperationException("unknown call " + call["method"]);
            }
        }
        catch (Exception caught)
        {
            error = caught;
        }

        Assert.True(transport.Failures.Count == 0, "harness: " + string.Join("; ", transport.Failures));
        Assert.True(transport.Requests.Count == script.Count, $"consumed {transport.Requests.Count} of {script.Count} scripted requests");
        if (expect["sleeps"] is JsonArray sleeps)
        {
            Assert.Equal(sleeps.Select(s => (long)s!).ToList(), clock.Sleeps);
        }
        if (expect["lastTaskId"] is JsonNode lastTaskId)
        {
            Assert.Equal((string)lastTaskId!, created.LastOrDefault());
        }

        var outcome = expect["outcome"] as JsonObject;
        if (outcome?["error"] is JsonObject expectedError)
        {
            Assert.True(error is not null, $"expected {expectedError["type"]}, got a result");
            var (type, fields) = ErrorFields(error!);
            Assert.True(type == (string)expectedError["type"]!, $"error {type} ({error}), want {expectedError["type"]}");
            foreach (var field in expectedError)
            {
                if (field.Key is "type" or "messageIncludes" or "messageExcludes") continue;
                Assert.True(JsonEquals(Canon(field.Value), Canon(fields[field.Key])),
                    $"{field.Key} = {fields[field.Key]?.ToJsonString() ?? "null"}, want {field.Value?.ToJsonString() ?? "null"}");
            }
            var rendered = error!.Message + "\n" + error;
            foreach (var text in Texts(expectedError["messageIncludes"]))
            {
                Assert.True(rendered.Contains(text), $"message {rendered} lacks {text}");
            }
            foreach (var text in Texts(expectedError["messageExcludes"]))
            {
                Assert.False(rendered.Contains(text), $"message {rendered} contains {text}");
            }
        }
        else
        {
            Assert.True(error is null, "unexpected error: " + error);
            var wire = Canon(Wire(result));
            if (outcome is not null && outcome.ContainsKey("ok"))
            {
                Assert.True(JsonEquals(Normalize(Canon(outcome["ok"]), false), Normalize(wire, false)),
                    $"result {wire?.ToJsonString()}, want {outcome["ok"]?.ToJsonString()}");
            }
            foreach (var check in (expect["outcomeSubset"] as JsonArray ?? new JsonArray()).Select(c => c!.AsObject()))
            {
                var value = PointerGet(wire, (string)check["pointer"]!);
                if (check["length"] is JsonNode length)
                {
                    Assert.Equal((int)length, value!.AsArray().Count);
                }
                if (check.ContainsKey("equals"))
                {
                    Assert.True(JsonEquals(Normalize(Canon(check["equals"]), false), Normalize(value, false)),
                        $"{check["pointer"]} = {value?.ToJsonString()}, want {check["equals"]?.ToJsonString()}");
                }
            }
        }

        if ((bool?)expect["redaction"] == true)
        {
            foreach (var request in transport.Requests)
            {
                Assert.DoesNotContain(apiKey, request.Url);
                Assert.DoesNotContain(request.Headers, header => header.Value.Contains(apiKey));
                var body = Encoding.UTF8.GetString(request.Body ?? Array.Empty<byte>());
                Assert.Single(Regex.Matches(body, Regex.Escape(apiKey)));
                Assert.Equal(apiKey, (string?)JsonNode.Parse(body)?["clientKey"]);
            }
            if (error is not null)
            {
                Assert.DoesNotContain(apiKey, error.ToString());
            }
        }
    }

    private static string FindRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "conformance", "scenarios"))) return dir;
        }
        throw new InvalidOperationException("no vendored conformance scenarios above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Texts(JsonNode? node) =>
        (node as JsonArray ?? new JsonArray()).Select(text => (string)text!);

    /// <summary>The result in wire spelling (snake_case property names; solutions untouched).</summary>
    private static JsonNode? Wire(object? result) => result switch
    {
        null => null,
        string text => JsonValue.Create(text),
        double number => JsonValue.Create(number),
        _ => JsonSerializer.SerializeToNode(result, result.GetType(), WireOptions),
    };

    /// <summary>A detached copy whose values are all JSON-element backed, so they compare by kind and value.</summary>
    private static JsonNode? Canon(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    /// <summary>The README key rule: lowercase without underscores, except inside a solution.</summary>
    private static JsonNode? Normalize(JsonNode? node, bool insideSolution)
    {
        switch (node)
        {
            case JsonArray array:
                return new JsonArray(array.Select(entry => Normalize(entry, insideSolution)).ToArray());
            case JsonObject obj when !insideSolution:
                var normalized = new JsonObject();
                foreach (var entry in obj)
                {
                    normalized[entry.Key.Replace("_", "").ToLowerInvariant()] = Normalize(entry.Value, entry.Key == "solution");
                }
                return normalized;
            default:
                return node?.DeepClone();
        }
    }

    private static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        switch (a)
        {
            case null:
                return b is null;
            case JsonObject objA:
                return b is JsonObject objB && objA.Count == objB.Count
                    && objA.All(entry => objB.TryGetPropertyValue(entry.Key, out var other) && JsonEquals(entry.Value, other));
            case JsonArray arrayA:
                return b is JsonArray arrayB && arrayA.Count == arrayB.Count
                    && arrayA.Zip(arrayB, JsonEquals).All(equal => equal);
            default:
                if (b is not JsonValue valueB) return false;
                var elementA = a.GetValue<JsonElement>();
                var elementB = valueB.GetValue<JsonElement>();
                if (elementA.ValueKind != elementB.ValueKind) return false;
                return elementA.ValueKind switch
                {
                    JsonValueKind.Number => elementA.GetDouble() == elementB.GetDouble(),
                    JsonValueKind.String => elementA.GetString() == elementB.GetString(),
                    _ => true,
                };
        }
    }

    private static JsonNode? PointerGet(JsonNode? root, string pointer)
    {
        var current = root;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            current = current is JsonArray array ? array[int.Parse(segment)] : current?[segment];
        }
        return current;
    }

    private static string OperationName(SurSolverOperation? operation)
    {
        var name = operation.ToString()!;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>An error's spec type name and fields under the scenario's names.</summary>
    internal static (string Type, JsonObject Fields) ErrorFields(Exception error)
    {
        switch (error)
        {
            case OperationCanceledException:
                return ("Cancelled", new JsonObject());
            case ConfigurationException:
                return ("ConfigurationError", new JsonObject());
            case SolveTimeoutException timeout:
                return ("SolveTimeoutError", new JsonObject
                {
                    ["taskId"] = timeout.TaskId,
                    ["lastErrorType"] = timeout.LastError is null ? null : ErrorFields(timeout.LastError).Type,
                });
            case TransportException transport:
                var fields = new JsonObject
                {
                    ["operation"] = OperationName(transport.Operation),
                    ["mayHaveCreatedTask"] = transport.MayHaveCreatedTask,
                };
                if (transport.Status is int status) fields["status"] = status;
                if (transport.ServerCode is string serverCode) fields["serverCode"] = serverCode;
                if (transport.RetryAfterSeconds is int retryAfter) fields["retryAfterSeconds"] = retryAfter;
                return ("TransportError", fields);
            case ProtocolException protocol:
                return ("ProtocolError", new JsonObject { ["operation"] = OperationName(protocol.Operation) });
            case ApiException api:
                var name = api.GetType().Name;
                return (name.Substring(0, name.Length - "Exception".Length) + "Error", new JsonObject
                {
                    ["operation"] = OperationName(api.Operation),
                    ["code"] = api.Code,
                    ["description"] = api.Description,
                    ["retryable"] = api.Retryable,
                    ["taskId"] = api.TaskId,
                    ["supportedUserAgents"] = new JsonArray(api.SupportedUserAgents.Select(ua => (JsonNode?)ua).ToArray()),
                });
            default:
                return ("unexpected " + error.GetType().Name + ": " + error.Message, new JsonObject());
        }
    }

    private sealed class VirtualClock : IClock
    {
        private readonly int _cancelAt;
        private readonly CancellationTokenSource _cancellation;

        public VirtualClock(int cancelAt, CancellationTokenSource cancellation)
        {
            _cancelAt = cancelAt;
            _cancellation = cancellation;
        }

        public List<long> Sleeps { get; } = new();

        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.FromUnixTimeSeconds(0);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Sleeps.Add((long)delay.TotalMilliseconds);
            UtcNow += delay;
            if (_cancelAt == Sleeps.Count) _cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedTransport : ITransport
    {
        private readonly JsonArray _script;

        public ScriptedTransport(JsonArray script) => _script = script;

        public List<TransportRequest> Requests { get; } = new();

        public List<string> Failures { get; } = new();

        public Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var step = Requests.Count;
            Exception Fail(string message)
            {
                Failures.Add($"request {step}: {message}");
                return new InvalidOperationException("harness mismatch");
            }
            if (step > _script.Count) throw Fail($"unexpected {request.Method} {request.Url}");

            var entry = _script[step - 1]!.AsObject();
            var expected = entry["expect"]!.AsObject();
            var url = new Uri(request.Url);
            if ((string)expected["method"]! != request.Method || (string)expected["path"]! != url.AbsolutePath)
            {
                throw Fail($"expected {expected["method"]} {expected["path"]}, got {request.Method} {url.AbsolutePath}");
            }
            if ((bool?)expected["noQuery"] == true && url.Query.Length > 0) throw Fail("unexpected query");
            if (expected.ContainsKey("json"))
            {
                var body = request.Body is null ? null : JsonNode.Parse(request.Body);
                if (!JsonEquals(Canon(expected["json"]), body))
                {
                    throw Fail($"body {body?.ToJsonString()} != {expected["json"]?.ToJsonString()}");
                }
            }
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value);
            foreach (var header in expected["headers"] as JsonObject ?? new JsonObject())
            {
                var present = headers.TryGetValue(header.Key, out var actual);
                var matches = header.Value is JsonObject pattern
                    ? present && Regex.IsMatch(actual!, (string)pattern["matches"]!)
                    : present && actual == (string?)header.Value;
                if (!matches) throw Fail($"header {header.Key} mismatch: {actual}");
            }

            switch ((string?)entry["fail"])
            {
                case null: break;
                case "request_timeout": throw new TimeoutException("no response within the request timeout");
                case "connection_reset":
                    throw new HttpRequestException("Error while copying content to a stream.",
                        new IOException("Connection reset by peer", new SocketException((int)SocketError.ConnectionReset)));
                default: throw new HttpRequestException("The response ended prematurely.");
            }
            return Task.FromResult(Respond(entry["respond"]!.AsObject()));
        }

        private static TransportResponse Respond(JsonObject respond)
        {
            var status = (int?)respond["status"] ?? 200;
            if (respond["respondFrom"] is JsonNode file)
            {
                var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "conformance", "recorded", (string)file!)))!["response"]!;
                if (!respond.ContainsKey("status")) status = (int)fixture["status"]!;
                var body = (bool)fixture["body_is_json"]! ? fixture["body"]!.ToJsonString() : (string)fixture["body"]!;
                return new TransportResponse(status, Encoding.UTF8.GetBytes(body));
            }
            if (respond.ContainsKey("json"))
            {
                return new TransportResponse(status, Encoding.UTF8.GetBytes(respond["json"]?.ToJsonString() ?? "null"));
            }
            return new TransportResponse(status, Encoding.UTF8.GetBytes((string?)respond["text"] ?? ""));
        }
    }
}
