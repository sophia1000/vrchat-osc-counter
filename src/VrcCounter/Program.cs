using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using VrcCounter.Services;
using VrcCounter.UI;
using VrcCounter.Web;

namespace VrcCounter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            if (!args.Contains("--smoke-test")) MessageBox.Show($"VRChat Counter could not start.\n\n{ex}", "VRChat Counter startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        var dataDirectory = ResolveDataDirectory(args); Directory.SetCurrentDirectory(dataDirectory);
        var store = new ConfigStore(Path.Combine(dataDirectory, "vrc_multi_param_counter.config.json"));
        var config = store.LoadAsync().GetAwaiter().GetResult();
        var repository = new EventRepository(Path.Combine(dataDirectory, "vrc_counter_events.sqlite3"));
        repository.InitializeAsync().GetAwaiter().GetResult();
        var state = new AppState(config, store, repository);
        var smokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var serverOnly = args.Contains("--server-only", StringComparer.OrdinalIgnoreCase);
        using var parameters = new AvatarParameterService(state.Osc);
        using var updates = new UpdateService(state, dataDirectory);
        if (smokeTest) config.WebUiPort = VRC.OSCQuery.Extensions.GetAvailableTcpPort();
        if (!smokeTest && !serverOnly) state.Osc.RestartAsync().GetAwaiter().GetResult();

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders(); builder.Logging.AddDebug();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
        builder.WebHost.UseUrls($"http://{config.WebUiBind}:{config.WebUiPort}");
        var app = builder.Build();
        var templateRoot = Path.Combine(AppContext.BaseDirectory, "Web", "Templates");
        app.MapVrcCounter(state, new HtmlRenderer(state, templateRoot), parameters, updates);
        try { app.StartAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { if (!smokeTest) MessageBox.Show($"Could not start the web interface on {config.WebUiBind}:{config.WebUiPort}.\n\n{ex.Message}", "VRChat Counter", MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; state.Osc.DisposeAsync().AsTask().GetAwaiter().GetResult(); repository.DisposeAsync().AsTask().GetAwaiter().GetResult(); return; }

        var navigationHost = config.WebUiBind is "0.0.0.0" or "::" or "*" ? "127.0.0.1" : config.WebUiBind;
        var url = $"http://{navigationHost}:{config.WebUiPort}/";
        if (smokeTest)
        {
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            foreach (var path in new[] { "health", "api/state", "api/counters", "api/graph-prefs?gid=g1", "api/series_multi?counter=Headpats" })
                client.GetAsync(path).GetAwaiter().GetResult().EnsureSuccessStatusCode();
            Console.WriteLine("C# VRChat Counter smoke test passed.");
        }
        else if (serverOnly)
        {
            Console.WriteLine($"C# VRChat Counter server: {url}");
            app.WaitForShutdownAsync().GetAwaiter().GetResult();
        }
        else if (config.WebviewEnabled)
        {
            using var form = new MainForm(state, config, url);
            updates.RequestExit = () => { if (!form.IsDisposed) form.BeginInvoke((Action)(() => form.Close())); };
            updates.Start();
            Application.Run(form);
            updates.RequestExit = null;
        }
        else
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            MessageBox.Show("The VRChat Counter is running in your browser. Click OK to stop it.", "VRChat Counter");
        }
        state.Osc.DisposeAsync().AsTask().GetAwaiter().GetResult();
        app.StopAsync().GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        store.CloseAsync(state.Snapshot()).GetAwaiter().GetResult();
        repository.DisposeAsync().AsTask().GetAwaiter().GetResult();
        updates.LaunchInstallerIfReady();
    }

    private static string ResolveDataDirectory(string[] args)
    {
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length) return Path.GetFullPath(args[index + 1]);
        if (File.Exists(Path.Combine(Environment.CurrentDirectory, "vrc_multi_param_counter.config.json"))) return Environment.CurrentDirectory;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "vrc_multi_param_counter.config.json"))) return dir.FullName;
        return Environment.CurrentDirectory;
    }
}
