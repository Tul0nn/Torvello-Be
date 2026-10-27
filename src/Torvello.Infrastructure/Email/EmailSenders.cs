using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Torvello.Application.Common;

namespace Torvello.Infrastructure.Email;

/// <summary>Development sender: writes each email to a text file instead of sending it.</summary>
public sealed class PickupEmailSender(IOptions<EmailOptions> options, ILogger<PickupEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(options.Value.PickupDirectory);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, $"To: {message.To}\nSubject: {message.Subject}\n\n{message.TextBody}\n", cancellationToken);
        logger.LogInformation("Email \"{Subject}\" written to {File}", message.Subject, file);
    }
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.TextBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, SecureSocketOptions.StartTls, cancellationToken);
        if (!string.IsNullOrEmpty(settings.SmtpUser))
        {
            await client.AuthenticateAsync(settings.SmtpUser, settings.SmtpPassword, cancellationToken);
        }
        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
