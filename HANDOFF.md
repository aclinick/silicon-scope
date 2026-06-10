# silicon-scope handoff

> First-read doc for a fresh Copilot CLI session picking up this repo.
> Created 2026-06-09 by the original Copilot session that scaffolded the WinUI 3 app.

## Mission

A tiny **always-on-top floating window** that shows live CPU / GPU / NPU usage for
**one specific process** (and its child processes). Think Snipping Tool sized, not
Task Manager sized. You launch it pointed at an app you care about and tuck it in
the corner of your screen while you work.

```
+---------------------+
| AudioWorker.exe     |
| CPU 12%  GPU  0%    |
| NPU 84%  ↑Hexagon   |
+---------------------+
```

Default target window: ~320×120 px. Maybe 280×100 in compact mode. Translucent
mica background. Borderless, click-through chrome on the title area to drag.

## Why it exists

Built to support the Contoso-Finance speech-engine demo (see neighbour repo
`EClinick/Contoso-Finance`, branch `feature/npu-speech`,
`SPEECH_ENGINE_EVALUATION.md`). The story we want to tell:

> "Look at the bottom-right of my screen. Watch what happens when I switch
> between Nemotron-on-CPU and WinAI-on-NPU."

Task Manager is too generic, too cluttered, and not on-top. silicon-scope is the
focused readout you wish you had.

## V1 scope (the corrected one — read this carefully)

**In:**
- Single floating window, always-on-top, no taskbar entry, ~320×120 px.
- Tracks ONE primary process plus its child processes (auto-discovered via
  `Win32_Process.ParentProcessId`). Picking `AudioWorker` automatically also
  tracks `Inference.Service.Agent` since Foundry Local is its child.
- Three small readouts: CPU% / GPU% / NPU%, each one number + tiny bar gauge.
  Numbers at ~28pt, not the projector-grade 96pt the v0 scaffold uses.
- Window title shows the target process name. Closing the target process makes
  the readouts go grey but doesn't crash the app — wait for it to come back.
- CLI surface for launching:
  ```
  silicon-scope.exe --process AudioWorker
  silicon-scope.exe --pid 12345
  silicon-scope.exe                       # falls back to a tiny picker dialog
  ```
- Drag the title to reposition. Position persists between runs in
  `%LOCALAPPDATA%\silicon-scope\window.json`.
- A right-click context menu on the window: Pin To Top toggle (on by default),
  Compact / Normal size toggle, Switch Process, Quit.

**Out (cut from the v0 scaffold):**
- Process picker as the dominant UI element. Demote to a one-time launcher
  dialog when no `--process`/`--pid` is supplied.
- Side-by-side pin slot for A/B comparison. (Cool, but if you want side-by-side
  you launch two silicon-scope windows pointed at two different processes.
  Simpler, composable.)
- Projector mode. The whole app is small now; projector mode was solving a
  different design's problem.
- Sparklines for v1. Numbers + bars are enough. Add sparklines in v2 if anyone
  asks. Keep the `Sparkline` control file in the tree, just don't wire it.
- The 96pt big-number readouts. Save the `BigNumberReadout` control as a
  reference but don't use it in V1.

## Lightweight constraints (read before writing any code)

**Andrew's words: "want it to be as lightweight as possible."** This is the
single most important constraint. The tool is a tiny always-on-top readout that
should feel like part of the OS, not like a 100 MB Electron app. Bake the
following into every decision.

### Footprint targets (aspirational, measure don't guess)

| Metric | Target | Why |
|---|---|---|
| Self-contained published size | < 25 MB | Smaller than a Slack message attachment, easy to send around |
| Working set steady-state | < 50 MB | Less than Notepad with a big file |
| CPU overhead from the app itself | < 0.5% of one core | The whole point is measuring others, not perturbing them |
| Cold start to first sample | < 800 ms | Should feel instant |
| Disk I/O steady-state | zero (after window-position load) | No telemetry, no log files |

### Stack decision — reconsider before refactoring

