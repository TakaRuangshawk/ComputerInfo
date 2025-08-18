// Program.cs  (.NET Framework 4.7.2)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Configuration;
class Program
{
    // ===== CONFIG =====
    static string GetLogFile()
    {
        string logDir = ConfigurationManager.AppSettings["LogDirectory"];
        string logPrefix = ConfigurationManager.AppSettings["LogPrefix"];

        if (string.IsNullOrEmpty(logDir))
            logDir = @"C:\Logs"; // fallback ถ้า config ไม่มี
        if (string.IsNullOrEmpty(logPrefix))
            logPrefix = "performance";

        // สร้างโฟลเดอร์ถ้ายังไม่มี
        if (!Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);

        // ตั้งชื่อไฟล์ตามวัน
        string fileName = string.Format("{0}_{1:yyyyMMdd}.log", logPrefix, DateTime.Now);
        return Path.Combine(logDir, fileName);
    }
    const int INTERVAL_MS = 2000; // เก็บทุก 2 วินาที
    // ===================
    [DllImport("kernel32")]
    extern static UInt64 GetTickCount64();

    // RAM (P/Invoke ให้ใช้ได้ทุกเวอร์ชัน)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    // Perf counters (global)
    static PerformanceCounter cpuTotal;
    static List<DiskCounters> disks = new List<DiskCounters>();
    static List<NicCounters> nics = new List<NicCounters>();
    static List<PerformanceCounter> gpu3D = new List<PerformanceCounter>();

    class DiskCounters
    {
        public string Instance; // เช่น "0 C:"
        public string DriveLetter; // "C:"
        public PerformanceCounter Idle;
        public PerformanceCounter ReadBps;
        public PerformanceCounter WriteBps;
    }

    class NicCounters
    {
        public string Instance; // ชื่อใน PerfMon
        public PerformanceCounter Rx;
        public PerformanceCounter Tx;
        public string IPv4;     // best-effort จาก WMI
    }

