using System.Net.Sockets;
using System.Text;

namespace BiometricDashboard.Infrastructure.Device;

internal sealed class HanwangTcpClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;

    public HanwangTcpClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    private TcpClient? _client;
    private NetworkStream? _stream;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_client is not null) return;
        _client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await _client.ConnectAsync(_host, _port, cts.Token);
        _client.Client.LingerState = new LingerOption(false, 0);
        _stream = _client.GetStream();
    }

    public async Task<string> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        await client.ConnectAsync(_host, _port, cts.Token);
        client.Client.LingerState = new LingerOption(false, 0);
        await using var stream = client.GetStream();
        return await SendAndReadAsync(stream, command, cts.Token);
    }

    public async Task<string> ExecuteOnSessionAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_stream is null)
            throw new InvalidOperationException("Call ConnectAsync before ExecuteOnSessionAsync.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return await SendAndReadAsync(_stream, command, cts.Token);
    }

    private static async Task<string> SendAndReadAsync(NetworkStream stream, string command, CancellationToken cancellationToken)
    {
        var payload = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(payload, cancellationToken);
        return await ReadResponseAsync(stream, cancellationToken);
    }

    private static async Task<string> ReadResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            var depth = 0;
            var started = false;
            for (var i = 0; i < sb.Length; i++)
            {
                var ch = sb[i];
                if (ch == '(')
                {
                    depth++;
                    started = true;
                }
                else if (ch == ')')
                {
                    depth--;
                    if (started && depth == 0)
                        return sb.ToString(0, i + 1);
                }
            }

            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return sb.ToString();
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().StartsWith("Wait(", StringComparison.Ordinal))
                sb.Clear();
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }
}
