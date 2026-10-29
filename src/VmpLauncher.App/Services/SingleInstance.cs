using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace VmpLauncher.App.Services;

/// <summary>
/// One launcher per data directory. The first instance owns a named mutex and listens on a named pipe;
/// later instances forward their activation argument (vmpack:// link or .vmpack path) and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _ = Task.Run(ListenAsync);
    }

    public event Action<string?>? ActivationReceived;

    public static SingleInstance? TryAcquire(string dataRoot)
    {
        var key = Key(dataRoot);
        var mutex = new Mutex(initiallyOwned: true, $"Local\\VmpLauncher.{key}", out var created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, $"VmpLauncher.Activate.{key}");
    }

    public static void ForwardActivation(string dataRoot, string? activation)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", $"VmpLauncher.Activate.{Key(dataRoot)}", PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(activation ?? string.Empty);
            writer.Flush();
        }
        catch (Exception)
        {
            // The first instance may be shutting down; nothing more we can do.
        }
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
                ActivationReceived?.Invoke(string.IsNullOrWhiteSpace(text) ? null : text);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // client dropped; keep listening
            }
        }
    }

    private static string Key(string dataRoot)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _cts.Dispose();
    }
}
