using System.IO.Compression;
using System.Net;
using System.Text.Json;
using VrcCounter.Models;
using VrcCounter.Services;
using VRC.OSCQuery;

namespace VrcCounter.Tests;

public sealed class UpdateAndParameterTests
{
    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.0.9", "1.1.0", false)]
    [InlineData("1.10.0", "1.9.0", true)]
    public void ReleaseVersions_AreComparedNumerically(string candidate, string current, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewer(candidate, current));

    [Theory]
    [InlineData("1.1.0-beta")]
    [InlineData("latest")]
    [InlineData("1.1")]
    public void ReleaseVersions_RejectUnstableTags(string candidate)
        => Assert.Throws<InvalidDataException>(() => UpdateService.IsNewer(candidate, "1.0.0"));

    [Fact]
    public void ReleaseMetadata_RequiresExpectedRepositoryAssetAndChecksum()
    {
        object Metadata(string digest, string repository = UpdateService.Repository, bool prerelease = false) => new
        {
            tag_name = "v1.1.0", draft = false, prerelease,
            assets = new[] { new { name = UpdateService.AssetName, browser_download_url = $"https://github.com/{repository}/releases/download/v1.1.0/{UpdateService.AssetName}", digest, size = 100 } }
        };
        ReleaseDownload Parse(object value) => UpdateService.ParseRelease(JsonSerializer.SerializeToElement(value));
        Assert.Equal("1.1.0", Parse(Metadata("sha256:" + new string('a', 64))).Version);
        Assert.Throws<InvalidDataException>(() => Parse(Metadata("")));
        Assert.Throws<InvalidDataException>(() => Parse(Metadata("sha256:" + new string('a', 64), "someone/else")));
        Assert.Throws<InvalidDataException>(() => Parse(Metadata("sha256:" + new string('a', 64), prerelease: true)));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("Web/../escape.exe")]
    [InlineData("VrcCounter.exe:payload")]
    [InlineData("Web/CON.txt")]
    [InlineData("vrc_multi_param_counter.config.json")]
    [InlineData("events.sqlite3-wal")]
    [InlineData("python/old.py")]
    [InlineData("VrcCounter.exe.WebView2/settings")]
    public void PackagePaths_RejectTraversalAndPersonalData(string path)
        => Assert.Throws<InvalidDataException>(() => UpdatePackage.ValidateRelativePath(path));

