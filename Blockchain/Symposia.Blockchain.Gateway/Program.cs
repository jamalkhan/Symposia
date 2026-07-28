using Symposia.Blockchain.Gateway;
using Symposia.Blockchain.Gateway.Chain;
using Symposia.Blockchain.Gateway.Nodes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.AddSingleton<BootstrapChainClient>();

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapNodeEndpoints();

app.Run();

public partial class Program;
