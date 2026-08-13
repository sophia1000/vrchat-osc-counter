# VRChat OSC Counter

VRChat OSC Counter is a native C#/.NET desktop application for counting VRChat OSC
parameter triggers, sending formatted Chatbox updates, and graphing counter history.

Features include:

- Multiple counters with threshold/hysteresis or integer-equality triggers
- OSCQuery discovery and automatic receiver advertisement
- SQLite event history with responsive, downsampled long-range graphs
- Multiple Grafana-style dashboards with zoom, pan, ranges, and delta/total modes
- VRChat Chatbox templates, aggregation, rate limiting, and auto-clear
- A native Windows shell powered by WebView2

OSCQuery starts automatically with the OSC listener. It advertises this app's OSC input
port and configured counter addresses so VRChat can discover the receiver automatically.

Graphs read the SQLite event history. Choose **All recorded history** to show every stored
change; shorter ranges intentionally show only events inside their selected time window.

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
