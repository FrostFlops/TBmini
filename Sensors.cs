using System;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TBMini
{
    public enum GpuPower { Unknown, Active, Asleep, Off }

    public sealed class SensorData
    {
        public double CpuTemp = double.NaN, CpuW = double.NaN, GpuTemp = double.NaN, GpuW = double.NaN, TotalW = double.NaN;
        public GpuPower Gpu = GpuPower.Unknown;
        public bool TotalIsBattery;      // TotalW = battery discharge (whole system); otherwise CPU + GPU on AC
    }

    // ---------------------------------------------------------------------------------------------
    // Live temperatures and power for the panel's header. Only polled while the panel is visible;
    // Release() closes every handle when it hides, so nothing here runs or stays open in the background.
    //   CPU power : Windows Energy Meter counter (Intel RAPL package), no admin
    //   CPU temp  : LENOVO_GAMEZONE_DATA.GetCPUTemp (admin) where the firmware implements it; the
    //               ThinkBook 16p G6 does not ("invalid object"), so the bubble then shows power only
    //   GPU       : NVML, but only while the dGPU is already in D0 (PnP power data), so a sleeping
    //               dGPU is never woken up just to be measured
    //   Total     : battery discharge rate on battery; on AC there is no whole-system meter, so CPU + GPU
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
                try { d.CpuW = Rapl.PackageWatts(); } catch { }
                d.CpuTemp = Ec.Temp("GetCPUTemp");
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
            try { Ec.Release(); } catch { }
        }

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
            if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
            {
                double mw = 0; bool any = false;
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT Discharging, DischargeRate FROM BatteryStatus");
                foreach (ManagementObject o in s.Get())
                    using (o)
                        if (o["Discharging"] is bool dis && dis && o["DischargeRate"] != null)
                        {
                            uint r = Convert.ToUInt32(o["DischargeRate"]);
                            if (r > 0 && r < 500000) { mw += r; any = true; }
                        }
                if (any) { d.TotalW = mw / 1000.0; d.TotalIsBattery = true; }
                return;
            }
            if (!double.IsNaN(d.CpuW))
                d.TotalW = d.CpuW + (d.Gpu == GpuPower.Active && !double.IsNaN(d.GpuW) ? d.GpuW : 0);
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

        // ------------------------------------------------------------------ Lenovo EC temperatures
        static class Ec
        {
            static ManagementObject _gz;
            static bool _missing;
            static int _fails;

            public static double Temp(string method)
            {
                if (_missing || !Program.IsAdmin) return double.NaN;
                try
                {
                    if (_gz == null)
                    {
                        using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_GAMEZONE_DATA");
                        foreach (ManagementObject o in s.Get()) { _gz = o; break; }
                        if (_gz == null) { _missing = true; return double.NaN; }
                    }
                    uint v = Convert.ToUInt32(_gz.InvokeMethod(method, null, null)["Data"]);
                    _fails = 0;
                    return v > 0 && v < 120 ? v : double.NaN;
                }
                catch
                {
                    Release();
                    if (++_fails >= 3) _missing = true;   // not on this model: stop asking
                    return double.NaN;
                }
            }

            public static void Release() { _gz?.Dispose(); _gz = null; }
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
