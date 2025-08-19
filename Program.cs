// .NET Framework 4.7.2  (C# 7.3)
using System;
using System.Collections.Generic;
using System.Configuration;         
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class Program
{
    // ===== P/Invoke =====
    [DllImport("kernel32")] static extern UInt64 GetTickCount64();

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

    // ===== Models =====
    class DiskCounters
    {
        public string Instance;            // e.g. "0 C:"
        public int DiskIndex;              // 0,1,2...
        public List<string> Letters;       // C:, D:, ...
        public PerformanceCounter Idle;    // % Idle Time
        public PerformanceCounter ReadBps; // Disk Read Bytes/sec
        public PerformanceCounter WriteBps;// Disk Write Bytes/sec
    }

    class NicCounters
    {
        public string Instance;            // PerfMon instance name
        public PerformanceCounter Rx;      // Bytes Received/sec
        public PerformanceCounter Tx;      // Bytes Sent/sec
        public string IPv4;                // best-effort from WMI
    }

    // ===== Fields =====
    static PerformanceCounter cpuTotal;
    static List<DiskCounters> disks = new List<DiskCounters>();
    static List<NicCounters> nics = new List<NicCounters>();
    static List<PerformanceCounter> gpu3D = new List<PerformanceCounter>();

    static void Main()
    {
        //Console.OutputEncoding = Encoding.UTF8;

        // ---- Init counters (create + warm-up) ----
        InitCpu();
        InitDisks();
        InitNics();
        InitGpu();

        // อุ่นค่า PerfCounter ให้พร้อมอ่าน
        Thread.Sleep(1000);

        var ts = DateTime.Now;

        // ===== CPU =====
        float cpuUsage = SafeNext(cpuTotal);
        double curMHz = 0, baseMHz = 0;
        int cores = 0, logical = 0, sockets = 0;
        try
        {
            using (var mos = new ManagementObjectSearcher(
                "select CurrentClockSpeed, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors from Win32_Processor"))
            {
                foreach (ManagementObject mo in mos.Get())
                {
                    curMHz = ToDouble(mo["CurrentClockSpeed"]);
                    baseMHz = ToDouble(mo["MaxClockSpeed"]);
                    cores = ToInt(mo["NumberOfCores"]);
                    logical = ToInt(mo["NumberOfLogicalProcessors"]);
                    sockets++;
                    break; // ส่วนมาก 1 ตัว
                }
            }
        }
        catch { }

        // Proc / Threads / Handles
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

        // ===== Write CPU / CPU_ALL / RAM =====
        using (var sw = new StreamWriter(GetLogFile(), true))
        {
            sw.WriteLine($"{ts:yyyy-MM-dd HH:mm:ss} | CPU | usage={cpuUsage:0.0}% speed={curMHz / 1000:0.00}GHz base={baseMHz / 1000:0.00}GHz sockets={sockets} cores={cores} logical={logical}");
            sw.WriteLine($"{ts:yyyy-MM-dd HH:mm:ss} | CPU_ALL | processes={procCount} threads={threadCount} handles={handleCount} uptime={FormatUptime(up)}");
            sw.WriteLine($"{ts:yyyy-MM-dd HH:mm:ss} | RAM | used={FmtBytes(usedRam)} total={FmtBytes(totalRam)} ({ramPct:0.0}%)");
        }

        // ===== DISK (per-physical + per-drive + ALL) =====
        double totalRead = 0, totalWrite = 0; float totalActive = 0; int diskCount = 0;

        foreach (var d in disks.OrderBy(x => x.DiskIndex))
        {
            float idle = SafeNext(d.Idle);
            float active = Clamp01to100(100f - idle);
            double r = SafeNext(d.ReadBps);
            double w = SafeNext(d.WriteBps);

            totalRead += r; totalWrite += w; totalActive += active; diskCount++;

            // รวม free/total ของทุก volume ในดิสก์นี้
            ulong sumFree = 0, sumTotal = 0;
            foreach (var letter in d.Letters)
            {
                try
                {
                    var di = new DriveInfo(letter);
                    if (di.IsReady) { sumFree += (ulong)di.TotalFreeSpace; sumTotal += (ulong)di.TotalSize; }
                }
                catch { }
            }
            string vols = d.Letters.Count > 0 ? string.Join(",", d.Letters) : "-";
            string dtype = GetPhysicalTypeByIndex(d.DiskIndex); // SSD/HDD/Unknown

            Append($"{ts:yyyy-MM-dd HH:mm:ss} | DISK_PHYS | disk={d.DiskIndex} vols={vols} active={active:0.0}% read={FmtRate(r)} write={FmtRate(w)} free={FmtBytes(sumFree)}/{FmtBytes(sumTotal)} type={dtype}");

            // รายไดรฟ์ (note: active/read/write ใช้ค่าของดิสก์ทั้งลูกเป็นตัวแทน)
            foreach (var letter in d.Letters)
            {
                try
                {
                    var di = new DriveInfo(letter);
                    if (di.IsReady)
                        Append($"{ts:yyyy-MM-dd HH:mm:ss} | DISK | drive={letter} active={active:0.0}% read={FmtRate(r)} write={FmtRate(w)} free={FmtBytes((ulong)di.TotalFreeSpace)}/{FmtBytes((ulong)di.TotalSize)}");
                }
                catch { }
            }
        }
        if (diskCount > 0)
        {
            float avgActive = totalActive / diskCount;
            Append($"{ts:yyyy-MM-dd HH:mm:ss} | DISK_ALL | active={avgActive:0.0}% read={FmtRate(totalRead)} write={FmtRate(totalWrite)}");
        }

        // ===== NET (per-NIC + ALL) =====
        double totalUp = 0, totalDown = 0;
        if (nics.Count == 0)
        {
            Append($"{ts:yyyy-MM-dd HH:mm:ss} | NET | none");
        }
        else
        {
            foreach (var n in nics)
            {
                double rx = SafeNext(n.Rx);
                double tx = SafeNext(n.Tx);
                totalUp += tx; totalDown += rx;
                string ip = string.IsNullOrEmpty(n.IPv4) ? "-" : n.IPv4;
                Append($"{ts:yyyy-MM-dd HH:mm:ss} | NET | {n.Instance} up={FmtRate(tx)} down={FmtRate(rx)} ip={ip}");
            }
            Append($"{ts:yyyy-MM-dd HH:mm:ss} | NET_ALL | up={FmtRate(totalUp)} down={FmtRate(totalDown)}");
        }

        // ===== GPU (sum of 3D engines) =====
        if (gpu3D.Count > 0)
        {
            double sum = 0; foreach (var g in gpu3D) sum += SafeNext(g);
            double gpuPct = Math.Max(0, Math.Min(100, sum));
            Append($"{ts:yyyy-MM-dd HH:mm:ss} | GPU | usage={gpuPct:0.0}%");
        }
        else
        {
            Append($"{ts:yyyy-MM-dd HH:mm:ss} | GPU | usage=0.0%");
        }

        // ---- Done (snapshot one-shot) ----
        // Console.WriteLine("Logged once to: " + GetLogFile());
    }

    // ===================== Init =====================
    static void InitCpu()
    {
        cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        SafeNext(cpuTotal);
    }

    static void InitDisks()
    {
        var diskVolMap = BuildDiskVolumeMap();
        try
        {
            var cat = new PerformanceCounterCategory("PhysicalDisk");
            foreach (var inst in cat.GetInstanceNames())
            {
                if (inst == "_Total") continue;
                int diskIdx = ExtractDiskIndexFromInstance(inst);
                if (diskIdx < 0) continue;

                try
                {
                    var idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", inst);
                    var rBps = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst);
                    var wBps = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst);
                    SafeNext(idle); SafeNext(rBps); SafeNext(wBps);

                    List<string> letters;
                    if (!diskVolMap.TryGetValue(diskIdx, out letters)) letters = new List<string>();

                    disks.Add(new DiskCounters
                    {
                        Instance = inst,
                        DiskIndex = diskIdx,
                        Letters = letters,
                        Idle = idle,
                        ReadBps = rBps,
                        WriteBps = wBps
                    });
                }
                catch { }
            }
        }
        catch { }
    }


    static void InitNics()
    {
        try
        {
            var cat = new PerformanceCounterCategory("Network Interface");
            foreach (var inst in cat.GetInstanceNames())
            {
                try
                {
                    var rx = new PerformanceCounter("Network Interface", "Bytes Received/sec", inst);
                    var tx = new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst);

                    // jump first value
                    _ = rx.NextValue();
                    _ = tx.NextValue();

                    nics.Add(new NicCounters { Instance = inst, Rx = rx, Tx = tx });
                }
                catch { }
            }
        }
        catch { }
    }

    static void InitGpu()
    {
        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            foreach (var inst in cat.GetInstanceNames())
            {
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
        catch { }
    }

    // ===================== Helpers =====================
    static string GetLogFile()
    {
        string logDir = ConfigurationManager.AppSettings["LogDirectory"];
        string logPrefix = ConfigurationManager.AppSettings["LogPrefix"];
        if (string.IsNullOrEmpty(logDir)) logDir = @"C:\Logs";
        if (string.IsNullOrEmpty(logPrefix)) logPrefix = "performance";

        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        string fileName = string.Format("{0}_{1:yyyyMMdd}.log", logPrefix, DateTime.Now);
        return Path.Combine(logDir, fileName);
    }

    static void Append(string line)
    {
        using (var sw = new StreamWriter(GetLogFile(), true))
            sw.WriteLine(line);
    }

    static float SafeNext(PerformanceCounter c) { try { return c.NextValue(); } catch { return 0f; } }
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

    // ----- Disk/Volume Mapping via WMI -----
    static Dictionary<int, List<string>> BuildDiskVolumeMap()
    {
        var map = new Dictionary<int, List<string>>();
        try
        {
            using (var partSearcher = new ManagementObjectSearcher("SELECT DeviceID, DiskIndex FROM Win32_DiskPartition"))
            using (var linkLD2Part = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition"))
            {
                var partToDisk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ManagementObject p in partSearcher.Get())
                {
                    string pId = (p["DeviceID"] ?? "").ToString(); // "Disk #0, Partition #1"
                    int dIdx = ToInt(p["DiskIndex"]);
                    if (!string.IsNullOrEmpty(pId)) partToDisk[pId] = dIdx;
                }

                foreach (ManagementObject link in linkLD2Part.Get())
                {
                    string antecedent = (link["Antecedent"] ?? "").ToString(); // partition
                    string dependent = (link["Dependent"] ?? "").ToString(); // logical
                    string partId = ExtractQuotedValue(antecedent);            // Disk #0, Partition #1
                    string letter = ExtractQuotedValue(dependent);             // C:
                    if (string.IsNullOrEmpty(partId) || string.IsNullOrEmpty(letter)) continue;

                    int diskIndex;
                    if (!partToDisk.TryGetValue(partId, out diskIndex)) continue;

                    List<string> list;
                    if (!map.TryGetValue(diskIndex, out list))
                    {
                        list = new List<string>();
                        map[diskIndex] = list;
                    }
                    if (!list.Contains(letter)) list.Add(letter);
                }
            }
        }
        catch { }
        return map;
    }

    static string ExtractQuotedValue(string wmiPath)
    {
        int i = wmiPath.IndexOf('"'); if (i < 0) return null;
        int j = wmiPath.IndexOf('"', i + 1); if (j < 0) return null;
        return wmiPath.Substring(i + 1, j - i - 1);
    }

    static int ExtractDiskIndexFromInstance(string inst)
    {
        // inst ตัวอย่าง: "0 C:" หรือ "1 D:"
        int i = 0; while (i < inst.Length && char.IsWhiteSpace(inst[i])) i++;
        int start = i; while (i < inst.Length && char.IsDigit(inst[i])) i++;
        int idx;
        if (i > start && int.TryParse(inst.Substring(start, i - start), out idx)) return idx;
        return -1;
    }

    static string GetPhysicalTypeByIndex(int diskIndex)
    {
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Index, MediaType, Model FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    int idx = ToInt(mo["Index"]);
                    if (idx != diskIndex) continue;

                    string media = (mo["MediaType"] ?? "").ToString().ToLower();
                    string model = (mo["Model"] ?? "").ToString();
                    if (media.Contains("ssd") ||
                        model.IndexOf("nvme", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        model.IndexOf("ssd", StringComparison.OrdinalIgnoreCase) >= 0) return "SSD";
                    if (media.Contains("fixed")) return "HDD";
                    return "Unknown";
                }
            }
        }
        catch { }
        return "Unknown";
    }
}
