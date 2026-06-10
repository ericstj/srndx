using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Srndx;

/// <summary>
/// A loopback query endpoint that lets one-shot <c>srndx search</c> invocations reuse a resident
/// index instead of paying the cold-start load. A running <c>serve</c>/<c>mcp</c> process starts this
/// server, advertises its port and a per-process token in a <c>&lt;index&gt;.srv</c> sidecar file, and
/// answers newline-delimited JSON requests by querying the live, in-memory <see cref="IndexService" />.
/// Bound to <see cref="IPAddress.Loopback" /> only; the token guards against unrelated local processes.
/// </summary>
internal sealed class WarmQueryServer : IAsyncDisposable
{
    private readonly IndexService _service;
    private readonly string _sidecarPath;
    private readonly string _token;
    private readonly TcpListener _listener;
    private readonly Func<Task>? _onStop;
    private Task? _acceptLoop;

    public WarmQueryServer(IndexService service, string indexPath, Func<Task>? onStop = null)
    {
        _service = service;
        _sidecarPath = WarmQuery.SidecarPath(indexPath);
        _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _onStop = onStop;
    }

    /// <summary>The loopback port the endpoint is listening on.</summary>
    public int Port { get; private set; }

    /// <summary>Starts listening and writes the sidecar so <c>search</c> can discover this endpoint.</summary>
    public void Start(CancellationToken cancellationToken)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var sidecar = new WarmSidecar { Port = Port, Token = _token, Pid = Environment.ProcessId };
        File.WriteAllText(_sidecarPath, JsonSerializer.Serialize(sidecar, WarmJsonContext.Default.WarmSidecar));

        _acceptLoop = Task.Run(() => AcceptLoopAsync(cancellationToken), CancellationToken.None);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleAsync(client, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Shutdown or listener stopped.
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                WarmResponse response = await BuildResponseAsync(line).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, WarmJsonContext.Default.WarmResponse)).ConfigureAwait(false);

                if (response.Stopped)
                {
                    await ShutdownAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException)
            {
                // Client went away or sent garbage; drop the connection.
            }
        }
    }

    private async Task<WarmResponse> BuildResponseAsync(string line)
    {
        WarmRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, WarmJsonContext.Default.WarmRequest);
        }
        catch (JsonException)
        {
            return new WarmResponse { Error = "malformed request" };
        }

        if (request is null || !string.Equals(request.Token, _token, StringComparison.Ordinal))
        {
            return new WarmResponse { Error = "unauthorized" };
        }

        if (string.Equals(request.Command, "stop", StringComparison.Ordinal))
        {
            return new WarmResponse { Stopped = true };
        }

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new WarmResponse { Error = "empty query" };
        }

        IReadOnlyList<(SearchRecord Record, float Score)> results =
            await _service.SearchAsync(request.Query, Math.Clamp(request.Top, 1, 50), request.Language, request.Source)
                .ConfigureAwait(false);

        var response = new WarmResponse();
        foreach ((SearchRecord record, float score) in results)
        {
            response.Results.Add(new WarmResult
            {
                Source = record.Source,
                Location = record.Location,
                Title = record.Title,
                Language = record.Language,
                Text = record.Text,
                Score = score,
            });
        }

        return response;
    }

    /// <summary>
    /// Handles a <c>stop</c> control request: persists the live index, removes the sidecar, then exits.
    /// The interactive console (<c>serve</c>) blocks on <see cref="Console.ReadLine" /> and the MCP host
    /// owns standard I/O, so neither can be unwound by cancellation alone; a clean process exit after a
    /// flush is the reliable way to stop a backgrounded server.
    /// </summary>
    private async Task ShutdownAsync()
    {
        try
        {
            if (_onStop is not null)
            {
                await _onStop().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort flush; still proceed to exit.
        }

        TryDeleteSidecar();
        Environment.Exit(0);
    }

    private void TryDeleteSidecar()
    {
        try
        {
            if (File.Exists(_sidecarPath))
            {
                File.Delete(_sidecarPath);
            }
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Already accounted for in the accept loop.
            }
        }

        TryDeleteSidecar();
    }
}

/// <summary>Client side of the warm-query endpoint: discovers a resident server and proxies a query to it.</summary>
internal static class WarmQuery
{
    public static string SidecarPath(string indexPath) => indexPath + ".srv";

