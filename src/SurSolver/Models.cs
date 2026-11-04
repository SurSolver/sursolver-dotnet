using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;

namespace SurSolver
{
    /// <summary>One <c>getTaskResult</c> answer.</summary>
    public sealed class TaskResult
    {
        /// <summary><see cref="Status"/> while the task runs.</summary>
        public const string Processing = "processing";

        /// <summary><see cref="Status"/> once <see cref="Solution"/> is available.</summary>
        public const string Ready = "ready";

        /// <summary>Creates a result.</summary>
        public TaskResult(string status, JsonObject? solution)
        {
            Status = status;
            Solution = solution;
        }

        /// <summary><see cref="Processing"/> or <see cref="Ready"/>.</summary>
        public string Status { get; }

        /// <summary>
        /// The solution when ready. Keys differ per type (<c>token</c>, <c>gRecaptchaResponse</c>,
        /// <c>cf_clearance</c>, <c>cookies</c>, ...) and are passed through untouched. Tokens are single-use.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonObject? Solution { get; }

        /// <summary>True when <see cref="Status"/> is <see cref="Ready"/>.</summary>
        [JsonIgnore]
        public bool IsReady => Status == Ready;
    }

    /// <summary>What <c>SolveAsync</c> returns.</summary>
    public sealed class SolveResult
    {
        /// <summary>Creates a result.</summary>
        public SolveResult(string taskId, JsonObject solution)
        {
            TaskId = taskId;
            Solution = solution;
        }

        /// <summary>The solved task.</summary>
        public string TaskId { get; }

        /// <summary>The solution, passed through untouched. Tokens and cookies are single-use.</summary>
        public JsonObject Solution { get; }
    }

    /// <summary>Per-call options of <c>SolveAsync</c>.</summary>
    public sealed class SolveOptions
    {
        /// <summary>Overrides the client's poll interval for this call.</summary>
        public TimeSpan? PollInterval { get; set; }

        /// <summary>Overrides the client's deadline for this call.</summary>
        public TimeSpan? SolveTimeout { get; set; }

        /// <summary>
        /// Called once with the task id as soon as the task is accepted (and charged), so the id
        /// survives a cancellation or a <see cref="SolveTimeoutException"/>.
        /// </summary>
        public Action<string>? OnTaskCreated { get; set; }
    }

    /// <summary>One entry of <c>GET /v1/task-types</c>.</summary>
    public sealed class TaskTypeInfo
    {
        internal TaskTypeInfo(
            string? pageSlug, string displayName, string taskType, string? taskTypeProxyless,
            IReadOnlyList<string> aliases, string proxy, IReadOnlyList<IReadOnlyList<string>> required,
            IReadOnlyList<IReadOnlyList<string>> anyOf, IReadOnlyList<string> optional, IReadOnlyList<string> solutionKeys)
        {
            PageSlug = pageSlug;
            DisplayName = displayName;
            TaskType = taskType;
            TaskTypeProxyless = taskTypeProxyless;
            Aliases = aliases;
            Proxy = proxy;
            Required = required;
            AnyOf = anyOf;
            Optional = optional;
            SolutionKeys = solutionKeys;
        }

        /// <summary>The solver page slug at <c>https://sursolver.com/solvers/&lt;slug&gt;</c>, when the type is active.</summary>
        public string? PageSlug { get; }

        /// <summary>Human-readable name.</summary>
        public string DisplayName { get; }

        /// <summary>Canonical <c>task.type</c>.</summary>
        public string TaskType { get; }

        /// <summary>The ProxyLess twin, when there is one.</summary>
        public string? TaskTypeProxyless { get; }

        /// <summary>Other accepted spellings.</summary>
        public IReadOnlyList<string> Aliases { get; }

        /// <summary><c>none</c>, <c>optional</c> or <c>required</c>.</summary>
        public string Proxy { get; }

        /// <summary>Groups of accepted spellings; supply one name per group.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Required { get; }

        /// <summary>Alternatives; supply every field of at least one.</summary>
        public IReadOnlyList<IReadOnlyList<string>> AnyOf { get; }

        /// <summary>Optional fields.</summary>
        public IReadOnlyList<string> Optional { get; }

        /// <summary>Keys of the solution object.</summary>
        public IReadOnlyList<string> SolutionKeys { get; }
    }

    /// <summary>The public task-type catalog.</summary>
    public sealed class TaskTypeCatalog
    {
        internal TaskTypeCatalog(IReadOnlyList<TaskTypeInfo> taskTypes, IReadOnlyList<string> errorCodes)
        {
            TaskTypes = taskTypes;
            ErrorCodes = errorCodes;
        }

        /// <summary>Every task type.</summary>
        public IReadOnlyList<TaskTypeInfo> TaskTypes { get; }

        /// <summary>Every <c>errorCode</c> the solver endpoints send.</summary>
        public IReadOnlyList<string> ErrorCodes { get; }
    }

    /// <summary>One entry of <c>GET /v1/services</c>.</summary>
    public sealed class ServiceInfo
    {
        internal ServiceInfo(long id, string slug, string displayName, string pricePerRequest, string status, long? avgLatencyMs)
        {
            Id = id;
            Slug = slug;
            DisplayName = displayName;
            PricePerRequest = pricePerRequest;
            Status = status;
            AvgLatencyMs = avgLatencyMs;
        }

        /// <summary>Service id.</summary>
        public long Id { get; }

        /// <summary>Service slug.</summary>
        public string Slug { get; }

        /// <summary>Human-readable name.</summary>
        public string DisplayName { get; }

        /// <summary>USD price per task as a decimal string, e.g. <c>"0.0006"</c>.</summary>
        public string PricePerRequest { get; }

        /// <summary>Service status.</summary>
        public string Status { get; }

        /// <summary>Recent average latency in milliseconds, when known.</summary>
        public long? AvgLatencyMs { get; }
    }
}
