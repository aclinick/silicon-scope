# silicon-scope

A tiny terminal readout that shows live CPU / GPU / NPU usage for one specific
process and its children. Built so on-device AI demos have a focused view you
can pin in a Windows Terminal pane while the demo runs.

> **Status**: v1 TUI. The earlier WinUI 3 scaffold is preserved on the
> `archive/winui-v0` branch.

## What it does

Launch with the process you want to watch:

```powershell
silicon-scope-tui.exe --process AudioWorker
silicon-scope-tui.exe --pid 12345
```

A live panel renders in the terminal showing CPU, GPU, and NPU bars that update
every 500 ms with a 1-second rolling average:

```
 AudioWorker.exe (5 procs)
 CPU  [################------------]  53%
 GPU  [##--------------------------]   6%
 NPU  [######################------]  78%
```

When the tracked process spawns child processes (Foundry Local, helper workers,
etc.) silicon-scope rolls them into the readout, so you see the true total load
attributable to that app. Press `q` to quit.

## Why a TUI

The whole point is a focused, always-visible readout during a demo. Windows
Terminal's per-pane "Always on top" setting does the window chrome work for
free, and a TUI lets the tool stay tiny (3.9 MB AOT-published binary, ~36 MB
working set). The earlier WinUI 3 attempt was archived because it added a 90+
MB framework dependency for a tool whose entire job is one panel of numbers.

See [HANDOFF.md](HANDOFF.md) (superseded but preserved for context) for the
original spec and the lightweight constraints that drove the TUI pivot.
[spikes/README.md](spikes/README.md) has the Rust vs C# AOT measurements that
locked in the C# stack.

## Pinning the readout

Open a small Windows Terminal pane and toggle "Always on top" from the WT
window menu (or the command palette: "Toggle always on top"). The pane stays
visible over the demo app without stealing focus.

## Build

silicon-scope targets .NET 10, ships as a NativeAOT binary, and builds with
`dotnet publish`.

```powershell
# Add VS Installer to PATH so vswhere.exe is found during AOT publish.
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"

# ARM64 host (Snapdragon X)
dotnet publish silicon-scope-tui\silicon-scope-tui.csproj -c Release -r win-arm64 -p:PublishAot=true

# x64 host
dotnet publish silicon-scope-tui\silicon-scope-tui.csproj -c Release -r win-x64 -p:PublishAot=true
```

The single-file binary lands in
`silicon-scope-tui\bin\Release\net10.0-windows\<rid>\publish\silicon-scope-tui.exe`.

### Prerequisites

- .NET 10 SDK
- Visual Studio 2022 (or VS 18 Insiders) with the C++ workload, for the MSVC
  linker NativeAOT calls into
- `vswhere.exe` on PATH (lives in `C:\Program Files (x86)\Microsoft Visual Studio\Installer\`)

For a quick non-AOT debug run, `dotnet run --project silicon-scope-tui` works
and skips the linker requirement.

## Hardware

Built for Snapdragon X Plus (Copilot+ PC, ARM64) with a Qualcomm Hexagon NPU.
Also runs on x64 Windows 11 boxes without an NPU; the NPU bar shows "n/a" when
no compute-only adapter is detected.

## Repository layout

```
silicon-scope.Core/      class library, services, AOT-compatible P/Invoke
silicon-scope-tui/       Spectre.Console TUI, publishes to a single-file AOT binary
spikes/                  Rust + C# AOT measurement spikes (preserved for posterity)
HANDOFF.md               original v1 spec, superseded
```

## Roadmap

- **v1** (now): TUI readout, single process with child rollup, AOT-published.
- **v2** (maybe): direct PDH / ETW sampling if `PerformanceCounter` overhead
  becomes a problem on long demos.
- **v2** (maybe): a thin WinUI variant that consumes the same `Core` library,
  if a GUI is requested.

## License

MIT, see [LICENSE](LICENSE).
