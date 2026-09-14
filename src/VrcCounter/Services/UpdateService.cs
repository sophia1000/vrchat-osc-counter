using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VrcCounter.Services;

public sealed record ReleaseDownload(string Version, Uri DownloadUrl, string Sha256, long Size);
public sealed record UpdateStatus(string CurrentVersion, string? LatestVersion, string Phase, string Message,
    bool Available, bool CanInstall, DateTimeOffset? LastChecked, string Token);

/// <summary>Only installs stable, checksum-verified releases from this application's repository.</summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "sophia1000/vrchat-osc-counter";
    public const string AssetName = "VRChat-OSC-Counter-Windows-x64.zip";
    private readonly AppState _state;
    private readonly string _dataDirectory;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private ReleaseDownload? _release;
    private UpdateStatus _status;
    private Task? _background;
    private string? _planPath;
    private string? _helperPath;
    public Action? RequestExit { get; set; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string CurrentVersion => typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "1.0.0";

    public UpdateService(AppState state, string dataDirectory)
    {
        _state = state;
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _status = new(CurrentVersion, null, "idle", "Updates are checked automatically. Installation only restarts the app when you choose.", false, false, null, Token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"VRChat-OSC-Counter/{CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public UpdateStatus Status => Volatile.Read(ref _status) with { CanInstall = RequestExit is not null && Environment.Is64BitProcess };
    private void SetStatus(string phase, string message) => Volatile.Write(ref _status, _status with { Phase = phase, Message = message });

    public void Start()
    {
        _background = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), _stop.Token);
                while (!_stop.IsCancellationRequested)
                {
                    if (_state.Read(c => c.AutoCheckUpdates)) await CheckAsync(_stop.Token);
                    await Task.Delay(TimeSpan.FromHours(6), _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return Status;
        try
        {
            if (_planPath is not null) return Status;
            SetStatus("checking", "Checking GitHub for a newer stable release...");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var release = ParseRelease(json.RootElement);
            var available = IsNewer(release.Version, CurrentVersion);
            _release = available ? release : null;
            Volatile.Write(ref _status, _status with
            {
                LatestVersion = release.Version, Available = available, LastChecked = DateTimeOffset.Now,
                Phase = available ? "available" : "current",
                Message = available ? $"Version {release.Version} is available. Your settings and history will be kept." : "You are up to date."
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { SetStatus("error", $"Could not check for updates: {ex.Message} Your current version still works."); }
        finally { _gate.Release(); }
        return Status;
    }

    public static bool IsNewer(string candidate, string current) => ParseVersion(candidate) > ParseVersion(current);
    private static Version ParseVersion(string tag)
    {
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$") || !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new InvalidDataException("The release does not have a stable version number.");
        return version;
    }

    public static ReleaseDownload ParseRelease(JsonElement release)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Preview releases are not installed automatically.");
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == AssetName).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("This release has no Windows x64 download.");
        var asset = assets[0];
        var expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{AssetName}";
        if (asset.GetProperty("browser_download_url").GetString() != expectedUrl)
            throw new InvalidDataException("The download does not belong to this repository.");
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("GitHub has not supplied a SHA-256 checksum for this download.");
        var size = asset.GetProperty("size").GetInt64();
        if (size < 1 || size > 512L * 1024 * 1024) throw new InvalidDataException("Unexpected download size.");
        return new(version.ToString(), new Uri(expectedUrl), digest[7..], size);
    }

    public async Task<UpdateStatus> PrepareAsync(CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return Status;
        try
        {
            if (_planPath is not null) return Status;
            if (_release is null || !Status.CanInstall) throw new InvalidOperationException("Check for a newer version in the desktop app first.");
            var release = _release;
            var installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            if (!string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.Combine(installDirectory, "VrcCounter.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run VrcCounter.exe from an extracted release folder to use the updater.");
            // Fail before closing the app when the installation directory is not writable.
            var probe = Path.Combine(installDirectory, $".update-write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRChatCounter", "Updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var zipPath = Path.Combine(stage, "download.zip");
            SetStatus("downloading", $"Downloading version {release.Version}...");
            using (var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidDataException("The download is larger than GitHub reported.");
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    SetStatus("downloading", $"Downloading version {release.Version}: {total * 100 / release.Size}%");
                }
                if (total != release.Size) throw new InvalidDataException("The download was incomplete. Please try again.");
            }
            SetStatus("verifying", "Verifying the download and preparing a backup...");
            await using (var file = File.OpenRead(zipPath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The download checksum did not match. Nothing was installed.");
            }
            var payload = Path.Combine(stage, "payload");
            var files = UpdatePackage.Extract(zipPath, payload);
            await SmokeTestAsync(payload, stage, token);
            var helper = Path.Combine(stage, "Install-Update.ps1");
            File.Copy(Path.Combine(installDirectory, "Updater", "Install-Update.ps1"), helper);
            var planPath = Path.Combine(stage, "plan.json");
            var plan = new
            {
                ProcessId = Environment.ProcessId,
                ProcessStartTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(),
                InstallDirectory = installDirectory, PayloadDirectory = payload, DataDirectory = _dataDirectory,
                BackupDirectory = Path.Combine(stage, "backup"), Files = files
            };
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), token);
            _helperPath = helper;
            _planPath = planPath;
            SetStatus("ready", "Update verified. Saving your data and restarting...");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { SetStatus("error", $"Update was not installed: {ex.Message}"); }
        finally { _gate.Release(); }
        return Status;
    }

    private static async Task SmokeTestAsync(string payload, string stage, CancellationToken token)
    {
        var data = Path.Combine(stage, "smoke-data");
        Directory.CreateDirectory(data);
        var start = new ProcessStartInfo(Path.Combine(payload, "VrcCounter.exe"))
            { WorkingDirectory = payload, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--smoke-test"); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(data);
        using var process = Process.Start(start) ?? throw new IOException("Could not test the new executable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new InvalidDataException("The new version did not pass its startup check. Your current version has not been changed.");
    }

    // Called only after the web server, OSC receiver, config and database have closed.
    public void LaunchInstallerIfReady()
    {
        if (_planPath is null || _helperPath is null) return;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _helperPath, "-PlanPath", _planPath }) start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _background?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _http.Dispose(); _stop.Dispose(); _gate.Dispose();
    }
}

public static class UpdatePackage
{
    public static string[] Extract(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 5000) throw new InvalidDataException("Too many package files.");
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            const string root = "VRChat-OSC-Counter/";
            if (!name.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected package folder.");
            var relative = name[root.Length..];
            if (relative.Length == 0) continue;
            ValidateRelativePath(relative.TrimEnd('/'));
            if (name.EndsWith('/')) continue;
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Package links are not allowed.");
            size += entry.Length;
            if (size > 1024L * 1024 * 1024 || !files.TryAdd(relative, entry)) throw new InvalidDataException("Invalid package size or duplicate file.");
        }
        foreach (var required in new[] { "VrcCounter.exe", "VrcCounter.dll", "VrcCounter.runtimeconfig.json", "Web/Templates/index.html", "Web/Templates/edit.html", "Updater/Install-Update.ps1" })
            if (!files.ContainsKey(required)) throw new InvalidDataException($"Package is missing {required}.");
        Directory.CreateDirectory(destination);
        foreach (var (relative, entry) in files)
        {
            var path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, false);
        }
        return files.Keys.ToArray();
    }

    public static void ValidateRelativePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
            || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe package path.");
        if (parts.Any(p => p.Equals("python", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".WebView2", StringComparison.OrdinalIgnoreCase))
            || Regex.IsMatch(parts[^1], @"\.(sqlite3?|db)(-wal|-shm)?$|^vrc_multi_param_counter\.config\.json$|^last-avatar-parameters\.json", RegexOptions.IgnoreCase))
            throw new InvalidDataException("The package contains personal runtime data.");
    }
}
