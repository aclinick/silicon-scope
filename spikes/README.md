# Spikes

Throwaway proofs-of-concept kept for posterity. Not part of the shipped product.

## June 9, 2026: language choice for v1

We weighed Rust vs C# NativeAOT for the v1 TUI rewrite. Both spikes do the same three things: PDH per-PID CPU sampling, DXGI adapter enumeration, and TUI-library initialization under a release/AOT build.

Measured on ARM64 (Snapdragon X Plus), sampling PID 12072 (explorer.exe):

| Metric | rust/ | csharp/ |
|---|---|---|
| Deployable binary | 457 KB | 2.5 MB |
| Working set (peak) | 17.1 MB | 20.2 MB |
| Private bytes (peak) | 7.3 MB | 10.8 MB |
| Threads | 4 | 5 |
| Idle stability | rock-flat 17.1 MB | drifts 18.9-20.2 MB (GC) |

Both worked end-to-end. The C# AOT spike printed `cpu=1.2%` for explorer.exe and correctly enumerated 2 DXGI adapters on the Snapdragon. The Rust spike compiled clean in two iterations against the `windows` 0.59 crate (strong PDH handle types and the COM `GetDesc1` returning by value).

**Decision: C# AOT.** A 3 MB working-set gap does not justify rewriting four already-working C# services in Rust and adopting a new toolchain. See `../HANDOFF.md` for the full reasoning and `silicon-scope.Core` / `silicon-scope-tui` for the actual v1 implementation.

## Building the spikes (if you want to rerun the measurement)

```powershell
# Rust spike
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cd spikes\rust
cargo build --release

# C# AOT spike (requires vswhere.exe on PATH for the AOT linker)
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
cd spikes\csharp
dotnet publish -c Release -r win-arm64 -p:PublishAot=true
```
