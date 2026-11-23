// Lightweight console battery monitor: live status, health, CSV log and a charge graph.
// Usage: BatteryMonitor [--interval ms] [--log-every s] [--height rows] [--log path] [--self-test]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Xml;

class Sample
{
    public DateTime Time;
    public double Percent;
    public string State;          // Charging | PluggedIn | Battery
    public double RateW;          // +charging, -discharging
    public long RemainingMWh, FullMWh;
    public double VoltageV;
}

class DesignInfo
{
    public long Design;
    public string Cycles, Name;
}

static class Program
{
    const char Block = '█';
    const int MaxHistory = 1000;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Dictionary<string, ConsoleColor> Colors = new()
    {
        ["Charging"] = ConsoleColor.Green, ["PluggedIn"] = ConsoleColor.Cyan, ["Battery"] = ConsoleColor.Yellow,
    };
    static readonly Dictionary<string, string> StateText = new()
    {
        ["Charging"] = "Charging", ["PluggedIn"] = "Plugged in, not charging", ["Battery"] = "On battery",
    };

    static readonly ManagementObjectSearcher StatusQuery = new(@"root\wmi", "SELECT * FROM BatteryStatus");
    static readonly ManagementObjectSearcher FullQuery = new(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");

    static int intervalMs = 500, logSeconds = 60, graphHeight = 10;
    static string logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryHealthMonitor", "battery-log.csv");
    static DesignInfo info;

    static int Main(string[] args)
    {
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--interval": intervalMs = int.Parse(args[++i]); break;
                    case "--log-every": logSeconds = int.Parse(args[++i]); break;
                    case "--height": graphHeight = int.Parse(args[++i]); break;
                    case "--log": logPath = args[++i]; break;
                    case "--self-test": return SelfTest();
                    default: throw new ArgumentException(args[i]);
                }
            }
            if (intervalMs < 1 || logSeconds < 1 || graphHeight < 2) throw new ArgumentException("values too small");
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Usage: BatteryMonitor [--interval ms] [--log-every s] [--height rows] [--log path] [--self-test]");
            return 1;
        }

        info = GetDesignInfo();
        var hist = LoadHistory();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));

        Console.CancelKeyPress += (_, _) => { Console.ResetColor(); Console.CursorVisible = true; };
        Console.CursorVisible = false;
        var lastLog = DateTime.MinValue;
        int lastWidth = 0;
        while (true)
        {
            try
            {
                var s = GetSample();
                hist.Enqueue(s);
                if (hist.Count > MaxHistory) hist.Dequeue();
                if ((DateTime.Now - lastLog).TotalSeconds >= logSeconds)
                {
                    AppendLog(s);
                    lastLog = DateTime.Now;
                }
                if (Console.WindowWidth != lastWidth) { Console.Clear(); lastWidth = Console.WindowWidth; }
                Console.SetCursorPosition(0, 0);
                WriteStatus(s);
                WriteGraph(hist.ToList());
            }
            catch (Exception e)
            {
                // WMI can hiccup after sleep/resume; show it and keep going.
                Console.SetCursorPosition(0, 0);
                Line("Error: " + e.Message, ConsoleColor.Red);
            }
            Thread.Sleep(intervalMs);
        }
    }

    static ManagementBaseObject First(ManagementObjectSearcher q) =>
        q.Get().Cast<ManagementBaseObject>().FirstOrDefault() ?? throw new Exception("No battery found.");

    static Sample GetSample()
    {
        var st = First(StatusQuery);
        long full = Convert.ToInt64(First(FullQuery)["FullChargedCapacity"]);
        long remaining = Convert.ToInt64(st["RemainingCapacity"]);
        bool charging = (bool)st["Charging"], online = (bool)st["PowerOnline"];
        // Rates of int.MinValue mean "unknown".
        long chargeRate = Convert.ToInt64(st["ChargeRate"]), dischargeRate = Convert.ToInt64(st["DischargeRate"]);
        long rate = charging && chargeRate > 0 ? chargeRate : dischargeRate > 0 ? -dischargeRate : 0;
        return new Sample
        {
            Time = DateTime.Now,
            Percent = Math.Min(100.0, 100.0 * remaining / full),
            State = charging ? "Charging" : online ? "PluggedIn" : "Battery",
            RateW = rate / 1000.0,
            RemainingMWh = remaining,
            FullMWh = full,
            VoltageV = Convert.ToInt64(st["Voltage"]) / 1000.0,
        };
    }

    // BatteryStaticData fails on many laptops; the battery report is reliable and needs no admin.
    static DesignInfo GetDesignInfo()
    {
        string f = Path.Combine(Path.GetTempPath(), $"battery-report-{Process.GetCurrentProcess().Id}.xml");
        try
        {
            var psi = new ProcessStartInfo("powercfg", $"/batteryreport /xml /output \"{f}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(15000); }
            var doc = new XmlDocument();
            doc.Load(f);
            var b = doc.GetElementsByTagName("Battery")[0];
            return new DesignInfo
            {
                Design = long.Parse(b["DesignCapacity"].InnerText, Inv),
                Cycles = b["CycleCount"]?.InnerText ?? "?",
                Name = $"{b["Manufacturer"]?.InnerText} {b["Id"]?.InnerText}".Trim(),
            };
        }
        catch { return null; }
        finally { try { File.Delete(f); } catch { } }
    }

    static void AppendLog(Sample s)
    {
        if (!File.Exists(logPath)) File.AppendAllText(logPath, "Time,Percent,State,RateW,RemainingMWh,FullMWh,VoltageV\r\n");
        File.AppendAllText(logPath, string.Format(Inv, "{0:s},{1:0.0},{2},{3:0.00},{4},{5},{6:0.00}\r\n",
            s.Time, s.Percent, s.State, s.RateW, s.RemainingMWh, s.FullMWh, s.VoltageV));
    }

    static Queue<Sample> LoadHistory()
    {
        var hist = new Queue<Sample>();
        if (!File.Exists(logPath)) return hist;
        // shortcut: streams the whole file to keep the last rows; switch to seeking from the end if the log reaches tens of MB.
        foreach (var line in File.ReadLines(logPath))
        {
            var f = line.Replace("\"", "").Split(',');   // quotes: logs written by the old PowerShell version
            if (f.Length < 3 || !DateTime.TryParse(f[0], Inv, DateTimeStyles.None, out var t)
                || !double.TryParse(f[1], NumberStyles.Float, Inv, out var pct)) continue;   // header or damaged row
            hist.Enqueue(new Sample { Time = t, Percent = pct, State = f[2] });
            if (hist.Count > MaxHistory) hist.Dequeue();
        }
        return hist;
    }

    static string Hours(double h)
    {
        h = Math.Max(0, h);
        return $"{(int)h}h {(int)(h % 1 * 60):00}m";
    }

    // One string per row, top to bottom; a cell is filled when the value reaches the middle of that row's band.
    static string[] GraphRows(IList<double> values, int height)
    {
        var rows = new string[height];
        for (int i = 0; i < height; i++)
        {
            double threshold = (height - i - 0.5) * 100 / height;
            rows[i] = new string(values.Select(v => v >= threshold ? Block : ' ').ToArray());
        }
        return rows;
    }

    static ConsoleColor ColorOf(string state) => Colors.TryGetValue(state ?? "", out var c) ? c : ConsoleColor.Gray;

    static void Write(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    static void Line(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        int w = Console.WindowWidth - 1;
        if (text.Length > w) text = text.Substring(0, w);
        Write(text.PadRight(w) + Environment.NewLine, color);
    }

    static void WriteStatus(Sample s)
    {
        string eta = s.RateW > 0 ? "full in " + Hours((s.FullMWh - s.RemainingMWh) / (s.RateW * 1000))
                   : s.RateW < 0 ? "empty in " + Hours(s.RemainingMWh / (-s.RateW * 1000)) : "";
        string health = info != null
            ? $"{100.0 * s.FullMWh / info.Design:N1} %   (full {s.FullMWh} / design {info.Design} mWh, {info.Cycles} cycles)"
            : "unknown (powercfg battery report unavailable)";

        Line($"Battery   {info?.Name ?? "?"}");
        Line($"Status    {StateText[s.State]}", ColorOf(s.State));
        Line($"Charge    {s.Percent:N1} %   ({s.RemainingMWh} / {s.FullMWh} mWh)");
        Line($"Rate      {s.RateW:+0.00;-0.00;0.00} W   {eta}");
        Line($"Voltage   {s.VoltageV:N2} V");
        Line($"Health    {health}");
        Line($"Updated   {DateTime.Now:HH:mm:ss}   logging every {logSeconds}s to {logPath}", ConsoleColor.DarkGray);
        Line("");
    }

    static void WriteGraph(List<Sample> all)
    {
        int width = Console.WindowWidth - 7;
        var h = all.Skip(Math.Max(0, all.Count - width)).ToList();
        var rows = GraphRows(h.Select(x => x.Percent).ToList(), graphHeight);
        for (int i = 0; i < graphHeight; i++)
        {
            int r = graphHeight - i;
            string label = r == graphHeight ? "100%" : r == (graphHeight + 1) / 2 ? " 50%" : "    ";
            Write(label + "|", ConsoleColor.DarkGray);
            // Write runs of same-state columns in one call, colored by charging state.
            int start = 0;
            for (int c = 1; c <= h.Count; c++)
            {
                if (c == h.Count || h[c].State != h[start].State)
                {
                    Write(rows[i].Substring(start, c - start), ColorOf(h[start].State));
                    start = c;
                }
            }
            Console.WriteLine(new string(' ', width - h.Count + 1));
        }
        string left = h.Count > 0 ? h[0].Time.ToString("MM-dd HH:mm") : "";
        Line("    +" + new string('-', width), ConsoleColor.DarkGray);
        Line("     " + left + "now".PadLeft(width - left.Length), ConsoleColor.DarkGray);
        Write("     charging  ", ConsoleColor.Green);
        Write("plugged in  ", ConsoleColor.Cyan);
        Write("on battery    ", ConsoleColor.Yellow);
        Write("Ctrl+C to quit" + Environment.NewLine, ConsoleColor.DarkGray);
    }

    static int SelfTest()
    {
        string got = string.Join("|", GraphRows(new double[] { 0, 50, 100 }, 4));
        string b = Block.ToString(), want = $"  {b}|  {b}| {b}{b}| {b}{b}";
        bool ok = got == want && Hours(1.5) == "1h 30m";
        Console.WriteLine(ok ? "Self-test passed." : $"Self-test FAILED: graph '{got}', hours '{Hours(1.5)}'");
        return ok ? 0 : 1;
    }
}
