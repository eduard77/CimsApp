using CimsApp.Services.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace CimsApp.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="IHubContext{T}"/> stand-in for tests.
/// Records every <c>Clients.Group(...).SendAsync(method, arg, ct)</c>
/// invocation as a tuple in <see cref="Sends"/>. Only the surface
/// area used by <see cref="NotificationPusher"/> is implemented.
/// </summary>
public sealed class FakeHubContext : IHubContext<NotificationsHub>
{
    public List<(string Group, string Method, object? Arg)> Sends { get; } = new();

    public IHubClients Clients { get; }
    public IGroupManager Groups { get; } = new NoopGroups();

    public FakeHubContext() { Clients = new FakeClients(this); }

    private sealed class FakeClients(FakeHubContext owner) : IHubClients
    {
        public IClientProxy All => new NoopProxy();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new NoopProxy();
        public IClientProxy Client(string connectionId) => new NoopProxy();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new NoopProxy();
        public IClientProxy Group(string groupName) => new RecordingProxy(owner, groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new NoopProxy();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new NoopProxy();
        public IClientProxy User(string userId) => new NoopProxy();
        public IClientProxy Users(IReadOnlyList<string> userIds) => new NoopProxy();
    }

    private sealed class RecordingProxy(FakeHubContext owner, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            owner.Sends.Add((group, method, args.Length > 0 ? args[0] : null));
            return Task.CompletedTask;
        }
    }

    private sealed class NoopProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoopGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
