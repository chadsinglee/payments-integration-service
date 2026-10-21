using Microsoft.Extensions.Configuration;

namespace Payments.Infrastructure.Acme;

public sealed record AcmeOptions(Uri BaseUrl, string ApiKey, string MerchantId, TimeSpan Timeout)
{
    public const double DefaultTimeoutSeconds = 40;

    public override string ToString() => $"AcmeOptions {{ BaseUrl = {BaseUrl}, Timeout = {Timeout} }}";

    /// <summary>Reads the Acme__* settings and fails fast, naming the missing keys but never printing values.</summary>
    public static AcmeOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Acme");
        var missing = new[] { "BaseUrl", "ApiKey", "MerchantId" }.Where(key => string.IsNullOrWhiteSpace(section[key])).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Missing required configuration: {string.Join(", ", missing.Select(key => $"Acme__{key}"))}");
        }

        if (!Uri.TryCreate(section["BaseUrl"], UriKind.Absolute, out var baseUrl))
        {
            throw new InvalidOperationException("Acme__BaseUrl must be an absolute URL.");
        }

        // Acme can hold a request for up to 30 s, so the client timeout must stay above that.
        var timeoutSeconds = section.GetValue("TimeoutSeconds", DefaultTimeoutSeconds);
        if (timeoutSeconds <= 0)
        {
            throw new InvalidOperationException("Acme__TimeoutSeconds must be greater than 0.");
        }
        return new AcmeOptions(baseUrl, section["ApiKey"]!, section["MerchantId"]!, TimeSpan.FromSeconds(timeoutSeconds));
    }
}
