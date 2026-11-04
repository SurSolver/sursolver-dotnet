# Changelog

## 0.1.0 — unreleased

First release. Tested against the SDK specification revision in `SPEC_VERSION` (catalog of
2026-11-04).

- `SurSolverClient` with `CreateTaskAsync`, `GetTaskResultAsync`, `GetBalanceAsync`,
  `ListTaskTypesAsync`, `ListServicesAsync` and `SolveAsync` (3 s polling, 210 s deadline,
  `OnTaskCreated`, `CancellationToken` cancellation).
- Typed exceptions: `AuthenticationException`, `InvalidTaskException` (with
  `SupportedUserAgents`), `InsufficientBalanceException`, `NoSlotAvailableException`,
  `TaskNotFoundException`, `TaskFailedException`, `ApiException`, `TransportException` (with
  `MayHaveCreatedTask`), `ProtocolException`, `SolveTimeoutException`, `ConfigurationException`.
- `TaskTypes` and `ErrorCodes` constants generated from the public catalog.
- Targets `netstandard2.0`; one dependency (`System.Text.Json`).
