using VigilWatt.Core.Plugins.Inphic;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 英菲克 F1（公版 1EA7:0066）抓包样本、边界与接口隔离的回归测试。
/// 样本来自本机 VigilWatt.HidSniff 抓包（2026/10/20）。
/// </summary>
public sealed class InphicF1CodecTests
{
    [Fact]
    public void QueryMatchesCapturedRequest()
    {
        var expected = Convert.FromHexString("B501000000000000");
        Assert.Equal(expected, InphicF1Codec.CreateQuery());
    }

    [Fact]
    public void CapturedResponseIs56Percent()
    {
        var input = Convert.FromHexString("B501013800000000");
        Assert.True(InphicF1Codec.TryParse(input, out int percent));
        Assert.Equal(56, percent);
    }

    [Fact]
    public void CapturedResponseWithTrailingFieldsIsAccepted()
    {
        // 探测轮首帧带非零尾字节（B5 01 01 38 46 02 02 00），尾字节语义未确认，解析应容忍。
        var input = Convert.FromHexString("B501013846020200");
        Assert.True(InphicF1Codec.TryParse(input, out int percent));
        Assert.Equal(56, percent);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(255, false)]
    public void PercentMustBeAtMost100(int percent, bool valid)
    {
        var input = Convert.FromHexString("B501013800000000");
        input[3] = (byte)percent;
        Assert.Equal(valid, InphicF1Codec.TryParse(input, out _));
    }

    [Theory]
    [InlineData(0)]  // report id
    [InlineData(1)]  // cmd
    public void CorruptPacketsAreRejected(int offset)
    {
        var input = Convert.FromHexString("B501013800000000");
        input[offset] ^= 1;
        Assert.False(InphicF1Codec.TryParse(input, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(64)]
    public void WrongLengthsAreRejected(int length)
        => Assert.False(InphicF1Codec.TryParse(new byte[length], out _));

    [Fact]
    public void StatusResponseToOtherCommandIsRejected()
    {
        // cmd 0x04 的应答（B5 04 01 ...）不能当电量帧解析。
        var input = Convert.FromHexString("B504010000000000");
        Assert.False(InphicF1Codec.TryParse(input, out _));
    }

    [Theory]
    // 抓包实录：FFB5 厂商集合可认领；同 dongle 的系统控制 / consumer / FFBC 集合不可认领。
    [InlineData(0x1EA7, 0x0066, 0xFFB5, 0x0001, 8, 8, 0xB5, true)]
    [InlineData(0x1EA7, 0x0066, 0x0001, 0x0080, 2, 0, 0x03, false)]
    [InlineData(0x1EA7, 0x0066, 0x000C, 0x0001, 3, 0, 0x01, false)]
    [InlineData(0x1EA7, 0x0066, 0xFFBC, 0x0088, 2, 0, 0x04, false)]
    // 别的 VID/PID/长度/Report ID 一律不认。
    [InlineData(0x1D57, 0xFA65, 0xFFB5, 0x0001, 8, 8, 0xB5, false)]
    [InlineData(0x1EA7, 0x0067, 0xFFB5, 0x0001, 8, 8, 0xB5, false)]
    [InlineData(0x1EA7, 0x0066, 0xFFB5, 0x0001, 8, 8, 0xB4, false)]
    [InlineData(0x1EA7, 0x0066, 0xFFB5, 0x0001, 16, 8, 0xB5, false)]
    [InlineData(0x1EA7, 0x0066, 0xFFB5, 0x0001, 8, 16, 0xB5, false)]
    public void OnlyCapturedVendorInterfaceIsEligible(ushort vid, ushort pid, ushort page,
        ushort usage, ushort input, ushort output, byte reportId, bool eligible)
        => Assert.Equal(eligible, InphicF1Codec.IsVendorInterface(
            vid, pid, page, usage, input, output, reportId));

    [Fact]
    public void UnknownReportIdIsRejected()
        => Assert.False(InphicF1Codec.IsVendorInterface(
            0x1EA7, 0x0066, 0xFFB5, 0x0001, 8, 8, null));
}