The v0 scaffold uses WinUI 3 / WindowsAppSDK 2.2.0 / net10.0. WinUI 3 published
self-contained is **~100 MB** and cold-starts in ~600 ms on this hardware. That
may already be too heavy for "as lightweight as possible". Three alternatives
worth weighing before committing time to the v1 refactor:

| Stack | Self-contained size | Cold start | Look & feel | Notes |
|---|---|---|---|---|
| WinUI 3 (current scaffold) | ~100 MB | ~600 ms | Mica, full Fluent | Heaviest. Best looking. |
| WinUI 3 + NativeAOT | ~30-40 MB | ~200 ms | Mica, full Fluent | Supported in WinAppSDK 1.6+, requires `<PublishAot>true</PublishAot>` + trimming. Some XAML reflection paths break. |
| **WPF** on net10.0-windows | **~15 MB** | **~150 ms** | Custom-themed, no Mica | Hands-down lightest for a tiny tool. Dark-themed `Window` with `AllowsTransparency=true` looks great at this size. Lose Mica acrylic but for a 320×120 window who cares. |
| Win32 / Direct2D (C++ or Rust) | < 1 MB | < 50 ms | Whatever you draw | Overkill. Not worth it. |

**Recommended**: spike WPF in 30 minutes. If you can render the v1 readout in
WPF with the existing `ProcessLoadMonitor` services, ship that. Drop the WinUI
3 scaffold (keep services). Update this handoff doc to reflect the swap. The
Andrew preference for `winapp run` for diagnostics still applies — `winapp`
works against any Windows app, not just WinAppSDK.

If WPF turns out to lose too much polish, fall back to WinUI 3 + NativeAOT
before settling on plain WinUI 3.

### Lightweight tactics regardless of stack

1. **Cache PerformanceCounter instances per PID.** Constructing a counter is
   expensive (~10 ms each). Rebuild only when the tracked PID set changes.
2. **Don't enumerate all processes every tick.** Re-check the process tree
   every 5 seconds, not every 250 ms. Track-target PID lookup at startup; child
   discovery on a slow timer.
3. **Single timer, single thread for sampling.** No `Task.Run` per metric, no
   per-counter async. One `PeriodicTimer` reads everything in sequence.
4. **No charting library.** Numbers + bars rendered with `Rectangle` + width
   binding. That's it.
5. **No DI container.** Three services, all stateful singletons. Manual ctor
   wiring in `App.OnLaunched`.
6. **No `Microsoft.Extensions.*` if you can avoid it.** Each one adds 1-3 MB
   trimmed. Plain `ILogger` abstraction is overkill for a tool with no logs.
7. **No Dependencies that pull in Json.NET / System.Text.Json reflection** unless
   absolutely needed. Window position persistence can use a hand-rolled
   `int x; int y; int w; int h;` text file. Four lines, no parser library.
8. **Trim the published output**: `<PublishTrimmed>true</PublishTrimmed>` plus
   `<TrimMode>partial</TrimMode>` (or `full` if you can get away with it).
   Test the trimmed output before declaring victory — XAML reflection breaks
   silently under trimming.
9. **No splash screen, no startup animation.** Window appears, samples start.
10. **Composition acrylic only if cheap.** WinUI 3 Mica is free at this size.
    WPF transparency is free too. Don't add `BlurBehind` Win32 effects.
11. **Do not include the WinAppSDK MSIX runtime in the install.** Either rely
    on the user already having it (most Win11 24H2+ boxes do) or, if WPF,
    skip the WinAppSDK entirely. If you ship a self-contained build that
    bundles the SDK, document it as a separate "portable" download.

### What "lightweight" does NOT mean

- Don't strip functionality from v1 (CPU/GPU/NPU + child-process tracking) to
  hit a size target. The features above are what make it useful.
- Don't write it in C++ to save 10 MB. The maintenance cost dwarfs the saving.
- Don't skip code review or testing to keep the diff small.

---


`winui:winui-dev` agent created a working WinUI 3 app on net10.0-windows10.0.26100.0
with the spec-bloated v0 UI. The runtime behaviour was verified — it launches,
samples, and renders. The pieces worth keeping verbatim:

