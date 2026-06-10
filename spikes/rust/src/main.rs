// silicon-scope Rust spike: prove that Rust can do the three things we care about
// before committing to a full v1 rewrite.
//
//   1. PDH per-PID CPU% sampling (the dominant runtime cost in the real app).
//   2. DXGI adapter enumeration (the primitive behind the NPU detection heuristic).
//   3. ratatui rendering of a compact readout (the v1 UI surface).
//
// Run as:    spike-rust.exe --pid 1234
// Quit with: q

use anyhow::{Context, Result, anyhow};
use clap::Parser;
use crossterm::{
    event::{self, Event, KeyCode},
    execute,
    terminal::{EnterAlternateScreen, LeaveAlternateScreen, disable_raw_mode, enable_raw_mode},
};
use ratatui::{
    Terminal,
    backend::CrosstermBackend,
    layout::{Constraint, Direction, Layout},
    style::{Color, Style},
    widgets::{Block, Borders, Gauge, Paragraph},
};
use std::{
    ffi::OsString,
    io::stdout,
    os::windows::ffi::OsStringExt,
    time::{Duration, Instant},
};
use windows::{
    Win32::Graphics::Dxgi::{
        CreateDXGIFactory1, IDXGIAdapter1, IDXGIFactory1, DXGI_ADAPTER_DESC1,
    },
    Win32::System::Performance::{
        PDH_FMT_DOUBLE, PDH_HCOUNTER, PDH_HQUERY, PdhAddEnglishCounterW, PdhCloseQuery,
        PdhCollectQueryData, PdhGetFormattedCounterValue, PdhOpenQueryW,
    },
    core::PCWSTR,
};

#[derive(Parser, Debug)]
struct Cli {
    /// PID of the process to monitor.
    #[arg(long)]
    pid: u32,
}

fn main() -> Result<()> {
    let cli = Cli::parse();

    // Enumerate DXGI adapters once at startup. This is the primitive the real
    // NpuDetectionService is built on: the Hexagon adapter is the one whose
    // engine instances are all engtype_Compute. The spike just prints them.
    let adapters = enumerate_dxgi_adapters()?;

    // Open a PDH query for the chosen PID. Process counters are looked up by
    // instance name, not PID, so resolve PID -> "name_pid" the way perfmon does.
    let instance_name = resolve_process_instance(cli.pid)?;
    let counter_path = format!("\\Process({})\\% Processor Time", instance_name);

    let mut pdh = PdhSession::new(&counter_path)?;
    // First sample is always zero on PDH; prime the pump.
    pdh.collect()?;

    enable_raw_mode()?;
    let mut stdout = stdout();
    execute!(stdout, EnterAlternateScreen)?;
    let backend = CrosstermBackend::new(stdout);
    let mut terminal = Terminal::new(backend)?;

    let mut last_sample = Instant::now();
    let mut cpu_pct = 0.0f64;
    let render_interval = Duration::from_millis(250);

    loop {
        if last_sample.elapsed() >= render_interval {
            pdh.collect()?;
            // Divide by logical CPU count so the displayed percent is "fraction
            // of all cores", matching the WinUI scaffold's convention.
            let core_count = num_logical_cpus();
            cpu_pct = (pdh.value()? / core_count as f64).clamp(0.0, 100.0);
            last_sample = Instant::now();
        }

        terminal.draw(|f| {
            let area = f.area();
            let chunks = Layout::default()
                .direction(Direction::Vertical)
                .constraints([
                    Constraint::Length(3),                              // CPU gauge
                    Constraint::Length(adapters.len() as u16 + 2),      // adapter list
                    Constraint::Min(1),                                 // hint line
                ])
                .split(area);

            let gauge = Gauge::default()
                .block(Block::default().title(format!(" PID {} CPU ", cli.pid)).borders(Borders::ALL))
                .gauge_style(Style::default().fg(Color::Cyan))
                .ratio((cpu_pct / 100.0).clamp(0.0, 1.0))
                .label(format!("{:>5.1}%", cpu_pct));
            f.render_widget(gauge, chunks[0]);

            let adapter_lines: Vec<String> = adapters
                .iter()
                .enumerate()
                .map(|(i, a)| format!("  {}. {}  vendor=0x{:04x}  device=0x{:04x}", i, a.name, a.vendor_id, a.device_id))
                .collect();
            let adapter_widget = Paragraph::new(adapter_lines.join("\n"))
                .block(Block::default().title(" DXGI adapters ").borders(Borders::ALL));
            f.render_widget(adapter_widget, chunks[1]);

            let hint = Paragraph::new("q to quit");
            f.render_widget(hint, chunks[2]);
        })?;

        if event::poll(Duration::from_millis(50))? {
            if let Event::Key(k) = event::read()? {
                if k.code == KeyCode::Char('q') {
                    break;
                }
            }
        }
    }

    disable_raw_mode()?;
    execute!(terminal.backend_mut(), LeaveAlternateScreen)?;
    terminal.show_cursor()?;
    Ok(())
}

