using System.Net;
using System.Net.Sockets;

namespace ThumbFree.Speech.Tests;

/// <summary>What <see cref="TestServer"/> does with one request.</summary>
internal enum Reply
{
    /// <summary>The file, or the part a Range header asks for (206).</summary>
    Normal,

    /// <summary>Half of what was asked, then the connection is cut.</summary>
    DropHalfway,

    /// <summary>Half of what was asked, then nothing more (a stalled connection).</summary>
    StallHalfway,

    /// <summary>HTTP 500.</summary>
    ServerError,

    /// <summary>The whole file with 200, ignoring a Range header (a server that can't resume).</summary>
    IgnoreRange,
}

/// <summary>A local HttpListener serving test bytes for the download tests, misbehaving on request.</summary>
internal sealed class TestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, byte[]> _files = [];
    private readonly List<string> _requests = [];
    private readonly CancellationTokenSource _closing = new();
    private readonly int _port;

    public TestServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    /// <summary>The reply to request number n (from 1, counting every request).</summary>
    public Func<int, Reply> Behave { get; set; } = _ => Reply.Normal;

    /// <summary>Every request so far as "name range" ("model.bin -", "model.bin bytes=150000-").</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    public string Add(string name, byte[] bytes)
    {
        lock (_files)
        {
            _files[name] = bytes;
        }

        return $"http://127.0.0.1:{_port}/{name}";
    }

    public void Dispose()
    {
        _closing.Cancel();
        _listener.Close();
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var name = context.Request.Url!.AbsolutePath.TrimStart('/');
        var range = context.Request.Headers["Range"];
        int number;
        lock (_requests)
        {
            _requests.Add($"{name} {range ?? "-"}");
            number = _requests.Count;
        }

        var reply = Behave(number);
        var response = context.Response;
        try
        {
            byte[]? bytes;
            lock (_files)
            {
                _files.TryGetValue(name, out bytes);
            }

            if (bytes is null || reply == Reply.ServerError)
            {
                response.StatusCode = bytes is null ? 404 : 500;
                response.Close();
                return;
            }

            var from = 0;
            if (range is not null && reply != Reply.IgnoreRange && range.StartsWith("bytes=", StringComparison.Ordinal) && range.EndsWith('-'))
            {
                from = int.Parse(range[6..^1], System.Globalization.CultureInfo.InvariantCulture);
                if (from >= bytes.Length)
                {
                    response.StatusCode = 416;
                    response.Close();
                    return;
                }

                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {from}-{bytes.Length - 1}/{bytes.Length}");
            }

            var length = bytes.Length - from;
            response.ContentLength64 = length;
            var send = reply is Reply.DropHalfway or Reply.StallHalfway ? length / 2 : length;
            await response.OutputStream.WriteAsync(bytes.AsMemory(from, send), _closing.Token);
            await response.OutputStream.FlushAsync(_closing.Token);
            if (reply == Reply.Normal || reply == Reply.IgnoreRange)
            {
                response.Close();
                return;
            }

            // Let the client read what was sent before the connection goes (a reset can discard unread data).
            await Task.Delay(reply == Reply.DropHalfway ? 200 : Timeout.Infinite, _closing.Token);
            response.Abort();
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            response.Abort();
        }
    }
}
