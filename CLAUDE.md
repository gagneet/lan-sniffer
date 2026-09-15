# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

LanInspector: a .NET 8 home-network inspection tool with a Windows WPF app (`LanInspector.UI`) and a cross-platform CLI (`laninspector`, `LanInspector.Cli`) that share `LanInspector.Core`.

## Commands

```bash
dotnet build LanInspector.sln -c Release          # whole solution; see below for the WPF project on Linux
dotnet test tests/LanInspector.Tests/LanInspector.Tests.csproj
dotnet test tests/LanInspector.Tests/LanInspector.Tests.csproj --filter "FullyQualifiedName~TopologyBuilderTests"   # one class
dotnet test tests/LanInspector.Tests/LanInspector.Tests.csproj --filter "FullyQualifiedName~TopologyBuilderTests.AddLocalProfile_AddsThisMachineAndGatewayNodes"
dotnet run --project src/LanInspector.Cli -- status   # CLI; args after --
./scripts/publish-all.sh [--skip-tests]           # tests, clears artifacts/, single-file CLI for win/linux/osx (WPF needs Windows: scripts/publish-all.ps1)
```

There is no lint step; CI (`.github/workflows/build-and-publish.yml`) builds Core, Platform.Linux and the CLI on Linux and runs the tests, then publishes the WPF app and the CLI for each OS.

Development happens on Linux, but the WPF app and packet capture (Npcap) only run on Windows. On Linux, check that the code builds and the unit tests pass. Leave the app and the network-dependent CLI commands for the user to validate on Windows.

**SDK gotchas.** The Ubuntu-packaged SDK (8.0.1xx) has no WindowsDesktop targets. `LanInspector.UI` fails with `MSB4019` there even with `EnableWindowsTargeting`. Build it with Microsoft's SDK from `dotnet-install.sh`, or build only Core, CLI and tests. The packaged SDK also reports `CS0121` for collection expressions passed to `string.Split` (`value.Split([',', ';'], ...)`), so write `new[] { ',', ';' }`. There is no `global.json` pinning the SDK.

## Architecture

**Project layering.** Keep interfaces and platform-neutral logic in `LanInspector.Core` (net8.0). OS-specific implementations go in `LanInspector.Platform.{Windows,Linux,MacOS}`, each of which references only Core. They implement `IRouteDiagnosticsService`, `ITerminalLauncher` and `ICapturePrerequisiteService` by shelling out to OS tools such as `Find-NetRoute`/`tracert`, `ip route`/`tracepath` and `route -n get`. To add a platform feature, put the interface in Core, add all three implementations, and wire them up in both front ends.

**Two composition roots, no DI container.** Services are constructed by hand in two places:
- `src/LanInspector.Cli/PlatformServiceFactory.cs` picks the platform implementation at runtime with `OperatingSystem.IsX()`, since the CLI references all three Platform projects.
- `src/LanInspector.UI/App.xaml.cs` builds everything with Windows implementations and passes it into `MainViewModel` through its constructor. That includes a UI-thread dispatch delegate and a shared `ConcurrentDictionary<string, Device>` that the packet analyzers write into. Tab view models are attached afterwards (for example `AttachSettingsTab`).

A new service usually has to be constructed in both places.

**CLI.** `CliApp.cs` is one large file. `RunAsync` dispatches on `args[0]` through a `switch` to `RunXxxAsync` methods. Flags are parsed by hand with `args.Contains` and `TryGetFlagValue`. Every command has its own timeout in `GetCommandTimeout`, 30s by default. A slow new command (scan, capture, radio) needs an entry there, or it gets cancelled partway through.

