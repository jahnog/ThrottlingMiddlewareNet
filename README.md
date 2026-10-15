# ThrottlingMiddleware

In-process throttling middleware for ASP.NET Core microservices on .NET 10. Register it with `AddThrottling`, then add `UseThrottling` to the pipeline. One rule from configuration admits each request.

Each rule has two gates:

- **Total** is the number of requests running or waiting. If it is full, the call gets **429** immediately.
- **Active** is the number of requests inside the rest of the pipeline. A call that got a total slot and no active slot waits up to `maxSleep`. If the wait expires, the call gets **429** and the total slot is released.

`Retry-After` is the rule period: 60 seconds for `minute`, 3600 for `hour`, and 86400 for `day`. The gates do not reset when that period ends. This is a bulkhead, not a counter of calls per minute.

Gates live in the process. Ten replicas enforce the limit ten times. Size `maxActiveRequests` from this process's own pools (threads, database connections, downstream calls). Keep `maxTotalRequests` a small multiple of that, and keep `maxSleep` under a typical client timeout.

## Advantages

Invalid options fail when the host starts, before it serves a request.

## Commands

Commands below name the directory they run from. Coverage files and the mutation report are build output.

### Install

The SDK for this repository is named in `global.json`: version `10.0.112`, with `rollForward` set to `latestFeature`.

From the repository root, restore the library and the unit-test project, including ReportGenerator:

```bash
dotnet restore tests/ThrottlingMiddleware.Tests/ThrottlingMiddleware.Tests.csproj
```

From the repository root, restore the local tool `dotnet-stryker` 5.0.0:

```bash
dotnet tool restore
```

### Uninstall

No uninstall command exists.

### Run

From the repository root, start the catalog sample:

```bash
dotnet run --project samples/CatalogApi
```

### Test

From the repository root, run the unit tests for the library:

```bash
dotnet test tests/ThrottlingMiddleware.Tests
```

From the repository root, run every test project, including `tests/CatalogApi.Tests`:

```bash
dotnet test
```

### Debug

No debug command exists.

### Mutation test

From `tests/ThrottlingMiddleware.Tests`:

```bash
dotnet stryker --since:master
```

### Reports

From the repository root, write Cobertura for the unit tests:

```bash
dotnet test tests/ThrottlingMiddleware.Tests --collect:"XPlat Code Coverage;Format=cobertura" --results-directory TestResults
```

From the repository root, write the stable Cobertura file and the HTML index:

```bash
dotnet ~/.nuget/packages/reportgenerator/5.5.11/tools/net10.0/ReportGenerator.dll -reports:"TestResults/**/coverage.cobertura.xml" -targetdir:coverage -reporttypes:"Html;Cobertura" -sourcedirs:src/ThrottlingMiddleware
```

The Cobertura file is `coverage/Cobertura.xml`. The HTML index is `coverage/index.html`.

## Rules

The rule is chosen in this order:

1. An endpoint rule whose `METHOD /path` matches the route pattern.
2. Otherwise a `remote_ip` rule, with a separate pair of gates per remote address.
3. Otherwise a non-default `global` rule.
4. Otherwise the required default rule, which must be `global`.

Inside one step the tighter rule wins: lower `maxTotalRequests`, then lower `maxActiveRequests`, then shorter `maxSleep`, then the rule name.

A more specific rule **replaces** the default. It does not also consume the default. A `remote_ip` rule therefore does not cap the whole process: each address gets its own budget, and the default does not run for routes that are not an endpoint match. Use the default global rule as the process cap. Add `remote_ip` only when the set of addresses is bounded. The address table keeps at most `maxTrackedIps` gates (default 10000) and drops idle ones when a new address would exceed that.

The library reads `Connection.RemoteIpAddress`. It does not parse `X-Forwarded-For`. Put `UseForwardedHeaders` in front of it, and in production set `KnownProxies` to the real gateway.

`UseThrottling` runs after `UseRouting`.

## Setup

```csharp
builder.Services.AddThrottling(builder.Configuration.GetSection("Throttling"));

app.UseForwardedHeaders();
app.UseRouting();
app.UseThrottling();
```

```json
{
  "Throttling": {
    "MaxTrackedIps": 10000,
    "Rules": [
      {
        "Name": "default",
        "IsDefault": true,
        "Key": "global",
        "Period": "minute",
        "MaxActiveRequests": 20,
        "MaxTotalRequests": 40,
        "MaxSleep": "00:00:01"
      },
      {
        "Name": "create-order",
        "Key": "endpoint",
        "Endpoint": "POST /orders",
        "Period": "minute",
        "MaxActiveRequests": 2,
        "MaxTotalRequests": 4,
        "MaxSleep": "00:00:01"
      }
    ]
  }
}
```

A per-address rule looks like this. It takes over every route that does not match an endpoint rule:

```json
{
  "Name": "by-ip",
  "Key": "remote_ip",
  "Period": "minute",
  "MaxActiveRequests": 2,
  "MaxTotalRequests": 4,
  "MaxSleep": "00:00:00.500"
}
```

Keys are `global`, `endpoint`, and `remote_ip`. Periods are `minute`, `hour`, and `day`. `maxSleep` may be zero, which means the active gate does not wait.

The JSON body is:

```json
{
  "title": "Too Many Requests",
  "status": 429,
  "rule": "default",
  "retryAfter": 60
}
```

Rejections are logged at Debug with the rule name and the reason (`total`, `sleep`, or `ip-table-full`). The address is not logged.

## Sample

`samples/CatalogApi` is a small catalog service. Start it with the run command.

- `GET /items`
- `POST /orders` with `{ "sku": "book", "quantity": 1 }` and optional `delayMs`
- `GET /work?delayMs=` waits, so tests can fill a gate. The response header `X-In-Flight` is how many `/work` calls were inside the handler when this one entered.

The sample trusts forwarded headers from loopback so tests can pass `X-Forwarded-For`. Do not copy that trust list into production.

## Tests

`tests/ThrottlingMiddleware.Tests` covers rule selection, validation, the gates, and the address table. Run it with the unit-test command.

`tests/CatalogApi.Tests` sends HTTP requests to `CatalogApi`, including the performance tests tagged `Category=Performance`. The command that runs every test project includes this project.

## License

The license is MIT.
