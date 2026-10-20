using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Inphic;

/// <summary>
/// 功 能： 解析英菲克 F1（公版 1EA7:0066 dongle）report 0xB5 电量查询协议。
/// 查询帧 B5 01 00*6 → 响应 B5 01 01 <percent> ...，percent 为十进制电量（实测稳定值 0x38=56）。
/// 帧布局来自本机 VigilWatt.HidSniff 抓包（2026/10/20），充电标志位尚未确认。
/// </summary>
internal static class InphicF1Codec
{
    public const ushort VendorId = 0x1EA7;
    public const ushort ProductId = 0x0066;
    public const ushort VendorUsagePage = 0xFFB5;
    public const ushort VendorUsage = 0x0001;
    public const int ReportLength = 8;

    public const byte ReportId = 0xB5;
    public const byte CmdStatus = 0x01;

    public static bool IsVendorInterface(ushort vid, ushort pid, ushort usagePage, ushort usage,
        ushort inputLen, ushort outputLen, byte? reportId)
        => vid == VendorId
           && pid == ProductId
           && usagePage == VendorUsagePage
           && usage == VendorUsage
           && inputLen == ReportLength
           && outputLen == ReportLength
           && reportId == ReportId;

    public static byte[] CreateQuery()
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = CmdStatus;
        return report;
    }

    public static bool TryParse(byte[] input, out int percent)
    {
        percent = 0;
        if (input.Length != ReportLength) return false;
        if (input[0] != ReportId || input[1] != CmdStatus) return false;
        if (input[3] > 100) return false;
        percent = input[3];
        return true;
    }
}
