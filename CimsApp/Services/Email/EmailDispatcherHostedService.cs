using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CimsApp.Services.Email;

/// <summary>
/// Background drain for <see cref="EmailQueue"/>. T-S14-03.
/// Singleton; reads <see cref="EmailQueue.Reader"/> in a loop and
/// hands each message to <see cref="IEmailSender"/>. The sender is
/// resolved per-iteration via <see cref="IServiceScopeFactory"/> to
/// honour the scoped lifetime of any DI dependencies (e.g. an SMTP
/// client wrapper that takes scoped configuration). Failures are
/// caught and logged so a single bad message can't kill the
/// dispatcher.
/// </summary>
public sealed class EmailDispatcherHostedService(
    EmailQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<EmailDispatcherHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                await sender.SendAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error dispatching email to {To}", message.ToAddress);
            }
        }
    }
}
