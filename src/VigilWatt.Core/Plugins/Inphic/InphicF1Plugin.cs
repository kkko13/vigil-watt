using System.Diagnostics;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Inphic;

/// <summary>
/// 功 能： 英菲克 F1 2.4G 鼠标插件（公版 1EA7:0066 dongle，厂商集合 0xFFB5/0x0001）。
/// 读取需发送查询帧（B5 01），默认关闭，在设置中按品牌开启。
/// 协议来自本机抓包：VigilWatt.HidSniff --probe / --watch（2026/10/20）。
/// </summary>
public sealed class InphicF1Plugin : BatteryPluginBase
{
    public override string Name => "InphicF1";
    public override string DisplayName => "英菲克 F1";
    public override string DisplayNameEn => "INPHIC F1";
    public override DeviceKind Kind => DeviceKind.Mouse;
    protected override OfflinePolicy Offline { get; } = new() { CountPollFailures = false };

    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (InphicF1Codec.IsVendorInterface(info.VendorId, info.ProductId, info.UsagePage, info.Usage,
                info.InputReportLength, info.OutputReportLength, info.OutputReportId)) return true;

        // 同 VID/PID 但接口判据不符时留诊断，否则用户只看到"鼠标没出现"。
        if (info.VendorId == InphicF1Codec.VendorId && info.ProductId == InphicF1Codec.ProductId)
            Log.InfoThrottled($"inphicf1-nomatch:{info.DevicePath}",
                $"英菲克 F1 接口判据不符，未认领: {info} path={info.DevicePath}", TimeSpan.FromMinutes(10));
        return false;
    }

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
        => ProbeAsync(handle).GetAwaiter().GetResult();

    protected override async Task<(string Protocol, int? Percent, ChargingState Charging)?> ProbeAsync(
        HidDeviceHandle handle)
    {
        var percent = await QueryAsync(handle).ConfigureAwait(false);
        return percent.HasValue ? ("inphic-f1-b5-status1", percent, ChargingState.Unknown) : null;
    }

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev)) return null;
        var percent = QueryAsync(dev.Handle).GetAwaiter().GetResult();
        if (!percent.HasValue)
        {
            ReportPollFailure(dev);
            return null;
        }
        ReportPollAnswered(dev);
        return (percent, ChargingState.Unknown);
    }

    private async Task<int?> QueryAsync(HidDeviceHandle handle)
    {
        // 写点再次校验开关和完整接口，禁止把同 VID/PID 的其它集合当作厂商通道。
        if (!WritesEnabled || !IsCandidate(handle.Info)) return null;
        if (!handle.WriteOutput(InphicF1Codec.CreateQuery(), TimeSpan.FromMilliseconds(500)))
            return null;
        var elapsed = Stopwatch.StartNew();
        var window = TimeSpan.FromMilliseconds(750);
        while (elapsed.Elapsed < window)
        {
            var remaining = window - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var input = await handle.ReadInputAsync(remaining).ConfigureAwait(false);
            if (input == null) break;
            if (!InphicF1Codec.TryParse(input, out int percent))
            {
                Log.Debug($"英菲克 F1 忽略非电量响应: {Log.Hex(input)}");
                continue;
            }
            Log.Info($"英菲克 F1 电量查询: {percent}%");
            return percent;
        }
        Log.Warn("英菲克 F1 电量查询未收到有效响应");
        return null;
    }

    protected override string BuildDisplayName(HidDeviceInfo info) => "英菲克 F1 (2.4G)";
}
