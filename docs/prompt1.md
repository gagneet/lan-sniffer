---

## 1. Project architecture

### Solution structure (Visual Studio)

- **Solution: `LanInspector`**
  - **Project 1 – `LanInspector.Core`** (Class Library)
    - Capture & packet processing
    - Device model & topology
    - Discovery & analyzers
    - Plugin infrastructure
  - **Project 2 – `LanInspector.UI`** (WPF App, .NET 8)
    - Views (XAML)
    - ViewModels (MVVM)
    - UI composition, charts, grids
  - **Project 3 – `LanInspector.Plugins`** (Class Library, optional)
    - Example plugins (DNS analyzer, mDNS analyzer, SSDP analyzer)

### Core namespaces

- **`LanInspector.Core.Capture`**
  - `ICaptureProvider`
  - `PcapCaptureProvider` (SharpPcap)
- **`LanInspector.Core.Model`**
  - `Device`
  - `NetworkInterfaceInfo`
  - `NetworkSnapshot`
- **`LanInspector.Core.Discovery`**
  - `INetworkDiscovery`
  - `LocalSubnetDiscovery`
- **`LanInspector.Core.Analysis`**
  - `IPacketAnalyzer`
  - `DnsAnalyzer`, `ArpAnalyzer`, `MdnsAnalyzer`, `SsdpAnalyzer`
- **`LanInspector.Core.Plugins`**
  - `IPlugin`
  - `PluginManager`
- **`LanInspector.UI.ViewModels`**
  - `MainViewModel`
  - `InterfaceSelectionViewModel`
  - `DeviceListViewModel`
  - `DeviceDetailViewModel`
- **`LanInspector.UI.Views`**
  - `MainWindow.xaml`
  - `InterfaceSelectionView.xaml`
  - `DeviceListView.xaml`
  - `DeviceDetailView.xaml`

---

## 2. Starter C# code (packet capture + ARP scan)

### 2.1 Basic capture with SharpPcap + PacketDotNet

```csharp
// LanInspector.Core.Capture.PcapCaptureProvider.cs
using System;
using PacketDotNet;
using SharpPcap;

namespace LanInspector.Core.Capture
{
    public interface ICaptureProvider
    {
        event EventHandler<PacketCapturedEventArgs> PacketCaptured;
        void Start(string deviceName, string filter);
        void Stop();
    }

    public class PacketCapturedEventArgs : EventArgs
    {
        public RawCapture RawCapture { get; }
        public Packet ParsedPacket { get; }

        public PacketCapturedEventArgs(RawCapture raw, Packet parsed)
        {
            RawCapture = raw;
            ParsedPacket = parsed;
        }
    }

    public class PcapCaptureProvider : ICaptureProvider
    {
        private ICaptureDevice _device;

        public event EventHandler<PacketCapturedEventArgs> PacketCaptured;

        public void Start(string deviceName, string filter)
        {
            var devices = CaptureDeviceList.Instance;
            _device = devices.FirstOrDefault(d => d.Name == deviceName)
                      ?? throw new InvalidOperationException("Device not found");

            _device.OnPacketArrival += OnPacketArrival;
            _device.Open(DeviceMode.Promiscuous, 1000);
            _device.Filter = filter; // e.g. "ip or arp"
            _device.StartCapture();
        }

        public void Stop()
        {
            if (_device == null) return;
            _device.StopCapture();
            _device.Close();
            _device.OnPacketArrival -= OnPacketArrival;
        }

        private void OnPacketArrival(object sender, CaptureEventArgs e)
        {
            var raw = e.Packet;
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            PacketCaptured?.Invoke(this, new PacketCapturedEventArgs(raw, packet));
        }
    }
}
```

### 2.2 Device model + ARP handling

```csharp
// LanInspector.Core.Model.Device.cs
using System;
using System.Collections.Generic;

namespace LanInspector.Core.Model
{
    public class Device
    {
        public string MacAddress { get; set; }
        public HashSet<string> IpAddresses { get; } = new();
        public string Hostname { get; set; }
        public string Vendor { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsGateway { get; set; }
        public bool IsAccessPoint { get; set; }
    }
}
```

```csharp
// LanInspector.Core.Analysis.ArpAnalyzer.cs
using System;
using System.Collections.Concurrent;
using PacketDotNet;
using LanInspector.Core.Model;

namespace LanInspector.Core.Analysis
{
    public class ArpAnalyzer : IPacketAnalyzer
    {
        private readonly ConcurrentDictionary<string, Device> _devices;

        public ArpAnalyzer(ConcurrentDictionary<string, Device> devices)
        {
            _devices = devices;
        }

        public void Analyze(Packet packet)
        {
            var arp = packet.Extract<ArpPacket>();
            if (arp == null) return;

            var mac = arp.SenderHardwareAddress.ToString();
            var ip = arp.SenderProtocolAddress.ToString();

            var device = _devices.GetOrAdd(mac, _ => new Device
            {
                MacAddress = mac,
                FirstSeen = DateTime.UtcNow
            });

            device.IpAddresses.Add(ip);
            device.LastSeen = DateTime.UtcNow;
        }
    }

    public interface IPacketAnalyzer
    {
        void Analyze(Packet packet);
    }
}
```

