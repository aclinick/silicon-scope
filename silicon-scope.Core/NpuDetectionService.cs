using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SiliconScope.Core;

/// <summary>
/// Detects the NPU adapter LUID by enumerating the <c>GPU Engine</c>
/// performance counter instances and looking for an adapter whose engines
/// are exclusively <c>engtype_Compute</c>. On Snapdragon X this matches the
/// Qualcomm Hexagon NPU (the Adreno GPU also exposes 3D / Copy / Video).
///
/// Perf counter instance names look like:
///   pid_12345_luid_0x00000000_0x0000A5C2_phys_0_eng_2_engtype_3D
/// We parse the luid_HIGH_LOW + engtype_NAME tokens.
/// </summary>
public sealed partial class NpuDetectionService
{
    [GeneratedRegex(@"luid_0x(?<high>[0-9A-Fa-f]+)_0x(?<low>[0-9A-Fa-f]+).+engtype_(?<engtype>[A-Za-z0-9]+)")]
    private static partial Regex InstanceNameRegex();

    public NpuDetectionResult Detect()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames();

            // luid string -> set of engine types observed
            var byLuid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var instance in instances)
            {
                var m = InstanceNameRegex().Match(instance);
                if (!m.Success) continue;
                var luid = $"0x{m.Groups["high"].Value}_0x{m.Groups["low"].Value}";
                if (!byLuid.TryGetValue(luid, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    byLuid[luid] = set;
                }
                set.Add(m.Groups["engtype"].Value);
            }

            // NPU heuristic: an adapter whose engines are *only* Compute.
            // The Adreno GPU on Snapdragon X exposes 3D, Copy, VideoDecode, etc.
            foreach (var kv in byLuid)
            {
                if (kv.Value.Count > 0 && kv.Value.All(e => string.Equals(e, "Compute", StringComparison.OrdinalIgnoreCase)))
                {
                    return new NpuDetectionResult(
                        IsPresent: true,
                        LuidToken: kv.Key,
                        DisplayName: $"compute adapter {kv.Key}");
                }
            }

            return new NpuDetectionResult(false, null, "no NPU on this system");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NpuDetectionService] Detection failed: {ex.Message}");
            return new NpuDetectionResult(false, null, "NPU detection unavailable");
        }
    }
}
