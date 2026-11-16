using MessagePack;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;

namespace AliceIdentityService.Services;

public sealed class EmailSettings
{
    public string AppUrl { get; set; } = "https://localhost:7443/";
    public string SenderName { get; set; } = "Alice Identity Service";
    public string SenderEmail { get; set; } = string.Empty;
}

public interface IAccountEmailSender
{
    Task SendConfirmationAsync(string email, string name, string confirmationPath, CancellationToken cancellationToken);
    Task SendPasswordResetAsync(string email, string name, string resetPath, CancellationToken cancellationToken);
}

public interface IMailPublisher
{
    Task PublishAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

public sealed class MailDeliveryException : Exception
{
    public MailDeliveryException() : base("The account email publication was not confirmed.") { }
}

public sealed class AccountEmailSender(IOptions<EmailSettings> settings, IMailPublisher publisher, EmailTemplates templates,
    ILogger<AccountEmailSender> logger) : IAccountEmailSender
{
    public Task SendConfirmationAsync(string email, string name, string confirmationPath, CancellationToken cancellationToken) =>
        SendAsync("EmailVerification", email, name, confirmationPath, cancellationToken);

    public Task SendPasswordResetAsync(string email, string name, string resetPath, CancellationToken cancellationToken) =>
        SendAsync("ResetPassword", email, name, resetPath, cancellationToken);

    private async Task SendAsync(string template, string email, string name, string path, CancellationToken cancellationToken)
    {
        var kind = template == "EmailVerification" ? "confirmation" : "password-reset";
        var stage = "configuration";
        try
        {
            var options = settings.Value;
            // A configured public URL, never the request Host or a client-supplied return URL.
            var link = new Uri(new Uri(options.AppUrl), path).AbsoluteUri;
            stage = "template";
            var content = await templates.RenderAsync(template, link, cancellationToken);
            stage = "message";
            using var message = new MimeMessage();
            message.From.Add(new MailboxAddress(options.SenderName, options.SenderEmail));
            message.To.Add(new MailboxAddress(name, email));
            message.Subject = content.Subject;
            message.MessageId = MimeUtils.GenerateMessageId();
            message.Body = new BodyBuilder
            {
                TextBody = content.TextBody,
                HtmlBody = content.HtmlBody
            }.ToMessageBody();
            using var stream = new MemoryStream();
            await message.WriteToAsync(stream, cancellationToken);
            // AliceMailService consumes MessagePack List<byte[]> containing serialized MIME messages.
            var body = MessagePackSerializer.Serialize(new List<byte[]> { stream.ToArray() }, cancellationToken: cancellationToken);
            stage = "broker";
            await publisher.PublishAsync(body, cancellationToken);
            logger.LogInformation("Account email {MailKind} broker-confirmed. Message ID: {MailMessageId}", kind, message.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Account email {MailKind} cancelled during {MailStage}", kind, stage);
            throw;
        }
        catch (Exception error)
        {
            // Exception messages, recipients, and links can contain secrets. Log the type only.
            logger.LogWarning("Account email {MailKind} not broker-confirmed during {MailStage}. Failure type: {FailureType}", kind, stage, error.GetType().Name);
            throw new MailDeliveryException();
        }
    }
}
