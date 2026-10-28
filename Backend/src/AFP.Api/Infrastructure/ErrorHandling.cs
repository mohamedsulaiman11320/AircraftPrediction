using AFP.Application.Common;
using AFP.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AFP.Api.Infrastructure;

/// <summary>Translates exceptions into RFC 9457 ProblemDetails; unexpected errors never leak internals.</summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case ValidationException v:
                problem = new ValidationProblemDetails(v.Errors.ToDictionary(e => e.Key, e => e.Value))
                {
                    Status = v.StatusCode, Title = v.Title, Detail = v.Message,
                };
                problem.Extensions["code"] = v.Code;
                break;
            case AppException app:
                problem = new ProblemDetails { Status = app.StatusCode, Title = app.Title, Detail = app.Message };
                problem.Extensions["code"] = app.Code;
                break;
            case DomainException domain:
                problem = new ProblemDetails { Status = 400, Title = "Business rule violation", Detail = domain.Message };
                problem.Extensions["code"] = domain.Code;
                break;
            case BadHttpRequestException bad:
                problem = new ProblemDetails { Status = bad.StatusCode, Title = "Bad request", Detail = bad.Message };
                problem.Extensions["code"] = "bad_request";
                break;
            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                logger.LogInformation("Request cancelled by client: {Path}", context.Request.Path);
                problem = new ProblemDetails { Status = 499, Title = "Client closed request" };
                break;
            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
                problem = new ProblemDetails
                {
                    Status = 500, Title = "An unexpected error occurred",
                    Detail = "The error has been logged. Quote the traceId when contacting support.",
                };
                problem.Extensions["code"] = "internal_error";
                break;
        }

        if (problem.Status is >= 400 and < 500 && problem.Status != 499)
            logger.LogInformation("Request failed with {Status} {Code}: {Message}", problem.Status, (problem.Extensions.TryGetValue("code", out var code) ? code : null), exception.Message);

        context.Response.StatusCode = problem.Status ?? 500;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context, ProblemDetails = problem, Exception = exception,
        });
    }
}

public static class ProblemDetailsSetup
{
    public static void Customize(ProblemDetailsContext ctx)
    {
        ctx.ProblemDetails.Type ??= $"https://httpstatuses.io/{ctx.ProblemDetails.Status}";
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
        if (!ctx.ProblemDetails.Extensions.ContainsKey("code"))
        {
            ctx.ProblemDetails.Extensions["code"] = ctx.ProblemDetails.Status switch
            {
                401 => "unauthorized",
                403 => "forbidden",
                404 => "not_found",
                405 => "method_not_allowed",
                415 => "unsupported_media_type",
                429 => "rate_limited",
                _ => "error",
            };
        }
    }

    /// <summary>Model-binding failures (malformed JSON, invalid enum names) use the same shape as service validation.</summary>
    public static IActionResult InvalidModelState(ActionContext context)
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = 400, Title = "Validation failed", Detail = "One or more validation errors occurred.",
        };
        problem.Extensions["code"] = "validation_failed";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        problem.Type = "https://httpstatuses.io/400";
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }
}
