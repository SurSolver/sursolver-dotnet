using System;
using System.Collections.Generic;

namespace SurSolver
{
    /// <summary>The client call an error belongs to; <c>SolveAsync</c> reports the inner call that failed.</summary>
    public enum SurSolverOperation
    {
        /// <summary><c>createTask</c></summary>
        CreateTask,
        /// <summary><c>getTaskResult</c></summary>
        GetTaskResult,
        /// <summary><c>getBalance</c></summary>
        GetBalance,
        /// <summary><c>GET /v1/task-types</c></summary>
        ListTaskTypes,
        /// <summary><c>GET /v1/services</c></summary>
        ListServices,
    }

    internal static class OperationNames
    {
        public static string Wire(this SurSolverOperation operation) => operation switch
        {
            SurSolverOperation.CreateTask => "createTask",
            SurSolverOperation.GetTaskResult => "getTaskResult",
            SurSolverOperation.GetBalance => "getBalance",
            SurSolverOperation.ListTaskTypes => "listTaskTypes",
            _ => "listServices",
        };
    }

    /// <summary>Base of every error the client raises. Messages never contain the API key.</summary>
    public abstract class SurSolverException : Exception
    {
        private protected SurSolverException(SurSolverOperation? operation, string message, Exception? inner = null)
            : base(message, inner)
        {
            Operation = operation;
        }

        /// <summary>The call that failed; <c>null</c> for <see cref="ConfigurationException"/> and <see cref="SolveTimeoutException"/>.</summary>
        public SurSolverOperation? Operation { get; }
    }

    /// <summary>The client was built with an unusable option. Nothing was sent.</summary>
    public sealed class ConfigurationException : SurSolverException
    {
        /// <summary>Creates the error.</summary>
        public ConfigurationException(string message) : base(null, message) { }
    }

    /// <summary>
    /// No usable answer: a connection failure, a timeout, a non-200 status, or a body that is not the
    /// API's envelope. It says nothing about the task.
    /// </summary>
    public sealed class TransportException : SurSolverException
    {
        /// <summary>Creates the error.</summary>
        public TransportException(
            SurSolverOperation operation,
            string reason,
            int? status = null,
            string? serverCode = null,
            int? retryAfterSeconds = null,
            Exception? inner = null)
            : base(operation, Describe(operation, reason), inner)
        {
            Status = status;
            ServerCode = serverCode;
            RetryAfterSeconds = retryAfterSeconds;
        }

        /// <summary>The HTTP status when a response arrived.</summary>
        public int? Status { get; }

        /// <summary><c>error.code</c> of the API's internal envelope, when present.</summary>
        public string? ServerCode { get; }

        /// <summary><c>error.retry_after_seconds</c> of the internal envelope, when present.</summary>
        public int? RetryAfterSeconds { get; }

        /// <summary>
        /// True for <c>CreateTaskAsync</c>: the request may have been accepted and charged before the
        /// answer was lost. Check <c>GetBalanceAsync</c> before submitting again.
        /// </summary>
        public bool MayHaveCreatedTask => Operation == SurSolverOperation.CreateTask;

        private static string Describe(SurSolverOperation operation, string reason)
        {
            var note = operation == SurSolverOperation.CreateTask
                ? " The task may have been created and charged; check GetBalanceAsync before submitting again."
                : "";
            return $"{operation.Wire()} failed: {reason}.{note}";
        }
    }

    /// <summary>HTTP 200 JSON whose shape the API contract does not allow.</summary>
    public sealed class ProtocolException : SurSolverException
    {
        /// <summary>Creates the error.</summary>
        public ProtocolException(SurSolverOperation operation, string detail)
            : base(operation, $"{operation.Wire()} returned an unexpected response: {detail}") { }
    }

    /// <summary>
    /// <c>SolveAsync</c> reached its deadline. The task may still finish and stays charged; poll it with
    /// <c>GetTaskResultAsync(TaskId)</c> if you still need the result.
    /// </summary>
    public sealed class SolveTimeoutException : SurSolverException
    {
        /// <summary>Creates the error.</summary>
        public SolveTimeoutException(string taskId, TimeSpan timeout, SurSolverException? lastError)
            : base(null, $"task {taskId} was not ready within {timeout.TotalSeconds:0.###} s; it may still finish and stays charged", lastError)
        {
            TaskId = taskId;
            Timeout = timeout;
        }

        /// <summary>The task that was not ready in time.</summary>
        public string TaskId { get; }

        /// <summary>The deadline that passed.</summary>
        public TimeSpan Timeout { get; }

        /// <summary>The final poll's transient failure, when there was one.</summary>
        public SurSolverException? LastError => InnerException as SurSolverException;
    }

    /// <summary>
    /// The API answered <c>errorId: 1</c>. Catch the subclasses for the codes you handle; this class
    /// itself means <c>ERROR_INTERNAL_ERROR</c>, <c>ERROR_FAILED</c> on create, or a code newer than this package.
    /// </summary>
    public class ApiException : SurSolverException
    {
        /// <summary>Creates the error.</summary>
        public ApiException(
            SurSolverOperation operation,
            string code,
            string description,
            string? taskId = null,
            IReadOnlyList<string>? supportedUserAgents = null)
            : base(operation, $"{code}: {description}{(taskId is null ? "" : $" (task {taskId})")}")
        {
            Code = code;
            Description = description;
            TaskId = taskId;
            SupportedUserAgents = supportedUserAgents ?? Array.Empty<string>();
        }

