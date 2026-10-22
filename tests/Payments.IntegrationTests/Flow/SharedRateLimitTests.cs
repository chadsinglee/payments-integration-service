using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Payments.Application.Payments;
using Payments.Infrastructure.Acme;
using Payments.IntegrationTests.Infrastructure;
using Testcontainers.Redis;

namespace Payments.IntegrationTests.Flow;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SharedRateLimitTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private readonly StubFactory _stub = new();
    private string _connectionString = "";
    private ApiFactory _api = null!;
    private HttpClient _client = null!;
    private HttpClient _stubClient = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _connectionString = await postgres.CreateIsolatedDatabaseAsync();
        _api = new ApiFactory(_connectionString);
        _client = _api.CreateClient();
        _stubClient = _stub.CreateClient();
        _stubClient.DefaultRequestHeaders.Authorization = new("Bearer", StubFactory.ApiKey);
    }

    public async Task DisposeAsync()
    {
        _stubClient.Dispose();
        _client.Dispose();
        await _api.DisposeAsync();
        await _stub.DisposeAsync();
        await _redis.DisposeAsync();
    }

    // The window is one second and the two calls are made back to back, so they land in the same window.
    [Fact]
    public async Task Reconcile_WhenThePostJustSpentTheRedisWindow_IsDeniedWithoutCallingAcme()
    {
        (await _client.PostAsJsonAsync("/payments", new { paymentId = "pay-1", customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 5_000 }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var acme = new AcmeCallRecorder();
        using var limiter = new RedisAcmeRateLimiter(new RedisRateLimiterOptions(_redis.GetConnectionString(), 1), NullLogger<RedisAcmeRateLimiter>.Instance);

        // A short MaxLimiterWait makes the gate give up at once instead of waiting out the one-second window.
        var gate = AcmeGateSettings.Default with { MaxLimiterWait = TimeSpan.FromMilliseconds(10) };
        using var harness = new WorkerHarness(_connectionString, _stub, inner => new LosePostResponseHandler(acme, inner), gate: gate, limiter: limiter);

        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        acme.Posts.ShouldBe(1);
        await Execute("UPDATE payments SET next_attempt_at = now() - interval '1 second'");

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        acme.Queries.ShouldBe(0);
        acme.Posts.ShouldBe(1);
        await using var connection = new NpgsqlConnection(_connectionString);
        var row = await connection.QuerySingleAsync<(string Status, int RttqAttempts)>("SELECT status AS Status, rttq_attempts AS RttqAttempts FROM payments");
        row.Status.ShouldBe("Unknown");
        row.RttqAttempts.ShouldBe(0);
        (await _stubClient.GetFromJsonAsync<JsonElement>("/admin/posts")).GetArrayLength().ShouldBe(1);
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync(sql);
    }

    private sealed class AcmeCallRecorder
    {
        private int _posts;
        private int _queries;

        public int Posts => _posts;

        public int Queries => _queries;

        public void Record(HttpMethod method)
        {
            if (method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _posts);
            }
            else
            {
                Interlocked.Increment(ref _queries);
            }
        }
    }

    // The POST reaches the stub but its response is lost, which leaves the payment Unknown and due for RTTQ.
    private sealed class LosePostResponseHandler(AcmeCallRecorder recorder, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            recorder.Record(request.Method);
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Post)
            {
                response.Dispose();
                throw new HttpRequestException("response lost");
            }

            return response;
        }
    }
}
