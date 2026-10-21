using System.Security.Cryptography;
using System.Text;
using Payments.Domain;

namespace Payments.Application.Payments;

public sealed record SubmitPaymentCommand(string PaymentId, string CustomerId, string SubMerchantId, long AmountMinor);

public enum SubmitPaymentOutcome
{
    Created,
    Duplicate,
    Conflict,
    CardNotFound,
    SubMerchantNotFound,
}

public sealed record SubmitPaymentResult(SubmitPaymentOutcome Outcome, PaymentView? Payment = null);

public sealed class PaymentService(IPaymentStore store)
{
    public async Task<SubmitPaymentResult> SubmitAsync(SubmitPaymentCommand command, CancellationToken cancellationToken)
    {
        var subMerchant = await store.GetSubMerchantAsync(command.SubMerchantId, cancellationToken);
        if (subMerchant is null)
        {
            return new SubmitPaymentResult(SubmitPaymentOutcome.SubMerchantNotFound);
        }

        var card = await store.GetDefaultCardAsync(command.CustomerId, cancellationToken);
        if (card is null)
        {
            return new SubmitPaymentResult(SubmitPaymentOutcome.CardNotFound);
        }

        var amount = Money.FromMinorUnits(command.AmountMinor);
        var requestHash = HashRequest(command);
        var sequence = await store.NextOrderSequenceAsync(cancellationToken);

        var inserted = await store.InsertAsync(
            new NewPayment(
                Guid.NewGuid(),
                command.PaymentId,
                requestHash,
                command.CustomerId,
                subMerchant.Id,
                card.Id,
                amount.MinorUnits,
                OrderNumber.FromSequence(sequence),
                Guid.NewGuid()),
            cancellationToken);

        if (inserted.Inserted)
        {
            return new SubmitPaymentResult(SubmitPaymentOutcome.Created, inserted.Payment);
        }

        return inserted.Payment.RequestHash == requestHash
            ? new SubmitPaymentResult(SubmitPaymentOutcome.Duplicate, inserted.Payment)
            : new SubmitPaymentResult(SubmitPaymentOutcome.Conflict, inserted.Payment);
    }

    public Task<PaymentView?> GetAsync(string paymentId, CancellationToken cancellationToken) =>
        store.GetByAppPaymentIdAsync(paymentId, cancellationToken);

    private static string HashRequest(SubmitPaymentCommand command) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{command.CustomerId}|{command.SubMerchantId}|{command.AmountMinor}")));
}