### 2.3 Simple ARP scan (local subnet)

```csharp
// LanInspector.Core.Discovery.LocalSubnetDiscovery.cs
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace LanInspector.Core.Discovery
{
    public class LocalSubnetDiscovery : INetworkDiscovery
    {
        public async Task<IEnumerable<IPAddress>> PingSweepAsync(IPAddress subnet, int cidr)
        {
            var results = new List<IPAddress>();
            var baseAddress = BitConverter.ToUInt32(subnet.GetAddressBytes().Reverse().ToArray(), 0);
            var hostCount = (int)Math.Pow(2, 32 - cidr);

            var pingTasks = new List<Task>();

            for (int i = 1; i < hostCount - 1; i++)
            {
                var addrInt = baseAddress + (uint)i;
                var bytes = BitConverter.GetBytes(addrInt).Reverse().ToArray();
                var ip = new IPAddress(bytes);

                var ping = new Ping();
                var t = ping.SendPingAsync(ip, 500).ContinueWith(r =>
                {
                    if (r.Result.Status == IPStatus.Success)
                    {
                        lock (results)
                        {
                            results.Add(ip);
                        }
                    }
                });

                pingTasks.Add(t);
            }

            await Task.WhenAll(pingTasks);
            return results;
        }
    }

    public interface INetworkDiscovery
    {
        Task<IEnumerable<IPAddress>> PingSweepAsync(IPAddress subnet, int cidr);
    }
}
```

You can refine subnet calculation later (read from `NetworkInterface.GetAllNetworkInterfaces()`).

---

## 3. UI layout (WPF, MVVM)

### Main views

1. **MainWindow**
   - Top: Menu/toolbar (Start/Stop capture, Settings)
   - Left: Interface selection + filters
   - Center: Device list
   - Right: Device detail
   - Bottom: Status bar (packets/sec, bandwidth, current interface)

2. **InterfaceSelectionView**
   - DataGrid/ListBox of interfaces:
     - Name
     - Description
     - IPs
     - Type (Ethernet/Wi‑Fi)
   - “Start Capture” button

3. **DeviceListView**
   - DataGrid:
     - IP
     - MAC
     - Hostname
     - Vendor
     - FirstSeen
     - LastSeen
     - Role (Router/AP/Client)
   - Filters:
     - Text search
     - “Active in last X minutes”
     - “Show only routers/APs”

4. **DeviceDetailView**
   - Header: IP/MAC/Hostname/Vendor
   - Tabs:
     - Overview (roles, services)
     - Activity (chart of packets/bytes over time)
     - Packets (list of recent packets for that device)
     - Protocols (DNS, mDNS, SSDP, etc.)

### Example XAML skeleton

```xml
<!-- LanInspector.UI.Views.MainWindow.xaml -->
<Window x:Class="LanInspector.UI.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:LanInspector.UI.ViewModels"
        Title="LanInspector" Height="800" Width="1400">
    <Window.DataContext>
        <vm:MainViewModel />
    </Window.DataContext>

    <DockPanel>
        <StatusBar DockPanel.Dock="Bottom">
            <StatusBarItem Content="{Binding StatusText}" />
        </StatusBar>

        <ToolBar DockPanel.Dock="Top">
            <Button Content="Start Capture" Command="{Binding StartCaptureCommand}" />
            <Button Content="Stop Capture" Command="{Binding StopCaptureCommand}" />
        </ToolBar>

        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="3*" />
                <ColumnDefinition Width="2*" />
            </Grid.ColumnDefinitions>

            <!-- Left: Interface + Device list -->
            <Grid Grid.Column="0">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>

                <GroupBox Header="Interfaces" Margin="5">
                    <!-- InterfaceSelectionView goes here -->
                </GroupBox>

                <GroupBox Header="Devices" Margin="5" Grid.Row="1">
                    <!-- DeviceListView goes here -->
                </GroupBox>
            </Grid>

            <!-- Right: Device detail -->
            <GroupBox Header="Device Details" Margin="5" Grid.Column="1">
                <!-- DeviceDetailView goes here -->
            </GroupBox>
        </Grid>
    </DockPanel>
</Window>
```

---

## 4. Plugin system outline

