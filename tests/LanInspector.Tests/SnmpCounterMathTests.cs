using LanInspector.Core.Snmp;
using Xunit;

namespace LanInspector.Tests;

/// <summary>
/// SNMP octet counters are free-running and wrap, so differencing them is where the bugs live.
/// </summary>
public sealed class SnmpCounterMathTests
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    [Fact]
    public void BytesPerSecond_NormalIncrease_DividesTheDeltaByTheInterval()
    {
        Assert.Equal(1_000, SnmpCounterMath.BytesPerSecond(5_000, 15_000, TenSeconds, isHighCapacity: true));
    }

    [Fact]
    public void BytesPerSecond_NoChange_IsZero()
    {
        Assert.Equal(0, SnmpCounterMath.BytesPerSecond(5_000, 5_000, TenSeconds, isHighCapacity: true));
    }

    [Fact]
    public void BytesPerSecond_ThirtyTwoBitWrap_IsTreatedAsOneLapNotANegative()
    {
        // 100 bytes before the ceiling, then 900 past it: 1,000 bytes across 10 seconds.
        var previous = uint.MaxValue - 99UL;
        const ulong current = 900;

        var rate = SnmpCounterMath.BytesPerSecond(previous, current, TenSeconds, isHighCapacity: false);

        Assert.Equal(100, rate);
    }

    [Fact]
    public void BytesPerSecond_SixtyFourBitWrap_UsesTheWiderCeiling()
    {
        var previous = ulong.MaxValue - 99UL;
        const ulong current = 900;

        var rate = SnmpCounterMath.BytesPerSecond(previous, current, TenSeconds, isHighCapacity: true);

        Assert.Equal(100, rate);
    }

    [Fact]
    public void BytesPerSecond_CounterResetOnAHighCapacityInterface_ReportsNothing()
    {
        // A reboot zeroes the counter. Unwrapped as a 64-bit lap this implies petabytes per
        // second, which no link could carry, so no rate is reported rather than a fantasy one.
        Assert.Null(SnmpCounterMath.BytesPerSecond(500, 0, TenSeconds, isHighCapacity: true));
    }

    [Fact]
    public void BytesPerSecond_CounterResetOnAThirtyTwoBitInterface_ReportsNothing()
    {
        // Same story at 32 bits: a full-width lap in ten seconds is ~3.4 Gbps, well past the
        // gigabit interface this claims to be.
        Assert.Null(SnmpCounterMath.BytesPerSecond(500, 0, TenSeconds, isHighCapacity: false, speedBps: 1_000_000_000));
    }

    [Fact]
    public void BytesPerSecond_GenuineWrapWithinTheLinkSpeed_IsStillReported()
    {
        // 1 KB/s across a wrap is entirely plausible on any link, so the sanity check must not
        // reject it.
        var previous = uint.MaxValue - 9_999UL;

        var rate = SnmpCounterMath.BytesPerSecond(previous, 0, TenSeconds, isHighCapacity: false, speedBps: 1_000_000_000);

        Assert.Equal(1_000, rate);
    }

    [Fact]
    public void BytesPerSecond_WrapBeyondTheRatedSpeed_IsRejected()
    {
        // 100 MB/s is 800 Mbps — impossible on a 10 Mbps interface, so this decrease was a reset.
        var previous = uint.MaxValue - 999_999_999UL;

        Assert.Null(SnmpCounterMath.BytesPerSecond(previous, 0, TenSeconds, isHighCapacity: false, speedBps: 10_000_000));
    }

    [Fact]
    public void BytesPerSecond_NormalIncrease_IsNeverRejectedForImplausibility()
    {
        // Only the unwrapped branch is sanity-checked; a rising counter is taken at face value
        // even when it outruns the speed the device reports for itself.
        var rate = SnmpCounterMath.BytesPerSecond(0, 10_000_000_000, TenSeconds, isHighCapacity: true, speedBps: 1_000_000);

        Assert.Equal(1_000_000_000, rate);
    }

    [Fact]
    public void BytesPerSecond_NonPositiveInterval_ReturnsNull()
    {
        Assert.Null(SnmpCounterMath.BytesPerSecond(0, 1_000, TimeSpan.Zero, isHighCapacity: true));
        Assert.Null(SnmpCounterMath.BytesPerSecond(0, 1_000, TimeSpan.FromSeconds(-1), isHighCapacity: true));
    }

    [Fact]
    public void Diff_MatchesInterfacesByIndexAndComputesBothDirections()
    {
        var first = new[] { Counters(1, "wan", inOctets: 1_000, outOctets: 2_000, at: 0) };
        var second = new[] { Counters(1, "wan", inOctets: 11_000, outOctets: 7_000, at: 10) };

        var result = Assert.Single(SnmpCounterMath.Diff(first, second));

        Assert.Equal(1, result.Index);
        Assert.Equal("wan", result.Description);
        Assert.Equal(1_000, result.InBytesPerSecond);
        Assert.Equal(500, result.OutBytesPerSecond);
        Assert.Equal(1_500, result.TotalBytesPerSecond);
        Assert.Equal(TenSeconds, result.Interval);
    }

    [Fact]
    public void Diff_InterfaceAbsentFromTheEarlierReading_IsSkipped()
    {
        var first = new[] { Counters(1, "wan", 1_000, 2_000, at: 0) };
        var second = new[]
        {
            Counters(1, "wan", 2_000, 3_000, at: 10),
            Counters(9, "new-interface", 500, 500, at: 10)
        };

        var result = SnmpCounterMath.Diff(first, second);

        Assert.Equal(1, Assert.Single(result).Index);
    }

    [Fact]
    public void Diff_EmptyReadings_ProduceNoThroughput()
    {
        Assert.Empty(SnmpCounterMath.Diff([], [Counters(1, "wan", 1, 1, at: 0)]));
        Assert.Empty(SnmpCounterMath.Diff([Counters(1, "wan", 1, 1, at: 0)], []));
    }

    [Fact]
    public void UtilisationPercent_ComparesTheBusierDirectionAgainstTheRatedSpeed()
    {
        // 12.5 MB/s in is 100 Mbps, against a 1 Gbps interface: 10%.
        var throughput = new SnmpInterfaceThroughput(1, "wan", 12_500_000, 1_000, TenSeconds, SpeedBps: 1_000_000_000);

        Assert.Equal(10, throughput.UtilisationPercent!.Value, 3);
    }

    [Fact]
    public void UtilisationPercent_UnknownSpeed_IsNull()
    {
        Assert.Null(new SnmpInterfaceThroughput(1, "wan", 100, 100, TenSeconds, SpeedBps: null).UtilisationPercent);
        Assert.Null(new SnmpInterfaceThroughput(1, "wan", 100, 100, TenSeconds, SpeedBps: 0).UtilisationPercent);
    }

    private static SnmpInterfaceCounters Counters(int index, string description, ulong inOctets, ulong outOctets, int at) =>
        new(index,
            description,
            "up",
            SpeedBps: 1_000_000_000,
            inOctets,
            outOctets,
            IsHighCapacity: true,
            ReadAt: new DateTimeOffset(2026, 9, 11, 10, 0, at, TimeSpan.Zero));
}
