# Agent guide

Stack: .NET 10, ASP.NET Core controllers, FluentValidation, EF Core/Npgsql,
ASP.NET Core Identity, JWT, xUnit and SQLite integration tests.

Layout:
- src/Torvello.Domain: dependency-free rules and roles.
- src/Torvello.Application: contracts, validators and interfaces.
- src/Torvello.Infrastructure: Identity, services, persistence, migrations and email.
- src/Torvello.Api: controllers, middleware and host configuration.
- tests/Torvello.Tests: tests and TestApp.

Conventions: keep controllers thin; implement business rules in services; use
AppException for ProblemDetails titles; pass cancellation tokens; never log secrets.
Follow docs/SECURITY.md and its API contract. Keep schema migrations explicit.
Use configuration/user-secrets for secrets; do not commit credentials.

Commands (from repository root):
- dotnet restore
- dotnet build --no-restore -warnaserror
- dotnet test --no-build
- dotnet list package --vulnerable --include-transitive
- dotnet format

Run dotnet format manually after each feature and before pushes; no pre-commit hooks.
Respect task-specific restrictions on commands and network access.