    private static bool ProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the sidecar next to <paramref name="indexPath" /> and returns it only when it names a live
    /// process. Returns <see langword="null" /> when there is no reachable server (no sidecar, garbage,
    /// or a dead PID), so callers can fall back without paying a connect timeout.
    /// </summary>
    private static WarmSidecar? TryReadLiveSidecar(string indexPath)
    {
        string sidecarPath = SidecarPath(indexPath);
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        WarmSidecar? sidecar;
        try
        {
            sidecar = JsonSerializer.Deserialize(File.ReadAllText(sidecarPath), WarmJsonContext.Default.WarmSidecar);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }

        if (sidecar is null || sidecar.Port is <= 0 or > 65535)
        {
            return null;
        }

        // A force-killed server can't delete its sidecar; skip a dead PID rather than pay the connect
        // timeout. The token and connect still guard correctness against the rare PID reuse.
        return ProcessAlive(sidecar.Pid) ? sidecar : null;
    }

    /// <summary>Sends one newline-delimited JSON request to a resident server and reads its response.</summary>
    private static async Task<WarmResponse?> SendAsync(WarmSidecar sidecar, WarmRequest request)
    {
        try
        {
            using var client = new TcpClient();
            using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            {
                await client.ConnectAsync(IPAddress.Loopback, sidecar.Port, connectTimeout.Token).ConfigureAwait(false);
            }

            client.NoDelay = true;
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, WarmJsonContext.Default.WarmRequest)).ConfigureAwait(false);

            string? line;
            using (var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                line = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
            }

            return line is null ? null : JsonSerializer.Deserialize(line, WarmJsonContext.Default.WarmResponse);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to answer a query from a resident <c>serve</c>/<c>mcp</c> process advertised by the
    /// sidecar next to <paramref name="indexPath" />. Returns <see langword="null" /> when no live
    /// server is reachable, so the caller can fall back to loading the index locally.
    /// </summary>
    public static async Task<IReadOnlyList<(SearchRecord Record, float Score)>?> TrySearchAsync(
        string indexPath, string query, int top, string? language, string? source)
    {
        WarmSidecar? sidecar = TryReadLiveSidecar(indexPath);
        if (sidecar is null)
        {
            return null;
        }

        var request = new WarmRequest
        {
            Token = sidecar.Token,
            Query = query,
            Top = top,
            Language = language,
            Source = source,
        };

        WarmResponse? response = await SendAsync(sidecar, request).ConfigureAwait(false);
        if (response is null || response.Error is not null)
        {
            return null;
        }

        var results = new List<(SearchRecord Record, float Score)>(response.Results.Count);
        foreach (WarmResult result in response.Results)
        {
            results.Add((new SearchRecord
            {
                Source = result.Source,
                Location = result.Location,
                Title = result.Title,
                Language = result.Language,
                Text = result.Text,
            }, result.Score));
        }

        return results;
    }

    /// <summary>
    /// Asks a resident server for <paramref name="indexPath" /> to flush and exit. Returns
    /// <see langword="true" /> when a server acknowledged the stop, <see langword="false" /> when none
    /// was running. The server tears its own connection down as it exits, so a missing reply still counts
    /// as a successful stop once a live server was contacted.
    /// </summary>
    public static async Task<bool> TryStopAsync(string indexPath)
    {
        WarmSidecar? sidecar = TryReadLiveSidecar(indexPath);
        if (sidecar is null)
        {
            return false;
        }

        WarmResponse? response = await SendAsync(sidecar, new WarmRequest { Token = sidecar.Token, Command = "stop" })
            .ConfigureAwait(false);
        return response?.Stopped ?? true;
    }
}

internal sealed class WarmSidecar
{
    public int Port { get; set; }
    public string Token { get; set; } = string.Empty;
    public int Pid { get; set; }
}

internal sealed class WarmRequest
{
    public string Token { get; set; } = string.Empty;
    public string? Command { get; set; }
    public string Query { get; set; } = string.Empty;
    public int Top { get; set; } = 5;
    public string? Language { get; set; }
    public string? Source { get; set; }
}

internal sealed class WarmResult
{
    public string Source { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public float Score { get; set; }
}

internal sealed class WarmResponse
{
    public string? Error { get; set; }
    public bool Stopped { get; set; }
    public List<WarmResult> Results { get; set; } = [];
}

[JsonSerializable(typeof(WarmSidecar))]
[JsonSerializable(typeof(WarmRequest))]
[JsonSerializable(typeof(WarmResponse))]
internal sealed partial class WarmJsonContext : JsonSerializerContext;
