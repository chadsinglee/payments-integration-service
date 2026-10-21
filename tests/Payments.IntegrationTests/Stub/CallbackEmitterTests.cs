using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Acme.Stub;

namespace Payments.IntegrationTests.Stub;

public sealed class CallbackEmitterTests
{
    private const string Secret = "s3cret";
    private static readonly StubCallbackSettings Settings = new("http://api:8080/", "MID", "user", "pw", Secret);

    private static readonly JsonObject Outcome = new()
    {
        ["TransactionID"] = "11111111-1111-1111-1111-111111111111",
        ["TransactionDateTime"] = "2026-03-01T13:56:36.533",
        ["RetrievalReferenceNumber"] = "Z00000000001",
        ["AuthCode"] = "123456",
        ["AuthAmount"] = 1000L,
    };

    [Fact]
    public async Task Send_WhenScriptRepeatsAndReorders_EmitsSignedCallbacksWithStableWebhookIds()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);

        var results = await new CallbackEmitter(http, Settings).SendAsync(Outcome, "refund:300,advice,refund:300,reversal", CancellationToken.None);

        results.ShouldBe(["refund:300 -> 200", "advice -> 200", "refund:300 -> 200", "reversal -> 200"]);
        handler.Requests.Select(r => r.Path).ShouldBe(["/acme/webhook", "/acme/advice", "/acme/webhook", "/acme/webhook"]);

        var refunds = handler.Requests.Where(r => r.Path == "/acme/webhook").ToList();
        JsonNode.Parse(refunds[0].Body)!.ToJsonString().ShouldBe(JsonNode.Parse(refunds[1].Body)!.ToJsonString());
        JsonNode.Parse(refunds[2].Body)!["transactionUUID"]!.ToString().ShouldNotBe(JsonNode.Parse(refunds[0].Body)!["transactionUUID"]!.ToString());
        foreach (var webhook in refunds)
        {
            webhook.Signature.ShouldBe(Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(webhook.Body))));
        }

        handler.Requests[1].Authorization.ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pw")));
    }

    [Fact]
    public async Task Send_WhenTheScriptHasAnUnknownItem_Throws()
    {
        using var http = new HttpClient(new RecordingHandler());

        await Should.ThrowAsync<ArgumentException>(() => new CallbackEmitter(http, Settings).SendAsync(Outcome, "bogus", CancellationToken.None));
    }

    [Fact]
    public void ToString_NeverShowsTheCredentials() =>
        Settings.ToString().ShouldNotContain("pw");

    private sealed record Seen(string Path, string Body, string? Signature, string? Authorization);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.RequestUri!.AbsolutePath,
                await request.Content!.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("x-signature", out var values) ? values.Single() : null,
                request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
