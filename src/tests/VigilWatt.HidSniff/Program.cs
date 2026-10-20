using System.Globalization;
using VigilWatt.Core.Hid;

namespace VigilWatt.HidSniff;

/// <summary>
/// 功 能：嗅探/探测指定 VID/PID 接收器的 HID 帧，用于逆向电量协议。
/// 用法：
///   VigilWatt.HidSniff [--vid 1EA7] [--pid 0066] [--seconds 60]      被动嗅探
///   VigilWatt.HidSniff --probe [--vid 1EA7] [--pid 0066]              主动发候选查询帧
/// </summary>
internal static class Program
{
    private static readonly string[][] ProbeFrames =
    [
        ["B5", "01"], ["B5", "02"], ["B5", "03"], ["B5", "04"], ["B5", "05"],
        ["B5", "06"], ["B5", "08"], ["B5", "09"], ["B5", "0A"], ["B5", "0B"],
        ["B5", "10"], ["B5", "76", "41"], ["B5", "AA"], ["B5", "5A"],
    ];

    private static async Task<int> Main(string[] args)
    {
        ushort? vid = ParseU16(args, "--vid");
        ushort? pid = ParseU16(args, "--pid");
        var probe = args.Contains("--probe");
        var seconds = int.TryParse(ValueOf(args, "--seconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 60;

        var all = HidDeviceScanner.Scan();
        var targets = all.Where(d => (!vid.HasValue || d.VendorId == vid.Value) && (!pid.HasValue || d.ProductId == pid.Value)).ToList();

        Console.WriteLine($"扫描到 {all.Count} 个 HID 集合，匹配 {targets.Count} 个：");
        foreach (var t in targets) Console.WriteLine($"  {t}");

        var opened = new List<(HidDeviceInfo Info, HidDeviceHandle Handle)>();
        foreach (var t in targets)
        {
            try { opened.Add((t, t.Open())); }
            catch (UnauthorizedAccessException) { Console.WriteLine($"  [跳过] 系统保护 collection：{t.DevicePath}"); }
            catch (Exception ex) { Console.WriteLine($"  [跳过] 打开失败：{ex.Message}"); }
        }
        if (opened.Count == 0) { Console.WriteLine("没有可打开的 collection。"); return 1; }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var framesOverride = ValueOf(args, "--frames");
        var frames = framesOverride != null
            ? framesOverride.Split(',').Select(f => f.Trim().Split(' ')).ToArray()
            : ProbeFrames;

        if (probe)
        {
            await ProbeAsync(opened, cts, frames);
            foreach (var (_, h) in opened) h.Dispose();
            return 0;
        }

        if (args.Contains("--watch"))
        {
            await WatchAsync(opened, seconds, cts);
            foreach (var (_, h) in opened) h.Dispose();
            return 0;
        }

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        Console.WriteLine($"开始嗅探 {seconds} 秒。期间请正常使用鼠标（移动、点击、按 DPI 键、切换开关）。Ctrl+C 提前结束。");
        var tasks = opened.Select((o, i) => SniffLoopAsync(i, o.Info, o.Handle, deadline, cts.Token)).ToArray();
        await Task.WhenAll(tasks);
        foreach (var (_, h) in opened) h.Dispose();
        Console.WriteLine("嗅探结束。");
        return 0;
    }

    private static async Task ProbeAsync(List<(HidDeviceInfo Info, HidDeviceHandle Handle)> opened,
        CancellationTokenSource cts, string[][] frames)
    {
        var ct = cts.Token;
        // 持续监听所有集合，后台打印帧
        var listenTasks = opened.Select((o, i) => Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var input = await o.Handle.ReadInputAsync(TimeSpan.FromMilliseconds(300));
                if (input != null)
                    Console.WriteLine($"  << 收到 #{i} usage=0x{o.Info.UsagePage:X4}/0x{o.Info.Usage:X4}  {BitConverter.ToString(input)}");
            }
        })).ToArray();

        var ch = opened.FirstOrDefault(o => o.Info.OutputReportLength > 0);
        if (ch.Handle == null)
        {
            Console.WriteLine("没有可写的 output collection，无法探测。");
            cts.Cancel();
            await Task.WhenAll(listenTasks);
            return;
        }

        Console.WriteLine($"向 report 0x{ch.Info.OutputReportId:X2} (usage=0x{ch.Info.UsagePage:X4}) 发送 {frames.Length} 个候选查询帧，每帧后监听 1.5 秒。");
        Console.WriteLine("注意：个别命令可能改变 DPI/灯效，属可恢复副作用。");
        foreach (var frame in frames)
        {
            if (ct.IsCancellationRequested) break;
            var report = new byte[ch.Info.OutputReportLength];
            for (var i = 0; i < frame.Length && i < report.Length; i++)
                report[i] = byte.Parse(frame[i], NumberStyles.HexNumber);
            if (ch.Info.OutputReportId is { } rid && report[0] == 0) report[0] = rid;

            var ok = ch.Handle.WriteOutput(report, TimeSpan.FromMilliseconds(500), quiet: true);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  >> 发送 {BitConverter.ToString(report)}  {(ok ? "ok" : "失败")}");
            try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
        }

        Console.WriteLine("探测帧发完，再监听 10 秒被动帧……");
        try { await Task.Delay(10000, ct); } catch (OperationCanceledException) { }
        cts.Cancel();
        await Task.WhenAll(listenTasks);
    }



    private static async Task WatchAsync(List<(HidDeviceInfo Info, HidDeviceHandle Handle)> opened, int seconds, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var ch = opened.First(o => o.Info.OutputReportLength > 0);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        Console.WriteLine($"轮询 B5 01 / B5 04，共 {seconds} 秒。请在这段时间里给鼠标插上充电线一次，并观察机身电量灯。");
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            foreach (var cmd in new byte[] { 0x01, 0x04 })
            {
                var report = new byte[ch.Info.OutputReportLength];
                report[0] = ch.Info.OutputReportId ?? 0xB5;
                report[1] = cmd;
                ch.Handle.WriteOutput(report, TimeSpan.FromMilliseconds(500), quiet: true);

                var until = DateTime.UtcNow.AddMilliseconds(800);
                while (DateTime.UtcNow < until)
                {
                    var input = await ch.Handle.ReadInputAsync(TimeSpan.FromMilliseconds(150));
                    if (input == null) continue;
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  cmd=0x{cmd:X2}  << {BitConverter.ToString(input)}");
                }
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static async Task SniffLoopAsync(int idx, HidDeviceInfo info, HidDeviceHandle handle, DateTime deadline, CancellationToken ct)
    {
        var tag = $"#{idx} {info.VidPid} usage=0x{info.UsagePage:X4}/0x{info.Usage:X4} in={info.InputReportLength}";
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var input = await handle.ReadInputAsync(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {tag}  {BitConverter.ToString(input)}");
        }
    }

    private static ushort? ParseU16(string[] args, string key)
    {
        var v = ValueOf(args, key);
        return v != null && ushort.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string? ValueOf(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
