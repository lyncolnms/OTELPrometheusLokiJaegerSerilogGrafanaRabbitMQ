using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using Serilog.Enrichers.Span;

const string serviceName = "Pdv.Sync";

// ActivitySource estático para o app
ActivitySource activitySource = new(serviceName);

// Host genérico para gerenciar OTEL e DI
using IHost host = Host.CreateDefaultBuilder(args).UseSerilog((_, _, loggerConfig) =>
{
    loggerConfig.Enrich.FromLogContext().Enrich.WithSpan() // adiciona traceId/spanId automaticamente
        .WriteTo.Console();
}).ConfigureServices(services =>
{
    services.AddOpenTelemetry().WithTracing(t =>
    {
        t.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName)).AddSource(serviceName)
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(Environment.GetEnvironmentVariable("OTLP_ENDPOINT_URL") ??
                                     "http://localhost:4317"); // Jaeger OTLP no host
                o.Protocol = OtlpExportProtocol.Grpc;
            });
    });
}).Build();

await host.StartAsync();

// ======================
// Lógica RabbitMQ / Sync
// ======================

Log.Information("Pdv.Sync iniciado. Aguardando mensagens de vendas.local.");

ConnectionFactory localFactory = new()
{
    HostName = Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_HOST") ?? "localhost",
    Port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_PORT"), out int lp) ? lp : 5673,
    UserName = Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_USER") ?? "guest",
    Password = Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_PASS") ?? "guest"
};

await using IConnection localConnection = await localFactory.CreateConnectionAsync();
await using IChannel localChannel = await localConnection.CreateChannelAsync();

const string localQueue = "pdv.vendas.local";
await localChannel.QueueDeclareAsync(localQueue, durable: true, exclusive: false, autoDelete: false);

ConnectionFactory centralFactory = new()
{
    HostName = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost",
    Port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT"), out int cp) ? cp : 5672,
    UserName = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "guest",
    Password = Environment.GetEnvironmentVariable("RABBITMQ_PASS") ?? "guest"
};

await using IConnection centralConnection = await centralFactory.CreateConnectionAsync();
await using IChannel centralChannel = await centralConnection.CreateChannelAsync();

const string centralQueue = "pdv.vendas.central";
await centralChannel.QueueDeclareAsync(centralQueue, durable: true, exclusive: false, autoDelete: false);

AsyncEventingBasicConsumer consumer = new(localChannel);

consumer.ReceivedAsync += async (_, ea) =>
{
    string json = Encoding.UTF8.GetString(ea.Body.ToArray());
    Venda? venda = null;

    using Activity? activity = activitySource.StartActivity("EnviarVendaParaConcentrador", ActivityKind.Producer);

    try
    {
        venda = JsonSerializer.Deserialize<Venda>(json);
        activity?.SetTag("venda.id", venda!.IdVenda);
        activity?.SetTag("venda.origem", venda?.Origem);
        activity?.SetTag("venda.total", venda?.Total);

        Log.Information("Bridge: encaminhando venda {Id} Total={Total}", venda?.IdVenda, venda?.Total);

        BasicProperties props = new()
        {
            Persistent = true,
            Headers = ea.BasicProperties.Headers ?? new Dictionary<string, object?>
            {
                ["x-trace-id"] = activity?.TraceId.ToString() // opcional
            }
        };

        await centralChannel.BasicPublishAsync(exchange: "",
            routingKey: centralQueue,
            mandatory: false,
            basicProperties: props,
            body: ea.Body.ToArray());

        await localChannel.BasicAckAsync(ea.DeliveryTag, multiple: false);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Erro ao encaminhar venda {Id}", venda?.IdVenda);
        await localChannel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
    }
};

await localChannel.BasicConsumeAsync(localQueue, autoAck: false, consumer: consumer);

Console.WriteLine("Pressione ENTER para sair...");
Console.ReadLine();

await host.StopAsync();

public class Venda
{
    public Guid IdVenda { get; set; }
    public string Origem { get; set; } = default!;
    public decimal Total { get; set; }
}


