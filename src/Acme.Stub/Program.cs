using Acme.Stub;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<StubState>();

var app = builder.Build();
app.MapAcmeStub();
app.Run();
