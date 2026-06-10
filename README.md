# silicon-scope

A tiny always-on-top window that shows live CPU / GPU / NPU usage for one
specific process (and its children). Built so on-device AI demos have a
focused readout you can tuck in a corner of your screen.

> **Status**: scaffolded, not yet built. See [HANDOFF.md](HANDOFF.md) for the
> v1 spec, lightweight constraints, and the first-session task list.

## What it does

Launch with the process you want to watch:

```powershell
silicon-scope.exe --process AudioWorker
silicon-scope.exe --pid 12345
```

A small floating window appears in the corner of your screen showing three
numbers that update every 250 ms:

```
AudioWorker.exe
CPU  12%   GPU   0%   NPU  84%
```

When the tracked process spawns child processes (Foundry Local, helper
workers, etc.) silicon-scope automatically rolls them into the readout, so you
see the true total load attributable to that app.

## Why

Built to support the [Contoso-Finance speech engine demo](https://github.com/EClinick/Contoso-Finance),
where the story is "watch the NPU bar saturate when WinAI Whisper runs, watch
the CPU bar light up when Nemotron runs". Task Manager works but is too busy,
not on-top, and not focused on one app. silicon-scope is the dedicated readout.

## Design constraints

- **Always-on-top, tiny** (~320×120 px). Stays out of the way.
- **One process at a time.** Want two? Launch two instances.
- **Lightweight.** Targets < 25 MB self-contained, < 50 MB working set,
  < 0.5% CPU overhead. See HANDOFF.md for the full lightweight playbook.
- **No telemetry, no log files, no network.** It samples, renders, exits.

## Hardware

Built for Snapdragon X Plus (Copilot+ PC, ARM64) with a Qualcomm Hexagon NPU.
Also runs on x64 Windows 11 boxes without an NPU; the NPU readout shows
"no NPU on this system".

## Roadmap

- **v1** (next): floating always-on-top window, single process, the spec
  above. See HANDOFF.md.
- **v1.5**: extract `silicon-scope.Core` as a class library and add
  `silicon-scope-tui`, a Spectre.Console terminal variant. Same data,
  terminal-rendered, on brand for the Copilot CLI workflow.

## Build

```powershell
# On ARM64 host
dotnet build -p:Platform=ARM64 -c Debug

# On x64 host
dotnet build -p:Platform=x64 -c Debug
```

See HANDOFF.md for the stack decision (WinUI 3 vs WPF vs WinUI 3 + NativeAOT)
that should be made before the v1 refactor.

## License

MIT, see [LICENSE](LICENSE).