// using System.Net.Http.Json;
// using System.Text;
// using System.Text.Json;
// using RabbitMQ.Client;
// using RabbitMQ.Client.Events;
// using Serilog;
//
// Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
//
// ConnectionFactory localFactory = new()
// {
//     HostName = Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_HOST") ?? "localhost",
//     Port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_PORT"), out int p) ? p : 5673,
//     UserName = Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_USER") ?? "guest",
//     Password = Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_PASS") ?? "guest"
// };
//
// await using IConnection localConnection = await localFactory.CreateConnectionAsync();
// await using IChannel localChannel = await localConnection.CreateChannelAsync();
//
// const string localQueue = "pdv.vendas.local";
// await localChannel.QueueDeclareAsync(localQueue, durable: true, exclusive: false, autoDelete: false);
//
// ConnectionFactory centralFactory = new()
// {
//     HostName = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost",
//     Port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT"), out int centralPort) ? centralPort : 5672,
//     UserName = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "guest",
//     Password = Environment.GetEnvironmentVariable("RABBITMQ_PASS") ?? "guest"
// };
//
// await using IConnection centralConnection = await centralFactory.CreateConnectionAsync();
// await using IChannel centralChannel = await centralConnection.CreateChannelAsync();
// const string centralQueue = "pdv.vendas";
//
// await centralChannel.QueueDeclareAsync(queue: centralQueue, durable: true, exclusive: false, autoDelete: false);
//
// await centralChannel.BasicQosAsync(0, 1, false);
//
// // HttpClient httpClient = new()
// // {
// //     BaseAddress = new Uri(Environment.GetEnvironmentVariable("CONCENTRADOR_API_URL") ?? "http://localhost:5081")
// // };
//
// Log.Information("Pdv.Sync iniciado. Aguardando mensagens de {Queue}.", centralQueue);
//
// AsyncEventingBasicConsumer consumer = new(centralChannel);
//
// // consumer.ReceivedAsync += async (_, ea) =>
// // {
// //     string json = Encoding.UTF8.GetString(ea.Body.ToArray());
// //
// //     Venda? venda = null;
// //     try
// //     {
// //         venda = JsonSerializer.Deserialize<Venda>(json);
// //     }
// //     catch (Exception e)
// //     {
// //         Log.Error(e, "Erro ao desserializar venda: {Json}", json);
// //         await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
// //     }
// //
// //     try
// //     {
// //         Log.Information("Enviando venda {Id} para Concentrador.Api", venda?.IdVenda);
// //
// //         HttpResponseMessage response = await httpClient.PostAsJsonAsync("/vendas", venda);
// //
// //         if (response.IsSuccessStatusCode)
// //         {
// //             Log.Information("Venda {Id} enviada com sucesso. Acking mensagem.", venda?.IdVenda);
// //             await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
// //         }
// //         else
// //         {
// //             Log.Warning(
// //                 "Falha ao enviar venda {Id} para Concentrador.Api. StatusCode={StatusCode}. Requeueing mensagem.",
// //                 venda?.IdVenda,
// //                 response.StatusCode);
// //             await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
// //         }
// //     }
// //     catch (Exception ex)
// //     {
// //         Log.Error(ex, "Erro ao chamar /vendas para venda {Id}", venda?.IdVenda);
// //         await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
// //     }
// // };
//
// consumer.ReceivedAsync += async (_, ea) =>
// {
//     byte[] body = ea.Body.ToArray();
//     string json = Encoding.UTF8.GetString(body);
//
//     try
//     {
//         Venda? venda = JsonSerializer.Deserialize<Venda>(json);
//         Log.Information("Bridge: encaminhando venda {Id} Total={Total}", venda!.IdVenda, venda.Total);
//
//         // reenvia para fila CENTRAL
//         BasicProperties props = new()
//         {
//             Persistent = true,
//             Headers = ea.BasicProperties.Headers // mantém headers
//         };
//
//         await centralChannel.BasicPublishAsync(exchange: "",
//             routingKey: centralQueue,
//             mandatory: true,
//             basicProperties: props,
//             body: body);
//
//         await localChannel.BasicAckAsync(ea.DeliveryTag, multiple: false);
//     }
//     catch (Exception ex)
//     {
//         Log.Error(ex, "Erro ao fazer bridge da mensagem.");
//         await localChannel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
//     }
// };
//
// await localChannel.BasicConsumeAsync(localQueue, autoAck: false, consumer: consumer);
//
// // await localChannel.BasicConsumeAsync(queue: centralQueue, autoAck: false, consumer: consumer);
//
// Console.WriteLine("Pressione ENTER para sair...");
// Console.ReadLine();
//
// public class Venda
// {
//     public Guid IdVenda { get; set; }
//     public string Origem { get; set; } = string.Empty;
//     public decimal Total { get; set; }
// }