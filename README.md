# VRChat OSC Counter

VRChat OSC Counter is a native C#/.NET desktop application for counting VRChat OSC
parameter triggers, sending formatted Chatbox updates, and graphing counter history.

Features include:

- Multiple counters with threshold/hysteresis or integer-equality triggers
- OSCQuery discovery and automatic receiver advertisement, with a legacy OSC-only input mode
- SQLite event history with responsive, downsampled long-range graphs
- Multiple Grafana-style dashboards with bounded zoom, drag-to-select, pan, live follow,
  per-series visibility, resizable panels, and delta/total modes
- VRChat Chatbox templates, aggregation, rate limiting, and auto-clear
- A native Windows shell powered by WebView2

OSCQuery is the default input mode. It runs the UDP OSC receiver and advertises the input
port and configured counter addresses so VRChat can discover them automatically. The
global settings can instead select **Legacy OSC**, which keeps the UDP receiver but turns
off OSCQuery discovery. The two input modes are mutually exclusive.

Graphs read the SQLite event history. Time presets define the initial and reset viewport;
they do not hide older history. Zooming and panning are bounded by the first and last real
events, and the default live-follow mode advances as new events arrive.

## Run

Requirements:

- Windows 10 or newer
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Microsoft Edge WebView2 Runtime
- VRChat OSC enabled

Build the application first:

```powershell
dotnet publish .\src\VrcCounter\VrcCounter.csproj -c Release -r win-x64 --self-contained false -o .\VrcCounter-CSharp
```

Then double-click `run-vrc-counter-csharp.bat`, or run from source:

```powershell
dotnet run --project .\src\VrcCounter\VrcCounter.csproj --configuration Release -- --data-dir .
```

On first launch, the application creates a local `vrc_multi_param_counter.config.json`
and `vrc_counter_events.sqlite3`. These files contain personal settings and history and
are intentionally excluded from Git.

## Tests

```powershell
dotnet test .\VrcCounter.slnx -c Release
```

## Data safety

The configuration, SQLite database, WAL/SHM files, backups, Python originals, compiled
output, and WebView2 profile are ignored. Never commit these files if you modify the
ignore rules.
