using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexUsageNotch;

internal interface IRpcTransport : IDisposable
{
    ValueTask<string?> ReadAsync(CancellationToken token);
    Task WriteAsync(string line, CancellationToken token);
}

internal sealed class ProcessTransport : IRpcTransport
{
    private readonly Process _process;
    private int _disposed;
    public ProcessTransport()
    {
        _process = Process.Start(new ProcessStartInfo(CodexLocator.Find(), "app-server --listen stdio://")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
        }) ?? throw new IOException("Could not start Codex.");
        _process.StandardInput.AutoFlush = true;
        _ = DrainAsync(_process.StandardError);
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        try { var buffer = new char[2048]; while (await reader.ReadAsync(buffer) > 0) { } }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public ValueTask<string?> ReadAsync(CancellationToken token) => _process.StandardOutput.ReadLineAsync(token);
    public Task WriteAsync(string line, CancellationToken token) => _process.StandardInput.WriteLineAsync(line.AsMemory(), token);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { Diagnostics.Write("process-stop", e); }
        _process.Dispose();
    }
}

internal sealed class RpcFailure : Exception
{
    public bool NeedsLogin { get; }
    public RpcFailure(JsonElement error) : base("Codex request failed.")
    {
        if (error.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid RPC error.");
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        NeedsLogin = message.Contains("authentication required", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("requires chatgpt", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record LoginStart(string Id, Uri Url)
{
    internal TaskCompletionSource<bool> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> Completion => CompletionSource.Task;
}
internal sealed record UsageRead(UsageUpdate Update, int AccountGeneration, int ConnectionGeneration, bool Full = false);

internal sealed class CodexUsageClient : IAsyncDisposable
{
    private sealed record PendingRequest(Action<JsonElement> Complete, Action<Exception> Fail);
    private sealed class Session(IRpcTransport transport, int generation)
    {
        public int Generation { get; } = generation;
        public IRpcTransport Transport { get; } = transport;
        public CancellationTokenSource Stop { get; } = new();
        public ConcurrentDictionary<int, PendingRequest> Pending { get; } = new();
        public LoginStart? Login { get; set; }
        public Task Reader { get; set; } = Task.CompletedTask;
        public volatile bool Dead;
        public bool Ready;
    }
    private readonly Func<IRpcTransport> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;
    private Session? _session;
    private int _id;
    private int _accountGeneration;
    private int _connectionGeneration;
    private bool _disposed;
    private volatile bool _acceptUpdates;
    public int AccountGeneration => Volatile.Read(ref _accountGeneration);
    public int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);
    public bool Connected => _session is { Dead: false };
    public event Action<UsageRead>? UsageUpdated;
    public event Action<int>? AccountChanged;
    public event Action<int>? Disconnected;

    public CodexUsageClient(Func<IRpcTransport>? factory = null, TimeSpan? timeout = null, TimeProvider? clock = null)
    { _factory = factory ?? (() => new ProcessTransport()); _timeout = timeout ?? TimeSpan.FromSeconds(15); _clock = clock ?? TimeProvider.System; }

    public async Task<UsageRead> ReadRateLimitsAsync(CancellationToken token)
    {
        return await ExecuteAsync(async (session, ct) =>
        {
            var generation = AccountGeneration;
            return await RequestAsync(session, "account/rateLimits/read", null, result =>
            {
                if (generation != AccountGeneration) throw new IOException("Account changed during read.");
                var read = new UsageRead(UsageUpdate.Parse(result), generation, session.Generation, Full: true);
                _acceptUpdates = true;
                // Publish on the reader before later notifications can overtake this snapshot.
                UsageUpdated?.Invoke(read);
                return read;
            }, ct);
        }, token);
    }
    public async Task<LoginStart> StartLoginAsync(CancellationToken token) => await ExecuteAsync(async (session, ct) =>
    {
        _acceptUpdates = false;
        return await RequestAsync(session, "account/login/start", new { type = "chatgpt" }, result =>
        {
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "chatgpt" ||
                !result.TryGetProperty("authUrl", out var u) || u.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(u.GetString(), UriKind.Absolute, out var url) || url.Scheme != "https" ||
                !result.TryGetProperty("loginId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new InvalidDataException("Invalid login response.");
            session.Login = new LoginStart(id.GetString()!, url);
            return session.Login;
        }, ct);
    }, token);

    private async Task<T> ExecuteAsync<T>(Func<Session, CancellationToken, Task<T>> action, CancellationToken caller)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = new CancellationTokenSource(_timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _stop.Token, deadline.Token);
        var entered = false;
        try
        {
            await _gate.WaitAsync(linked.Token);
            entered = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null || _session.Dead)
            {
                await CloseSessionAsync();
                var session = new Session(_factory(), Interlocked.Increment(ref _connectionGeneration));
                _session = session;
                session.Reader = ReadLoopAsync(session);
                await RequestAsync(session, "initialize", new
                {
                    clientInfo = new { name = "codex-usage-notch", version = typeof(CodexUsageClient).Assembly.GetName().Version?.ToString(3) }
                }, result => result.Clone(), linked.Token);
                await session.Transport.WriteAsync("{\"method\":\"initialized\"}", linked.Token);
                session.Ready = true;
            }
            var result = await action(_session, linked.Token);
            if (_session.Dead) throw new IOException("Codex disconnected.");
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !caller.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            if (entered) await CloseSessionAsync();
            throw new TimeoutException("Codex request timed out.");
        }
        catch (Exception error)
        {
            // A server-side RPC failure does not invalidate the stdio connection.
            // In particular, an auth error should leave the same Codex process available for login.
            if (entered && (error is not RpcFailure || _session is not { Dead: false, Ready: true }))
                await CloseSessionAsync();
            throw;
        }
        finally { if (entered) _gate.Release(); }
    }
    private async Task<T> RequestAsync<T>(Session session, string method, object? parameters, Func<JsonElement, T> parse, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _id);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[id] = new(result => completion.TrySetResult(parse(result)), error => completion.TrySetException(error));
        try
        {
            if (session.Dead) throw new IOException("Codex disconnected.");
            var request = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
            if (parameters is not null) request["params"] = parameters;
            await session.Transport.WriteAsync(JsonSerializer.Serialize(request), token);
            return await completion.Task.WaitAsync(token);
        }
        finally { session.Pending.TryRemove(id, out _); }
    }
    private async Task ReadLoopAsync(Session session)
    {
        try
        {
            while (!session.Stop.IsCancellationRequested)
            {
                var line = await session.Transport.ReadAsync(session.Stop.Token);
                if (line is null) break;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var n))
                    {
                        if (session.Pending.TryRemove(n, out var request))
                        {
                            try
                            {
                                if (root.TryGetProperty("error", out var error)) request.Fail(new RpcFailure(error));
                                else if (root.TryGetProperty("result", out var result)) request.Complete(result);
                                else request.Fail(new InvalidDataException("Missing result."));
                            }
                            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or FormatException or UnsupportedLimitException or IOException)
                            { request.Fail(e); }
                        }
                        continue;
                    }
                    if (!ReferenceEquals(session, _session) || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) continue;
                    root.TryGetProperty("params", out var args);
                    switch (method.GetString())
                    {
                        case "account/rateLimits/updated" when _acceptUpdates:
                            UsageUpdated?.Invoke(new UsageRead(UsageUpdate.Parse(args), AccountGeneration, session.Generation));
                            break;
                        case "account/updated":
                            if (args.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid account update.");
                            _acceptUpdates = false;
                            var generation = Interlocked.Increment(ref _accountGeneration);
                            AccountChanged?.Invoke(generation);
                            break;
                        case "account/login/completed":
                            var loginId = args.TryGetProperty("loginId", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                            if (!args.TryGetProperty("success", out var s) || s.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                                throw new InvalidDataException("Invalid login completion.");
                            if (session.Login is { } login && login.Id == loginId) login.CompletionSource.TrySetResult(s.GetBoolean());
                            break;
                    }
                }
                catch (UnsupportedLimitException) { }
                catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or FormatException)
                { Diagnostics.Write("protocol-invalid", e); }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { if (!session.Stop.IsCancellationRequested) Diagnostics.Write("connection-read", e); }
        finally
        {
            session.Dead = true;
            foreach (var pending in session.Pending.Values) pending.Fail(new IOException("Codex disconnected."));
            session.Login?.CompletionSource.TrySetCanceled();
            if (!session.Stop.IsCancellationRequested && ReferenceEquals(session, _session)) Disconnected?.Invoke(session.Generation);
        }
    }
    private async Task CloseSessionAsync()
    {
        var session = _session;
        _session = null;
        _acceptUpdates = false;
        if (session is null) return;
        Interlocked.Increment(ref _connectionGeneration);
        session.Stop.Cancel();
        session.Transport.Dispose();
        await session.Reader;
        session.Stop.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        await _gate.WaitAsync();
        try { await CloseSessionAsync(); }
        finally { _gate.Release(); }
        _stop.Dispose();
        // Semaphores remain valid for callers already unwinding cancellation.
    }
    public void Abort()
    {
        if (_disposed) return;
        _stop.Cancel();
        _session?.Stop.Cancel();
        _session?.Transport.Dispose();
    }
}

internal static class CodexLocator
{
    public static string Find()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "codex.exe"), Path.Combine(profile, ".codex", "bin", "codex.exe"),
            Path.Combine(local, "Programs", "Codex", "codex.exe"), Path.Combine(local, "Programs", "Codex", "resources", "codex.exe")
        }) if (File.Exists(candidate)) return candidate;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try { var path = Path.Combine(folder.Trim('"'), "codex.exe"); if (File.Exists(path)) return path; }
            catch (ArgumentException) { }
        }
        foreach (var root in new[] { ".vscode", ".vscode-insiders" })
        {
            try
            {
                var found = Directory.EnumerateDirectories(Path.Combine(profile, root, "extensions"), "openai.chatgpt-*")
                    .Select(path => new { Path = path, Version = ParseVersion(Path.GetFileName(path)) })
                    .OrderByDescending(x => x.Version)
                    .Select(x => Path.Combine(x.Path, "bin", "windows-x86_64", "codex.exe")).FirstOrDefault(File.Exists);
                if (found is not null) return found;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return "codex.exe";
    }
    private static Version ParseVersion(string name) => Version.TryParse(name["openai.chatgpt-".Length..].Split('-')[0], out var version) ? version : new Version();
}
