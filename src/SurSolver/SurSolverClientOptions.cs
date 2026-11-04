using System;

namespace SurSolver
{
    /// <summary>Options of <see cref="SurSolverClient"/>. Every property has a working default.</summary>
    public sealed class SurSolverClientOptions
    {
        /// <summary>API origin. Plain <c>http://</c> is accepted only for localhost.</summary>
        public string BaseUrl { get; set; } = SurSolverClient.DefaultBaseUrl;

        /// <summary>Bound on each HTTP request. Default 30 s.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Wait between polls in <c>SolveAsync</c>. Default 3 s.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary><c>SolveAsync</c> deadline. Default 210 s.</summary>
        public TimeSpan SolveTimeout { get; set; } = TimeSpan.FromSeconds(210);

        /// <summary>How often <c>SolveAsync</c> resubmits after <c>ERROR_NO_SLOT_AVAILABLE</c>. Default 1.</summary>
        public int MaxSubmitRetries { get; set; } = 1;

        /// <summary>Your referral app id, sent with every <c>CreateTaskAsync</c>.</summary>
        public string? AppId { get; set; }

        /// <summary>Text appended to the <c>User-Agent</c> header <c>sursolver-dotnet/&lt;version&gt;</c>.</summary>
        public string? UserAgentSuffix { get; set; }

        /// <summary>Replaces the HTTP transport (proxies, custom TLS, tests). Default <see cref="HttpTransport"/>.</summary>
        public ITransport? Transport { get; set; }

        /// <summary>Replaces the real clock (tests).</summary>
        public IClock? Clock { get; set; }

        /// <summary>Replaces the jitter source; it must return values in [0, 1).</summary>
        public Func<double>? Random { get; set; }
    }
}
