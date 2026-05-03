using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CimsApp.Services.Email;

/// <summary>
/// SMTP implementation backed by <see cref="System.Net.Mail.SmtpClient"/>
/// (built into the framework — no NuGet package, so no Ch 24.6 24-hour
/// wait). Configuration read from <c>Email:Smtp</c>:
/// <c>Host</c>, <c>Port</c>, <c>UseSsl</c>, <c>Username</c>,
/// <c>Password</c>, <c>FromAddress</c>, <c>FromName</c>.
/// Failures are logged but not rethrown — the dispatcher decides
/// whether to retry. Persistent queue / audit-trail of attempts
/// is v1.1 / B-091.
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<EmailOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _opts = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!_opts.Enabled)
        {
            logger.LogInformation("Email disabled by config — skipping send to {To}", message.ToAddress);
            return;
        }
        if (string.IsNullOrWhiteSpace(_opts.Smtp?.Host))
        {
            logger.LogWarning("Email enabled but Email:Smtp:Host not configured — skipping send to {To}", message.ToAddress);
            return;
        }

        using var client = new SmtpClient(_opts.Smtp.Host, _opts.Smtp.Port)
        {
            EnableSsl = _opts.Smtp.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = string.IsNullOrEmpty(_opts.Smtp.Username)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(_opts.Smtp.Username, _opts.Smtp.Password),
        };
        var msg = new MailMessage
        {
            From = new MailAddress(_opts.Smtp.FromAddress, _opts.Smtp.FromName),
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = message.IsHtml,
        };
        msg.To.Add(string.IsNullOrEmpty(message.ToName)
            ? new MailAddress(message.ToAddress)
            : new MailAddress(message.ToAddress, message.ToName));

        try
        {
            await client.SendMailAsync(msg, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMTP send failed to {To} (subject: {Subject})",
                message.ToAddress, message.Subject);
        }
    }
}

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public SmtpOptions? Smtp { get; set; }
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "no-reply@cims.local";
    public string FromName { get; set; } = "CIMS";
}

/// <summary>
/// No-op sender used when <c>Email:Enabled</c> is false. Drains
/// the queue without doing anything. Tests use a recording sender
/// instead (<see cref="IEmailSender"/> implementation in tests).
/// </summary>
public sealed class NoopEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        => Task.CompletedTask;
}
