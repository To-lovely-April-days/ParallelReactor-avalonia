using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>搅拌传动换算：界面上是釜里的转速，下发电机 = 釜 × 85/38。</summary>
public class StirGearTests
{
    [Fact]
    public void DefaultRatio_Is85Over38()
    {
        Assert.Equal(2.236842, StirGear.DefaultRatio, 6);
        Assert.Equal(2.236842, new AppSettings().StirGearRatio, 6);
    }

    [Theory]
    [InlineData(447, 1000)]   // 釜 447 rpm → 电机约 1000 rpm
    [InlineData(400, 895)]
    [InlineData(0, 0)]
    [InlineData(2000, 3000)]  // 超过电机上限：限幅
    public void ToMotor_MultipliesByRatio_AndClamps(int vessel, int motor)
        => Assert.Equal(motor, StirGear.ToMotor(vessel, StirGear.DefaultRatio, 3000));

    [Fact]
    public void ToVessel_DividesByRatio()
    {
        Assert.Equal(447.06, StirGear.ToVessel(1000, StirGear.DefaultRatio), 2);
        Assert.Equal(300, StirGear.ToVessel(300, 0), 6);   // 传动比无效按 1
    }
}