### Interfaces

```csharp
// LanInspector.Core.Plugins.IPlugin.cs
using System;

namespace LanInspector.Core.Plugins
{
    public interface IPlugin
    {
        string Name { get; }
        string Description { get; }
        Version Version { get; }

        void Initialize(IPluginContext context);
        void Shutdown();
    }

    public interface IPluginContext
    {
        void RegisterPacketAnalyzer(IPacketAnalyzer analyzer);
        void RegisterDiscoveryModule(INetworkDiscovery discovery);
        // Add more hooks as needed
    }
}
```

### Plugin manager

```csharp
// LanInspector.Core.Plugins.PluginManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LanInspector.Core.Plugins
{
    public class PluginManager : IPluginContext
    {
        private readonly List<IPlugin> _plugins = new();
        private readonly List<IPacketAnalyzer> _analyzers = new();
        private readonly List<INetworkDiscovery> _discoveries = new();

        public IReadOnlyList<IPacketAnalyzer> Analyzers => _analyzers;
        public IReadOnlyList<INetworkDiscovery> Discoveries => _discoveries;

        public void LoadPlugins(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;

            foreach (var dll in Directory.GetFiles(folderPath, "*.dll"))
            {
                var asm = Assembly.LoadFrom(dll);
                var types = asm.GetTypes()
                    .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);

                foreach (var type in types)
                {
                    var plugin = (IPlugin)Activator.CreateInstance(type);
                    plugin.Initialize(this);
                    _plugins.Add(plugin);
                }
            }
        }

        public void RegisterPacketAnalyzer(IPacketAnalyzer analyzer)
        {
            _analyzers.Add(analyzer);
        }

        public void RegisterDiscoveryModule(INetworkDiscovery discovery)
        {
            _discoveries.Add(discovery);
        }

        public void ShutdownAll()
        {
            foreach (var plugin in _plugins)
            {
                plugin.Shutdown();
            }
        }
    }
}
```

### Example plugin (DNS analyzer)

```csharp
// LanInspector.Plugins.DnsPlugin.DnsPlugin.cs
using LanInspector.Core.Analysis;
using LanInspector.Core.Plugins;

namespace LanInspector.Plugins.DnsPlugin
{
    public class DnsPlugin : IPlugin
    {
        public string Name => "DNS Analyzer";
        public string Description => "Parses DNS packets to map hostnames to IPs.";
        public Version Version => new(1, 0, 0);

        public void Initialize(IPluginContext context)
        {
            var analyzer = new DnsAnalyzer(/* dependencies */);
            context.RegisterPacketAnalyzer(analyzer);
        }

        public void Shutdown()
        {
            // Cleanup if needed
        }
    }
}
```

---

## 5. Class diagram (textual)

### Core

- **`PcapCaptureProvider`** implements `ICaptureProvider`
  - Emits `PacketCapturedEventArgs`
  - Used by `MainViewModel` (via a service layer)

- **`Device`**
  - `MacAddress : string`
  - `IpAddresses : HashSet<string>`
  - `Hostname : string`
  - `Vendor : string`
  - `FirstSeen : DateTime`
  - `LastSeen : DateTime`
  - `IsGateway : bool`
  - `IsAccessPoint : bool`

- **`IPacketAnalyzer`**
  - `Analyze(Packet packet)`
  - Implementations:
    - `ArpAnalyzer`
    - `DnsAnalyzer`
    - `MdnsAnalyzer`
    - `SsdpAnalyzer`

- **`INetworkDiscovery`**
  - `PingSweepAsync(IPAddress subnet, int cidr)`
  - Implementation:
    - `LocalSubnetDiscovery`

- **`PluginManager`** implements `IPluginContext`
  - Holds:
    - `List<IPlugin>`
    - `List<IPacketAnalyzer>`
    - `List<INetworkDiscovery>`

### UI (MVVM)

- **`MainViewModel`**
  - `Interfaces : ObservableCollection<NetworkInterfaceInfo>`
  - `Devices : ObservableCollection<Device>`
  - `SelectedDevice : Device`
  - Commands:
    - `StartCaptureCommand`
    - `StopCaptureCommand`
  - Uses:
    - `ICaptureProvider`
    - `PluginManager`

- **`InterfaceSelectionViewModel`**
  - `Interfaces : ObservableCollection<NetworkInterfaceInfo>`
  - `SelectedInterface : NetworkInterfaceInfo`

- **`DeviceListViewModel`**
  - `Devices : ObservableCollection<Device>`
  - `SelectedDevice : Device`
  - Filter properties

- **`DeviceDetailViewModel`**
  - `Device : Device`
  - `RecentPackets : ObservableCollection<PacketSummary>`

---