| Path | Status | Notes |
|---|---|---|
| `Services/ProcessLoadMonitor.cs` | **Keep as-is.** | The 250 ms PeriodicTimer + perf-counter sampler. Already does CPU + GPU + NPU correctly. |
| `Services/ProcessTreeService.cs` | **Keep as-is.** | Walks `Win32_Process.ParentProcessId`. Tested: picking AudioWorker expanded to 17 PIDs. |
| `Services/NpuDetectionService.cs` | **Keep as-is.** | Heuristic-based NPU LUID detection works on Snapdragon X Plus. Identifies the Hexagon adapter as "the GPU adapter whose only GPU Engine instances are `engtype_Compute`". |
| `Services/MetricSnapshot.cs` | **Keep as-is.** | The observable model the UI binds to. |
| `Controls/Sparkline.cs`, `Themes/Generic.xaml` | Keep on disk, unused in v1. | Reuse in v2. |
| `Controls/BigNumberReadout.xaml(.cs)`, `Themes/BigNumber.xaml` | Keep on disk, unused in v1. | Reuse if we ever do a projector mode. |
| `silicon-scope.csproj` | Mostly keep. | Targets `net10.0-windows10.0.26100.0`, Platforms `x64;ARM64`. Already correct. |

The pieces to rewrite:

| Path | Action |
|---|---|
| `MainWindow.xaml(.cs)` | Replace with tiny floating window: 320×120 default, AlwaysOnTop, no taskbar, mica background, title + 3-row readout grid. |
| `Views/ProcessPickerView.xaml(.cs)` | Demote to a separate launcher dialog `Views/ProcessLauncherDialog.xaml`, shown only if no CLI args. Strip the side-by-side comparison UI. |
| `Views/ProcessLoadView.xaml(.cs)` | Replace with compact `Views/FloatingReadoutView.xaml`: 3 small rows of `Label : NN% [▮▮▮▮▮▮▱▱▱▱]`. |
| `ViewModels/MainViewModel.cs` | Strip pinned-process logic, projector mode, and the dual readout. Keep CLI arg parsing + window-position persistence. |
| `ViewModels/ProcessPickerViewModel.cs` | Slim to a single-pick combo for the launcher dialog. |
| `ViewModels/ProcessLoadViewModel.cs` | Single set of CPU/GPU/NPU values, not Primary + Pinned. |
| `App.xaml.cs` | Add CLI arg parsing (`--process <name>`, `--pid <int>`). Show launcher dialog when neither is supplied. |

## TUI variant (roadmap item, very on brand)

The CLI is the home turf of the Copilot agent that built this. Add a
**`silicon-scope-tui`** project that's the same data, terminal-rendered:

```
$ silicon-scope-tui AudioWorker
┌─ AudioWorker.exe (+ Inference.Service.Agent) ───┐
│ CPU  ▮▮▱▱▱▱▱▱▱▱  12%                            │
│ GPU  ▱▱▱▱▱▱▱▱▱▱   0%                            │
│ NPU  ▮▮▮▮▮▮▮▮▮▱  84%   ↑ Hexagon                │
└─ q to quit ─────────────────────────────────────┘
```