    static void Main()
    {
        //Console.OutputEncoding = Encoding.UTF8;

        // ----- CPU total -----
        cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        SafeNext(cpuTotal); // warm-up

        // ----- Disks: ดึงทุก instance ที่มี drive letter (เช่น "0 C:") -----
        try
        {
            var cat = new PerformanceCounterCategory("PhysicalDisk");
            foreach (var inst in cat.GetInstanceNames())
            {
                // หา drive letter ในชื่อ instance
                string drv = ExtractDriveLetter(inst); // "C:" | null
                if (drv == null) continue;

                try
                {
                    var idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", inst);
                    var rBps = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst);
                    var wBps = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst);
                    SafeNext(idle); SafeNext(rBps); SafeNext(wBps);
                    disks.Add(new DiskCounters { Instance = inst, DriveLetter = drv, Idle = idle, ReadBps = rBps, WriteBps = wBps });
                }
                catch { /* ข้าม instance ที่เปิดไม่ได้ */ }
            }
        }
        catch { /* ไม่มี PhysicalDisk (หายาก) */ }

        // ----- Network -----
        try
        {
            var cat = new PerformanceCounterCategory("Network Interface");
            var wmiIPs = GetNicIPv4Map(); // map: perf name (approx) -> ip
            foreach (var inst in cat.GetInstanceNames())
            {
                try
                {
                    var rx = new PerformanceCounter("Network Interface", "Bytes Received/sec", inst);
                    var tx = new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst);
                    SafeNext(rx); SafeNext(tx);
                    var ip = FindIPv4ForPerfInstance(inst, wmiIPs);
                    var nic = new NicCounters { Instance = inst, Rx = rx, Tx = tx, IPv4 = ip };
                    nics.Add(nic);
                }
                catch { }
            }
        }
        catch { }

        // ----- GPU (รวม 3D engines) -----
        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            foreach (var inst in cat.GetInstanceNames())
            {
                // รวมเฉพาะ 3D
                if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                        SafeNext(c);
                        gpu3D.Add(c);
                    }
                    catch { }
                }
            }
        }
        catch { /* ไม่มีบนบางเครื่อง/ไดรเวอร์ */ }

        // warm-up รอบแรก
        Thread.Sleep(INTERVAL_MS);

       
            var ts = DateTime.Now;

            // ===== CPU =====
            float cpu = SafeNext(cpuTotal);
            double curMHz = 0, baseMHz = 0;
            int cores = 0, logical = 0, sockets = 0;
            try
            {
                using (var mos = new ManagementObjectSearcher(
                    "select CurrentClockSpeed, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors, SocketDesignation from Win32_Processor"))
                {
                    foreach (ManagementObject mo in mos.Get())
                    {
                        curMHz = ToDouble(mo["CurrentClockSpeed"]);
                        baseMHz = ToDouble(mo["MaxClockSpeed"]);
                        cores = ToInt(mo["NumberOfCores"]);
                        logical = ToInt(mo["NumberOfLogicalProcessors"]);
                        sockets++; // นับจำนวนโปรเซสเซอร์ซ็อกเก็ต
                        break; // ส่วนใหญ่เครื่องทั่วไปมีตัวเดียว
                    }
                }
            }
            catch { }

            // Proc/Thread/Handle
            int procCount = 0; long threadCount = 0; long handleCount = 0;
            foreach (var p in Process.GetProcesses())
            {
                try { procCount++; threadCount += p.Threads.Count; handleCount += p.HandleCount; }
                catch { }
            }

            // Uptime
            var up = TimeSpan.FromMilliseconds(GetTickCount64());

            // ===== RAM =====
            ulong totalRam = 0, freeRam = 0;
            var ms = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(ms)) { totalRam = ms.ullTotalPhys; freeRam = ms.ullAvailPhys; }
            ulong usedRam = totalRam > freeRam ? totalRam - freeRam : 0;
            double ramPct = totalRam > 0 ? (double)usedRam / totalRam * 100.0 : 0;

            using (var sw = new StreamWriter(GetLogFile(), true))
            {
                sw.WriteLine($"{ts:yyyy-MM-dd HH:mm:ss} | CPU | usage={cpu:0.0}% speed={curMHz / 1000:0.00}GHz base={baseMHz / 1000:0.00}GHz sockets={sockets} cores={cores} logical={logical}");
                sw.WriteLine($"{ts:yyyy-MM-dd HH:mm:ss} | PROC | processes={procCount} threads={threadCount} handles={handleCount} uptime={FormatUptime(up)}");
                sw.WriteLine($"{ts:yyyy-MM-dd HH:mm:ss} | RAM | used={FmtBytes(usedRam)} total={FmtBytes(totalRam)} ({ramPct:0.0}%)");
            }

            // ===== DISK รายไดรฟ์ =====
            foreach (var d in disks)
            {
                float idle = SafeNext(d.Idle);
                float active = Clamp01to100(100f - idle);
                double r = SafeNext(d.ReadBps);
                double w = SafeNext(d.WriteBps);

                // ขนาด/ชนิดไดรฟ์
                string type = GetDriveTypeLabel(d.DriveLetter); // best-effort "SSD/HDD/Unknown"
                try
                {
                    var di = new DriveInfo(d.DriveLetter);
                    if (di.IsReady)
                    {
                        string line = string.Format(
                            "{0:yyyy-MM-dd HH:mm:ss} | DISK | drive={1} active={2:0.0}% read={3} write={4} free={5}/{6} type={7}",
                            ts, d.DriveLetter, active, FmtRate(r), FmtRate(w),
                            FmtBytes((ulong)di.TotalFreeSpace), FmtBytes((ulong)di.TotalSize), type);
                        Append(line);
                    }
                    else
                    {
                        Append(string.Format("{0:yyyy-MM-dd HH:mm:ss} | DISK | drive={1} not_ready", ts, d.DriveLetter));
                    }
                }
                catch
                {
                    Append(string.Format("{0:yyyy-MM-dd HH:mm:ss} | DISK | drive={1} error", ts, d.DriveLetter));
                }
            }

            // ===== NETWORK รายการ์ด =====
            if (nics.Count == 0)
            {
                Append(string.Format("{0:yyyy-MM-dd HH:mm:ss} | NET | none", ts));
            }
            else
            {
                foreach (var n in nics)
                {
                    double rx = SafeNext(n.Rx);
                    double tx = SafeNext(n.Tx);
                    string ip = string.IsNullOrEmpty(n.IPv4) ? "-" : n.IPv4;
                    Append(string.Format("{0:yyyy-MM-dd HH:mm:ss} | NET | {1} up={2} down={3} ip={4}",
                        ts, n.Instance, FmtRate(tx), FmtRate(rx), ip));
                }
            }

            // ===== GPU (รวม 3D engines) =====
            if (gpu3D.Count > 0)
            {
                double sum = 0;
                foreach (var g in gpu3D) sum += SafeNext(g);
                double gpuPct = Math.Max(0, Math.Min(100, sum)); // clamp
                Append(string.Format("{0:yyyy-MM-dd HH:mm:ss} | GPU | usage={1:0.0}%", ts, gpuPct));
            }
            else
            {
                Append(string.Format("{0:yyyy-MM-dd HH:mm:ss} | GPU | usage=0.0%"));
            }

            Thread.Sleep(INTERVAL_MS);
        
    }

    // ---------- helpers ----------
    static void Append(string line)
    {
        using (var sw = new StreamWriter(GetLogFile(), true))
            sw.WriteLine(line);
    }

    static float SafeNext(PerformanceCounter c)
    {
        try { return c.NextValue(); } catch { return 0f; }
    }
    static float Clamp01to100(float v) { if (v < 0) return 0; if (v > 100) return 100; return v; }
    static double ToDouble(object o) { try { return Convert.ToDouble(o); } catch { return 0; } }
    static int ToInt(object o) { try { return Convert.ToInt32(o); } catch { return 0; } }

    static string FmtBytes(ulong bytes)
    {
        double v = bytes; string[] u = { "B", "KB", "MB", "GB", "TB", "PB" }; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return string.Format("{0:0.0}{1}", v, u[i]);
    }
    static string FmtRate(double bytesPerSec)
    {
        double v = bytesPerSec; string[] u = { "B/s", "KB/s", "MB/s", "GB/s" }; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return string.Format("{0:0.0}{1}", v, u[i]);
    }
    static string FormatUptime(TimeSpan t) =>
        string.Format("{0}d {1:00}:{2:00}:{3:00}", t.Days, t.Hours, t.Minutes, t.Seconds);

    // หา drive letter จากชื่อ instance "0 C:" / "1 D:" เป็นต้น
    static string ExtractDriveLetter(string instanceName)
    {
        // หา pattern " X:" ใน string
        for (int i = 0; i < instanceName.Length - 1; i++)
        {
            char c = instanceName[i];
            if (((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) &&
                instanceName[i + 1] == ':')
            {
                return (c.ToString().ToUpper() + ":");
            }
        }
        return null;
    }

    // เดาว่า SSD/HDD จาก WMI (ไม่ 100% แต่ใช้ได้ส่วนใหญ่)
    static string GetDriveTypeLabel(string driveLetter)
    {
        try
        {
            // map partition -> disk
            string query = "ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + driveLetter + "'} " +
                           "ASSOCIATORS OF {Win32_LogicalDiskToPartition} WHERE AssocClass=Win32_LogicalDiskToPartition " +
                           "ASSOCIATORS OF {Win32_DiskDriveToDiskPartition}";
            using (var searcher = new ManagementObjectSearcher(query))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    var mediaType = (mo["MediaType"] ?? "").ToString().ToLower(); // Fixed hard disk media/SSD
                    var model = (mo["Model"] ?? "").ToString();
                    // NVMe/SSD คีย์เวิร์ด
                    if (mediaType.Contains("ssd") || model.IndexOf("nvme", StringComparison.OrdinalIgnoreCase) >= 0
                        || model.IndexOf("ssd", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "SSD";
                    if (mediaType.Contains("fixed")) return "HDD";
                }
            }
        }
        catch { }
        return "Unknown";
    }

    // ดึง IPv4 ต่อ adapter จาก WMI (map แบบใกล้เคียง)
    static Dictionary<string, string> GetNicIPv4Map()
    {
        var map = new Dictionary<string, string>(); // key: WMI Name or NetConnectionID -> IPv4
        try
        {
            using (var mos = new ManagementObjectSearcher("SELECT Description, NetConnectionID, IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
            {
                foreach (ManagementObject mo in mos.Get())
                {
                    var ips = mo["IPAddress"] as string[];
                    string ip4 = ips != null ? ips.FirstOrDefault(ip => ip.IndexOf(':') < 0) : null;
                    if (string.IsNullOrEmpty(ip4)) continue;

                    string desc = (mo["Description"] ?? "").ToString();
                    string id = (mo["NetConnectionID"] ?? "").ToString();
                    if (!string.IsNullOrEmpty(desc) && !map.ContainsKey(desc)) map[desc] = ip4;
                    if (!string.IsNullOrEmpty(id) && !map.ContainsKey(id)) map[id] = ip4;
                }
            }
        }
        catch { }
        return map;
    }

    // จับคู่ชื่อ perf instance กับชื่อ WMI แบบคร่าว ๆ
    static string FindIPv4ForPerfInstance(string perfInstance, Dictionary<string, string> wmiMap)
    {
        // Perf จะ escape ชื่อ (เช่นมี # หรือ () ) ลอง match แบบ contains
        foreach (var kv in wmiMap)
        {
            if (perfInstance.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                return kv.Value;
        }
        return null;
    }
}
