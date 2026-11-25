using System.Text;

namespace FastGrep.Core;

internal sealed class AtomicOutput : IAsyncDisposable
{
    private readonly string _destination;
    private readonly string _temporary;
    private bool _committed;
    public StreamWriter Writer { get; }

    private AtomicOutput(string destination, string temporary, StreamWriter writer)
    {
        _destination = destination;
        _temporary = temporary;
        Writer = writer;
    }

    public static AtomicOutput Create(string destination, Encoding encoding, bool append = false, int bufferSize = 1024 * 1024)
    {
        var full = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + $".{Guid.NewGuid():N}.tmp";
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var writer = new StreamWriter(stream, encoding, bufferSize, leaveOpen: false);
        try
        {
            if (append && File.Exists(full))
            {
                using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
                input.CopyTo(stream, bufferSize);
            }
            return new AtomicOutput(full, temporary, writer);
        }
        catch
        {
            try { writer.Dispose(); }
            finally { File.Delete(temporary); }
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default, Action? beforeReplace = null)
    {
        await Writer.FlushAsync(cancellationToken);
        await Writer.DisposeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        beforeReplace?.Invoke();
        File.Move(_temporary, _destination, true);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            try { await Writer.DisposeAsync(); }
            finally
            {
                try { File.Delete(_temporary); } catch { }
            }
        }
    }
}
