using Microsoft.AspNetCore.Diagnostics;

namespace Payments.Api;

/// <summary>Turns unreadable request bodies (e.g. a fractional amount) into 400 instead of 500.</summary>
public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = StatusCodes.Status400BadRequest, Title = "The request body is not valid JSON for this endpoint" },
        });
    }
}
