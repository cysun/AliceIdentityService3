using System.Threading.RateLimiting;
using AliceIdentityService.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AliceIdentityService.Services;

public sealed class EmailConfirmationTokenOptions : DataProtectionTokenProviderOptions
{
    public EmailConfirmationTokenOptions()
    {
        Name = "AIS3.EmailConfirmation.v1";
        TokenLifespan = TimeSpan.FromHours(24);
    }
}

public sealed class EmailConfirmationTokenProvider(IDataProtectionProvider protection,
    IOptions<EmailConfirmationTokenOptions> options, ILogger<DataProtectorTokenProvider<User>> logger)
    : DataProtectorTokenProvider<User>(protection, options, logger);

public static class RegistrationExtensions
{
    public static IServiceCollection AddAisRegistration(this IServiceCollection services)
    {
        services.AddDataProtection();
        new IdentityBuilder(typeof(User), services).AddTokenProvider<EmailConfirmationTokenProvider>("AIS3.EmailConfirmation");
        services.Configure<IdentityOptions>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Tokens.EmailConfirmationTokenProvider = "AIS3.EmailConfirmation";
        });
        services.AddOptions<EmailSettings>().BindConfiguration("Email")
            .Validate(options => Uri.TryCreate(options.AppUrl, UriKind.Absolute, out var url) &&
                url.Scheme == "https" && url.AbsolutePath == "/" && string.IsNullOrEmpty(url.Query) &&
                string.IsNullOrEmpty(url.Fragment) && string.IsNullOrEmpty(url.UserInfo), "Email:AppUrl must be an HTTPS origin.")
            .Validate(options => MailboxAddress.TryParse(options.SenderEmail, out var address) &&
                address.Address == options.SenderEmail && !string.IsNullOrWhiteSpace(options.SenderName), "Configure Email:SenderEmail and SenderName.");
        services.AddOptions<RabbitMqSettings>().BindConfiguration("RabbitMQ")
            .Validate(options => !string.IsNullOrWhiteSpace(options.HostName) && !string.IsNullOrWhiteSpace(options.QueueName) &&
                options.Port is > 0 and <= 65535 && options.PublishTimeoutSeconds is >= 1 and <= 60, "Invalid RabbitMQ host, queue, port, or timeout.");
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ConfirmationEmailLimit>();
        services.AddSingleton<IMailPublisher, RabbitMailPublisher>();
        services.AddSingleton(provider => new EmailTemplates(Path.Combine(
            provider.GetRequiredService<IHostEnvironment>().ContentRootPath, "EmailTemplates")));
        services.AddScoped<IAccountEmailSender, AccountEmailSender>();
        services.AddScoped<RegistrationService>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("account-email", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0
                }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "900";
                await context.HttpContext.Response.WriteAsync("Too many account requests. Please wait before trying again.", cancellationToken);
            };
        });
        return services;
    }
}
