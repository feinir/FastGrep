namespace FastGrep.Core;

public readonly record struct SearchRuntimeState(
    int ReadLimit,
    int ComputeLimit,
    int ActiveReaders,
    int ActiveComputations,
    int QueuedBlocks);

/// <summary>
/// Thread-safe runtime controls and observable state for an active search pipeline.
/// Reducing a limit takes effect when an in-flight file or compute block reaches its
/// next safe boundary; increasing a limit wakes the scheduler immediately.
/// </summary>
public sealed class SearchConcurrencyController
{
    public const int MaximumConcurrency = 64;

    private readonly object _sync = new();
    private int _readLimit;
    private int _computeLimit;
    private int _activeReaders;
    private int _activeComputations;
    private int _queuedBlocks;
    private int _queuedBlockPeak;
    private long _version;
    private TaskCompletionSource<bool> _changed = NewSignal();

    public SearchConcurrencyController(int readLimit, int computeLimit)
    {
        _readLimit = Math.Clamp(readLimit, 1, MaximumConcurrency);
        _computeLimit = Math.Clamp(computeLimit, 1, MaximumConcurrency);
    }

    public event Action<SearchRuntimeState>? StateChanged;

    public int ReadLimit { get { lock (_sync) return _readLimit; } }
    public int ComputeLimit { get { lock (_sync) return _computeLimit; } }
    public SearchRuntimeState State { get { lock (_sync) return Snapshot(); } }

    /// <summary>
    /// Returns the highest real queue depth observed since the previous sample. This keeps a
    /// fast enqueue/dequeue cycle visible to a slower UI timer without changing scheduling.
    /// </summary>
    public int TakeQueuedBlockPeak()
    {
        lock (_sync)
        {
            var peak = Math.Max(_queuedBlocks, _queuedBlockPeak);
            _queuedBlockPeak = _queuedBlocks;
            return peak;
        }
    }

    public int AdjustReadLimit(int delta) => SetReadLimit(ReadLimit + delta);
    public int AdjustComputeLimit(int delta) => SetComputeLimit(ComputeLimit + delta);

    public int SetReadLimit(int value)
    {
        SearchRuntimeState state;
        TaskCompletionSource<bool>? signal = null;
        lock (_sync)
        {
            var next = Math.Clamp(value, 1, MaximumConcurrency);
            if (next == _readLimit) return _readLimit;
            _readLimit = next;
            signal = AdvanceVersion();
            state = Snapshot();
        }
        signal.TrySetResult(true);
        StateChanged?.Invoke(state);
        return state.ReadLimit;
    }

    public int SetComputeLimit(int value)
    {
        SearchRuntimeState state;
        TaskCompletionSource<bool>? signal = null;
        lock (_sync)
        {
            var next = Math.Clamp(value, 1, MaximumConcurrency);
            if (next == _computeLimit) return _computeLimit;
            _computeLimit = next;
            signal = AdvanceVersion();
            state = Snapshot();
        }
        signal.TrySetResult(true);
        StateChanged?.Invoke(state);
        return state.ComputeLimit;
    }

    internal long Version { get { lock (_sync) return _version; } }

    internal Task WaitForChangeAsync(long observedVersion, CancellationToken token)
    {
        Task task;
        lock (_sync)
        {
            if (_version != observedVersion) return Task.CompletedTask;
            task = _changed.Task;
        }
        return task.WaitAsync(token);
    }

    internal void ReaderStarted() => UpdateState(activeReadersDelta: 1);
    internal void ReaderStopped() => UpdateState(activeReadersDelta: -1);
    internal void ComputationStarted() => UpdateState(activeComputationsDelta: 1);
    internal void ComputationStopped() => UpdateState(activeComputationsDelta: -1);
    internal void BlockQueued() => UpdateState(queuedBlocksDelta: 1);
    internal void BlockDequeued() => UpdateState(queuedBlocksDelta: -1);

    private void UpdateState(int activeReadersDelta = 0, int activeComputationsDelta = 0, int queuedBlocksDelta = 0)
    {
        SearchRuntimeState state;
        lock (_sync)
        {
            _activeReaders = Math.Max(0, _activeReaders + activeReadersDelta);
            _activeComputations = Math.Max(0, _activeComputations + activeComputationsDelta);
            _queuedBlocks = Math.Max(0, _queuedBlocks + queuedBlocksDelta);
            if (_queuedBlocks > _queuedBlockPeak) _queuedBlockPeak = _queuedBlocks;
            state = Snapshot();
        }
        StateChanged?.Invoke(state);
    }

    private TaskCompletionSource<bool> AdvanceVersion()
    {
        var previous = _changed;
        _changed = NewSignal();
        _version++;
        return previous;
    }

    private SearchRuntimeState Snapshot() => new(
        _readLimit, _computeLimit, _activeReaders, _activeComputations, _queuedBlocks);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
