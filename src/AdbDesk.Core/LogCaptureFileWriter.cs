using System.Text;

namespace AdbDesk.Core;

/// <summary>Append-only UTF-8 segments; rotation never deletes or overwrites older segments.</summary>
public sealed class LogCaptureFileWriter : IDisposable
{
    private readonly string _folder;
    private readonly long _limit;
    private FileStream? _stream;
    private int _index;
    private string _pending = "";
    private bool _disposed;
    public long BytesWritten { get; private set; }
    public int FileCount => _index;

    public LogCaptureFileWriter(string folder, long rotationBytes = 20 * 1024 * 1024)
    {
        if (rotationBytes < 4) throw new ArgumentOutOfRangeException(nameof(rotationBytes));
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = folder; _limit = rotationBytes;
        Directory.CreateDirectory(folder);
        NextFile();
    }
    public void Write(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        text = _pending + text; _pending = "";
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) { _pending = text[^1..]; text = text[..^1]; }
        WriteBytes(Encoding.UTF8.GetBytes(text));
    }
    private void WriteBytes(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (_stream!.Length >= _limit) NextFile();
            var count = (int)Math.Min(bytes.Length - offset, _limit - _stream!.Length);
            // Keep each UTF-8 codepoint in one file, allowing at most 3 extra bytes.
            while (offset + count < bytes.Length && (bytes[offset + count] & 0xc0) == 0x80) count++;
            _stream.Write(bytes, offset, count); offset += count; BytesWritten += count;
        }
        _stream!.Flush();
    }
    private void NextFile()
    {
        _stream?.Dispose();
        _stream = new FileStream(Path.Combine(_folder, $"logcat-{++_index:D6}.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
    public void Dispose()
    {
        if (_disposed) return;
        try { if (_pending.Length > 0) WriteBytes(Encoding.UTF8.GetBytes(_pending)); }
        finally { _disposed = true; _stream?.Dispose(); }
    }
}
