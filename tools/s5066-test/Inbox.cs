namespace M0LTE.S5066.TestClient;

/// <summary>Things that arrived, kept until a step takes the one it waits for.</summary>
internal sealed class Inbox<T>
{
    private readonly List<T> _items = [];
    private readonly object _gate = new();
    private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Adds an item and wakes any waiter.</summary>
    public void Add(T item)
    {
        TaskCompletionSource pulse;
        lock (_gate)
        {
            _items.Add(item);
            pulse = _pulse;
            _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        pulse.TrySetResult();
    }

    /// <summary>Removes and returns the oldest item that matches, waiting for one if none has arrived.</summary>
    public async Task<T> TakeAsync(Func<T, bool> match, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                int i = _items.FindIndex(x => match(x));
                if (i >= 0)
                {
                    T item = _items[i];
                    _items.RemoveAt(i);
                    return item;
                }

                wait = _pulse.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
