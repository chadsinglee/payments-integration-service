using Payments.Application.Payments;
using Payments.Domain;

namespace Payments.Api.Endpoints;

public sealed record SubmitPaymentRequest(string? PaymentId, string? CustomerId, string? SubMerchantId, long Amount);

public sealed record PaymentResponse(
    string PaymentId,
    string Status,
    long Amount,
    string Currency,
    long? AuthAmount,
    bool NeedsInvestigation,
    string ReconciliationState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PaymentResponse From(PaymentView view) =>
        new(view.PaymentId, view.Status.ToString(), view.AmountMinor, view.Currency, view.AuthAmountMinor, view.NeedsInvestigation, view.ReconciliationState, view.CreatedAt, view.UpdatedAt);
}

public static class PaymentEndpoints
{
    private const int MaxIdLength = 50;

    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/payments", SubmitAsync);
        routes.MapGet("/payments/{paymentId}", GetAsync);
        return routes;
    }

    private static async Task<IResult> SubmitAsync(SubmitPaymentRequest request, PaymentService payments, CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await payments.SubmitAsync(
            new SubmitPaymentCommand(request.PaymentId!, request.CustomerId!, request.SubMerchantId!, request.Amount),
            cancellationToken);

        return result.Outcome switch
        {
            SubmitPaymentOutcome.Created => Results.Accepted($"/payments/{Uri.EscapeDataString(request.PaymentId!)}", PaymentResponse.From(result.Payment!)),
            SubmitPaymentOutcome.Duplicate => Results.Ok(PaymentResponse.From(result.Payment!)),
            SubmitPaymentOutcome.Conflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Payment id already used",
                detail: "A payment with this id exists with a different customer, sub-merchant or amount."),
            SubmitPaymentOutcome.CardNotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No default card for this customer"),
            SubmitPaymentOutcome.SubMerchantNotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown sub-merchant"),
            _ => throw new InvalidOperationException($"Unhandled outcome {result.Outcome}"),
        };
    }

    private static async Task<IResult> GetAsync(string paymentId, PaymentService payments, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(paymentId, cancellationToken);
        return payment is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Payment not found")
            : Results.Ok(PaymentResponse.From(payment));
    }

    private static Dictionary<string, string[]> Validate(SubmitPaymentRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        Require(errors, nameof(request.PaymentId), request.PaymentId, MaxIdLength);
        Require(errors, nameof(request.CustomerId), request.CustomerId, MaxIdLength);
        Require(errors, nameof(request.SubMerchantId), request.SubMerchantId, 15);
        if (request.Amount <= 0)
        {
            errors[nameof(request.Amount)] = ["Amount must be a positive whole number of cents."];
        }

        return errors;
    }

    private static void Require(Dictionary<string, string[]> errors, string name, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            errors[name] = [$"{name} is required and must be at most {maxLength} characters."];
        }
    }
}
