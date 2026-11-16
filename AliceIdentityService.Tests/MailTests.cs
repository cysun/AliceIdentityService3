using System.Net;
using AliceIdentityService.Services;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace AliceIdentityService.Tests;

internal sealed class CapturedMailPublisher : IMailPublisher
{
    public byte[]? Body { get; private set; }
    public bool Fail { get; set; }
    public Task PublishAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (Fail) throw new IOException("Test transport failure");
        Body = body.ToArray();
        return Task.CompletedTask;
    }
}

public sealed class RabbitFactAttribute : FactAttribute
{
    public RabbitFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIS3_TEST_RABBITMQ_HOST")))
            Skip = "Set AIS3_TEST_RABBITMQ_HOST to test persistent confirmed publication to an isolated temporary queue.";
    }
}

public sealed class MailTests
{
    private sealed class PublicationLogger : ILogger<AccountEmailSender>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private sealed class RecordingPublisher(Func<CancellationToken, Task> publish) : IMailPublisher
    {
        public int Calls { get; private set; }
        public byte[]? Body { get; private set; }
        public Task PublishAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            Calls++;
            Body = body.ToArray();
            return publish(cancellationToken);
        }
    }

    private static EmailTemplates Templates() => new(Path.Combine(AppContext.BaseDirectory, "EmailTemplates"));
    // Copy edits belong to the templates, not assertions about MIME transport.
    private static string Subject(string template) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "EmailTemplates", template + ".Subject.txt")).Trim();

    [Fact]
    public async Task PublicationLogsAreAccurateAndPrivateAndUncertainAttemptsAreNeverReplayed()
    {
        var logger = new PublicationLogger();
        AccountEmailSender Sender(IMailPublisher publisher) => new(Options.Create(new EmailSettings { SenderEmail = "noreply@example.test" }), publisher, Templates(), logger);
        Task Send(IMailPublisher publisher, CancellationToken cancellationToken = default) => Sender(publisher)
            .SendConfirmationAsync("private-recipient@example.test", "Private Name", "/Account/ConfirmEmail?code=PRIVATE-TOKEN", cancellationToken);

        var success = new RecordingPublisher(_ => Task.CompletedTask);
        await Send(success);
        using var message = MimeMessage.Load(new MemoryStream(Assert.Single(MessagePackSerializer.Deserialize<List<byte[]>>(success.Body!))));
        Assert.Contains(logger.Lines, line => line.Contains("broker-confirmed") && line.Contains(message.MessageId!));

        // The publisher may have accepted these bytes before its acknowledgment was lost.
        var uncertain = new RecordingPublisher(_ => throw new IOException("PRIVATE-TOKEN private-recipient@example.test"));
        var failure = await Assert.ThrowsAsync<MailDeliveryException>(() => Send(uncertain));
        Assert.Equal("The account email publication was not confirmed.", failure.Message);
        Assert.Equal(1, uncertain.Calls);
        Assert.Contains(logger.Lines, line => line.Contains("not broker-confirmed during broker") && line.Contains(nameof(IOException)));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new RecordingPublisher(async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        using var cancellation = new CancellationTokenSource();
        var pending = Send(cancelled, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, cancelled.Calls);
        Assert.Contains(logger.Lines, line => line.Contains("cancelled during broker"));
        Assert.Single(logger.Lines, line => line.StartsWith("Account email confirmation broker-confirmed"));
        var output = string.Join('\n', logger.Lines);
        Assert.DoesNotContain("private-recipient", output);
        Assert.DoesNotContain("Private Name", output);
        Assert.DoesNotContain("PRIVATE-TOKEN", output);
        Assert.DoesNotContain("/Account/ConfirmEmail", output);
    }

    [RabbitFact]
    public async Task RabbitPublicationSucceedsWithoutAConsumerAndRecoversAfterAChannelFailure()
    {
        var options = new RabbitMqSettings
        {
            HostName = Environment.GetEnvironmentVariable("AIS3_TEST_RABBITMQ_HOST")!,
            UserName = Environment.GetEnvironmentVariable("AIS3_TEST_RABBITMQ_USERNAME") ?? "guest",
            Password = Environment.GetEnvironmentVariable("AIS3_TEST_RABBITMQ_PASSWORD") ?? "guest",
            QueueName = "ais3_step12_publish_" + Guid.NewGuid().ToString("N")
        };
        await using var connection = await new ConnectionFactory { HostName = options.HostName, UserName = options.UserName, Password = options.Password }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await using var publisher = new RabbitMailPublisher(Options.Create(options));
        try
        {
            // A declaration conflict closes the publisher's channel, not its connection.
            await channel.QueueDeclareAsync(options.QueueName, durable: true, exclusive: false, autoDelete: true);
            await Assert.ThrowsAsync<OperationInterruptedException>(() => publisher.PublishAsync(new byte[] { 42 }, default));
            Assert.Equal(0U, await channel.MessageCountAsync(options.QueueName));
            await channel.QueueDeleteAsync(options.QueueName);

            // A subsequent explicit request can publish on a fresh channel without a mail consumer.
            await publisher.PublishAsync(new byte[] { 42 }, default);
            Assert.Equal(0U, await channel.ConsumerCountAsync(options.QueueName));
            Assert.Equal(1U, await channel.MessageCountAsync(options.QueueName));
            var delivery = await channel.BasicGetAsync(options.QueueName, autoAck: true);
            Assert.NotNull(delivery);
            Assert.Equal(new byte[] { 42 }, delivery.Body.ToArray());
            Assert.True(delivery.BasicProperties.Persistent);
            Assert.Equal("application/x-msgpack", delivery.BasicProperties.ContentType);
            Assert.False(string.IsNullOrEmpty(delivery.BasicProperties.MessageId));
            Assert.Null(await channel.BasicGetAsync(options.QueueName, autoAck: true));
        }
        finally { await channel.QueueDeleteAsync(options.QueueName); }
    }

    [Fact]
    public async Task TemplatesUseEditedContentAndEncodeHtmlWhileRejectingBrokenActionTemplates()
    {
        var directory = Directory.CreateTempSubdirectory("ais3-email-template-test-");
        try
        {
            var prefix = Path.Combine(directory.FullName, "Review");
            await File.WriteAllTextAsync(prefix + ".Subject.txt", "A custom subject\n");
            await File.WriteAllTextAsync(prefix + ".Body.txt", "Open {{link}}");
            await File.WriteAllTextAsync(prefix + ".Body.html", "<a href=\"{{link}}\">Continue</a>");
            var templates = new EmailTemplates(directory.FullName);
            const string link = "https://identity.example.test/?code=one&value=\"<test>\"";
            var content = await templates.RenderAsync("Review", link, default);
            Assert.Equal("A custom subject", content.Subject);
            Assert.Equal("Open " + link, content.TextBody);
            Assert.Contains("&amp;value=&quot;&lt;test&gt;&quot;", content.HtmlBody);
            Assert.DoesNotContain("{{link}}", content.HtmlBody);
            await File.WriteAllTextAsync(prefix + ".Subject.txt", "Updated subject");
            Assert.Equal("Updated subject", (await templates.RenderAsync("Review", link, default)).Subject);
            await File.WriteAllTextAsync(prefix + ".Subject.txt", "Subject\r\nBcc: injected@example.test");
            await Assert.ThrowsAsync<InvalidOperationException>(() => templates.RenderAsync("Review", link, default));
            await File.WriteAllTextAsync(prefix + ".Subject.txt", "Updated subject");
            await File.WriteAllTextAsync(prefix + ".Body.html", "Missing the action link");
            await Assert.ThrowsAsync<InvalidOperationException>(() => templates.RenderAsync("Review", link, default));
            var publisher = new CapturedMailPublisher();
            var sender = new AccountEmailSender(Options.Create(new EmailSettings { SenderEmail = "noreply@example.test" }),
                publisher, templates, NullLogger<AccountEmailSender>.Instance);
            await Assert.ThrowsAsync<MailDeliveryException>(() => sender.SendConfirmationAsync("user@example.test", "User", "/Account/ConfirmEmail", default));
            Assert.Null(publisher.Body);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task RegisteredSenderLoadsTemplatesFromTheWebContentRoot()
    {
        var publisher = new CapturedMailPublisher();
        using var factory = new PreviewFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IMailPublisher>(publisher);
            services.PostConfigure<EmailSettings>(settings => settings.SenderEmail = "noreply@example.test");
        }));
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAccountEmailSender>()
            .SendConfirmationAsync("user@example.test", "User", "/Account/ConfirmEmail?code=test", default);
        var messages = MessagePackSerializer.Deserialize<List<byte[]>>(publisher.Body!);
        using var message = await MimeMessage.LoadAsync(new MemoryStream(Assert.Single(messages)));
        Assert.Equal(Subject("EmailVerification"), message.Subject);
        Assert.Contains("/Account/ConfirmEmail?code=test", message.HtmlBody);
    }

    [Theory]
    [InlineData(false, "EmailVerification", "24 hours")]
    [InlineData(true, "ResetPassword", "1 hour")]
    public async Task AccountMessagesUseTheAliceMailServiceWireFormatAndConfiguredHttpsOrigin(bool reset, string template, string expiry)
    {
        var publisher = new CapturedMailPublisher();
        var sender = new AccountEmailSender(Options.Create(new EmailSettings
        {
            AppUrl = "https://identity.example.test/",
            SenderEmail = "noreply@example.test"
        }), publisher, Templates(), NullLogger<AccountEmailSender>.Instance);
        var path = (reset ? "/Account/ResetPassword" : "/Account/ConfirmEmail") + "?code=secret&userId=123";
        if (reset) await sender.SendPasswordResetAsync("user@example.test", "User <test>", path, default);
        else await sender.SendConfirmationAsync("user@example.test", "User <test>", path, default);
        var batch = MessagePackSerializer.Deserialize<List<byte[]>>(publisher.Body!);
        using var message = await MimeMessage.LoadAsync(new MemoryStream(Assert.Single(batch)));
        Assert.Equal("user@example.test", message.To.Mailboxes.Single().Address);
        Assert.Equal("noreply@example.test", message.From.Mailboxes.Single().Address);
        Assert.Equal(Subject(template), message.Subject);
        Assert.Contains("https://identity.example.test" + path, message.TextBody);
        Assert.Contains("code=secret&amp;userId=123", message.HtmlBody);
        Assert.Contains(expiry, message.TextBody);
        Assert.DoesNotContain("{{link}}", message.TextBody + message.HtmlBody);
        Assert.False(string.IsNullOrEmpty(message.MessageId));
        publisher.Fail = true;
        await Assert.ThrowsAsync<MailDeliveryException>(() => sender.SendConfirmationAsync("user@example.test", "User", "/Account/ConfirmEmail", default));
        await Assert.ThrowsAsync<MailDeliveryException>(() => sender.SendPasswordResetAsync("user@example.test", "User", "/Account/ResetPassword", default));
    }

    [Theory]
    [InlineData("http://identity.example.test")]
    [InlineData("https://identity.example.test/?bad=yes")]
    [InlineData("https://user:password@identity.example.test/")]
    public void MailConfigurationRejectsUnsafePublicUrls(string origin)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddAisRegistration();
        services.Configure<EmailSettings>(options => { options.AppUrl = origin; options.SenderEmail = "noreply@example.test"; });
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EmailSettings>>().Value);
    }

    [RabbitFact]
    public async Task RabbitPublicationIsPersistentCompatibleAndRetriesAnInitiallyFailedConnection()
    {
        var host = Environment.GetEnvironmentVariable("AIS3_TEST_RABBITMQ_HOST")!;
        var username = Environment.GetEnvironmentVariable("AIS3_TEST_RABBITMQ_USERNAME") ?? "guest";
        var password = Environment.GetEnvironmentVariable("AIS3_TEST_RABBITMQ_PASSWORD") ?? "guest";
        var queue = "ais3_step5_test_" + Guid.NewGuid().ToString("N");
        var options = new RabbitMqSettings { HostName = host, UserName = username, Password = password, QueueName = queue };
        await using var connection = await new ConnectionFactory { HostName = host, UserName = username, Password = password }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await using var publisher = new RabbitMailPublisher(Options.Create(options));
        try
        {
            options.Port = 1; options.PublishTimeoutSeconds = 1;
            await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(new byte[] { 1 }, default));
            options.Port = 5672; options.PublishTimeoutSeconds = 10;
            var sender = new AccountEmailSender(Options.Create(new EmailSettings { SenderEmail = "noreply@example.test" }), publisher, Templates(),
                NullLogger<AccountEmailSender>.Instance);
            await sender.SendConfirmationAsync("user@example.test", "Test", "/Account/ConfirmEmail?code=test", default);
            var delivery = await channel.BasicGetAsync(queue, autoAck: true);
            Assert.NotNull(delivery);
            Assert.True(delivery.BasicProperties.Persistent);
            Assert.Equal("application/x-msgpack", delivery.BasicProperties.ContentType);
            var messages = MessagePackSerializer.Deserialize<List<byte[]>>(delivery.Body);
            using var message = await MimeMessage.LoadAsync(new MemoryStream(Assert.Single(messages)));
            Assert.Contains("/Account/ConfirmEmail?code=test", message.TextBody);
            Assert.Equal("user@example.test", message.To.Mailboxes.Single().Address);
            await sender.SendPasswordResetAsync("user@example.test", "Test", "/Account/ResetPassword?code=test", default);
            var resetDelivery = await channel.BasicGetAsync(queue, autoAck: true);
            Assert.NotNull(resetDelivery);
            Assert.True(resetDelivery.BasicProperties.Persistent);
            using var resetMessage = await MimeMessage.LoadAsync(new MemoryStream(Assert.Single(
                MessagePackSerializer.Deserialize<List<byte[]>>(resetDelivery.Body))));
            Assert.Equal(Subject("ResetPassword"), resetMessage.Subject);
            Assert.Contains("/Account/ResetPassword?code=test", resetMessage.TextBody);
            Assert.Null(await channel.BasicGetAsync(queue, autoAck: true));
        }
        finally { await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false); }
    }
}
