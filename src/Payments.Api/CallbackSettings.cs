using Microsoft.Extensions.Configuration;

namespace Payments.Api;

/// <summary>Inbound callback credentials. Fails fast on missing keys, naming them but never printing values.</summary>
public sealed record CallbackSettings(string MerchantId, string AdviceUsername, string AdvicePassword, string WebhookSecret)
{
    public static CallbackSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Acme");
        var missing = new[] { "MerchantId", "AdviceUsername", "AdvicePassword", "WebhookSecret" }
            .Where(key => string.IsNullOrWhiteSpace(section[key]))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Missing required configuration: {string.Join(", ", missing.Select(key => $"Acme__{key}"))}");
        }

        return new CallbackSettings(section["MerchantId"]!, section["AdviceUsername"]!, section["AdvicePassword"]!, section["WebhookSecret"]!);
    }

    public override string ToString() => "CallbackSettings { ... }";
}
