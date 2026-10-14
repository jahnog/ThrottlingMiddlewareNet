using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ThrottlingMiddleware;

internal sealed class ThrottlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RuleSet _rules;
    private readonly GateRegistry _gates;
    private readonly ILogger<ThrottlingMiddleware> _logger;

    public ThrottlingMiddleware(
        RequestDelegate next,
        RuleSet rules,
        GateRegistry gates,
        ILogger<ThrottlingMiddleware> logger)
    {
        _next = next;
        _rules = rules;
        _gates = gates;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var path = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
            ?? context.Request.Path.Value
            ?? "/";
        var rule = RuleSet.Select(_rules.Rules, method, path);
        var gate = _gates.Get(rule, context.Connection.RemoteIpAddress);
        if (gate is null)
        {
            await WriteRejectionAsync(context, rule, "ip-table-full").ConfigureAwait(false);
            return;
        }

        var decision = await gate.AcquireAsync(context.RequestAborted).ConfigureAwait(false);
        if (decision != GateDecision.Acquired)
        {
            var reason = decision == GateDecision.RejectedTotal ? "total" : "sleep";
            await WriteRejectionAsync(context, rule, reason).ConfigureAwait(false);
            return;
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteRejectionAsync(HttpContext context, ThrottleRule rule, string reason)
    {
        _logger.LogDebug("Throttled {Rule} because {Reason}.", rule.Name, reason);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var retryAfter = rule.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["Retry-After"] = retryAfter;
        await context.Response.WriteAsJsonAsync(
            new
            {
                title = "Too Many Requests",
                status = StatusCodes.Status429TooManyRequests,
                rule = rule.Name,
                retryAfter = rule.RetryAfterSeconds,
            },
            context.RequestAborted).ConfigureAwait(false);
    }
}