Build with [Spectre.Console](https://spectreconsole.net/) — it ships
`BarChart`, `Live`, `Rule`, and `Panel` primitives that map directly to what we
need. Live update at 500 ms cadence (slower than the GUI; terminals don't need
250 ms). Share the `ProcessLoadMonitor` / `ProcessTreeService` /
`NpuDetectionService` services by extracting them into a `silicon-scope.Core`
class library that both `silicon-scope` (WinUI) and `silicon-scope-tui`
(console) reference. Suggested repo layout after extract:

```
silicon-scope/
  silicon-scope.sln
  silicon-scope.Core/           ← new class library
    ProcessLoadMonitor.cs
    ProcessTreeService.cs
    NpuDetectionService.cs
    MetricSnapshot.cs
  silicon-scope/                ← WinUI 3 app, references Core
  silicon-scope-tui/            ← new console app, references Core + Spectre.Console
```

The TUI variant is **NOT** v1 work; it's the obvious v1.5 follow-up. Land v1
first, then add the Core library + TUI in one PR.

## First-session task list (suggested order)

1. **Cold-build verify**: `dotnet build -p:Platform=ARM64 -c Debug` (host is ARM64
   Snapdragon X Plus). Should succeed with 9 known WUI2010 analyzer warnings
   from the v0 scaffold.
2. **Read** `Services/ProcessLoadMonitor.cs`, `ProcessTreeService.cs`,
   `NpuDetectionService.cs` end-to-end. Don't touch them unless you find a bug.
3. **Refactor UI to v1 spec** (the rewrites in the table above). Use
   `winapp run <output-dir> --debug-output` after each meaningful change to
   verify the window renders.
4. **Fix the 9 WUI2010 warnings** while refactoring — most will become moot when
   the nested `Primary.Cpu.Value` paths are flattened to single-segment binds in
   the new compact UI.
5. **Code review with `gpt-5.5` via the `code-review` agent before the first
   commit to this repo.** (Stored Andrew preference: always run gpt-5.5
   code-review before committing.) Surface issues, address them, then commit.
6. **Commit and push** as `feat: v1 floating always-on-top single-process readout`.
7. **Then** extract `silicon-scope.Core` and add `silicon-scope-tui` as a
   separate PR.

## Environment & preferences the new session needs to know

These are stored in Andrew's Copilot memory but the new CLI session won't have
loaded them yet; bake them into local context:

- **Windows / PowerShell only** — backslash paths, `;` chains, no bash operators.
- **ARM64 host**: detect via `$env:PROCESSOR_ARCHITECTURE` and pass
  `-p:Platform=ARM64` when building. On x64 hosts pass `-p:Platform=x64`.
- **No em dashes anywhere in user-visible strings or docs.** Reword sentences
  entirely; don't substitute with hyphens. This includes commit messages and
  this very file going forward.
- **winapp for everything**: build, run, debug, capture logs. Specifically
  `winapp run <output-dir> --debug-output` in async PowerShell mode for runs;
  `winapp logs` for diagnostics. Never invent file-based logging.
- **No emoji in UI** unless I (Andrew) explicitly asked for it. Use FontIcon
  with Segoe Fluent glyphs when an icon is needed.
- **Winget for installing tools** — never zip extractions, never direct MSI
  downloads, never Chocolatey.
- **Python on this box** is at
  `C:\Users\andre\AppData\Local\Programs\Python\Python312`; prepend to PATH
  in any PowerShell call that needs it.
- **Code review before every commit**: spawn the `code-review` agent with
  `model: gpt-5.5` against the staged diff. Address findings, then commit.
- **Commit messages**: imperative present tense. Always include the Copilot
  co-author trailer:
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`

## Open questions for Andrew

If/when these matter, ask before assuming:

1. **Click-through?** Should the readout be optionally click-through (mouse
   events pass through to the app underneath), or always interactive? Default
   plan: interactive, with a context-menu toggle.
2. **Per-second average vs instantaneous?** The 250 ms sampler currently shows
   the most recent sample. Should the displayed number be a 1-second rolling
   average instead, so it doesn't twitch? Default plan: 1-second rolling.
3. **Privacy of process names**: any concern about silicon-scope showing
   sensitive process names when screen-sharing? Default plan: no special
   handling; user picks what to monitor.

## Verified by previous session

- WinUI 3 app launches via `winapp run`.
- `ProcessLoadMonitor` correctly samples CPU%, GPU%, NPU% at 250 ms cadence.
- `ProcessTreeService` walks Win32_Process.ParentProcessId successfully (17 PIDs
  expanded from a chosen parent in the verification run).
- `NpuDetectionService` correctly identifies the Hexagon NPU on the Snapdragon
  X Plus host as "compute-only adapter".
- AutomationProperties.AutomationId set on every interactive control.

## What was NOT done

- No screenshot captured.
- v1 floating-window UI not yet built (that's the first session's job).
- TUI variant not built.
- No git commit yet beyond the auto-init from GitHub repo create. This handoff
  doc + the v0 scaffold will be the first real commit.
- No code review run on v0 — deferred so the new session sees analyzer warnings
  and decides whether they survive the v1 refactor or get fixed in flight.

Good luck. Have fun. Build it small.
