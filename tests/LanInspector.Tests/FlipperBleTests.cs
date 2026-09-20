using LanInspector.Core.Flipper;
using LanInspector.Core.Flipper.Ble;
using Xunit;

namespace LanInspector.Tests;

/// <summary>
/// Replays what the firmware actually writes to the serial CLI, so the parsing is
/// covered without a Flipper attached. See applications/services/bt/bt_cli.c upstream:
/// rx_carrier prints "RSSI: %6.1f dB\r", and with Debug mode off the command is never
/// registered and the usage text comes back instead.
/// </summary>
public sealed class FlipperBleTests
{
    private sealed class FakeFlipper : IFlipperConnectionService
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _streaming;
        private readonly string _command;

        public FakeFlipper(Dictionary<string, IReadOnlyList<string>> streaming, string commandReply = "")
        {
            _streaming = streaming;
            _command = commandReply;
        }

        public FlipperConnectionState State { get; set; } = FlipperConnectionState.Connected;
        public FlipperDeviceInfo? DeviceInfo => null;
        public List<string> Sent { get; } = [];

        public IReadOnlyList<FlipperPortInfo> DetectPorts() => [];
        public Task<bool> ConnectAsync(string? portName = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<string> ExecuteCommandAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.FromResult(_command);
        }

        public Task<IReadOnlyList<string>> ExecuteStreamingCommandAsync(string command, TimeSpan duration, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.FromResult(_streaming.TryGetValue(command, out var lines) ? lines : []);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly string[] CarrierOutput =
    [
        "Receiving carrier at 0 channel",
        "Press CTRL+C to stop",
        "RSSI:  -90.5 dB",
        "RSSI:  -72.0 dB",
        "RSSI:  -80.5 dB",
    ];

    // What `bt` prints when Debug mode is off: hci_info is the only registered subcommand.
    private static readonly string[] UsageOutput =
    [
        "Usage:",
        "bt <cmd> <args>",
        "Cmd list:",
        "\thci_info\t - HCI info",
    ];

    [Fact]
    public void ParseRssiSamples_ReadsEveryReadingAndIgnoresHeaders()
    {
        var samples = FlipperBleService.ParseRssiSamples(CarrierOutput);
        Assert.Equal([-90.5, -72.0, -80.5], samples);
    }

    [Fact]
    public void LooksLikeUsageText_TrueOnlyWhenNoReadingsCameBack()
    {
        Assert.True(FlipperBleService.LooksLikeUsageText(UsageOutput));
        Assert.False(FlipperBleService.LooksLikeUsageText(CarrierOutput));
    }

    [Fact]
    public async Task SurveyAsync_SamplesTheThreeAdvertisingChannelsWithBandMath()
    {
        var fake = new FakeFlipper(new()
        {
            ["bt rx_carrier 0"]  = CarrierOutput,
            ["bt rx_carrier 12"] = CarrierOutput,
            ["bt rx_carrier 39"] = CarrierOutput,
        });

        var result = await new FlipperBleService(fake).SurveyAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.DebugModeRequired);
        Assert.Equal([37, 38, 39], result.Channels.Select(c => c.AdvertisingChannel));
        Assert.All(result.Channels, c => Assert.NotNull(c.AdvertisingChannel));
        // HCI LE test channel N is 2402 + 2N MHz.
        Assert.Equal([2402d, 2426d, 2480d], result.Channels.Select(c => c.FrequencyMhz));
        Assert.Equal(-80.5, result.Channels[0].MedianDbm);
        Assert.Equal(-72.0, result.Channels[0].PeakDbm);
    }

    [Fact]
    public async Task SurveyAsync_ExplainsDebugModeInsteadOfReportingAQuietBand()
    {
        var fake = new FakeFlipper(new()
        {
            ["bt rx_carrier 0"]  = UsageOutput,
            ["bt rx_carrier 12"] = UsageOutput,
            ["bt rx_carrier 39"] = UsageOutput,
        });

        var result = await new FlipperBleService(fake).SurveyAsync();

        Assert.True(result.DebugModeRequired);
        Assert.False(result.Succeeded);
        Assert.Contains("Debug", result.Error);
        Assert.Empty(result.Channels);
    }

    [Fact]
    public async Task SurveyAsync_DegradesWhenTheFlipperIsNotConnected()
    {
        var fake = new FakeFlipper(new()) { State = FlipperConnectionState.Disconnected };

        var result = await new FlipperBleService(fake).SurveyAsync();

        Assert.False(result.Succeeded);
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task SurveyAsync_LeavesADataChannelWithoutAnAdvertisingNumber()
    {
        // RF index 0 IS advertising channel 37, so "no match" must not come back as 0.
        var fake = new FakeFlipper(new() { ["bt rx_carrier 5"] = CarrierOutput });

        var result = await new FlipperBleService(fake).SurveyAsync([5]);

        var channel = Assert.Single(result.Channels);
        Assert.Null(channel.AdvertisingChannel);
        Assert.Equal(2412d, channel.FrequencyMhz);
        Assert.Contains("data channel", channel.WifiOverlapLabel);
        Assert.Equal(37, FlipperBleService.AdvertisingChannelFor(0));
    }

    [Fact]
    public async Task SurveyAsync_RejectsAChannelOutsideTheTestRange()
    {
        var result = await new FlipperBleService(new FakeFlipper(new())).SurveyAsync([40]);

        Assert.False(result.Succeeded);
        Assert.Contains("0-39", result.Error);
    }
}
