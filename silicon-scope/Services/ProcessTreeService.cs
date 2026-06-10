using System.Diagnostics;
using System.Management;

namespace silicon_scope.Services;

/// <summary>
/// Walks <c>Win32_Process.ParentProcessId</c> via WMI to expand a root PID
/// into the full descendant tree. Includes a name-based alias map for the
/// motivating case where picking "AudioWorker" should also pull in
/// "Inference.Service.Agent" even when that agent is not a child process.
/// </summary>
public sealed class ProcessTreeService
{
    private static readonly Dictionary<string, string[]> NameAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Nemotron demo: AudioWorker hosts capture, agent does inference.
            ["AudioWorker"] = new[] { "Inference.Service.Agent" }
        };

    /// <summary>
    /// Returns the union of: the root PID, all descendants found via
    /// ParentProcessId, and any PIDs matching name aliases for the root.
    /// Failures are swallowed (returns just the root) so callers don't have
    /// to wrap in try/catch.
    /// </summary>
    public IReadOnlyList<int> ExpandToTree(int rootPid, string rootProcessName)
    {
        var pids = new HashSet<int> { rootPid };

        try
        {
            var parentMap = BuildParentMap();
            CollectDescendants(rootPid, parentMap, pids);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessTreeService] WMI walk failed: {ex.Message}");
        }

        if (NameAliases.TryGetValue(rootProcessName, out var aliases))
        {
            foreach (var alias in aliases)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(alias))
                    {
                        pids.Add(p.Id);
                        p.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcessTreeService] Alias lookup failed for '{alias}': {ex.Message}");
                }
            }
        }

        return pids.ToArray();
    }

    private static Dictionary<int, List<int>> BuildParentMap()
    {
        var map = new Dictionary<int, List<int>>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId FROM Win32_Process");
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            try
            {
                var pid = Convert.ToInt32(mo["ProcessId"]);
                var ppid = Convert.ToInt32(mo["ParentProcessId"]);
                if (!map.TryGetValue(ppid, out var list))
                {
                    list = new List<int>();
                    map[ppid] = list;
                }
                list.Add(pid);
            }
            finally
            {
                mo.Dispose();
            }
        }
        return map;
    }

    private static void CollectDescendants(int parent, Dictionary<int, List<int>> map, HashSet<int> acc)
    {
        if (!map.TryGetValue(parent, out var children)) return;
        foreach (var child in children)
        {
            if (acc.Add(child))
            {
                CollectDescendants(child, map, acc);
            }
        }
    }
}
