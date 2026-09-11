using System.Net;
using LanInspector.Core.Locator;
using Xunit;

namespace LanInspector.Tests;

public sealed class DeviceLocationHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"laninspector-history-{Guid.NewGuid():N}.json");

    [Fact]
    public void Record_FirstObservation_HasNoPreviousAddress()
    {
        var store = new DeviceLocationHistoryStore(_path);

        var record = store.Record("home-server", IPAddress.Parse("192.168.0.148"), LocationSource.ArpTable, DateTimeOffset.UtcNow);

        Assert.Equal("192.168.0.148", record.LastAddress);
        Assert.Null(record.PreviousAddress);
    }

    [Fact]
    public void Record_NewAddress_MovesPreviousAndStampsTheChange()
    {
        var store = new DeviceLocationHistoryStore(_path);
        var first = DateTimeOffset.UtcNow.AddHours(-2);
        var second = DateTimeOffset.UtcNow;

        store.Record("home-server", IPAddress.Parse("192.168.0.148"), LocationSource.ArpTable, first);
        var record = store.Record("home-server", IPAddress.Parse("192.168.0.154"), LocationSource.ArpTable, second);

        Assert.Equal("192.168.0.154", record.LastAddress);
        Assert.Equal("192.168.0.148", record.PreviousAddress);
        Assert.Equal(second, record.ChangedAt);
    }

    [Fact]
    public void Record_SameAddressAgain_KeepsTheOriginalChangeTimestamp()
    {
        var store = new DeviceLocationHistoryStore(_path);
        var moved = DateTimeOffset.UtcNow.AddHours(-2);

        store.Record("home-server", IPAddress.Parse("192.168.0.148"), LocationSource.ArpTable, moved.AddHours(-1));
        store.Record("home-server", IPAddress.Parse("192.168.0.154"), LocationSource.ArpTable, moved);

        // A poll that sees no change must not look like a fresh move.
        var record = store.Record("home-server", IPAddress.Parse("192.168.0.154"), LocationSource.ArpTable, DateTimeOffset.UtcNow);

        Assert.Equal(moved, record.ChangedAt);
        Assert.Equal("192.168.0.148", record.PreviousAddress);
    }

    [Fact]
    public void Record_PersistsAcrossStoreInstances()
    {
        new DeviceLocationHistoryStore(_path).Record("home-server", IPAddress.Parse("192.168.0.154"), LocationSource.TailscalePing, DateTimeOffset.UtcNow);

        var reloaded = new DeviceLocationHistoryStore(_path).Get("home-server");

        Assert.Equal("192.168.0.154", reloaded?.LastAddress);
        Assert.Equal(nameof(LocationSource.TailscalePing), reloaded?.Source);
    }

    [Fact]
    public void Record_TracksDevicesIndependently()
    {
        var store = new DeviceLocationHistoryStore(_path);

        store.Record("home-server", IPAddress.Parse("192.168.0.154"), LocationSource.ArpTable, DateTimeOffset.UtcNow);
        store.Record("printer", IPAddress.Parse("192.168.0.60"), LocationSource.ArpTable, DateTimeOffset.UtcNow);

        Assert.Equal("192.168.0.154", store.Get("home-server")?.LastAddress);
        Assert.Equal("192.168.0.60", store.Get("printer")?.LastAddress);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyHistoryRatherThanThrowing()
    {
        File.WriteAllText(_path, "{ this is not json");

        var store = new DeviceLocationHistoryStore(_path);

        Assert.Empty(store.Load().Devices);
        Assert.Null(store.Get("home-server"));
    }

    [Fact]
    public void Get_UnknownDevice_ReturnsNull()
    {
        Assert.Null(new DeviceLocationHistoryStore(_path).Get("never-seen"));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
