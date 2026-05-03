namespace CimsApp.Services.Email;

/// <summary>
/// Outbound email port. T-S14-03 / PAFM-SD F.14 second bullet.
/// Implementations:
///   - <see cref="SmtpEmailSender"/> — production
///     <c>System.Net.Mail.SmtpClient</c> shape; credentials read
///     from configuration <c>Email:Smtp</c>.
///   - <see cref="NoopEmailSender"/> — used when
///     <c>Email:Enabled = false</c> (default in tests / development).
/// Persistent queue with retry / audit trail is v1.1 / B-091.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed record EmailMessage(
    string ToAddress,
    string? ToName,
    string Subject,
    string Body,
    bool IsHtml = false);
