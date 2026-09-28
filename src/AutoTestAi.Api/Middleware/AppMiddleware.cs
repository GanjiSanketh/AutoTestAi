using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Diagnostics;

namespace AutoTestAi.Api.Middleware;

/// <summary>
/// Adds/echoes X-Correlation-ID so logs, traces and workflow operations
/// can be correlated (docs/03 §15: execution/project correlation IDs).
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var values) &&
                            !string.IsNullOrWhiteSpace(values.FirstOrDefault())
            ? values.First()!
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}

/// <summary>
/// Global exception handler producing the error envelope from docs/06 §2.
/// Never logs or returns passwords, tokens or API keys.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception ex)
    {
        var (status, code, message, details) = ex switch
        {
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "UNAUTHORIZED", ex.Message, (IReadOnlyList<object>)Array.Empty<object>()),
            AutoTestAi.Application.Authorization.ForbiddenException => (HttpStatusCode.Forbidden, "FORBIDDEN", ex.Message, (IReadOnlyList<object>)Array.Empty<object>()),
            AutoTestAi.Application.Common.ValidationException validation => (HttpStatusCode.BadRequest, "VALIDATION_ERROR", validation.Message, validation.Errors.Select(e => (object)new { field = e.Field, message = e.Message }).ToList()),
            AutoTestAi.Application.Common.ConflictException => (HttpStatusCode.Conflict, "CONFLICT", ex.Message, (IReadOnlyList<object>)Array.Empty<object>()),
            AutoTestAi.Application.Common.NotFoundException => (HttpStatusCode.NotFound, "NOT_FOUND", ex.Message, (IReadOnlyList<object>)Array.Empty<object>()),
            ArgumentException => (HttpStatusCode.BadRequest, "VALIDATION_ERROR", ex.Message, (IReadOnlyList<object>)Array.Empty<object>()),
            InvalidOperationException invalidOp => (HttpStatusCode.ServiceUnavailable, "DEPENDENCY_UNAVAILABLE", FriendlyDependencyMessage(invalidOp), (IReadOnlyList<object>)Array.Empty<object>()),
            _ => (HttpStatusCode.InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred.", (IReadOnlyList<object>)Array.Empty<object>())
        };

        // No exception details that could carry secrets are logged beyond the message/type.
        logger.LogError(ex, "Unhandled exception {ErrorCode} on {Method} {Path}.",
            code, context.Request.Method, context.Request.Path);

        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/problem+json";

        var problem = new HttpValidationProblemDetails
        {
            Status = (int)status,
            Title = message,
            Detail = code,
            Instance = context.Request.Path,
        };
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString()
                                        ?? context.TraceIdentifier;

        // Shape the contract envelope from docs/06 §2 around ProblemDetails.
        var envelope = new
        {
            error = new
            {
                code,
                message = problem.Title,
                details,
                traceId = problem.Extensions["traceId"],
            }
        };

        await context.Response.WriteAsJsonAsync(envelope);
    }

    /// <summary>
    /// Never leak framework internals (e.g. missing-scheme errors in open
    /// local mode) to API consumers.
    /// </summary>
    private static string FriendlyDependencyMessage(InvalidOperationException ex)
        => ex.Message.Contains("authenticationScheme", StringComparison.OrdinalIgnoreCase)
            ? "Authentication is not configured on the server. Set Authentication:Authority."
            : ex.Message;
}
