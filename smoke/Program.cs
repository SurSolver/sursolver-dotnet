// Reads the public catalog and calls GetBalanceAsync with a fake key (expects AuthenticationException).
// Set SURSOLVER_API_KEY to also read your real balance; SURSOLVER_BASE_URL overrides the origin.
using SurSolver;

var baseUrl = Environment.GetEnvironmentVariable("SURSOLVER_BASE_URL") ?? SurSolverClient.DefaultBaseUrl;
var client = new SurSolverClient("sur_00000000000000000000000000000000", new SurSolverClientOptions { BaseUrl = baseUrl });

var catalog = await client.ListTaskTypesAsync();
Console.WriteLine($"ListTaskTypesAsync: {catalog.TaskTypes.Count} types, {catalog.ErrorCodes.Count} error codes");
var services = await client.ListServicesAsync();
Console.WriteLine($"ListServicesAsync: {services.Count} active services");
try
{
    await client.GetBalanceAsync();
    throw new InvalidOperationException("GetBalanceAsync accepted a fake key");
}
catch (AuthenticationException error)
{
    Console.WriteLine($"GetBalanceAsync (fake key): {error.Message}");
}

var apiKey = Environment.GetEnvironmentVariable("SURSOLVER_API_KEY");
if (!string.IsNullOrEmpty(apiKey))
{
    var real = new SurSolverClient(apiKey, new SurSolverClientOptions { BaseUrl = baseUrl });
    Console.WriteLine($"GetBalanceAsync (your key): {await real.GetBalanceAsync()} USD");
}
Console.WriteLine("smoke OK");
