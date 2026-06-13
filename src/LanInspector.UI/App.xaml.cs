using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using LanInspector.Core.Analysis;
using LanInspector.Core.Capture;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Identity;
using LanInspector.Core.Model;
using LanInspector.Core.Network;
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
        var analyzers = new IDeviceObservingAnalyzer[]
        {
            new ArpAnalyzer(devices),
            new DnsAnalyzer(devices),
            new DhcpAnalyzer(devices)
        };

        var vendorLookup = new OuiVendorLookup();
        vendorLookup.LoadCsv(Path.Combine(AppContext.BaseDirectory, "Data", "oui.csv"));

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        var knownDevices = KnownDevicesConfiguration.LoadMany(
            Path.Combine(dataDirectory, "known-devices.json"),
            Path.Combine(dataDirectory, "known-devices.local.json"));
        var localNetworkProvider = new LocalNetworkProfileProvider();

        var viewModel = new MainViewModel(
            _captureProvider,
            analyzers,
            knownDevices.KnownDevices,
            vendorLookup,
            new HostnameResolver(),
            new PortScanner(),
            new WindowsPowerShellRouteDiagnosticsService(),
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
            });

        var mainWindow = new MainWindow(viewModel);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _captureProvider?.Dispose();
        base.OnExit(e);
    }
}
