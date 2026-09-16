using CodexUsageNotch;
using System.Text.Json;
using System.Threading.Channels;

internal static class Tests
{
    private static int _count;
    private const string Full = "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":62,\"windowDurationMins\":300,\"resetsAt\":1800000000},\"secondary\":{\"usedPercent\":10,\"windowDurationMins\":10080}}}";
    [STAThread]
    private static int Main(string[] args) => args.Contains("--ui") ? UiTests.Run()
        : args.Contains("--live") ? LiveCheck().GetAwaiter().GetResult() : RunAll().GetAwaiter().GetResult();
    private static async Task<int> LiveCheck()
    {
        try
        {
            await using var client = new CodexUsageClient();
            var read = await client.ReadRateLimitsAsync(default);
            var snapshot = read.Update.Apply(null, true);
            Console.WriteLine($"PASS live read: primary={snapshot.Primary is not null}, secondary={snapshot.Secondary is not null}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("Live read failed: " + error.GetType().Name); return 1; }
    }
    private static async Task<int> RunAll()
    {
        try
        {
            Run("full read replaces missing secondary", () =>
            {
                var old = Parse(Full).Apply(null, true);
                var next = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":80}}}").Apply(old, true);
                Check(next.Secondary is null && next.Primary?.ResetsAt is null);
            });
            Run("partial notification preserves omitted window and metadata", () =>
            {
                var old = Parse(Full).Apply(null, true);
                var next = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":80}}}").Apply(old, false);
                Check(next.Primary?.Remaining == 20 && next.Primary.ResetsAt == old.Primary?.ResetsAt && next.Secondary == old.Secondary);
            });
            Run("explicit null clears a window and its metadata", () =>
            {
                var old = Parse(Full).Apply(null, true);
                var next = Parse("{\"rateLimits\":{\"secondary\":null,\"primary\":{\"usedPercent\":80,\"resetsAt\":null}}}").Apply(old, false);
                Check(next.Secondary is null && next.Primary?.ResetsAt is null);
            });
            foreach (var value in new[] { "-1", "101", "1.5", "\"oops\"", "null" })
                Run("reject invalid percentage " + value, () => Throws<InvalidDataException>(() => Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":" + value + "}}}")));
            Run("reject invalid time", () => Throws<InvalidDataException>(() => Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":5,\"resetsAt\":9223372036854775807}}}")));
            Run("ignore unrelated limit", () => Throws<UnsupportedLimitException>(() => Parse("{\"rateLimits\":{\"limitId\":\"other\"}}")));
            Run("prefer the codex bucket in a multi-bucket snapshot", () =>
            {
                var update = Parse("{\"rateLimits\":{\"limitId\":\"other\"},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":12}}}}");
                Check(update.Apply(null, true).Primary?.Remaining == 88);
            });
            Run("reject invalid limit identifier", () => Throws<InvalidDataException>(() => Parse("{\"rateLimits\":{\"limitId\":42}}")));
            Run("unavailable plan metadata in notifications preserves the known plan", () =>
            {
                var old = Parse("{\"rateLimits\":{\"planType\":\"plus\"}}").Apply(null, true);
                var update = Parse("{\"rateLimits\":{\"planType\":null}}");
                Check(update.Apply(old, false).Plan == "plus" && update.Apply(old, true).Plan is null);
            });
            Run("freshness changes without changing state", () =>
            {
                var now = DateTimeOffset.UtcNow;
                var state = new UsageState(Parse(Full).Apply(null, true), ConnectionStatus.Ready, now);
                Check(!state.IsStale(now.AddSeconds(119)) && state.IsStale(now.AddMinutes(2)));
                Check((state with { Status = ConnectionStatus.Retrying }).IsStale(now));
                Check((state with { Status = ConnectionStatus.Retrying }).StatusText(now).Contains("Last known"));
            });
            Run("countdown never invents reset", () =>
            {
                var now = DateTimeOffset.UtcNow;
                Check(UsageText.Reset(now.AddSeconds(-1), now) == "Awaiting reset update");
                Check(UsageText.Reset(null, now) == "Reset time unavailable");
                Check(UsageText.Reset(now.AddHours(3.5), now) == "Resets in 3h 30m");
                Check(UsageText.Window(new(0, null, 720)) == "12-hour window");
                Check(UsageText.Window(new(0, null, 15)) == "15-minute window");
            });
            Run("backoff uses 5, 15, 30, 60 seconds and resets", () =>
            {
                var clock = new FakeClock(); var schedule = new RefreshSchedule(clock);
                foreach (var delay in new[] { 5, 15, 30, 60, 60 })
                { schedule.Failed(); Check(schedule.Next == clock.GetUtcNow().AddSeconds(delay)); clock.Advance(TimeSpan.FromSeconds(delay)); Check(schedule.Due); }
                schedule.Succeeded(); Check(schedule.Next == clock.GetUtcNow().AddMinutes(1));
                schedule.Failed(); Check(schedule.Next == clock.GetUtcNow().AddSeconds(5));
            });
            Run("settings migration preserves unknown fields", () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "CodexUsageNotch-tests-" + Guid.NewGuid());
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "settings.json");
                try
                {
                    File.WriteAllText(path, "{\"taskbarUsageRingEnabled\":true,\"futurePreference\":42}");
                    var preferences = new UserPreferences(path); preferences.Load();
                    Check(preferences.RingEnabled && preferences.StripVisible);
                    preferences.StripVisible = false; Check(preferences.Save());
                    var next = new UserPreferences(path); next.Load(); Check(!next.StripVisible && next.RingEnabled);
                    Check(File.ReadAllText(path).Contains("futurePreference"));
                    File.WriteAllText(path, "corrupt"); var corrupt = new UserPreferences(path); corrupt.Load(); Check(corrupt.StripVisible && !corrupt.RingEnabled);
                }
                finally { File.Delete(path); Directory.Delete(directory); }
            });
            Run("popup clamps on negative-coordinate and edge monitors", () =>
            {
                var work = new Rectangle(-1920, -200, 1920, 1040); var size = new Size(498, 400);
                foreach (var anchor in new[] { new Rectangle(-1900, 10, 20, 20), new Rectangle(-30, 800, 20, 20), new Rectangle(-900, -200, 20, 20) })
                    Check(work.Contains(new Rectangle(PopupPlacement.Place(anchor, size, work), size)));
            });
            await RunAsync("handshake, full read and serialized concurrent requests", async () =>
            {
                var transport = new FakeTransport(Full); var starts = 0;
                await using var client = new CodexUsageClient(() => { starts++; return transport; });
                var reads = await Task.WhenAll(client.ReadRateLimitsAsync(default), client.ReadRateLimitsAsync(default));
                Check(starts == 1 && transport.Methods.Count(m => m == "initialize") == 1 && transport.Methods.Contains("initialized"));
                Check(reads.All(x => x.Update.Apply(null, true).Primary?.Remaining == 38));
            });
            await RunAsync("timeout tears down transport and reconnects", async () =>
            {
                var clock = new FakeClock(); var first = new FakeTransport(Full) { HangReads = true }; var second = new FakeTransport(Full); var attempts = 0;
                await using var client = new CodexUsageClient(() => ++attempts == 1 ? first : second, TimeSpan.FromSeconds(15), clock);
                var read = client.ReadRateLimitsAsync(default);
                await Until(() => first.Methods.Contains("account/rateLimits/read"));
                clock.Advance(TimeSpan.FromSeconds(15));
                await ThrowsAsync<TimeoutException>(() => read);
                Check(first.Disposed);
                var next = await client.ReadRateLimitsAsync(default); Check(next.Update.Primary is not null && attempts == 2);
            });
            await RunAsync("shutdown cancels pending requests", async () =>
            {
                var transport = new FakeTransport(Full) { HangReads = true }; var client = new CodexUsageClient(() => transport);
                var read = client.ReadRateLimitsAsync(default);
                await Until(() => transport.Methods.Contains("account/rateLimits/read"));
                await client.DisposeAsync(); await ThrowsAsync<OperationCanceledException>(() => read); Check(transport.Disposed);
            });
            await RunAsync("account change invalidates an in-flight read", async () =>
            {
                var transport = new FakeTransport(Full) { HangReads = true }; await using var client = new CodexUsageClient(() => transport);
                var read = client.ReadRateLimitsAsync(default);
                await Until(() => transport.LastReadId > 0);
                transport.Push("{\"method\":\"account/updated\",\"params\":{}}");
                await Until(() => client.AccountGeneration == 1);
                transport.Push("{\"id\":" + transport.LastReadId + ",\"result\":" + Full + "}");
                await ThrowsAsync<IOException>(() => read);
            });
            await RunAsync("malformed notification does not kill healthy reader", async () =>
            {
                var transport = new FakeTransport(Full); await using var client = new CodexUsageClient(() => transport);
                await client.ReadRateLimitsAsync(default); var count = 0;
                client.UsageUpdated += _ => Interlocked.Increment(ref count);
                transport.Push("not json");
                transport.Push("{\"method\":\"account/rateLimits/updated\",\"params\":" + Full + "}");
                await Until(() => count == 1);
            });
            await RunAsync("EOF fails pending read and next request reconnects", async () =>
            {
                var first = new FakeTransport(Full) { HangReads = true }; var second = new FakeTransport(Full); var starts = 0;
                await using var client = new CodexUsageClient(() => ++starts == 1 ? first : second);
                var read = client.ReadRateLimitsAsync(default); await Until(() => first.LastReadId > 0); first.End();
                await ThrowsAsync<IOException>(() => read);
                await client.ReadRateLimitsAsync(default); Check(starts == 2);
            });
            await RunAsync("EOF before initialize fails without waiting for the request deadline", async () =>
            {
                var transport = new FakeTransport(Full); transport.End();
                await using var client = new CodexUsageClient(() => transport);
                await ThrowsAsync<IOException>(() => client.ReadRateLimitsAsync(default));
                Check(transport.Disposed);
            });
            await RunAsync("cancelling a queued request leaves the active request connected", async () =>
            {
                var transport = new FakeTransport(Full) { HangReads = true };
                await using var client = new CodexUsageClient(() => transport);
                var first = client.ReadRateLimitsAsync(default);
                await Until(() => transport.LastReadId > 0);
                using var cancellation = new CancellationTokenSource();
                var second = client.ReadRateLimitsAsync(cancellation.Token); cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => second);
                Check(!transport.Disposed);
                transport.Push("{\"id\":" + transport.LastReadId + ",\"result\":" + Full + "}");
                Check((await first).Update.Primary is not null && client.Connected);
            });
            await RunAsync("login completion is correlated and accepted only once", async () =>
            {
                var transport = new FakeTransport(Full); await using var client = new CodexUsageClient(() => transport);
                var login = await client.StartLoginAsync(default);
                Check(login.Id == "test-login" && login.Url.Scheme == "https");
                transport.Push("{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"other-login\",\"success\":true}}");
                transport.Push("{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"test-login\",\"success\":null}}");
                transport.Push("{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"test-login\",\"success\":false}}");
                transport.Push("{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"test-login\",\"success\":true}}");
                Check(!await login.Completion);
            });
            await RunAsync("reject insecure login URL and dispose connection", async () =>
            {
                var transport = new FakeTransport(Full) { LoginResult = "{\"type\":\"chatgpt\",\"loginId\":\"test\",\"authUrl\":\"http://example.com\"}" };
                await using var client = new CodexUsageClient(() => transport);
                await ThrowsAsync<InvalidDataException>(() => client.StartLoginAsync(default)); Check(transport.Disposed);
            });
            await RunAsync("cancellation tears down pending login", async () =>
            {
                var transport = new FakeTransport(Full) { HangLogin = true }; await using var client = new CodexUsageClient(() => transport);
                using var cancellation = new CancellationTokenSource();
                var login = client.StartLoginAsync(cancellation.Token);
                await Until(() => transport.Methods.Contains("account/login/start")); cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => login); Check(transport.Disposed);
            });
            await RunAsync("disconnected snapshots carry obsolete connection generation", async () =>
            {
                var first = new FakeTransport(Full); var second = new FakeTransport(Full); var starts = 0;
                await using var client = new CodexUsageClient(() => ++starts == 1 ? first : second);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected += _ => disconnected.TrySetResult();
                var old = await client.ReadRateLimitsAsync(default); first.End();
                await disconnected.Task;
                var current = await client.ReadRateLimitsAsync(default);
                Check(old.ConnectionGeneration != current.ConnectionGeneration && current.ConnectionGeneration == client.ConnectionGeneration);
            });
            await RunAsync("full response precedes immediately buffered usage notifications", async () =>
            {
                var transport = new FakeTransport(Full)
                {
                    AfterRead = "{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{\"primary\":{\"usedPercent\":80}}}}"
                };
                await using var client = new CodexUsageClient(() => transport);
                var updates = new System.Collections.Concurrent.ConcurrentQueue<UsageRead>();
                client.UsageUpdated += updates.Enqueue;
                await client.ReadRateLimitsAsync(default);
                await Until(() => updates.Count == 2);
                var ordered = updates.ToArray();
                Check(ordered[0].Full && !ordered[1].Full);
                var snapshot = ordered.Aggregate((UsageSnapshot?)null, (old, read) => read.Update.Apply(old, read.Full));
                Check(snapshot?.Primary?.Remaining == 20 && snapshot.Primary.DurationMinutes == 300 && snapshot.Secondary?.Remaining == 90);
            });
            await RunAsync("immediate login completion survives the start response continuation", async () =>
            {
                var transport = new FakeTransport(Full)
                {
                    AfterLogin = "{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"test-login\",\"success\":true}}"
                };
                await using var client = new CodexUsageClient(() => transport);
                var login = await client.StartLoginAsync(default);
                Check(await login.Completion);
            });
            await RunAsync("disconnect cancels a started login", async () =>
            {
                var transport = new FakeTransport(Full);
                await using var client = new CodexUsageClient(() => transport);
                var login = await client.StartLoginAsync(default);
                transport.End();
                await ThrowsAsync<OperationCanceledException>(() => login.Completion);
            });
            foreach (var result in new[] { "null", "{}", "{\"type\":\"apiKey\",\"loginId\":\"x\",\"authUrl\":\"https://example.com\"}",
                "{\"type\":\"chatgpt\",\"loginId\":\" \",\"authUrl\":\"https://example.com\"}" })
                await RunAsync("reject malformed login response " + result, async () =>
                {
                    var transport = new FakeTransport(Full) { LoginResult = result };
                    await using var client = new CodexUsageClient(() => transport);
                    await ThrowsAsync<InvalidDataException>(() => client.StartLoginAsync(default));
                    Check(transport.Disposed);
                });
            await RunAsync("malformed RPC error fails immediately without waiting for timeout", async () =>
            {
                var transport = new FakeTransport(Full) { HangReads = true };
                await using var client = new CodexUsageClient(() => transport);
                var read = client.ReadRateLimitsAsync(default);
                await Until(() => transport.LastReadId > 0);
                transport.Push("{\"id\":" + transport.LastReadId + ",\"error\":null}");
                await ThrowsAsync<InvalidDataException>(() => read);
                Check(transport.Disposed);
            });
            await RunAsync("server request IDs cannot complete a client request", async () =>
            {
                var transport = new FakeTransport(Full) { HangReads = true };
                await using var client = new CodexUsageClient(() => transport);
                var read = client.ReadRateLimitsAsync(default);
                await Until(() => transport.LastReadId > 0);
                transport.Push("{\"id\":" + transport.LastReadId + ",\"method\":\"server/request\",\"params\":{}}");
                transport.Push("{\"id\":" + transport.LastReadId + ",\"result\":" + Full + "}");
                Check((await read).Update.Primary is not null);
            });
            await RunAsync("account invalidation survives connection teardown", async () =>
            {
                var transport = new FakeTransport(Full) { HangReads = true };
                await using var client = new CodexUsageClient(() => transport);
                var accountGeneration = -1;
                client.AccountChanged += value => accountGeneration = value;
                var read = client.ReadRateLimitsAsync(default);
                await Until(() => transport.LastReadId > 0);
                transport.Push("{\"method\":\"account/updated\",\"params\":{}}");
                transport.Push("{\"id\":" + transport.LastReadId + ",\"result\":" + Full + "}");
                await ThrowsAsync<IOException>(() => read);
                Check(accountGeneration == client.AccountGeneration && accountGeneration > 0);
            });
            Console.WriteLine($"PASS: {_count} tests"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static UsageUpdate Parse(string json) { using var doc = JsonDocument.Parse(json); return UsageUpdate.Parse(doc.RootElement); }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Run(string name, Action test) { test(); Console.WriteLine("PASS " + name); _count++; }
    private static async Task RunAsync(string name, Func<Task> test) { await test().WaitAsync(TimeSpan.FromSeconds(5)); Console.WriteLine("PASS " + name); _count++; }
    private static async Task Until(Func<bool> condition) { for (var i = 0; !condition(); i++) { if (i > 400) throw new TimeoutException(); await Task.Delay(5); } }

    internal sealed class FakeTransport(string result) : IRpcTransport
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public System.Collections.Concurrent.ConcurrentBag<string> Methods { get; } = new();
        public bool HangReads { get; set; }
        public bool HangLogin { get; set; }
        public string? AfterRead { get; set; }
        public string? AfterLogin { get; set; }
        public string LoginResult { get; set; } = "{\"type\":\"chatgpt\",\"loginId\":\"test-login\",\"authUrl\":\"https://example.com/login\"}";
        public bool Disposed { get; private set; }
        public int LastReadId;
        public void Push(string line) => _lines.Writer.TryWrite(line);
        public void End() => _lines.Writer.TryComplete();
        public async ValueTask<string?> ReadAsync(CancellationToken token)
        { try { return await _lines.Reader.ReadAsync(token); } catch (ChannelClosedException) { return null; } }
        public Task WriteAsync(string line, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(line); var root = doc.RootElement; var method = root.GetProperty("method").GetString()!; Methods.Add(method);
            if (root.TryGetProperty("id", out var id))
            {
                if (method == "account/rateLimits/read") { LastReadId = id.GetInt32(); if (HangReads) return Task.CompletedTask; }
                if (method == "account/login/start" && HangLogin) return Task.CompletedTask;
                Push("{\"id\":" + id.GetInt32() + ",\"result\":" + (method == "initialize" ? "{}" : method == "account/login/start" ? LoginResult : result) + "}");
                if (method == "account/rateLimits/read" && AfterRead is { } update) Push(update);
                if (method == "account/login/start" && AfterLogin is { } completed) Push(completed);
            }
            return Task.CompletedTask;
        }
        public void Dispose() { Disposed = true; End(); }
    }
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-12T12:00:00Z");
        private readonly List<FakeTimer> _timers = new();
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) { _now += delta; foreach (var timer in _timers.ToArray()) timer.Fire(_now); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new FakeTimer(this, callback, state, dueTime); _timers.Add(timer); return timer; }
        private sealed class FakeTimer(FakeClock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private DateTimeOffset _at = clock.GetUtcNow() + due;
            private bool _disposed;
            public void Fire(DateTimeOffset now) { if (!_disposed && now >= _at) { _disposed = true; callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) { _at = clock.GetUtcNow() + dueTime; return !_disposed; }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
