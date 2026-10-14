using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace AdbDesk.Core;

/// <summary>Acquire/dispose on the same thread. The callback runs on a pipe worker thread.</summary>
public sealed class SingleInstanceGate : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    private int _disposed;

    private SingleInstanceGate(Mutex mutex, string key, Action activate)
    {
        _mutex = mutex;
        // Create the first listening handle before publishing primary ownership to the caller.
        _listener = ListenAsync(key, activate, CreateServer(key));
    }

    public static SingleInstanceGate? TryAcquire(string name, Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        var key = Key(name);
        var mutex = new Mutex(false, "Local\\" + key);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new SingleInstanceGate(mutex, key, activate);
            mutex.Dispose();
            return null;
        }
        catch
        {
            if (acquired) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public static async Task<bool> NotifyAsync(string name, CancellationToken cancellationToken = default,
        Action<int>? allowForeground = null)
    {
        using var pipe = new NamedPipeClientStream(".", Key(name), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var ownerBytes = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(ownerBytes, timeout.Token).ConfigureAwait(false);
            var ownerPid = BitConverter.ToInt32(ownerBytes);
            if (ownerPid <= 0) return false;
            allowForeground?.Invoke(ownerPid);
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, timeout.Token).ConfigureAwait(false);
            return acknowledgement[0] == 1;
        }
        catch (IOException) { return false; }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static NamedPipeServerStream CreateServer(string key) => new(key, PipeDirection.InOut,
        1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(string key, Action activate, NamedPipeServerStream server)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using (server)
                {
                    try
                    {
                        await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await server.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), readTimeout.Token).ConfigureAwait(false);
                        var data = new byte[1];
                        if (await server.ReadAsync(data, readTimeout.Token).ConfigureAwait(false) == 1 && data[0] == 1)
                        {
                            activate();
                            await server.WriteAsync(data, readTimeout.Token).ConfigureAwait(false);
                        }
                    }
                    catch (IOException) { }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { }
                }
                if (_stop.IsCancellationRequested) break;
                server = CreateServer(key);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { server.Dispose(); }
    }

    private static string Key(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return "AdbDesk-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _listener.GetAwaiter().GetResult(); }
        finally
        {
            _stop.Dispose();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