// -- DXGI adapter enumeration ------------------------------------------------

struct AdapterInfo {
    name: String,
    vendor_id: u32,
    device_id: u32,
}

fn enumerate_dxgi_adapters() -> Result<Vec<AdapterInfo>> {
    let mut out = Vec::new();
    unsafe {
        let factory: IDXGIFactory1 = CreateDXGIFactory1().context("CreateDXGIFactory1")?;
        for i in 0u32.. {
            let adapter: IDXGIAdapter1 = match factory.EnumAdapters1(i) {
                Ok(a) => a,
                Err(_) => break,
            };
            let desc: DXGI_ADAPTER_DESC1 = adapter.GetDesc1().context("GetDesc1")?;
            let name_len = desc.Description.iter().position(|&c| c == 0).unwrap_or(desc.Description.len());
            let name = OsString::from_wide(&desc.Description[..name_len])
                .to_string_lossy()
                .into_owned();
            out.push(AdapterInfo {
                name,
                vendor_id: desc.VendorId,
                device_id: desc.DeviceId,
            });
        }
    }
    Ok(out)
}

// -- PDH wrapper -------------------------------------------------------------

struct PdhSession {
    query: PDH_HQUERY,
    counter: PDH_HCOUNTER,
}

impl PdhSession {
    fn new(counter_path: &str) -> Result<Self> {
        let wide_path = to_wide(counter_path);
        unsafe {
            let mut query = PDH_HQUERY::default();
            let r = PdhOpenQueryW(PCWSTR::null(), 0, &mut query);
            if r != 0 {
                return Err(anyhow!("PdhOpenQueryW failed: 0x{:x}", r));
            }
            let mut counter = PDH_HCOUNTER::default();
            let r = PdhAddEnglishCounterW(query, PCWSTR(wide_path.as_ptr()), 0, &mut counter);
            if r != 0 {
                PdhCloseQuery(query);
                return Err(anyhow!("PdhAddEnglishCounterW failed: 0x{:x} for {}", r, counter_path));
            }
            Ok(Self { query, counter })
        }
    }

    fn collect(&mut self) -> Result<()> {
        unsafe {
            let r = PdhCollectQueryData(self.query);
            if r != 0 {
                return Err(anyhow!("PdhCollectQueryData failed: 0x{:x}", r));
            }
        }
        Ok(())
    }

    fn value(&self) -> Result<f64> {
        unsafe {
            let mut fmt = std::mem::zeroed();
            let r = PdhGetFormattedCounterValue(self.counter, PDH_FMT_DOUBLE, None, &mut fmt);
            if r != 0 {
                return Err(anyhow!("PdhGetFormattedCounterValue failed: 0x{:x}", r));
            }
            Ok(fmt.Anonymous.doubleValue)
        }
    }
}

impl Drop for PdhSession {
    fn drop(&mut self) {
        unsafe {
            PdhCloseQuery(self.query);
        }
    }
}

// -- Process instance name resolution ----------------------------------------

fn resolve_process_instance(pid: u32) -> Result<String> {
    // Use the basename of the process exe as the PDH instance name. PDH
    // disambiguates multiple instances with #1, #2, etc. For the spike we
    // assume one instance; the real service will use the ID Process counter
    // to disambiguate. This keeps the spike small.
    use windows::Win32::Foundation::CloseHandle;
    use windows::Win32::System::ProcessStatus::GetProcessImageFileNameW;
    use windows::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};

    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid)
            .context("OpenProcess")?;
        let mut buf = vec![0u16; 1024];
        let n = GetProcessImageFileNameW(handle, &mut buf);
        let _ = CloseHandle(handle);
        if n == 0 {
            return Err(anyhow!("GetProcessImageFileNameW returned 0 for pid {}", pid));
        }
        let path = OsString::from_wide(&buf[..n as usize])
            .to_string_lossy()
            .into_owned();
        let basename = path.rsplit('\\').next().unwrap_or(&path);
        let stem = basename.strip_suffix(".exe").unwrap_or(basename);
        Ok(stem.to_string())
    }
}

fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn num_logical_cpus() -> usize {
    std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(1)
}
