using System.Text;

namespace FastGrep.Core;

public sealed class TextReaderService
{
    public async Task<string> ReadHeadAsync(string file, int maxBytes, string encodingName = "utf-8", CancellationToken token = default)
        => await ReadRangeAsync(file, 0, maxBytes, encodingName, alignStartToLine: false, token);

    public async Task<string> ReadTailAsync(string file, int maxBytes, string encodingName = "utf-8", CancellationToken token = default)
    {
        var length = new FileInfo(file).Length;
        return await ReadRangeAsync(file, Math.Max(0, length - maxBytes), maxBytes, encodingName, alignStartToLine: length > maxBytes, token);
    }

    public async Task<string> ReadRangeAsync(string file, long byteOffset, int maxBytes, string encodingName = "utf-8",
        bool alignStartToLine = true, CancellationToken token = default)
    {
        if (byteOffset < 0 || maxBytes < 1) throw new ArgumentOutOfRangeException();
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (byteOffset > stream.Length) throw new ArgumentOutOfRangeException(nameof(byteOffset));
        stream.Position = byteOffset;
        if (alignStartToLine && byteOffset > 0)
        {
            var one = new byte[1];
            while (stream.Position < stream.Length)
            {
                if (await stream.ReadAsync(one, token) == 0 || one[0] == (byte)'\n') break;
            }
        }
        var buffer = new byte[Math.Min(maxBytes, (int)Math.Min(int.MaxValue, stream.Length - stream.Position))];
        var read = await stream.ReadAsync(buffer, token);
        var encoding = Encodings.Get(encodingName);
        return encoding.GetString(buffer, 0, read);
    }
}
