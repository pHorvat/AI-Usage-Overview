using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexUsageNotch;

internal sealed record ReleaseUpdate(Version Version, Uri Download, string Sha256);

internal static class ReleaseUpdater
{
    private const string Repository = "pHorvat/AI-Usage-Overview";
    private const long MaximumDownloadBytes = 300L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodexUsageNotch", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static HttpClient CreateDownloadClient()
    {
        var client = CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    public static async Task<ReleaseUpdate?> CheckAsync(string executable, Version installed, CancellationToken token)
    {
        using var response = await Client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return Parse(document.RootElement, Path.GetFileName(executable), installed);
    }

    internal static ReleaseUpdate? Parse(JsonElement release, string edition, Version installed)
    {
        if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("tag_name", out var tagValue) ||
            tagValue.ValueKind != JsonValueKind.String) return null;
        var tag = tagValue.GetString();
        if (tag is null || !System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+$") ||
            !Version.TryParse(tag[1..], out var version) || version <= installed ||
            !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() != edition ||
                !asset.TryGetProperty("browser_download_url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String ||
                !asset.TryGetProperty("digest", out var digestValue) || digestValue.ValueKind != JsonValueKind.String) continue;
            var expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{edition}";
            if (urlValue.GetString() != expectedUrl) continue;
            var digest = digestValue.GetString();
            if (digest is null || !System.Text.RegularExpressions.Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$")) continue;
            return new ReleaseUpdate(version, new Uri(expectedUrl), digest[7..]);
        }
        return null;
    }

    public static async Task<string> DownloadAsync(ReleaseUpdate update, string executable, IProgress<int> progress,
        CancellationToken token, HttpClient? clientOverride = null)
    {
        var staged = executable + ".update-" + Guid.NewGuid().ToString("N") + ".exe";
        try
        {
            using var response = await (clientOverride ?? DownloadClient).GetAsync(update.Download, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes) throw new InvalidDataException("Release asset is too large.");
            await using var source = await response.Content.ReadAsStreamAsync(token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long received = 0;
            await using (var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    received += count;
                    if (received > MaximumDownloadBytes) throw new InvalidDataException("Release asset is too large.");
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), token);
                    if (response.Content.Headers.ContentLength is > 0 and var total)
                        progress.Report((int)Math.Min(100, received * 100 / total));
                }
                await destination.FlushAsync(token);
            }
            if (response.Content.Headers.ContentLength is { } length && received != length)
                throw new InvalidDataException("Release download was incomplete.");
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release checksum did not match.");
            var downloadedVersion = FileVersionInfo.GetVersionInfo(staged).FileVersion;
            if (!Version.TryParse(downloadedVersion, out var actualVersion) ||
                actualVersion.Major != update.Version.Major || actualVersion.Minor != update.Version.Minor ||
                actualVersion.Build != update.Version.Build)
                throw new InvalidDataException("Release executable version did not match.");
            return staged;
        }
        catch
        {
            if (File.Exists(staged)) File.Delete(staged);
            throw;
        }
    }

    public static void LaunchHelper(string executable, string staged, string sha256)
    {
        var folder = Path.Combine(UserPreferences.DataDirectory, "updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var helper = Path.Combine(folder, Path.GetFileName(executable));
        try
        {
            File.Copy(executable, helper);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = folder, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add(staged);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(sha256);
            using var process = Process.Start(start);
            if (process is null) throw new IOException("Updater did not start.");
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void CleanupHelpers()
    {
        if (!Directory.Exists(UserPreferences.DataDirectory)) return;
        foreach (var folder in Directory.EnumerateDirectories(UserPreferences.DataDirectory, "updater-*"))
        {
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var edition in new[] { "CodexUsageNotch.exe", "CodexUsageNotch-lite.exe" })
                {
                    var helper = Path.Combine(folder, edition);
                    if (File.Exists(helper)) File.Delete(helper);
                }
                Directory.Delete(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static int Apply(string executable, string staged, int oldPid, string sha256)
    {
        var backup = executable + ".backup-" + Guid.NewGuid().ToString("N");
        var replaced = false;
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(staged)) != Path.GetDirectoryName(Path.GetFullPath(executable)) ||
                !Path.GetFileName(staged).StartsWith(Path.GetFileName(executable) + ".update-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update location.");
            using (var input = File.OpenRead(staged))
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Staged release checksum did not match.");
            try { using var old = Process.GetProcessById(oldPid); if (!old.WaitForExit(30000)) throw new TimeoutException("App did not exit."); }
            catch (ArgumentException) { }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Replace(staged, executable, backup); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
            }
            replaced = true;
            using var started = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! });
            if (started is null) throw new IOException("Updated app did not start.");
            started.WaitForExit(2000);
            if (started.HasExited) throw new IOException("Updated app exited during startup.");
            try { File.Delete(backup); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            { Diagnostics.Write("update-backup-cleanup", cleanupError); }
            return 0;
        }
        catch (Exception error)
        {
            Diagnostics.Write("update-apply", error);
            var restored = !replaced;
            try { if (replaced && File.Exists(backup)) { File.Replace(backup, executable, null); restored = true; } }
            catch (Exception restoreError) { Diagnostics.Write("update-restore", restoreError); }
            var restarted = false;
            if (restored && File.Exists(executable))
            {
                try
                {
                    using var previous = Process.Start(new ProcessStartInfo(executable)
                    { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! });
                    restarted = previous is not null;
                }
                catch (Exception restartError) { Diagnostics.Write("update-restart", restartError); }
            }
            var message = restored
                ? restarted ? "The update failed. The previous version was restarted." : "The update failed. Your previous executable is still in place; start it manually."
                : $"The update failed and could not be restored automatically. The backup is at {backup}.";
            MessageBox.Show(message,
                "Codex Usage Notch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch (IOException) { }
        }
    }
}
