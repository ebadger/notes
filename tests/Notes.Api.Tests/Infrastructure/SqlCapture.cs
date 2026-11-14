using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Notes.Api.Tests.Infrastructure;

internal sealed class SqlCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly IDisposable listenerSubscription;
    private readonly List<IDisposable> eventSubscriptions = [];
    private readonly ConcurrentQueue<string> commands = [];

    public SqlCapture()
    {
        listenerSubscription = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public IReadOnlyCollection<string> Commands => commands.ToArray();
    public Exception? Error { get; private set; }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.EntityFrameworkCore")
        {
            eventSubscriptions.Add(listener.Subscribe(this));
        }
    }

    public void OnNext(KeyValuePair<string, object?> diagnosticEvent)
    {
        if (diagnosticEvent.Key == RelationalEventId.CommandExecuting.Name &&
            diagnosticEvent.Value is CommandEventData command)
        {
            commands.Enqueue(command.Command.CommandText);
        }
    }

    public void OnError(Exception error) => Error = error;

    public void OnCompleted()
    {
    }

    public void Dispose()
    {
        listenerSubscription.Dispose();
        foreach (var subscription in eventSubscriptions)
        {
            subscription.Dispose();
        }
    }
}
