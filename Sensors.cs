using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TBMini
{
    public enum GpuPower { Unknown, Active, Asleep, Off }

    /// Where TotalW comes from.
    public enum TotalSource { None, Platform, Battery, CpuGpu }

    public sealed class SensorData
    {
        public double CpuTemp = double.NaN, CpuW = double.NaN, GpuTemp = double.NaN, GpuW = double.NaN, TotalW = double.NaN;
        public double ChargeW = double.NaN;     // power going into the battery while charging on AC (not total draw)
        public GpuPower Gpu = GpuPower.Unknown;
        public TotalSource Total;
    }

    // ---------------------------------------------------------------------------------------------
    // Live temperatures and power for the panel's header. Only polled while the panel is visible;
    // End()/Release() close every handle when it hides, so nothing here runs or stays open in the background.
    //   CPU power : Windows Energy Meter counter (Intel RAPL package), no admin
    //   CPU temp  : package digital thermal sensor (MSR 0x1B1) through the PawnIO driver, if installed (admin)
    //   GPU       : NVML, but only while the dGPU is already in D0 (PnP power data), so a sleeping
    //               dGPU is never woken up just to be measured
    //   Total     : platform energy counter (RAPL PSys, MSR 0x64D) through PawnIO when the board wires it up;
    //               otherwise the battery discharge rate on battery; otherwise CPU + GPU on AC
    // ---------------------------------------------------------------------------------------------
    public static class Sensors
    {
        static readonly object Gate = new object();
        static volatile bool _active;

        /// Call before polling; Release() ends the session (a read still in flight then opens nothing).
        public static void Begin() => _active = true;

        public static SensorData Read()
        {
            lock (Gate)
            {
                var d = new SensorData();
                if (!_active) return d;
                try { Msr.Prime(); } catch { }          // baseline for the platform counter before RAPL's first-sample pause
                try { d.CpuW = Rapl.PackageWatts(); } catch { }
                try { d.CpuTemp = Msr.CpuTemp(); } catch { }
                try { ReadGpu(d); } catch { }
                try { ReadTotal(d); } catch { }
                return d;
            }
        }

        /// Panel hidden: stop and close every handle in the background (unless polling resumed meanwhile).
        public static void End()
        {
            _active = false;
            System.Threading.Tasks.Task.Run(() => { lock (Gate) if (!_active) CloseAll(); });
        }

        public static void Release()
        {
            _active = false;
            lock (Gate) CloseAll();
        }

        static void CloseAll()
        {
            try { Rapl.Close(); } catch { }
            try { Nvml.Shutdown(); } catch { }
            try { Msr.Close(); } catch { }
            try { Battery.Close(); } catch { }
        }

        /// True when PawnIO is installed (whether or not this process may use it).
        public static bool PawnIOInstalled => Pio.LibraryPath() != null;

        public static string Fmt(double v, int decimals = 0) =>
            double.IsNaN(v) ? "n/a" : v.ToString(decimals == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);

        static void ReadGpu(SensorData d)
        {
            d.Gpu = DGpu.State();
            if (d.Gpu != GpuPower.Active) { Nvml.Shutdown(); return; }
            if (Nvml.Read(out double t, out double w)) { d.GpuTemp = t; d.GpuW = w; }
        }

        static void ReadTotal(SensorData d)
        {
            bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            double discharge = double.NaN, charge = double.NaN;
            try { Battery.Read(out discharge, out charge); } catch { }
            if (!onBattery) d.ChargeW = charge;
            double p = double.NaN;
            try { p = Msr.PlatformWatts(); } catch { }
            if (!double.IsNaN(p)) { d.TotalW = p; d.Total = TotalSource.Platform; return; }
            if (onBattery)
            {
                if (!double.IsNaN(discharge)) { d.TotalW = discharge; d.Total = TotalSource.Battery; }
                return;
            }
            if (!double.IsNaN(d.CpuW))
            {
                d.TotalW = d.CpuW + (d.Gpu == GpuPower.Active && !double.IsNaN(d.GpuW) ? d.GpuW : 0);
                d.Total = TotalSource.CpuGpu;
            }
        }

        // ------------------------------------------------------------------ CPU package power (PDH)
        static class Rapl
        {
            [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQueryW(string src, IntPtr user, out IntPtr q);
            [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr c);
            [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
            [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr c, uint fmt, out uint type, out FmtValue v);
            [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr q);

            [StructLayout(LayoutKind.Explicit)]
            struct FmtValue { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Value; }

            const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
            const string Path = @"\Energy Meter(rapl_package0_pkg)\Power";   // milliwatts
            static IntPtr _q, _c;
            static bool _missing;

            public static double PackageWatts()
            {
                if (_missing) return double.NaN;
                if (_q == IntPtr.Zero)
                {
                    if (PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) { _q = IntPtr.Zero; _missing = true; return double.NaN; }
                    if (PdhAddEnglishCounterW(_q, Path, IntPtr.Zero, out _c) != 0) { Close(); _missing = true; return double.NaN; }
                    PdhCollectQueryData(_q);                // power is computed between two samples:
                    System.Threading.Thread.Sleep(250);     // prime once so the first reading is not empty
                }
                if (PdhCollectQueryData(_q) != 0) return double.NaN;
                if (PdhGetFormattedCounterValue(_c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) != 0 || v.CStatus > 1) return double.NaN;
                return v.Value > 0 && v.Value < 1e6 ? v.Value / 1000.0 : double.NaN;
            }

            public static void Close()
            {
                if (_q != IntPtr.Zero) PdhCloseQuery(_q);
                _q = _c = IntPtr.Zero;
            }
        }

        // ------------------------------------------------------------------ PawnIO (optional driver)
        // PawnIO (https://pawnio.eu) is a signed driver that runs small signed modules. TBMini embeds the official
        // IntelMSR module, which only allows reading a fixed list of MSRs.
        // TBMini never installs the driver; without it (or without admin rights) these readings are simply absent.
        static class Pio
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
            [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenFn(out IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int LoadFn(IntPtr handle, byte[] blob, UIntPtr size);
            [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
            delegate int ExecFn(IntPtr handle, string name, ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returned);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CloseFn(IntPtr handle);

            static bool _loaded, _unusable;
            static OpenFn _open; static LoadFn _load; static ExecFn _exec; static CloseFn _close;

            public static string LibraryPath()
            {
                try
                {
                    string dir = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO", "InstallLocation", null) as string;
                    if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
                    string lib = Path.Combine(dir, "PawnIOLib.dll");
                    return File.Exists(lib) ? lib : null;
                }
                catch { return null; }
            }

            static IntPtr Proc(IntPtr m, string name) => m == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(m, name);

            static bool Ready()
            {
                if (_loaded) return true;
                if (_unusable) return false;
                bool intel = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "VendorIdentifier", "") as string == "GenuineIntel";
                string lib = Program.IsAdmin && intel ? LibraryPath() : null;
                // TBMini runs elevated: only load the library when it carries a valid signature from PawnIO's author
                if (lib == null || !Trust.IsSignedBy(lib, "O=namazso")) { _unusable = true; return false; }
                IntPtr m = LoadLibraryExW(lib, IntPtr.Zero, 0x00000100 | 0x00000800);   // DLL dir + System32 only
                IntPtr po = Proc(m, "pawnio_open"), pl = Proc(m, "pawnio_load"), pe = Proc(m, "pawnio_execute"), pc = Proc(m, "pawnio_close");
                if (po == IntPtr.Zero || pl == IntPtr.Zero || pe == IntPtr.Zero || pc == IntPtr.Zero) { _unusable = true; return false; }
                _open = (OpenFn)Marshal.GetDelegateForFunctionPointer(po, typeof(OpenFn));
                _load = (LoadFn)Marshal.GetDelegateForFunctionPointer(pl, typeof(LoadFn));
                _exec = (ExecFn)Marshal.GetDelegateForFunctionPointer(pe, typeof(ExecFn));
                _close = (CloseFn)Marshal.GetDelegateForFunctionPointer(pc, typeof(CloseFn));
                return _loaded = true;
            }

            public static byte[] Module(string resource)
            {
                using var s = typeof(Sensors).Assembly.GetManifestResourceStream(resource);
                if (s == null) return null;
                var b = new byte[s.Length];
                int n = 0;
                while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                return b;
            }

            /// Opens a PawnIO handle with the module loaded. Zero when unavailable; refused is set when it never will
            /// be (no driver, no admin, or the module declined this hardware), as opposed to the service being stopped.
            public static IntPtr Open(byte[] module, out bool refused)
            {
                refused = false;
                if (module == null || !Ready()) { refused = true; return IntPtr.Zero; }
                if (_open(out IntPtr h) != 0 || h == IntPtr.Zero) return IntPtr.Zero;     // service stopped: retry next session
                if (_load(h, module, (UIntPtr)module.Length) != 0) { _close(h); refused = true; return IntPtr.Zero; }
                return h;
            }

            public static bool Exec(IntPtr h, string fn, ulong[] input, ulong[] output) =>
                _exec(h, fn, input, (UIntPtr)input.Length, output, (UIntPtr)output.Length, out _) == 0;

            public static void Close(IntPtr h) { if (h != IntPtr.Zero) { try { _close(h); } catch { } } }
        }

        // CPU package temperature and platform (PSys) energy through the IntelMSR module
        static class Msr
        {
            const uint MSR_RAPL_POWER_UNIT = 0x606, MSR_TEMPERATURE_TARGET = 0x1A2, MSR_PACKAGE_THERM_STATUS = 0x1B1, MSR_PLATFORM_ENERGY = 0x64D;
            static byte[] _module;
            static bool _unusable;
            static IntPtr _h;
            static int _tjMax;
            static double _joulesPerUnit;
            static bool _noPsys;                        // the board does not wire up PSys (counter never moves)
            static int _psysFlat;
            static bool _havePrev;
            static uint _prevCount;
            static long _prevTicks;

            static bool Open()
            {
                if (_h != IntPtr.Zero) return true;
                if (_unusable) return false;
                _module ??= Pio.Module("IntelMSR.bin");
                _h = Pio.Open(_module, out bool refused);
                if (_h == IntPtr.Zero) { _unusable = refused; return false; }
                if (_tjMax == 0)
                {
                    int tj = Read(MSR_TEMPERATURE_TARGET, out ulong tt) ? (int)((tt >> 16) & 0xFF) : 0;
                    _tjMax = tj >= 60 && tj <= 130 ? tj : 100;
                    _joulesPerUnit = Read(MSR_RAPL_POWER_UNIT, out ulong u) ? 1.0 / (1L << (int)((u >> 8) & 0x1F)) : 0;
                    if (_joulesPerUnit == 0) _noPsys = true;
                }
                return true;
            }

            static bool Read(uint msr, out ulong value)
            {
                var o = new ulong[1];
                bool ok = Pio.Exec(_h, "ioctl_read_msr", new ulong[] { msr }, o);
                value = o[0];
                return ok;
            }

            public static double CpuTemp()
            {
                if (!Open() || !Read(MSR_PACKAGE_THERM_STATUS, out ulong v)) return double.NaN;
                int t = _tjMax - (int)((v >> 16) & 0x7F);       // digital readout = degrees below TjMax
                return t > 0 && t < 125 ? t : double.NaN;
            }

            /// Takes the first sample of the platform energy counter, so the next reading has an interval.
            public static void Prime()
            {
                if (_havePrev || _noPsys || !Open() || !Read(MSR_PLATFORM_ENERGY, out ulong v)) return;
                _prevCount = (uint)v; _prevTicks = System.Diagnostics.Stopwatch.GetTimestamp(); _havePrev = true;
            }

            public static double PlatformWatts()
            {
                if (_noPsys || !Open() || !Read(MSR_PLATFORM_ENERGY, out ulong v)) return double.NaN;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                uint count = (uint)v;
                double w = double.NaN;
                if (_havePrev)
                {
                    double dt = (now - _prevTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                    uint delta = unchecked(count - _prevCount);                 // 32-bit counter wraps
                    if (dt > 0.05)
                    {
                        if (delta == 0) { if (++_psysFlat >= 2) _noPsys = true; }  // never moves: not wired on this board
                        else { _psysFlat = 0; double p = delta * _joulesPerUnit / dt; if (p > 0.5 && p < 500) w = p; }
                    }
                }
                _prevCount = count; _prevTicks = now; _havePrev = true;
                return w;
            }

            public static void Close()
            {
                Pio.Close(_h);
                _h = IntPtr.Zero;
                _havePrev = false;
            }
        }

        // ------------------------------------------------------------------ battery discharge (battery class IOCTLs)
        static class Battery
        {
            static Guid BatteryClass = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");   // GUID_DEVICE_BATTERY
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid cls, string id, uint flags);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string id, char[] buf, int len, uint flags);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint input, int inLen, out uint output, int outLen, out int returned, IntPtr overlapped);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref WaitStatus input, int inLen, out Status output, int outLen, out int returned, IntPtr overlapped);

            [StructLayout(LayoutKind.Sequential)] struct WaitStatus { public uint Tag, Timeout, PowerState, Low, High; }
            [StructLayout(LayoutKind.Sequential)] struct Status { public uint PowerState, Capacity, Voltage; public int Rate; }

            const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
            const uint BATTERY_DISCHARGING = 0x2, BATTERY_CHARGING = 0x4, PRESENT = 0x0;   // PRESENT = CM_GET_DEVICE_INTERFACE_LIST_PRESENT
            static SafeFileHandle[] _h;
            static uint[] _tag;

            static void Open()
            {
                _h = new SafeFileHandle[0]; _tag = new uint[0];
                if (CM_Get_Device_Interface_List_SizeW(out int len, ref BatteryClass, null, PRESENT) != 0 || len <= 1) return;
                var buf = new char[len];
                if (CM_Get_Device_Interface_ListW(ref BatteryClass, null, buf, len, PRESENT) != 0) return;
                var hs = new System.Collections.Generic.List<SafeFileHandle>(); var tags = new System.Collections.Generic.List<uint>();
                foreach (var path in new string(buf).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var h = CreateFileW(path, 0xC0000000 /*GENERIC_READ|WRITE*/, 3, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x80, IntPtr.Zero);
                    if (h.IsInvalid) { h.Dispose(); continue; }
                    uint wait = 0;
                    if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref wait, 4, out uint tag, 4, out _, IntPtr.Zero) || tag == 0) { h.Dispose(); continue; }
                    hs.Add(h); tags.Add(tag);
                }
                _h = hs.ToArray(); _tag = tags.ToArray();
            }

            /// Watts leaving the battery (whole-system draw when unplugged) and watts going into it while charging;
            /// NaN when not discharging / not charging.
            public static void Read(out double dischargeW, out double chargeW)
            {
                dischargeW = chargeW = double.NaN;
                if (_h == null) Open();
                double outMw = 0, inMw = 0; bool dis = false, chg = false;
                for (int i = 0; i < _h.Length; i++)
                {
                    var w = new WaitStatus { Tag = _tag[i] };
                    if (!DeviceIoControl(_h[i], IOCTL_BATTERY_QUERY_STATUS, ref w, Marshal.SizeOf<WaitStatus>(), out Status s, Marshal.SizeOf<Status>(), out _, IntPtr.Zero))
                    { Close(); return; }                              // tag changed (battery re-enumerated): reopen next time
                    if (s.Rate == int.MinValue) continue;             // BATTERY_UNKNOWN_RATE
                    if ((s.PowerState & BATTERY_DISCHARGING) != 0 && s.Rate < 0) { outMw += -(double)s.Rate; dis = true; }
                    else if ((s.PowerState & BATTERY_CHARGING) != 0 && s.Rate > 0) { inMw += s.Rate; chg = true; }
                }
                if (dis && outMw < 500000) dischargeW = outMw / 1000.0;
                if (chg && inMw < 500000) chargeW = inMw / 1000.0;
            }

            public static void Close()
            {
                if (_h != null) foreach (var h in _h) h.Dispose();
                _h = null; _tag = null;
            }
        }

        // ------------------------------------------------------------------ dGPU power state without waking it
        static class DGpu
        {
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out int len, string filter, uint flags);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string filter, char[] buf, int len, uint flags);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint dn, string id, int flags);
            [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint dn, int flags);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_PropertyW(uint dn, ref DevPropKey key, out uint type, byte[] buf, ref int size, int flags);

            [StructLayout(LayoutKind.Sequential)] struct DevPropKey { public Guid Fmtid; public uint Pid; }

            static readonly Guid DevGroup = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
            static readonly Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
            const uint FILTER_ENUMERATOR = 0x1, FILTER_PRESENT = 0x100, DN_STARTED = 0x8;
            static string _id;

            static byte[] Prop(uint dn, uint pid)
            {
                var k = new DevPropKey { Fmtid = DevGroup, Pid = pid };
                var buf = new byte[64]; int size = buf.Length;
                return CM_Get_DevNode_PropertyW(dn, ref k, out _, buf, ref size, 0) == 0 ? buf : null;
            }

            static string Find()
            {
                if (CM_Get_Device_ID_List_SizeW(out int len, "PCI", FILTER_ENUMERATOR | FILTER_PRESENT) != 0 || len <= 1) return null;
                var buf = new char[len];
                if (CM_Get_Device_ID_ListW("PCI", buf, len, FILTER_ENUMERATOR | FILTER_PRESENT) != 0) return null;
                foreach (var id in new string(buf).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!id.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (CM_Locate_DevNodeW(out uint dn, id, 0) != 0) continue;
                    var cls = Prop(dn, 10);   // DEVPKEY_Device_ClassGuid
                    if (cls == null) continue;
                    var g = new byte[16];
                    Array.Copy(cls, g, 16);
                    if (new Guid(g) == DisplayClass) return id;
                }
                return null;
            }

            public static GpuPower State()
            {
                uint dn = 0;
                if (_id == null || CM_Locate_DevNodeW(out dn, _id, 0) != 0)
                {
                    _id = Find();
                    if (_id == null || CM_Locate_DevNodeW(out dn, _id, 0) != 0) return GpuPower.Off;   // detached (iGPU only) or absent
                }
                if (CM_Get_DevNode_Status(out uint st, out uint problem, dn, 0) != 0 || (st & DN_STARTED) == 0 || problem != 0)
                    return GpuPower.Off;
                var pd = Prop(dn, 32);        // DEVPKEY_Device_PowerData -> CM_POWER_DATA.PD_MostRecentPowerState
                if (pd == null) return GpuPower.Unknown;
                return BitConverter.ToInt32(pd, 4) == 1 /*PowerDeviceD0*/ ? GpuPower.Active : GpuPower.Asleep;
            }
        }

        // ------------------------------------------------------------------ NVIDIA NVML (System32\nvml.dll from the driver)
        static class Nvml
        {
            [DllImport("nvml.dll")] static extern int nvmlInit_v2();
            [DllImport("nvml.dll")] static extern int nvmlShutdown();
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint t);
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);

            static bool _inited, _missing;
            static IntPtr _dev;

            public static bool Read(out double temp, out double watts)
            {
                temp = watts = double.NaN;
                if (_missing) return false;
                try
                {
                    if (!_inited)
                    {
                        if (nvmlInit_v2() != 0) return false;
                        _inited = true;
                        if (nvmlDeviceGetHandleByIndex_v2(0, out _dev) != 0) { Shutdown(); return false; }
                    }
                    if (nvmlDeviceGetTemperature(_dev, 0 /*NVML_TEMPERATURE_GPU*/, out uint t) == 0 && t > 0 && t < 120) temp = t;
                    if (nvmlDeviceGetPowerUsage(_dev, out uint mw) == 0 && mw < 1000000) watts = mw / 1000.0;
                    return true;
                }
                catch (DllNotFoundException) { _missing = true; return false; }
                catch (EntryPointNotFoundException) { _missing = true; return false; }
            }

            public static void Shutdown()
            {
                if (!_inited) return;
                _inited = false; _dev = IntPtr.Zero;
                try { nvmlShutdown(); } catch { }
            }
        }
    }
}