**Packet pipeline (UI only).** `PcapCaptureProvider` (SharpPcap) raises `PacketCaptured` with a PacketDotNet `Packet`. `MainViewModel.OnPacketCaptured` passes it to each `IPacketAnalyzer` inside its own try/catch, so a failing analyzer is logged and skipped. The analyzers are the `IDeviceObservingAnalyzer`s from `App.xaml.cs` (ARP, DNS/mDNS, DHCP), plus `TrafficFlowAnalyzer` and `LldpAnalyzer`. The device-observing analyzers update the shared device dictionary and raise events, which `DeviceNameRegistry` subscribes to for names. `TrafficFlowAnalyzer` feeds `TrafficFlowService`, which keeps 1-second buckets with 1-minute rollups for Traffic tab windows of up to 3 hours. It takes a `TimeProvider` so tests control the clock.

**Evidence and confidence model.** Several features gather evidence from several sources, rank it, and report a confidence level instead of a single answer:
- `Locator/DeviceLocatorService` (`laninspector locate`) collects candidates from the ARP cache (matched by MAC), Tailscale peers and `tailscale ping`, DNS/mDNS, location history, and configured IPs, then verifies them with a TCP probe. It has three opt-in steps:
  - `SweepLocalSubnets` pings the local subnets to repopulate ARP.
  - A MAC mismatch on a Tailscale direct path marks the NAT router in front of the device (`NatAddress`).
  - `InspectOverSsh` runs `SshNetworkInspector`'s script on the device, then `DeviceNetworkReportParser` turns the output into its addresses, gateway, router chain and advertised routes. The script output is covered by fixture tests in `DeviceNetworkDiagnosisTests`, so if you change the script, re-capture the fixture.
- `Topology/TopologyBuilder` is a fluent builder: `AddLocalProfile`, `AddKnownDevices`, `AddTailscaleStatus`, `AddFlipperSubGhzDevices`, then `Build`. Nodes and edges carry confidence and evidence, and the result can be exported as JSON or Mermaid.
- `Visibility` and `Diagnostics/ReachabilityExplainer` explain in plain English why a target is or isn't reachable. `RouteHelpers` flags RFC1918 traffic routed via CGNAT (`100.64/10`).

**External tools are optional wrappers.** Tailscale, nmap, tshark, AdGuard Home/Pi-hole, SNMP (SharpSnmpLib) and Flipper Zero (USB serial, `Core/Flipper`) each sit behind a Core interface. They must degrade gracefully when the tool isn't installed. Parsers live next to their services and are unit-tested against captured output, for example `TailscaleStatusParser` and the Nmap XML parser. To test process-based code without shelling out, follow the `ArpTableReader` pattern: a public parameterless constructor plus an `internal` constructor that takes a fake runner, made visible to tests with `InternalsVisibleTo LanInspector.Tests` in Core.

**Configuration.** `KnownDevicesConfiguration.LoadMany(paths...)` merges `known-devices.json` / `known-devices.local.json` by device `id`, with later files winning. From least to most specific, the sources are: exe `Data/`, then the user config dir (`~/.config/laninspector/` or `%APPDATA%\LanInspector\`), then the CWD (CLI only). The app ships with no devices configured. The Settings tab writes only to the per-user file. DNS provider credentials are in `integrations.json` in the same user config dir (`DnsIntegrationsConfigLoader`).

**Single-file publishing.** `oui.csv` and `known-devices.example.json` are `EmbeddedResource`s in Core, UI assets are WPF `Resource`s, and `DebugType` is `embedded` (`Directory.Build.props`). Every published folder holds exactly one executable, so don't add files that must sit next to the exe. `Version` lives in `Directory.Build.props`. Publishing with `-p:SourceRevisionId=<sha>` stamps the commit that `laninspector version` reports.

**UI.** WPF MVVM using CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). `MainWindow.xaml` hosts the tabs, and each tab has its own `*TabViewModel`.

## Docs

`README.md` is the most current overview. For task-specific docs, see `docs/tracking-a-moving-server-ip.md` and `docs/configuring-your-devices.md`. `docs/building-the-app.md`, `docs/initial-*.md` and the `next-phase-*`/`enhance*` docs are early design notes and prompts, and they don't match the code (for example, the separate `LanInspector.Plugins` project they describe doesn't exist).
