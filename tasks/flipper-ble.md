# Open tasks: Flipper Zero BLE survey and the Flipper tab

Follow-ups left behind by `laninspector flipper ble`
(`src/LanInspector.Core/Flipper/Ble/`) and the WPF Flipper tab
(`src/LanInspector.UI/ViewModels/FlipperTabViewModel.cs`).

| ID | Task | Status | Severity |
|---|---|---|---|
| [FLP-01](#flp-01--the-wpf-tab-has-never-run) | The WPF tab has never run | **blocked on the user** | high |
| [FLP-02](#flp-02--debug-mode-is-discovered-by-failing) | Debug mode is discovered by failing | open | medium |
| [FLP-03](#flp-03--no-calibration-for-the-rssi-floor) | No calibration for the RSSI floor | open | medium |
| [FLP-04](#flp-04--no-fixture-coverage-for-custom-firmware) | No fixture coverage for custom firmware | open | medium |
| [FLP-05](#flp-05--the-serial-layer-screen-scrapes-a-human-cli) | The serial layer screen-scrapes a human CLI | open | low |
| [FLP-06](#flp-06--nfc-and-rfid-are-cli-only) | NFC and RFID are CLI-only | open | low |
| [FLP-07](#flp-07--the-survey-does-not-reach-the-topology) | The survey does not reach the topology | open | low |

---

## FLP-01 — The WPF tab has never run

**Status:** blocked on the user · **Severity:** high

**Analysis.** `LanInspector.UI` cannot be compiled on the development machine: the
Ubuntu-packaged SDK has no WindowsDesktop targets and fails with `MSB4019`, as
`CLAUDE.md` records. What *has* been verified:

- `LanInspector.Core` and `LanInspector.Cli` build clean, and the 301-test suite passes.
- `MainWindow.xaml` is well-formed XML.
- `FlipperTabViewModel` uses no WPF type, so it was compiled in a scratch `net8.0`
  library against CommunityToolkit.Mvvm 8.4.2, and the five generated command properties
  (`RefreshPortsCommand`, `ConnectCommand`, `DisconnectCommand`, `ScanSubGhzCommand`,
  `SurveyBleCommand`) were confirmed present in the emitted assembly and to match the
  names the XAML binds to.

What has **not** been verified: that the tab renders, that the `DataContext` resolves,
that the `NonEmptyToVisibility` converter behaves on these bindings, that `DwellSeconds`
survives a non-numeric entry, and that the whole thing works against a real Flipper.

**Required action.** Build and run on Windows:

```powershell
dotnet build LanInspector.sln -c Release
dotnet run --project src\LanInspector.UI
```

Then, with a Flipper attached: Refresh ports → Connect → Scan sub-GHz → Survey BLE band.

**Why it matters.** Everything else here assumes the tab works.

---

## FLP-02 — Debug mode is discovered by failing

**Status:** open · **Severity:** medium

**Analysis.** Stock firmware registers `bt rx_carrier` only when the RTC debug flag is set
(`furi_hal_rtc_is_flag_set(FuriHalRtcFlagDebug)` in `applications/services/bt/bt_cli.c`).
With it off, `bt <anything but hci_info>` falls through to `bt_cli_print_usage`.
`FlipperBleService.LooksLikeUsageText` detects that and returns a result carrying
`DebugModeRequired`, which the CLI and the tab both surface.

The cost is that the user waits the full dwell on **every** channel before being told.
Three channels at the default 5 s is a 15-second wait to learn the feature is switched off.

**Required fix.** Probe once before the loop: send bare `bt`, and if the reply is the usage
text without `rx_carrier` in it, return `DebugModeRequired` immediately. The reply is a
single-response command, so `ExecuteCommandAsync` covers it — no dwell. Keep
`LooksLikeUsageText` as the per-channel fallback: a firmware could register some
subcommands and not others.

**Why it matters.** It is the first thing every new user will hit, and the current
experience is fifteen seconds of apparent silence.

**Verification.** Extend `FlipperBleTests`: a fake whose `bt` reply lists only `hci_info`
must produce `DebugModeRequired` with **no** `rx_carrier` command sent (assert on
`FakeFlipper.Sent`).

---

## FLP-03 — No calibration for the RSSI floor

**Status:** open · **Severity:** medium

**Analysis.** The survey reports raw median and peak dBm and classifies nothing. That was
deliberate — there is no honest threshold to ship — but it leaves the reader to interpret
"-82 dBm median" with no reference. The floor is not a constant: it varies with the unit,
the antenna, the enclosure and the room, so a hard-coded "quiet below -90" would be wrong
on somebody's desk.

**Required fix.** Two parts, and the first is the important one:

1. **A baseline capture.** `laninspector flipper ble --baseline` records a survey to the
   user config directory (`~/.config/laninspector/` or `%APPDATA%\LanInspector\`, the same
   place `known-devices.local.json` lives). Later surveys report the delta against it.
   A delta against this radio in this room is meaningful where an absolute number is not.
2. **A knob**, not a constant: `--floor <dBm>` and a matching settings value, defaulting to
   the baseline when one exists and to no classification when it does not.

**Why it matters.** This is the calibration a physical measurement needs. Shipping a
number that looks authoritative and is not would be worse than shipping none, which is why
none was shipped — but the gap should close.

**Verification.** A fixture baseline plus a fixture survey, asserting the reported delta.

---

## FLP-04 — No fixture coverage for custom firmware

**Status:** open · **Severity:** medium

**Analysis.** `FlipperBleTests` replays official-firmware output only. Unleashed, Momentum
and RogueMaster are widely used and differ in ways this code touches:

- sub-GHz protocol names differ, which `ParseProtocol` degrades to `SubGhzProtocol.Unknown`
  — acceptable, and untested.
- some builds register additional `bt` subcommands, or register the RF test commands
  without requiring debug mode, which changes what `LooksLikeUsageText` sees.
- `device_info` field names differ, which `ParseVersionOutput` already tries three
  variants for.

**Required fix.** Capture real output from one custom firmware — `bt`, `bt rx_carrier 0`,
`device_info`, `subghz rx` — the way `DeviceNetworkDiagnosisTests` captures SSH script
output, and add a fixture test per parser. Do not guess the text: capture it.

**Why it matters.** Parsers built against one output format break silently on another, and
a silent break here reads as "no signals", which is the wrong answer.

**Research needed.** Which firmware the user actually runs. If it is stock, this drops to
low.

---

## FLP-05 — The serial layer screen-scrapes a human CLI

**Status:** open · **Severity:** low

**Analysis.** `FlipperSerialService` writes commands and parses the text meant for a
person, including prompt detection on the literal `">: "`. The firmware exposes a protobuf
RPC (`start_rpc_session`) which is what qFlipper and the mobile app use, and which does not
change shape when output formatting does. `pyFlipper` is a readable reference
implementation.

One concrete instance of the fragility was fixed in this change: `RunStreamingAsync` split
records on `'\n'` only, so `bt rx_carrier` — which rewrites one line with a bare `'\r'`
about ten times a second — would have buffered forever and returned nothing. It now splits
on either terminator.

**Required fix.** Not now. Move to RPC when a firmware update breaks a parser, not before;
it is a large rewrite of a layer that currently works and is fixture-tested.

**Why it matters.** Recorded so the next parser break is recognised as the signal rather
than patched again.

---

## FLP-06 — NFC and RFID are CLI-only

**Status:** open · **Severity:** low

**Analysis.** `laninspector flipper nfc` and `flipper rfid` work; the tab does not expose
them. Deliberate: both need the user physically holding a card against the device, which is
a poor fit for a network-inspection tab, and neither produces a node in the topology.

**Required fix.** Add them only if asked for. The view model pattern is already there —
each would be one `[RelayCommand(CanExecute = nameof(CanUseRadio))]` and one results row
type.

---

## FLP-07 — The survey does not reach the topology

**Status:** open · **Severity:** low

**Analysis.** `TopologyBuilder.AddFlipperSubGhzDevices` turns sub-GHz signals into
`WirelessIoT` nodes. The BLE survey has no equivalent, and should not have one as it
stands: a band-energy reading is not a device, and inventing a node for it would be exactly
the kind of false confidence the topology's evidence-and-confidence model exists to
prevent.

**Required fix.** None until there is a device to attach it to. If FLP-03's baseline lands,
the right shape is probably an *attribute* of the local node ("2.4 GHz band: adv 38 is
14 dB above baseline") rather than a node of its own.

**Why it matters.** Recorded so nobody adds `AddFlipperBleSurvey` by symmetry with the
sub-GHz method without noticing that the two are not the same kind of evidence.