        /// <summary>The <c>errorCode</c> exactly as sent, including codes newer than this package.</summary>
        public string Code { get; }

        /// <summary>The <c>errorDescription</c> as sent.</summary>
        public string Description { get; }

        /// <summary>True only for <c>ERROR_NO_SLOT_AVAILABLE</c>.</summary>
        public bool Retryable => Code == ErrorCodes.NoSlotAvailable;

        /// <summary>Set for <c>GetTaskResultAsync</c> and for <c>SolveAsync</c> after the task was accepted.</summary>
        public string? TaskId { get; }

        /// <summary>The list sent with <c>ERROR_WRONG_USERAGENT</c>; empty otherwise.</summary>
        public IReadOnlyList<string> SupportedUserAgents { get; }

        internal static ApiException Create(
            SurSolverOperation operation, string code, string description, string? taskId, IReadOnlyList<string> supported)
        {
            if (operation == SurSolverOperation.GetTaskResult && TaskFailureCodes.Contains(code))
            {
                return new TaskFailedException(operation, code, description, taskId, supported);
            }
            return code switch
            {
                ErrorCodes.WrongClientKey or ErrorCodes.KeyDoesNotExist =>
                    new AuthenticationException(operation, code, description, taskId, supported),
                ErrorCodes.InvalidRequest or ErrorCodes.TypeNotSupported or ErrorCodes.WrongUseragent =>
                    new InvalidTaskException(operation, code, description, taskId, supported),
                ErrorCodes.ZeroBalance => new InsufficientBalanceException(operation, code, description, taskId, supported),
                ErrorCodes.NoSlotAvailable => new NoSlotAvailableException(operation, code, description, taskId, supported),
                ErrorCodes.TaskNotFound => new TaskNotFoundException(operation, code, description, taskId, supported),
                _ => new ApiException(operation, code, description, taskId, supported),
            };
        }

        private static readonly HashSet<string> TaskFailureCodes = new HashSet<string>
        {
            ErrorCodes.Failed, ErrorCodes.TaskTimeout, ErrorCodes.TaskExpired, ErrorCodes.WrongUseragent,
        };
    }

    /// <summary><c>ERROR_WRONG_CLIENT_KEY</c> or <c>ERROR_KEY_DOES_NOT_EXIST</c>: fix the API key.</summary>
    public sealed class AuthenticationException : ApiException
    {
        /// <summary>Creates the error.</summary>
        public AuthenticationException(SurSolverOperation operation, string code, string description, string? taskId = null, IReadOnlyList<string>? supportedUserAgents = null)
            : base(operation, code, description, taskId, supportedUserAgents) { }
    }

    /// <summary>
    /// <c>ERROR_INVALID_REQUEST</c>, <c>ERROR_TYPE_NOT_SUPPORTED</c>, or <c>ERROR_WRONG_USERAGENT</c> on create
    /// (see <see cref="ApiException.SupportedUserAgents"/>). Nothing was created or charged.
    /// </summary>
    public sealed class InvalidTaskException : ApiException
    {
        /// <summary>Creates the error.</summary>
        public InvalidTaskException(SurSolverOperation operation, string code, string description, string? taskId = null, IReadOnlyList<string>? supportedUserAgents = null)
            : base(operation, code, description, taskId, supportedUserAgents) { }
    }

    /// <summary><c>ERROR_ZERO_BALANCE</c>: top up. Nothing was charged.</summary>
    public sealed class InsufficientBalanceException : ApiException
    {
        /// <summary>Creates the error.</summary>
        public InsufficientBalanceException(SurSolverOperation operation, string code, string description, string? taskId = null, IReadOnlyList<string>? supportedUserAgents = null)
            : base(operation, code, description, taskId, supportedUserAgents) { }
    }

    /// <summary><c>ERROR_NO_SLOT_AVAILABLE</c>: all your threads are busy. Nothing was created; submitting later is safe.</summary>
    public sealed class NoSlotAvailableException : ApiException
    {
        /// <summary>Creates the error.</summary>
        public NoSlotAvailableException(SurSolverOperation operation, string code, string description, string? taskId = null, IReadOnlyList<string>? supportedUserAgents = null)
            : base(operation, code, description, taskId, supportedUserAgents) { }
    }

    /// <summary><c>ERROR_TASK_NOT_FOUND</c>.</summary>
    public sealed class TaskNotFoundException : ApiException
    {
        /// <summary>Creates the error.</summary>
        public TaskNotFoundException(SurSolverOperation operation, string code, string description, string? taskId = null, IReadOnlyList<string>? supportedUserAgents = null)
            : base(operation, code, description, taskId, supportedUserAgents) { }
    }

    /// <summary>
    /// The task ended without a result (<c>ERROR_FAILED</c>, <c>ERROR_TASK_TIMEOUT</c>, <c>ERROR_TASK_EXPIRED</c>,
    /// or <c>ERROR_WRONG_USERAGENT</c> after acceptance). Its charge is returned once.
    /// </summary>
    public sealed class TaskFailedException : ApiException
    {
        /// <summary>Creates the error.</summary>
        public TaskFailedException(SurSolverOperation operation, string code, string description, string? taskId = null, IReadOnlyList<string>? supportedUserAgents = null)
            : base(operation, code, description, taskId, supportedUserAgents) { }
    }
}
