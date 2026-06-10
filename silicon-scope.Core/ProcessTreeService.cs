using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SiliconScope.Core;

/// <summary>
/// Walks process parent / child relationships via the Win32 Toolhelp32 API.
/// Expands a root PID into the full descendant tree.
///
/// Includes a name-based alias map for cases where picking one process
/// should also pull in a non-child collaborator (e.g. "AudioWorker" should
/// also track "Inference.Service.Agent" since Foundry Local is a service
/// not parented under the picker target).
///
/// Implementation note: this used to live on top of <c>System.Management</c>
/// (WMI). WMI is reflection-heavy and not friendly to NativeAOT, so the
/// parent-map build was rewritten against <c>CreateToolhelp32Snapshot</c>
/// + <c>Process32NextW</c> which is plain P/Invoke. Functionally equivalent.
/// </summary>
public sealed partial class ProcessTreeService
{
    private static readonly Dictionary<string, string[]> NameAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Nemotron demo: AudioWorker hosts capture, agent does inference.
            ["AudioWorker"] = new[] { "Inference.Service.Agent" }
        };

    /// <summary>
    /// Returns the union of: the root PID, all descendants found via
    /// parent PID, and any PIDs matching name aliases for the root.
    /// Failures are swallowed (returns at least the root) so callers do not
    /// have to wrap in try / catch.
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
            Debug.WriteLine($"[ProcessTreeService] Toolhelp walk failed: {ex.Message}");
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

        var snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snap == NativeMethods.INVALID_HANDLE_VALUE)
        {
            throw new InvalidOperationException(
                $"CreateToolhelp32Snapshot failed: 0x{Marshal.GetLastWin32Error():x}");
        }

        try
        {
            var pe = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>()
            };
            if (!NativeMethods.Process32FirstW(snap, ref pe)) return map;
            do
            {
                var pid = (int)pe.th32ProcessID;
                var ppid = (int)pe.th32ParentProcessID;
                if (!map.TryGetValue(ppid, out var list))
                {
                    list = new List<int>();
                    map[ppid] = list;
                }
                list.Add(pid);
            }
            while (NativeMethods.Process32NextW(snap, ref pe));
        }
        finally
        {
            NativeMethods.CloseHandle(snap);
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

    private static unsafe partial class NativeMethods
    {
        public const uint TH32CS_SNAPPROCESS = 0x00000002;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            public fixed char szExeFile[260];
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(IntPtr hObject);
    }
}
