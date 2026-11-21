using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace AliceIdentityService.Services;

public sealed class RabbitMqSettings
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string QueueName { get; set; } = "alice-mail-service";
    public bool UseTls { get; set; }
    public string? TlsServerName { get; set; }
    public int PublishTimeoutSeconds { get; set; } = 10;
}

// One lazily opened connection; channels are per publication. Serialization avoids
// concurrent channel use. A failed connection can be retried by the next request.
public sealed class RabbitMailPublisher(IOptions<RabbitMqSettings> settings) : IMailPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;

    public async Task<string> PublishAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var options = settings.Value;
        var queue = options.QueueName;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.PublishTimeoutSeconds));
        await gate.WaitAsync(timeout.Token);
        try
        {
            if (connection?.IsOpen != true)
            {
                if (connection is not null) await connection.DisposeAsync();
                connection = null;
                var factory = new ConnectionFactory
                {
                    HostName = options.HostName,
                    Port = options.Port,
                    VirtualHost = options.VirtualHost,
                    UserName = options.UserName,
                    Password = options.Password,
                    AutomaticRecoveryEnabled = false,
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(options.PublishTimeoutSeconds),
                    Ssl = new SslOption { Enabled = options.UseTls, ServerName = string.IsNullOrWhiteSpace(options.TlsServerName) ? options.HostName : options.TlsServerName }
                };
                connection = await factory.CreateConnectionAsync(timeout.Token);
            }
            await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), timeout.Token);
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: timeout.Token);
            await channel.BasicPublishAsync(string.Empty, queue, mandatory: true,
                basicProperties: new BasicProperties
                {
                    Persistent = true,
                    ContentType = "application/x-msgpack",
                    MessageId = Guid.NewGuid().ToString("N")
                }, body: body, cancellationToken: timeout.Token);
            // Awaiting publish with confirmation tracking rejects nacks/returns. No automatic replay:
            // a timeout may mean the broker accepted the message but its acknowledgement was lost.
            return queue;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null) await connection.DisposeAsync();
        gate.Dispose();
    }
}