    [Fact]
    public void PackageExtraction_ValidatesAllFilesBeforeWriting()
    {
        using var temp = new TempFolder();
        var zipPath = Path.Combine(temp.Path, "package.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in new[] { "VrcCounter.exe", "VrcCounter.dll", "VrcCounter.runtimeconfig.json", "Web/Templates/index.html", "Web/Templates/edit.html", "Updater/Install-Update.ps1" })
                using (var writer = new StreamWriter(zip.CreateEntry("VRChat-OSC-Counter/" + file).Open())) writer.Write("test");
        }
        var payload = Path.Combine(temp.Path, "payload");
        Assert.Equal(6, UpdatePackage.Extract(zipPath, payload).Length);
        Assert.Equal("test", File.ReadAllText(Path.Combine(payload, "VrcCounter.exe")));
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update)) zip.CreateEntry("VRChat-OSC-Counter/../escape.txt");
        var badPayload = Path.Combine(temp.Path, "bad");
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(zipPath, badPayload));
        Assert.False(Directory.Exists(badPayload));
    }

    [Fact]
    public void ParameterTree_TraversesNestedNamesAndPreservesTypesAndAccess()
    {
        using var json = JsonDocument.Parse("""
        {"FULL_PATH":"/","CONTENTS":{"avatar":{"CONTENTS":{
          "change":{"TYPE":"s","VALUE":["avtr_test"]},
          "parameters":{"CONTENTS":{
            "Touch":{"TYPE":"f","ACCESS":1,"VALUE":[0.75]},
            "Nested":{"CONTENTS":{"Happy":{"TYPE":"T","ACCESS":3,"VALUE":[true]}}},
            "Choice":{"TYPE":"i","VALUE":[2]},
            "InputOnly":{"TYPE":"f","ACCESS":2},
            "Text":{"TYPE":"s","ACCESS":1,"VALUE":["<img src=x>"]}
          }}
        }},"input":{"CONTENTS":{"Jump":{"TYPE":"i","ACCESS":2}}}}}
        """);
        var result = AvatarParameterService.ParseTree(json.RootElement);
        Assert.Equal("avtr_test", result.AvatarId);
        Assert.Equal(5, result.Parameters.Count);
        Assert.Equal("/avatar/parameters/Nested/Happy", result.Parameters.Single(p => p.Name == "Nested/Happy").Address);
        Assert.True(result.Parameters.Single(p => p.Name == "Nested/Happy").CanCount);
        Assert.Equal("Float", result.Parameters.Single(p => p.Name == "Touch").Type);
        Assert.False(result.Parameters.Single(p => p.Name == "InputOnly").CanCount);
        Assert.False(result.Parameters.Single(p => p.Name == "Text").CanCount);
        Assert.DoesNotContain(result.Parameters, p => p.Name == "Jump");
    }

    [Fact]
    public async Task ParameterBrowser_DiscoversLiveTreeAndRefreshesAfterAvatarSwitch()
    {
        using var temp = new TempFolder();
        var cfg = AppConfig.CreateDefault();
        await using var repository = new EventRepository(Path.Combine(temp.Path, "events.sqlite3"));
        await repository.InitializeAsync();
        var state = new AppState(cfg, new ConfigStore(Path.Combine(temp.Path, "config.json")), repository);
        await using var osc = state.Osc;
        await osc.RestartAsync();
        using var browser = new AvatarParameterService(osc);
        using var fakeVrchat = new OSCQueryServiceBuilder().WithServiceName("VRChat-Client-CounterTest-" + Guid.NewGuid().ToString("N"))
            .WithHostIP(IPAddress.Loopback).WithOscIP(IPAddress.Loopback)
            .WithTcpPort(VRC.OSCQuery.Extensions.GetAvailableTcpPort()).WithUdpPort(VRC.OSCQuery.Extensions.GetAvailableUdpPort()).WithDefaults().Build();
        fakeVrchat.AddEndpoint("/avatar/change", "s", Attributes.AccessValues.ReadOnly, ["avtr_first"]);
        fakeVrchat.AddEndpoint("/avatar/parameters/Touch", "f", Attributes.AccessValues.ReadOnly, [0.8f]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        AvatarParameterList first;
        do { first = await browser.GetAsync(true, timeout.Token); if (first.AvatarId == "avtr_first") break; await Task.Delay(500, timeout.Token); } while (true);
        Assert.Single(first.Parameters);
        Assert.Equal("Touch", first.Parameters[0].Name);
        fakeVrchat.RemoveEndpoint("/avatar/parameters/Touch");
        fakeVrchat.AddEndpoint("/avatar/parameters/Poke", "i", Attributes.AccessValues.ReadOnly, [1]);
        fakeVrchat.SetValue("/avatar/change", ["avtr_second"]);
        var second = await browser.GetAsync(true, timeout.Token);
        Assert.Equal("avtr_second", second.AvatarId);
        Assert.Equal("Poke", Assert.Single(second.Parameters).Name);
        fakeVrchat.Dispose();
        var offline = await browser.GetAsync(true, timeout.Token);
        Assert.Empty(offline.Parameters);
    }

    [Fact]
    public void ExistingSettings_EnableAutomaticChecksByDefault()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", JsonOptions.Default)!;
        Assert.True(config.AutoCheckUpdates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installer_PreservesDataAndRollsBackWhenAFileIsLocked(bool simulateFailure)
    {
        using var temp = new TempFolder();
        var install = Path.Combine(temp.Path, "app with spaces");
        var payload = Path.Combine(temp.Path, "payload");
        var backup = Path.Combine(temp.Path, "backup");
        Directory.CreateDirectory(install); Directory.CreateDirectory(payload);
        foreach (var name in new[] { "first.dll", "second.dll" }) await File.WriteAllTextAsync(Path.Combine(install, name), "old");
        foreach (var name in new[] { "first.dll", "new.dll", "second.dll" }) await File.WriteAllTextAsync(Path.Combine(payload, name), "new");
        foreach (var name in new[] { "vrc_multi_param_counter.config.json", "vrc_counter_events.sqlite3", "vrc_counter_events.sqlite3-wal" })
            await File.WriteAllTextAsync(Path.Combine(install, name), "private-data");
        var plan = Path.Combine(temp.Path, "plan.json");
        var marker = Path.Combine(temp.Path, "restart.json");
        await File.WriteAllTextAsync(plan, JsonSerializer.Serialize(new
        {
            ProcessId = int.MaxValue, ProcessStartTicks = "0", InstallDirectory = install,
            PayloadDirectory = payload, BackupDirectory = backup, DataDirectory = install,
            Files = new[] { "first.dll", "new.dll", "second.dll" }
        }));
        using var locked = simulateFailure ? new FileStream(Path.Combine(install, "second.dll"), FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "InstallerHarness.ps1"), "-Helper", Path.Combine(AppContext.BaseDirectory, "Updater/Install-Update.ps1"), "-TestPlan", plan, "-Marker", marker }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var logPath = Path.Combine(temp.Path, "update.log");
        var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : "No update log.";
        var diagnostics = $"Exit {process.ExitCode}\n{await error}\n{await output}\n{log}";
        Assert.True(process.ExitCode == (simulateFailure ? 1 : 0), diagnostics);
        Assert.True((simulateFailure ? "old" : "new") == await File.ReadAllTextAsync(Path.Combine(install, "first.dll")), diagnostics);
        Assert.Equal(!simulateFailure, File.Exists(Path.Combine(install, "new.dll")));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(backup, "first.dll")));
        foreach (var name in new[] { "vrc_multi_param_counter.config.json", "vrc_counter_events.sqlite3", "vrc_counter_events.sqlite3-wal" })
            Assert.Equal("private-data", await File.ReadAllTextAsync(Path.Combine(install, name)));
        Assert.True(File.Exists(marker));
        using var restarted = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
        Assert.Equal(install, restarted.RootElement.GetProperty("WorkingDirectory").GetString());
        Assert.Equal("Hidden", restarted.RootElement.GetProperty("WindowStyle").GetString());
    }

    [Fact]
    public async Task FinalConfigSave_CancelsAnOlderScheduledSnapshot()
    {
        using var temp = new TempFolder();
        var store = new ConfigStore(Path.Combine(temp.Path, "settings.json"));
        var old = AppConfig.CreateDefault();
        await store.FlushAsync(old);
        store.Schedule(() => old, 100);
        var final = old.Clone(); final.AutoCheckUpdates = false;
        await store.CloseAsync(final);
        await Task.Delay(200);
        Assert.False((await store.LoadAsync()).AutoCheckUpdates);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VrcCounterTests", Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
