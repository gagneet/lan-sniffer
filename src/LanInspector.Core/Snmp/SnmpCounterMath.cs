namespace LanInspector.Core.Snmp;

/// <summary>
/// Turns successive SNMP counter readings into throughput.
/// </summary>
public static class SnmpCounterMath
{
    /// <summary>
    /// Ceiling used when an interface does not report its speed. 25 GB/s is 200 Gbps — far above
    /// anything a home router carries, so a rate beyond it is arithmetic gone wrong, not traffic.
    /// </summary>
    private const double ImplausibleBytesPerSecond = 25_000_000_000d;

    /// <summary>
    /// Allowance over the rated speed before a derived rate is rejected. Counters and speeds are
    /// reported independently and do not always agree exactly, so a little headroom avoids
    /// discarding good readings on a saturated link.
    /// </summary>
    private const double SpeedTolerance = 1.5;

    /// <summary>
    /// Bytes per second between two readings of a free-running counter, or <see langword="null"/>
    /// when no honest rate can be derived.
    /// </summary>
    /// <remarks>
    /// SNMP octet counters wrap back to zero — the 32-bit ones at 4 GB, which is a matter of
    /// minutes on a fast link — so a decrease is normally a wrap and is unwrapped as one.
    /// <para>
    /// A device reboot also zeroes the counters, and arithmetic alone cannot tell that from a
    /// wrap: both look like a decrease. Plausibility can. A reset presents as a near-full-width
    /// wrap, implying a rate orders of magnitude beyond what the link could carry, so an unwrapped
    /// rate is rejected when it exceeds the interface's rated speed (or a hard ceiling when the
    /// speed is unknown). Reporting nothing for one interval is right; reporting petabytes per
    /// second is not.
    /// </para>
    /// <para>
    /// A 32-bit counter that lapped more than once between polls is undetectable — it produces a
    /// plausible but understated delta. That is the reason to prefer the 64-bit ifHC counters and
    /// to poll often when only the 32-bit ones exist.
    /// </para>
    /// </remarks>
    public static double? BytesPerSecond(
        ulong previous,
        ulong current,
        TimeSpan interval,
        bool isHighCapacity,
        long? speedBps = null)
    {
        if (interval <= TimeSpan.Zero)
        {
            return null;
        }

        if (current >= previous)
        {
            var rate = (current - previous) / interval.TotalSeconds;
            return double.IsFinite(rate) ? rate : null;
        }

        var width = isHighCapacity ? ulong.MaxValue : uint.MaxValue;
        if (previous > width)
        {
            // A reading wider than the counter it claims to be: nothing sound can be derived.
            return null;
        }

        var wrappedRate = (width - previous + current + 1) / interval.TotalSeconds;
        if (!double.IsFinite(wrappedRate))
        {
            return null;
        }

        var ceiling = speedBps is > 0
            ? speedBps.Value / 8d * SpeedTolerance
            : ImplausibleBytesPerSecond;

        return wrappedRate > ceiling ? null : wrappedRate;
    }

    /// <summary>
    /// Throughput for every interface present in both readings. Interfaces that appeared or
    /// disappeared between polls, or whose counters could not be differenced, are omitted.
    /// </summary>
    public static IReadOnlyList<SnmpInterfaceThroughput> Diff(
        IReadOnlyList<SnmpInterfaceCounters> previous,
        IReadOnlyList<SnmpInterfaceCounters> current)
    {
        var previousByIndex = previous.ToDictionary(item => item.Index);
        var result = new List<SnmpInterfaceThroughput>();

        foreach (var now in current)
        {
            if (!previousByIndex.TryGetValue(now.Index, out var before))
            {
                continue;
            }

            var interval = now.ReadAt - before.ReadAt;
            var inRate = BytesPerSecond(before.InOctets, now.InOctets, interval, now.IsHighCapacity, now.SpeedBps);
            var outRate = BytesPerSecond(before.OutOctets, now.OutOctets, interval, now.IsHighCapacity, now.SpeedBps);

            if (inRate is null || outRate is null)
            {
                continue;
            }

            result.Add(new SnmpInterfaceThroughput(
                now.Index,
                now.Description,
                inRate.Value,
                outRate.Value,
                interval,
                now.SpeedBps));
        }

        return result;
    }
}
