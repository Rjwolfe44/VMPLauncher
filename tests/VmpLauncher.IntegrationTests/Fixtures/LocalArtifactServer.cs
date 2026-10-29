using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;

namespace VmpLauncher.IntegrationTests.Fixtures;

/// <summary>
/// Serves files from a directory over http://127.0.0.1:port/ with per-file fault modes, Range support and a
/// request log. CKAN's downloader needs a real socket, so this is an HttpListener rather than a fake handler.
/// </summary>
public sealed class LocalArtifactServer : IDisposable
{
    public enum Fault
    {
        Ok,
        NotFound,
        /// <summary>Close the connection after sending half the body (once; the next request is Ok).</summary>
        DropHalfwayOnce,
        /// <summary>Flip one byte in the body.</summary>
        CorruptByte,
    }

    public sealed record RequestRecord(string Path, string? Range, int Status);

    private readonly HttpListener _listener = new();
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Fault> _faults = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    public LocalArtifactServer(string rootDir)
    {
        _root = rootDir;
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";
    public ConcurrentQueue<RequestRecord> Requests { get; } = new();

    public void SetFault(string fileName, Fault fault) => _faults[fileName] = fault;
    public void ClearRequests() => Requests.Clear();
    public IReadOnlyList<RequestRecord> RequestsFor(string fileName) => Requests.Where(r => r.Path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)).ToList();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
        var file = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        var range = ctx.Request.Headers["Range"];
        var fault = _faults.TryGetValue(Path.GetFileName(file), out var f) ? f : Fault.Ok;
        try
        {
            if (fault == Fault.NotFound || !File.Exists(file))
            {
                ctx.Response.StatusCode = 404;
                Requests.Enqueue(new RequestRecord(rel, range, 404));
                ctx.Response.Close();
                return;
            }
            var bytes = File.ReadAllBytes(file);
            if (fault == Fault.CorruptByte && bytes.Length > 10)
            {
                bytes[bytes.Length / 2] ^= 0xFF;
            }
            long start = 0, end = bytes.Length - 1;
            var status = 200;
            if (range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = range[6..].Split('-');
                start = long.Parse(parts[0], CultureInfo.InvariantCulture);
                if (parts.Length > 1 && parts[1].Length > 0)
                {
                    end = long.Parse(parts[1], CultureInfo.InvariantCulture);
                }
                status = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
            }
            ctx.Response.StatusCode = status;
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.Headers["ETag"] = $"\"{bytes.Length}-{Path.GetFileName(file)}\"";
            ctx.Response.ContentType = "application/zip";
            var length = end - start + 1;
            ctx.Response.ContentLength64 = length;
            Requests.Enqueue(new RequestRecord(rel, range, status));
            if (fault == Fault.DropHalfwayOnce)
            {
                _faults[Path.GetFileName(file)] = Fault.Ok;
                ctx.Response.OutputStream.Write(bytes, (int)start, (int)(length / 2));
                ctx.Response.OutputStream.Flush();
                ctx.Response.Abort();
                return;
            }
            ctx.Response.OutputStream.Write(bytes, (int)start, (int)length);
            ctx.Response.Close();
        }
        catch (Exception)
        {
            try { ctx.Response.Abort(); } catch (Exception) { }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
        _listener.Close();
        _cts.Dispose();
    }
}
