using System.Diagnostics.Metrics;
using System.Globalization;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.Grafana.Loki;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// ===== Serilog (Console + Loki) =====
string lokiUrl = builder.Configuration["LOKI_URL"] ?? "http://localhost:3100";

Log.Logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Console().WriteTo.GrafanaLoki(lokiUrl)
    .CreateLogger();

builder.Host.UseSerilog();

string jaegerOtlpEndpoint = builder.Configuration["OTLP_ENDPOINT_URL"] ?? "http://localhost:4317";

// ===== OpenTelemetry Resource =====
ResourceBuilder resourceBuilder =
    ResourceBuilder.CreateDefault().AddService(serviceName: "Concentrador.Api", serviceVersion: "1.0.0");

// ===== OpenTelemetry Metrics + Traces =====
builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
{
    metrics.SetResourceBuilder(resourceBuilder);
    metrics.AddAspNetCoreInstrumentation();
    metrics.AddRuntimeInstrumentation();
    metrics.AddHttpClientInstrumentation();
    metrics.AddPrometheusExporter(); // expõe /metrics
}).WithTracing(tracer =>
{
    tracer.SetResourceBuilder(resourceBuilder);
    tracer.AddAspNetCoreInstrumentation();
    tracer.AddHttpClientInstrumentation();
    tracer.AddOtlpExporter(options =>
    {
        options.Endpoint = new Uri(jaegerOtlpEndpoint);
        options.Protocol = OtlpExportProtocol.Grpc;
    });
});

WebApplication app = builder.Build();

app.UseSerilogRequestLogging();

// endpoint de métricas Prometheus (exposto por OpenTelemetry Metrics)
app.UseOpenTelemetryPrometheusScrapingEndpoint(); // /metrics

// /health
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" })).WithName("HealthCheck");

// métricas custom de vendas recebidas
Meter meter = new("Concentrador.Api");
Counter<int> vendasRecebidasCounter = meter.CreateCounter<int>("concentrador_vendas_recebidas_total");

TracerProvider tracerProvider = app.Services.GetRequiredService<TracerProvider>();
Tracer tracer = tracerProvider.GetTracer("Concentrador.Api");

app.MapPost("/vendas",
    async (Venda venda, IHttpClientFactory httpClientFactory) =>
    {
        using TelemetrySpan? activity = tracer.StartActiveSpan("ProcessarVenda", SpanKind.Server);
        activity?.SetAttribute("venda.id", venda.IdVenda.ToString());
        activity?.SetAttribute("venda.origem", venda.Origem);
        activity?.SetAttribute("venda.total", venda.Total.ToString(CultureInfo.CurrentCulture));

        try
        {
            using (tracer.StartActiveSpan("ValidarVenda", SpanKind.Internal)) // Default é Internal
            {
                // validações de negócio
                await Task.Delay(20);
            }

            using (tracer.StartActiveSpan("PersistirVenda"))
            {
                // simular gravação em banco
                await Task.Delay(30);
            }

            using (tracer.StartActiveSpan("PublicarEvento", SpanKind.Producer))
            {
                // simular publish em RabbitMQ / outro sistema
                await Task.Delay(40);
            }

            return Results.Ok(new { status = "received" });
        }
        catch (Exception ex)
        {
            activity?.SetStatus(Status.Error);
            Log.Error(ex, "Erro ao processar venda {Id}", venda.IdVenda);
            return Results.Problem("Erro ao processar venda.");
        }
    });


app.Run();

public record Venda(Guid IdVenda, string Origem, decimal Total);