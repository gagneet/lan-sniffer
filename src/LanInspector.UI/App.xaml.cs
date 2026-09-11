using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using LanInspector.Core.Analysis;
using LanInspector.Core.Capture;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Dns;
using LanInspector.Core.Identity;
using LanInspector.Core.Locator;
using LanInspector.Core.Model;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;
using LanInspector.Core.Scanning;
using LanInspector.Platform.Windows;
using LanInspector.UI.ViewModels;
using LanInspector.UI.Views;

namespace LanInspector.UI;

public partial class App : Application
{
    private ICaptureProvider? _captureProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var devices = new ConcurrentDictionary<string, Device>();
        _captureProvider = new PcapCaptureProvider();
        var dnsAnalyzer = new DnsAnalyzer(devices);
        var analyzers = new IDeviceObservingAnalyzer[]
        {
            new ArpAnalyzer(devices),
            dnsAnalyzer,
            new DhcpAnalyzer(devices)
        };

        var vendorLookup = new OuiVendorLookup();
        vendorLookup.LoadCsv(Path.Combine(AppContext.BaseDirectory, "Data", "oui.csv"));

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        var userConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanInspector");

        // Merged by device id with later files winning, matching the CLI's search order so both
        // front ends see the same configuration.
        var knownDevices = KnownDevicesConfiguration.LoadMany(
            Path.Combine(dataDirectory, "known-devices.json"),
            Path.Combine(dataDirectory, "known-devices.local.json"),
            Path.Combine(userConfigDirectory, "known-devices.json"),
            Path.Combine(userConfigDirectory, "known-devices.local.json"));
        var localNetworkProvider = new LocalNetworkProfileProvider();

        var tailscale = new TailscaleCliService();
        var portScanner = new PortScanner();
        var hostnameResolver = new HostnameResolver();
        var deviceLocator = new DeviceLocatorService(
            tailscale,
            localNetworkProvider,
            new ArpTableReader(),
            portScanner,
            hostnameResolver,
            new DeviceLocationHistoryStore());
        var dnsConfig = DnsIntegrationsConfigLoader.Load();
        var dnsService = DnsIntegrationsConfigLoader.CreateService(dnsConfig);

        // Names for the traffic view: configured devices, devices seen in the capture, this
        // machine's interfaces, and hostnames learned from DNS/mDNS answers.
        var nameRegistry = new DeviceNameRegistry(
            knownDevices.KnownDevices,
            () => devices.Values,
            localNetworkProvider);
        nameRegistry.Observe(dnsAnalyzer);

        // The tailnet peer list labels 100.x addresses; fetched once in the background so startup
        // is not held up by a CLI call that may not be installed.
        _ = Task.Run(async () =>
        {
            try
            {
                nameRegistry.SetTailscaleStatus(await tailscale.GetStatusAsync());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tailscale name lookup failed: {ex}");
            }
        });

        var viewModel = new MainViewModel(
            _captureProvider,
            analyzers,
            knownDevices.KnownDevices,
            vendorLookup,
            hostnameResolver,
            portScanner,
            new WindowsRouteDiagnosticsService(),
            new ReachabilityClassifier(localNetworkProvider),
            new WindowsTerminalLauncher(),
            devices.Clear,
            action =>
            {
                if (Dispatcher.CheckAccess())
                {
                    action();
                    return;
                }

                Dispatcher.Invoke(action);
            },
            tailscale,
            knownDevices,
            deviceLocator,
            nameRegistry,
            dnsService);

        var mainWindow = new MainWindow(viewModel);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _captureProvider?.Dispose();
        base.OnExit(e);
    }
}
