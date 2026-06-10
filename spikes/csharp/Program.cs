// silicon-scope C# AOT spike: same scope as the Rust spike.
//
//   1. PDH per-PID CPU% sampling via P/Invoke (avoids PerformanceCounter
//      reflection paths that struggle under AOT).
//   2. DXGI adapter enumeration via P/Invoke.
//   3. Spectre.Console Live region rendering.
//
// Run as:    spike-csharp.exe --pid 1234
// Quit with: Ctrl+C

using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;

internal static class Program
{
    static int Main(string[] args)
    {
        var pid = ParsePid(args);
        if (pid is null)
        {
            Console.Error.WriteLine("usage: spike-csharp.exe --pid <int>");
            return 1;
        }

        var adapters = EnumerateDxgiAdapters();
        var instance = ResolveProcessInstance(pid.Value);
        using var pdh = new PdhSession($@"\Process({instance})\% Processor Time");
        pdh.Collect();

        var coreCount = Environment.ProcessorCount;
        var cpuPct = 0.0;
        var lastSample = Stopwatch.StartNew();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("metric");
        table.AddColumn(new TableColumn("value").RightAligned());

        if (Console.IsOutputRedirected)
        {
            // No TTY (e.g., piped from PowerShell for measurement). Skip Live
            // and just spin the sampling loop so we can capture working set.
            for (var t = 0; t < 8; t++)
            {
                Thread.Sleep(250);
                pdh.Collect();
                cpuPct = Math.Clamp(pdh.Value() / coreCount, 0.0, 100.0);
                Console.WriteLine($"tick {t}: cpu={cpuPct:F1}%  adapters={adapters.Count}");
            }
            return 0;
        }

        AnsiConsole.Live(table).Start(ctx =>
        {
            var ticks = 0;
            while (ticks < 8)
            {
                if (lastSample.ElapsedMilliseconds >= 250)
                {
                    pdh.Collect();
                    cpuPct = Math.Clamp(pdh.Value() / coreCount, 0.0, 100.0);
                    lastSample.Restart();
                    ticks++;
                }

                table.Rows.Clear();
                table.AddRow($"PID {pid}", $"{cpuPct:F1}%");
                foreach (var (i, a) in adapters.Select((a, i) => (i, a)))
                {
                    table.AddRow($"adapter {i}", $"{a.Name}  vendor=0x{a.VendorId:x4}  device=0x{a.DeviceId:x4}");
                }
                ctx.Refresh();
                Thread.Sleep(50);
            }
        });

        return 0;
    }

    static int? ParsePid(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pid" && int.TryParse(args[i + 1], out var v))
                return v;
        }
        return null;
    }

    // -- DXGI adapter enumeration via P/Invoke -------------------------------

    record AdapterInfo(string Name, uint VendorId, uint DeviceId);

    static List<AdapterInfo> EnumerateDxgiAdapters()
    {
        var iidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        var hr = CreateDXGIFactory1(ref iidFactory1, out var factoryPtr);
        if (hr != 0) throw new InvalidOperationException($"CreateDXGIFactory1 failed: 0x{hr:x}");

        var factory = new DxgiFactory1(factoryPtr);
        var result = new List<AdapterInfo>();
        for (uint i = 0; ; i++)
        {
            if (factory.EnumAdapters1(i, out var adapterPtr) != 0) break;
            var adapter = new DxgiAdapter1(adapterPtr);
            if (adapter.GetDesc1(out var desc) == 0)
            {
                unsafe
                {
                    var name = new string((char*)desc.Description).TrimEnd('\0');
                    result.Add(new AdapterInfo(name, desc.VendorId, desc.DeviceId));
                }
            }
            adapter.Release();
        }
        factory.Release();
        return result;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr ppFactory);

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct DXGI_ADAPTER_DESC1
    {
        public fixed byte Description[256]; // 128 wchar_t
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    sealed class DxgiFactory1
    {
        readonly IntPtr _ptr;
        public DxgiFactory1(IntPtr ptr) { _ptr = ptr; }

        public int EnumAdapters1(uint i, out IntPtr ppAdapter)
        {
            // IDXGIFactory1::EnumAdapters1 is slot 12 in the vtable.
            unsafe
            {
                var vtbl = *(IntPtr**)_ptr;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[12];
                IntPtr p;
                var hr = fn(_ptr, i, &p);
                ppAdapter = p;
                return hr;
            }
        }

        public void Release()
        {
            unsafe
            {
                var vtbl = *(IntPtr**)_ptr;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
                fn(_ptr);
            }
        }
    }

    sealed class DxgiAdapter1
    {
        readonly IntPtr _ptr;
        public DxgiAdapter1(IntPtr ptr) { _ptr = ptr; }

        public int GetDesc1(out DXGI_ADAPTER_DESC1 desc)
        {
            // IDXGIAdapter1::GetDesc1 is slot 10 in the vtable.
            unsafe
            {
                var vtbl = *(IntPtr**)_ptr;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>)vtbl[10];
                DXGI_ADAPTER_DESC1 d;
                var hr = fn(_ptr, &d);
                desc = d;
                return hr;
            }
        }

        public void Release()
        {
            unsafe
            {
                var vtbl = *(IntPtr**)_ptr;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
                fn(_ptr);
            }
        }
    }

    // -- PDH wrapper via P/Invoke --------------------------------------------

    sealed class PdhSession : IDisposable
    {
        IntPtr _query;
        IntPtr _counter;

        public PdhSession(string counterPath)
        {
            var r = PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out _query);
            if (r != 0) throw new InvalidOperationException($"PdhOpenQueryW failed: 0x{r:x}");
            r = PdhAddEnglishCounterW(_query, counterPath, IntPtr.Zero, out _counter);
            if (r != 0)
            {
                PdhCloseQuery(_query);
                throw new InvalidOperationException($"PdhAddEnglishCounterW failed: 0x{r:x} for {counterPath}");
            }
        }

        public void Collect()
        {
            var r = PdhCollectQueryData(_query);
            if (r != 0) throw new InvalidOperationException($"PdhCollectQueryData failed: 0x{r:x}");
        }

        public double Value()
        {
            var r = PdhGetFormattedCounterValue(_counter, PDH_FMT_DOUBLE, IntPtr.Zero, out var fmt);
            if (r != 0) throw new InvalidOperationException($"PdhGetFormattedCounterValue failed: 0x{r:x}");
            return fmt.doubleValue;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
        }

        const uint PDH_FMT_DOUBLE = 0x00000200;

        [StructLayout(LayoutKind.Sequential)]
        struct PdhFmtCounterValue
        {
            public uint CStatus;
            public uint pad0;
            public double doubleValue;
            public IntPtr longStatus;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint PdhOpenQueryW(IntPtr szDataSource, IntPtr dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll", ExactSpelling = true)]
        static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll", ExactSpelling = true)]
        static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, out PdhFmtCounterValue pValue);

        [DllImport("pdh.dll", ExactSpelling = true)]
        static extern uint PdhCloseQuery(IntPtr hQuery);
    }

    static string ResolveProcessInstance(int pid)
    {
        // Use the basename of the process exe as the PDH instance name. The
        // spike assumes only one instance with that basename is alive.
        using var p = Process.GetProcessById(pid);
        var name = p.ProcessName;
        return name;
    }
}
