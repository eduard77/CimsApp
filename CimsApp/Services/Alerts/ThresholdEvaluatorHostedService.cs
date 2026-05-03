using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services.Email;
using CimsApp.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CimsApp.Services.Alerts;

/// <summary>
/// Background loop that walks every active <see cref="AlertRule"/>
/// across all tenants on a configurable interval. T-S14-04.
///
/// Tenancy: this service runs OUTSIDE the request scope, so it
/// cannot use the request-scoped <see cref="Tenancy.ITenantContext"/>.
/// It explicitly bypasses the global query filter via
/// <c>IgnoreQueryFilters()</c> on every read; writes set
/// concrete tenant-bound fields (<c>RuleId</c>, <c>RecipientUserId</c>)
/// so audit attribution still works.
///
/// Default tick: <c>Alerts:TickIntervalSeconds</c> (default 60).
/// </summary>
public sealed class ThresholdEvaluatorHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ThresholdEvaluatorHostedService> logger,
    IConfiguration config) : BackgroundService
{
    private readonly TimeSpan _tickInterval =
        TimeSpan.FromSeconds(config.GetValue("Alerts:TickIntervalSeconds", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so app startup / migration doesn't race.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Threshold evaluator tick failed");
            }

            try { await Task.Delay(_tickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CimsDbContext>();
        var pusher = scope.ServiceProvider.GetRequiredService<INotificationPusher>();
        var queue = scope.ServiceProvider.GetRequiredService<EmailQueue>();

        var rules = await db.AlertRules
            .IgnoreQueryFilters()
            .Where(r => r.IsActive)
            .Include(r => r.Project)
            .Include(r => r.RecipientUser)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var rule in rules)
        {
            try
            {
                var value = await AlertMetricComputer.ComputeAsync(db, rule.Metric, rule.ProjectId, ct);
                rule.LastObservedValue = value;
                rule.LastObservedAt = now;

                var breached = ThresholdRule.IsBreached(value, rule.Comparison, rule.Threshold);
                if (breached && !ThresholdRule.IsInCooldown(rule.LastFiredAt, rule.CooldownMinutes, now))
                {
                    await FireAsync(rule, value, pusher, queue, ct);
                    rule.LastFiredAt = now;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed evaluating AlertRule {RuleId}", rule.Id);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task FireAsync(
        AlertRule rule, decimal observed,
        INotificationPusher pusher, EmailQueue queue,
        CancellationToken ct)
    {
        var title = $"Alert: {rule.Title}";
        var body = $"Project '{rule.Project.Name}' — {rule.Metric} is {observed:0.##} "
                 + $"({rule.Comparison} {rule.Threshold:0.##}).";
        var link = $"/projects/{rule.ProjectId}/alert-rules/{rule.Id}";

        await pusher.PushAsync(rule.RecipientUserId,
            type: "alert.threshold", title: title, body: body, link: link, ct: ct);

        if (!string.IsNullOrWhiteSpace(rule.RecipientUser.Email))
        {
            queue.Enqueue(new EmailMessage(
                ToAddress: rule.RecipientUser.Email,
                ToName: $"{rule.RecipientUser.FirstName} {rule.RecipientUser.LastName}",
                Subject: title,
                Body: body));
        }
    }
}
