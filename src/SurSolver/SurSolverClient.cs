using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SurSolver
{
    /// <summary>
    /// Client of the SurSolver captcha-solving API. Thread-safe; create one and reuse it.
    /// The API key travels only in request bodies and never appears in exceptions or <see cref="ToString"/>.
    /// </summary>
    [DebuggerDisplay("{ToString(),nq}")]
    public sealed class SurSolverClient
    {
        /// <summary>The production API origin.</summary>
        public const string DefaultBaseUrl = "https://api.sursolver.com";

        /// <summary>Upper bound of the random delay added to a submit retry.</summary>
        public static readonly TimeSpan SubmitRetryJitter = TimeSpan.FromSeconds(1);

        /// <summary>This package's version, sent in the <c>User-Agent</c> header.</summary>
        public static string Version { get; } =
            typeof(SurSolverClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        private static readonly HashSet<string> LoopbackHosts = new HashSet<string> { "localhost", "127.0.0.1", "[::1]" };
        private static readonly object RandomLock = new object();
        private static readonly System.Random SharedRandom = new System.Random();

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly TimeSpan _requestTimeout;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _solveTimeout;
        private readonly int _maxSubmitRetries;
        private readonly string? _appId;
        private readonly string _userAgent;
        private readonly ITransport _transport;
        private readonly IClock _clock;
        private readonly Func<double> _random;

        /// <summary>Builds a client.</summary>
        /// <exception cref="ConfigurationException">An option is unusable; nothing was sent.</exception>
        public SurSolverClient(string apiKey, SurSolverClientOptions? options = null)
        {
            options ??= new SurSolverClientOptions();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ConfigurationException("apiKey must be a non-empty string");
            }
            if (apiKey.Trim() != apiKey)
            {
                throw new ConfigurationException("apiKey has leading or trailing whitespace");
            }
            _baseUrl = NormalizeBaseUrl(options.BaseUrl);
            RequirePositive("RequestTimeout", options.RequestTimeout);
            RequirePositive("PollInterval", options.PollInterval);
            RequirePositive("SolveTimeout", options.SolveTimeout);
            if (options.MaxSubmitRetries < 0)
            {
                throw new ConfigurationException("MaxSubmitRetries must not be negative");
            }
            var userAgent = "sursolver-dotnet/" + Version;
            if (options.UserAgentSuffix is not null)
            {
                if (options.UserAgentSuffix.Length == 0 || options.UserAgentSuffix.Any(c => c < 0x20 || c > 0x7e))
                {
                    throw new ConfigurationException("UserAgentSuffix must be non-empty printable ASCII");
                }
                userAgent += " " + options.UserAgentSuffix;
            }

            _apiKey = apiKey;
            _requestTimeout = options.RequestTimeout;
            _pollInterval = options.PollInterval;
            _solveTimeout = options.SolveTimeout;
            _maxSubmitRetries = options.MaxSubmitRetries;
            _appId = string.IsNullOrWhiteSpace(options.AppId) ? null : options.AppId!.Trim();
            _userAgent = userAgent;
            _transport = options.Transport ?? HttpTransport.Shared;
            _clock = options.Clock ?? SystemClock.Instance;
            _random = options.Random ?? NextRandom;
        }

        /// <summary>Never reveals the API key.</summary>
        public override string ToString() => $"SurSolverClient {{ BaseUrl = {_baseUrl} }}";

        /// <summary>
        /// Submits a task and returns its id. The task is charged from this point. It is never retried:
        /// after a <see cref="TransportException"/> the task may exist (check <see cref="GetBalanceAsync"/>
        /// before submitting again).
        /// </summary>
        /// <param name="task">The task object: <c>type</c> plus that type's fields, sent verbatim. Browser
        /// types require <c>userAgent</c>.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public async Task<string> CreateTaskAsync(JsonObject task, CancellationToken cancellationToken = default)
        {
            if (task is null) throw new ArgumentNullException(nameof(task));
            var body = await SolverCallAsync(SurSolverOperation.CreateTask, "/createTask", writer =>
            {
                writer.WritePropertyName("task");
                task.WriteTo(writer);
                if (_appId is not null) writer.WriteString("appId", _appId);
            }, null, cancellationToken).ConfigureAwait(false);
            if (body["taskId"] is JsonValue id && id.GetValueKind() == JsonValueKind.String && id.GetValue<string>().Length > 0)
            {
                return id.GetValue<string>();
            }
            throw new ProtocolException(SurSolverOperation.CreateTask, "errorId 0 without a taskId");
        }

        /// <summary>
        /// Reads a task's state once. A terminal failure is a <see cref="TaskFailedException"/>.
        /// </summary>
        public async Task<TaskResult> GetTaskResultAsync(string taskId, CancellationToken cancellationToken = default)
        {
            if (taskId is null) throw new ArgumentNullException(nameof(taskId));
            var body = await SolverCallAsync(SurSolverOperation.GetTaskResult, "/getTaskResult",
                writer => writer.WriteString("taskId", taskId), taskId, cancellationToken).ConfigureAwait(false);
            var status = body["status"];
            var statusText = status is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
            if (statusText == TaskResult.Processing)
            {
                return new TaskResult(TaskResult.Processing, null);
            }
            if (statusText == TaskResult.Ready)
            {
                if (body["solution"] is JsonObject solution)
                {
                    body.Remove("solution");
                    return new TaskResult(TaskResult.Ready, solution);
                }
                throw new ProtocolException(SurSolverOperation.GetTaskResult, "status ready without a solution object");
            }
            var shown = status is null ? "null" : status.ToJsonString();
            throw new ProtocolException(SurSolverOperation.GetTaskResult, "unknown status " + shown);
        }

        /// <summary>
        /// The cash balance in USD as the JSON number the API sends. Display it; do not do money
        /// arithmetic on it (the dashboard's <c>GET /v1/balance</c> has the exact decimal).
        /// </summary>
        public async Task<double> GetBalanceAsync(CancellationToken cancellationToken = default)
        {
            var body = await SolverCallAsync(SurSolverOperation.GetBalance, "/getBalance", _ => { }, null, cancellationToken)
                .ConfigureAwait(false);
            if (body["balance"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
                && value.TryGetValue(out double balance) && !double.IsInfinity(balance) && !double.IsNaN(balance))
            {
                return balance;
            }
            throw new ProtocolException(SurSolverOperation.GetBalance, "errorId 0 without a numeric balance");
        }

        /// <summary>
        /// Creates a task and polls it until it is ready, failed, or the deadline passes.
        /// </summary>
        /// <remarks>
        /// <c>createTask</c> is resubmitted only after <c>ERROR_NO_SLOT_AVAILABLE</c> (nothing was created), at
        /// most <see cref="SurSolverClientOptions.MaxSubmitRetries"/> times; any other submit failure is thrown at
        /// once. Polls that fail with a <see cref="TransportException"/> or <c>ERROR_INTERNAL_ERROR</c> are retried
        /// until the deadline; any other error ends the call and carries the task id. On the deadline a
        /// <see cref="SolveTimeoutException"/> is thrown; the task may still finish. Cancellation throws
        /// <see cref="OperationCanceledException"/>.
        /// </remarks>
        public async Task<SolveResult> SolveAsync(JsonObject task, SolveOptions? options = null, CancellationToken cancellationToken = default)
        {
            var pollInterval = options?.PollInterval ?? _pollInterval;
            var solveTimeout = options?.SolveTimeout ?? _solveTimeout;
            RequirePositive("PollInterval", pollInterval);
            RequirePositive("SolveTimeout", solveTimeout);
            cancellationToken.ThrowIfCancellationRequested();
            var deadline = _clock.UtcNow + solveTimeout;

            string taskId;
            for (var retries = 0; ; retries++)
            {
                try
                {
                    taskId = await CreateTaskAsync(task, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (NoSlotAvailableException) when (retries < _maxSubmitRetries)
                {
                    var jitter = TimeSpan.FromMilliseconds(Math.Floor(_random() * SubmitRetryJitter.TotalMilliseconds));
                    await _clock.DelayAsync(pollInterval + jitter, cancellationToken).ConfigureAwait(false);
                }
            }
            options?.OnTaskCreated?.Invoke(taskId);

            while (true)
            {
                SurSolverException? lastError = null;
                try
                {
                    var result = await GetTaskResultAsync(taskId, cancellationToken).ConfigureAwait(false);
                    if (result.IsReady)
                    {
                        return new SolveResult(taskId, result.Solution!);
                    }
                }
                catch (TransportException error)
                {
                    lastError = error;
                }
                catch (ApiException error) when (error.Code == ErrorCodes.InternalError)
                {
                    lastError = error;
                }
                await _clock.DelayAsync(pollInterval, cancellationToken).ConfigureAwait(false);
                if (_clock.UtcNow >= deadline)
                {
                    throw new SolveTimeoutException(taskId, solveTimeout, lastError);
                }
            }
        }

        /// <summary>The public task-type catalog: accepted names, fields, proxy modes and result keys.</summary>
        public async Task<TaskTypeCatalog> ListTaskTypesAsync(CancellationToken cancellationToken = default)
        {
            const SurSolverOperation operation = SurSolverOperation.ListTaskTypes;
            var data = await CatalogCallAsync(operation, "/v1/task-types", cancellationToken).ConfigureAwait(false);
            if (data["task_types"] is not JsonArray taskTypes || data["error_codes"] is not JsonArray errorCodes)
            {
                throw new ProtocolException(operation, "data without task_types and error_codes arrays");
            }
            var read = new EntryReader(operation);
            var codes = read.Texts(errorCodes, "error_codes");
            var entries = taskTypes.Select(raw =>
            {
                var entry = read.Object(raw);
                return new TaskTypeInfo(
                    read.OptionalText(entry, "page_slug"),
                    read.Text(entry, "display_name"),
                    read.Text(entry, "task_type"),
                    read.OptionalText(entry, "task_type_proxyless"),
                    read.Texts(entry["aliases"], "aliases"),
                    read.Text(entry, "proxy"),
                    read.Groups(entry, "required"),
                    read.Groups(entry, "any_of"),
                    read.Texts(entry["optional"], "optional"),
                    read.Texts(entry["solution_keys"], "solution_keys"));
            }).ToList();
            return new TaskTypeCatalog(entries, codes);
        }

        /// <summary>Active services with their current USD price per task (decimal strings).</summary>
        public async Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(CancellationToken cancellationToken = default)
        {
            const SurSolverOperation operation = SurSolverOperation.ListServices;
            var data = await CatalogCallAsync(operation, "/v1/services", cancellationToken).ConfigureAwait(false);
            if (data["services"] is not JsonArray services)
            {
                throw new ProtocolException(operation, "data without a services array");
            }
            var read = new EntryReader(operation);
            return services.Select(raw =>
            {
                var entry = read.Object(raw);
                return new ServiceInfo(
                    read.Integer(entry, "id"),
                    read.Text(entry, "slug"),
                    read.Text(entry, "display_name"),
                    read.Text(entry, "price_per_request"),
                    read.Text(entry, "status"),
                    entry.TryGetPropertyValue("avg_latency_ms", out var latency) && latency is null
                        ? null
                        : read.Integer(entry, "avg_latency_ms"));
            }).ToList();
        }

        private static void RequirePositive(string name, TimeSpan value)
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ConfigurationException(name + " must be positive");
            }
        }

        private static string NormalizeBaseUrl(string? raw)
        {
            if (raw is null || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                throw new ConfigurationException("BaseUrl is not a valid absolute URL");
            }
            var loopbackHttp = uri.Scheme == "http" && LoopbackHosts.Contains(uri.Host.ToLowerInvariant());
            if (uri.Scheme != "https" && !loopbackHttp)
            {
                throw new ConfigurationException("BaseUrl must use https:// (plain http:// only for localhost)");
            }
            if (uri.UserInfo.Length > 0 || raw.Contains("?") || raw.Contains("#"))
            {
                throw new ConfigurationException("BaseUrl must not carry credentials, a query or a fragment");
            }
            return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
        }

        private static double NextRandom()
        {
            lock (RandomLock)
            {
                return SharedRandom.NextDouble();
            }
        }

        private TransportRequest Request(string method, string path, byte[]? body)
        {
            var headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["User-Agent"] = _userAgent,
            };
            if (body is not null) headers["Content-Type"] = "application/json";
            return new TransportRequest(method, _baseUrl + path, headers, body, _requestTimeout);
        }

        private async Task<TransportResponse> SendAsync(SurSolverOperation operation, TransportRequest request, CancellationToken cancellationToken)
        {
            TransportResponse response;
            try
            {
                response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not SurSolverException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reason = error is TimeoutException || error is OperationCanceledException
                    ? $"request timed out after {request.Timeout.TotalMilliseconds:0} ms"
                    : "connection failed";
                throw new TransportException(operation, reason, inner: error);
            }
            if (response.Status == 200)
            {
                return response;
            }
            string? serverCode = null;
            int? retryAfter = null;
            if (TryParse(response.Body, out var parsed) && parsed is JsonObject envelope && envelope["error"] is JsonObject detail)
            {
                if (detail["code"] is JsonValue code && code.GetValueKind() == JsonValueKind.String)
                {
                    serverCode = code.GetValue<string>();
                }
                if (detail["retry_after_seconds"] is JsonValue retry && retry.GetValueKind() == JsonValueKind.Number
                    && retry.TryGetValue(out double seconds))
                {
                    retryAfter = (int)seconds;
                }
            }
            var reasonText = serverCode is null ? $"HTTP {response.Status}" : $"HTTP {response.Status} ({serverCode})";
            throw new TransportException(operation, reasonText, response.Status, serverCode, retryAfter);
        }

        /// <summary>POSTs to a solver endpoint; returns the errorId 0 body or throws the classified error.</summary>
        private async Task<JsonObject> SolverCallAsync(
            SurSolverOperation operation, string path, Action<Utf8JsonWriter> writeFields, string? taskId,
            CancellationToken cancellationToken)
        {
            byte[] payload;
            using (var buffer = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("clientKey", _apiKey);
                    writeFields(writer);
                    writer.WriteEndObject();
                }
                payload = buffer.ToArray();
            }
            var response = await SendAsync(operation, Request("POST", path, payload), cancellationToken).ConfigureAwait(false);

            var isJson = TryParse(response.Body, out var parsed);
            var errorIdNode = (parsed as JsonObject)?["errorId"] as JsonValue;
            if (parsed is not JsonObject body || errorIdNode is null || errorIdNode.GetValueKind() != JsonValueKind.Number
                || !errorIdNode.TryGetValue(out double errorId) || errorId != Math.Floor(errorId))
            {
                var what = isJson ? "not the solver envelope" : "not JSON";
                throw new TransportException(operation, "HTTP 200 body is " + what, 200);
            }
            if (errorId == 0)
            {
                return body;
            }
            if (errorId != 1)
            {
                throw new ProtocolException(operation, $"errorId {errorId}");
            }
            if (body["errorCode"] is not JsonValue code || code.GetValueKind() != JsonValueKind.String)
            {
                throw new ProtocolException(operation, "errorId 1 without a string errorCode");
            }
            var description = body["errorDescription"] is JsonValue text && text.GetValueKind() == JsonValueKind.String
                ? text.GetValue<string>()
                : "";
            var supported = body["supportedUserAgents"] is JsonArray list
                ? list.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>()).ToList()
                : new List<string>();
            throw ApiException.Create(operation, code.GetValue<string>(), description, taskId, supported);
        }

        /// <summary>GETs a public catalog endpoint and returns its <c>data</c> object.</summary>
        private async Task<JsonObject> CatalogCallAsync(SurSolverOperation operation, string path, CancellationToken cancellationToken)
        {
            var response = await SendAsync(operation, Request("GET", path, null), cancellationToken).ConfigureAwait(false);
            if (TryParse(response.Body, out var parsed) && parsed is JsonObject envelope
                && envelope["success"] is JsonValue success && success.GetValueKind() == JsonValueKind.True
                && envelope["data"] is JsonObject data)
            {
                return data;
            }
            throw new TransportException(operation, "HTTP 200 body is not the API envelope", 200);
        }

        /// <summary>
        /// Parses JSON. Returns false when the body is not JSON (including duplicate top-level keys);
        /// <paramref name="node"/> is null for the JSON literal <c>null</c>.
        /// </summary>
        private static bool TryParse(byte[] body, out JsonNode? node)
        {
            try
            {
                node = JsonNode.Parse(body);
                if (node is JsonObject obj) _ = obj.Count; // materialise now so duplicate keys fail here
                return true;
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException)
            {
                node = null;
                return false;
            }
        }

        /// <summary>Typed reads of catalog entries; a missing or mistyped field is a ProtocolException.</summary>
        private sealed class EntryReader
        {
            private readonly SurSolverOperation _operation;

            public EntryReader(SurSolverOperation operation) => _operation = operation;

            private ProtocolException Fail(string key, string expected) =>
                new ProtocolException(_operation, $"catalog field {key} is not {expected}");

            public JsonObject Object(JsonNode? raw) =>
                raw as JsonObject ?? throw new ProtocolException(_operation, "a catalog entry is not an object");

            public string Text(JsonObject entry, string key) =>
                AsText(entry[key]) ?? throw Fail(key, "a string");

            public string? OptionalText(JsonObject entry, string key)
            {
                var value = entry[key];
                return value is null ? null : AsText(value) ?? throw Fail(key, "a string or null");
            }

            public long Integer(JsonObject entry, string key)
            {
                if (entry[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
                    && value.TryGetValue(out double number) && number == Math.Floor(number)
                    && number >= long.MinValue && number <= long.MaxValue)
                {
                    return (long)number;
                }
                throw Fail(key, "an integer");
            }

            public IReadOnlyList<string> Texts(JsonNode? raw, string key)
            {
                if (raw is JsonArray array)
                {
                    var texts = array.Select(AsText).ToList();
                    if (texts.All(text => text is not null)) return texts!;
                }
                throw Fail(key, "a list of strings");
            }

            public IReadOnlyList<IReadOnlyList<string>> Groups(JsonObject entry, string key)
            {
                if (entry[key] is JsonArray array)
                {
                    var groups = array.Select(group => group is JsonArray list ? list.Select(AsText).ToList() : null).ToList();
                    if (groups.All(group => group is not null && group.All(text => text is not null)))
                    {
                        return groups.Select(group => (IReadOnlyList<string>)group!.Select(text => text!).ToList()).ToList();
                    }
                }
                throw Fail(key, "a list of string lists");
            }

            private static string? AsText(JsonNode? node) =>
                node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        }
    }
}
