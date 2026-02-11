using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

ConnectionFactory factory = new()
{
    HostName = Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_HOST") ?? "localhost",
    Port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_PORT"), out int p) ? p : 5673,
    UserName = Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_USER") ?? "guest",
    Password = Environment.GetEnvironmentVariable("RABBITMQ_LOCAL_PASS") ?? "guest"
};

await using IConnection connection = await factory.CreateConnectionAsync();
await using IChannel channel = await connection.CreateChannelAsync();

const string queueName = "pdv.vendas.local";

// declara fila (idempotente)
await channel.QueueDeclareAsync(queue: queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);

Log.Information("PDV.Client iniciado. Digite o valor da venda ou 'q' para sair.");

while (true)
{
    Console.Write("Valor da venda: ");
    string? input = Console.ReadLine();

    if (string.Equals(input, "q", StringComparison.OrdinalIgnoreCase)) break;

    if (!decimal.TryParse(input, out decimal total))
    {
        Log.Warning("Valor inválido: {Input}", input);
        continue;
    }

    var venda = new
    {
        IdVenda = Guid.NewGuid(),
        Origem = "PDVJ",
        Total = total
    };

    byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(venda));
    BasicProperties props = new()
    {
        Persistent = true,
        Headers = new Dictionary<string, object?>
        {
            ["x-id-venda"] = venda.IdVenda.ToString(),
            ["x-origem"] = venda.Origem
        }
    };

    await channel.BasicPublishAsync(exchange: string.Empty,
        routingKey: queueName,
        mandatory: true,
        basicProperties: props,
        body: body);

    Log.Information("Venda publicada: {Id} Total={Total}", venda.IdVenda, venda.Total);
}

Log.Information("Encerrando PDV.Client.");

public class Venda
{
    public Guid IdVenda { get; set; }
    public string Origem { get; set; }
    public decimal Total { get; set; }
}