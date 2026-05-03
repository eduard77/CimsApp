using System.Threading.Channels;

namespace CimsApp.Services.Email;

/// <summary>
/// In-memory bounded channel for outbound emails. T-S14-03 /
/// PAFM-SD F.14 second bullet. Producer side: <see cref="Enqueue"/>
/// from any service that wants to send mail without blocking the
/// request thread. Consumer side: <see cref="EmailDispatcherHostedService"/>
/// drains via <see cref="Reader"/>.
///
/// **Restart loses pending mail by design** (Decision 2 in the
/// S14 kickoff doc). Persistent queue with retry/backoff is v1.1
/// / B-091. The unblock condition is "first incident where
/// missed restart-time email caused user-visible regression OR
/// pilot user requests delivery audit trail".
/// </summary>
public sealed class EmailQueue
{
    private readonly Channel<EmailMessage> _channel;

    public EmailQueue()
    {
        _channel = Channel.CreateBounded<EmailMessage>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    public ChannelReader<EmailMessage> Reader => _channel.Reader;

    public bool Enqueue(EmailMessage message)
        => _channel.Writer.TryWrite(message);
}
