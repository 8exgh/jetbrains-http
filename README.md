# JetBrains HTTP Client + .NET API demo

This repository demonstrates manual API invocation with a JetBrains `.http` file. The sample is a .NET 10 minimal API using JWT authentication, CQRS, and event sourcing.

## Run it

```bash
dotnet restore
dotnet run --project src/JetBrainsHttpDemo.Api
```

Open `requests/api.http` in Rider or another JetBrains IDE, select the `dev` environment, and run any request. Protected requests use the `demo-jwt` Password-flow auth configuration and `{{$auth.token(...)}}`, so the HTTP Client automatically acquires a JWT when absent and reuses it while valid. The explicit JSON login request also shows the raw endpoint and stores its result in the `jwt` global variable for experimentation.

The demo credentials are `demo` / `demo-password`. They and the JWT key are intentionally local development settings; use a secret store and a real identity system outside a demo.

## Design

- Command: `POST /api/tasks` appends a `TaskCreated` event.
- Query: `GET /api/tasks` rebuilds task views from events, then supports `search`, `status`, `page`, and `pageSize`.
- Authentication: `POST /auth/login` returns a one-hour JWT and records its ID in `sessions`.
- Storage: SQLite contains only `events` (the append-only domain event store) and `sessions` (login sessions).

This is deliberately compact and replay-oriented. A production event store would also add optimistic concurrency, schema/version handling, projections, password hashing/user management, session cleanup, and key rotation.

## Test it

```bash
dotnet test
```

The xUnit integration suite boots the real minimal API in memory and gives each test an isolated SQLite database. Fluent Assertions verifies both HTTP behavior and persisted event/session records.
