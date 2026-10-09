using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RedmineCli.Tests;

/// <summary>
/// The smallest HTTPS proxy: answers CONNECT host:port with a tunnel, optionally asking for Basic credentials first (407).
/// Records every CONNECT it was asked for and the credentials it saw.
/// </summary>
public sealed class TunnelProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _connects = [];
    private readonly List<string> _credentials = [];
    private readonly Task _loop;

    public TunnelProxy()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>"user:pass" the proxy requires (Basic), or null for none.</summary>
    public string? RequiredCredentials { get; set; }

    public IReadOnlyList<string> Connects
    {
        get
        {
            lock (_connects)
            {
                return [.. _connects];
            }
        }
    }

    public IReadOnlyList<string> Credentials
    {
        get
        {
            lock (_connects)
            {
                return [.. _credentials];
            }
        }
    }

    public void Clear()
    {
        lock (_connects)
        {
            _connects.Clear();
            _credentials.Clear();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream);
            if (head is null)
            {
                return;
            }
            var lines = head.Split("\r\n");
            var request = lines[0].Split(' ');
            var auth = lines.Skip(1)
                .Where(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                .Select(l => l["Proxy-Authorization:".Length..].Trim())
                .FirstOrDefault();
            var given = auth is not null && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()))
                : null;
            if (RequiredCredentials is not null && given != RequiredCredentials)
            {
                await WriteAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            if (request.Length < 2 || request[0] != "CONNECT")
            {
                await WriteAsync(stream, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            lock (_connects)
            {
                _connects.Add(request[1]);
                if (given is not null)
                {
                    _credentials.Add(given);
                }
            }
            var colon = request[1].LastIndexOf(':');
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(request[1][..colon], int.Parse(request[1][(colon + 1)..]));
            await WriteAsync(stream, "HTTP/1.1 200 Connection established\r\n\r\n");
            var server = upstream.GetStream();
            await Task.WhenAny(PumpAsync(stream, server), PumpAsync(server, stream));
        }
    }

    // Byte by byte up to the blank line: a CONNECT client sends nothing more until it gets the answer.
    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16384)
        {
            if (await stream.ReadAsync(one) == 0)
            {
                return null;
            }
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
            {
                return Encoding.ASCII.GetString([.. bytes]).TrimEnd();
            }
        }
        return null;
    }

    private static Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    private static async Task PumpAsync(Stream from, Stream to)
    {
        try
        {
            await from.CopyToAsync(to);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Either side closed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}
