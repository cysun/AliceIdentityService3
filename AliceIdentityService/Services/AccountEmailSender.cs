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
}

public interface IMailPublisher
{
    Task PublishAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

public sealed class MailDeliveryException : Exception
{
    public MailDeliveryException() : base("The confirmation email could not be queued.") { }
}

public sealed class AccountEmailSender(IOptions<EmailSettings> settings, IMailPublisher publisher, EmailTemplates templates,
    ILogger<AccountEmailSender> logger) : IAccountEmailSender
{
    public async Task SendConfirmationAsync(string email, string name, string confirmationPath, CancellationToken cancellationToken)
    {
        try
        {
            var options = settings.Value;
            // A configured public URL, never the request Host or a client-supplied return URL.
            var link = new Uri(new Uri(options.AppUrl), confirmationPath).AbsoluteUri;
            var content = await templates.RenderAsync("EmailVerification", link, cancellationToken);
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
            await publisher.PublishAsync(body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Exception messages, recipients, and links can contain secrets. Log the type only.
            logger.LogWarning("Confirmation email was not queued. Failure type: {FailureType}", error.GetType().Name);
            throw new MailDeliveryException();
        }
    }
}
