using AliceIdentityService.Models;
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
    Task SendConfirmationAsync(User user, string confirmationPath, CancellationToken cancellationToken);
    Task SendPasswordResetAsync(User user, string resetPath, CancellationToken cancellationToken);
}

public interface IMailPublisher
{
    // Returns the destination queue after broker confirmation.
    Task<string> PublishAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

public sealed class MailDeliveryException : Exception
{
    public MailDeliveryException() : base("The account email publication was not confirmed.") { }
}

public sealed class AccountEmailSender(IOptions<EmailSettings> settings, IMailPublisher publisher, EmailTemplates templates,
    ILogger<AccountEmailSender> logger) : IAccountEmailSender
{
    public Task SendConfirmationAsync(User user, string confirmationPath, CancellationToken cancellationToken) =>
        SendAsync("EmailVerification", "Email confirmation", user, confirmationPath, cancellationToken);

    public Task SendPasswordResetAsync(User user, string resetPath, CancellationToken cancellationToken) =>
        SendAsync("ResetPassword", "Password reset", user, resetPath, cancellationToken);

    private async Task SendAsync(string template, string kind, User user, string path, CancellationToken cancellationToken)
    {
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
            message.To.Add(new MailboxAddress(user.FullName, user.Email ?? throw new InvalidOperationException("Account email is missing.")));
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
            var queue = await publisher.PublishAsync(body, cancellationToken);
            logger.LogInformation("{MailKind} email published to {QueueName} for account {UserId} (message {MailMessageId})",
                kind, OperationLog.Text(queue), user.Id, message.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{MailKind} email publication for account {UserId} cancelled during {MailStage}", kind, user.Id, stage);
            throw;
        }
        catch (Exception error)
        {
            // Exception messages, recipients, and links can contain secrets. Log the type only.
            logger.LogWarning("{MailKind} email publication for account {UserId} not confirmed during {MailStage} ({FailureType})",
                kind, user.Id, stage, error.GetType().Name);
            throw new MailDeliveryException();
        }
    }
}
