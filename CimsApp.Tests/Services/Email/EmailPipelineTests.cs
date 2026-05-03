using CimsApp.Services.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CimsApp.Tests.Services.Email;

/// <summary>
/// T-S14-03 email pipeline behavioural tests. Bounded channel +
/// dispatcher + sender wiring. SMTP itself is not tested (would
/// need a fake server); the contract is: queued message → sender
/// SendAsync invoked exactly once.
/// </summary>
public class EmailPipelineTests
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Dispatcher_drains_queue_through_sender()
    {
        var queue = new EmailQueue();
        var sender = new RecordingSender();
        var services = new ServiceCollection();
        services.AddScoped<IEmailSender>(_ => sender);
        var sp = services.BuildServiceProvider();
        var dispatcher = new EmailDispatcherHostedService(queue, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmailDispatcherHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        var run = dispatcher.StartAsync(cts.Token);
        await run;

        Assert.True(queue.Enqueue(new EmailMessage("a@b.com", null, "S", "B")));
        Assert.True(queue.Enqueue(new EmailMessage("c@d.com", "C D", "S2", "B2")));

        // Give the background loop a moment.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (sender.Sent.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        cts.Cancel();
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("a@b.com", sender.Sent[0].ToAddress);
        Assert.Equal("c@d.com", sender.Sent[1].ToAddress);
    }

    [Fact]
    public async Task NoopEmailSender_completes_without_error()
    {
        var sender = new NoopEmailSender();
        await sender.SendAsync(new EmailMessage("a@b.com", null, "S", "B"));
        // No assertion; the contract is "do not throw, do not block".
    }

    [Fact]
    public async Task SmtpEmailSender_with_Enabled_false_short_circuits()
    {
        var opts = Options.Create(new EmailOptions { Enabled = false });
        var sender = new SmtpEmailSender(opts, NullLogger<SmtpEmailSender>.Instance);
        // Should not throw even with no Smtp config.
        await sender.SendAsync(new EmailMessage("a@b.com", null, "S", "B"));
    }

    [Fact]
    public async Task SmtpEmailSender_with_missing_host_short_circuits_with_warning()
    {
        var opts = Options.Create(new EmailOptions
        {
            Enabled = true,
            Smtp = new SmtpOptions { Host = "" },
        });
        var sender = new SmtpEmailSender(opts, NullLogger<SmtpEmailSender>.Instance);
        await sender.SendAsync(new EmailMessage("a@b.com", null, "S", "B"));
    }
}
