# silicon-scope — Copilot instructions

Read `HANDOFF.md` at the repo root before doing anything substantial. It is the authoritative spec: v1 scope, lightweight constraints, stack decision (WinUI 3 vs WPF vs WinUI 3 + NativeAOT), file-by-file keep/rewrite table, and the first-session task list. This file is a short pointer; HANDOFF.md is the contract.

## What this is

A tiny always-on-top floating window (~320×120 px) that shows live CPU / GPU / NPU usage for one process and its child processes. Built to support an on-device AI demo where the story is "watch the NPU light up". The current tree is a v0 scaffold; v1 is a refactor, not a rewrite.

## Stack

- WinUI 3 / WindowsAppSDK 2.2.0, `net10.0-windows10.0.26100.0`, C# with `Nullable` + `ImplicitUsings` enabled.
- MVVM via `CommunityToolkit.Mvvm` (no DI container — manual ctor wiring in `App.OnLaunched`, per the lightweight rules in HANDOFF.md).
- Sampling uses `System.Diagnostics.PerformanceCounter` + `System.Management` (WMI for process tree).
- Root namespace is `silicon_scope` (underscore — the project name has a hyphen).

## Build and run

Host arch matters. Detect first, then build:

```powershell
# Always pick platform from $env:PROCESSOR_ARCHITECTURE
$plat = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }
dotnet build silicon-scope\silicon-scope.csproj -p:Platform=$plat -c Debug
```

Never build with `AnyCPU` and never omit `-p:Platform`. The dev box is ARM64 (Snapdragon X Plus); CI / x64 boxes use `x64`.

To run and capture logs, use `winapp` (not `dotnet run`, not file-based logging):

```powershell
winapp run silicon-scope\bin\ARM64\Debug\net10.0-windows10.0.26100.0\ --debug-output
winapp logs   # for diagnostics
```

There are no tests in this repo yet.

## Architecture worth knowing before editing

The v0 scaffold has two layers, and the HANDOFF.md keep/rewrite table tells you which files survive v1:

- **Services (keep as-is unless you find a bug)** — `Services/ProcessLoadMonitor.cs` (single `PeriodicTimer` @ 250 ms, samples CPU/GPU/NPU in sequence on one thread), `Services/ProcessTreeService.cs` (walks `Win32_Process.ParentProcessId` to roll children into the readout), `Services/NpuDetectionService.cs` (Snapdragon Hexagon heuristic: the GPU adapter whose only engine instances are `engtype_Compute`), `Services/MetricSnapshot.cs` (observable model the UI binds to).
- **UI (rewrite for v1)** — `MainWindow`, `Views/ProcessPickerView`, `Views/ProcessLoadView`, and their viewmodels are sized for a projector-style v0 layout. v1 replaces them with a compact floating window and demotes the picker to a launcher dialog shown only when no `--process` / `--pid` arg is supplied.
- **Controls retained but unused in v1** — `Controls/Sparkline.cs` and `Controls/BigNumberReadout.xaml(.cs)` stay on disk for v2.

CLI surface (parse in `App.OnLaunched`): `--process <name>`, `--pid <int>`, or no args → launcher dialog. Window position persists at `%LOCALAPPDATA%\silicon-scope\window.json` (hand-rolled `x y w h` text per HANDOFF — no JSON library).

## Conventions specific to this repo

- **Lightweight is the prime directive.** Targets: < 25 MB self-contained, < 50 MB working set, < 0.5% CPU overhead. Before adding any dependency or abstraction, read the "Lightweight tactics" section in HANDOFF.md. No `Microsoft.Extensions.*`, no JSON library, no DI container, no charting library, no per-metric `Task.Run`.
- **Cache `PerformanceCounter` instances per PID.** Construction is ~10 ms; rebuild only when the tracked PID set changes. Re-walk the process tree every ~5 s, not every tick.
- **No em dashes anywhere in user-visible strings, code comments, commit messages, or docs.** Reword the sentence; do not substitute a hyphen.
- **No emoji in UI** unless explicitly requested. Use `FontIcon` with Segoe Fluent glyphs.
- **`AutomationProperties.AutomationId` on every interactive control** (already true across the v0 scaffold; preserve it through the refactor).
- **WUI2010 analyzer warnings**: the v0 scaffold has 9 known ones from nested `Primary.Cpu.Value` x:Bind paths. Expect them to disappear when the compact UI flattens to single-segment binds; fix any survivors rather than suppressing.

## Workflow

- Always run a `code-review` agent pass with `model: gpt-5.5` against the staged diff before committing. Address findings, then commit.
- Commit messages: imperative present tense, no em dashes, include trailer:
  ```
  Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
  ```
- PowerShell only (Windows host). Chain with `;`, use backslash paths, never bash `&&` / `export`.
- Install tools with `winget`. No zips, no MSIs, no Chocolatey.

## Roadmap shape (affects refactor choices)

v1.5 extracts `silicon-scope.Core` as a class library so a `silicon-scope-tui` (Spectre.Console) can share `ProcessLoadMonitor`, `ProcessTreeService`, `NpuDetectionService`, `MetricSnapshot`. Keep those four services free of WinUI / XAML types so the extraction is mechanical.
