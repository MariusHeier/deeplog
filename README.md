# DeepLog

30-second controller recorder for MH gamepads. Records the actual data your
controller sends - every stick, trigger, and button value - and uploads it
with a short note, so joystick feel, drift, and "something is off" reports
come with the data attached.

A recording of everything feeling **normal** is just as useful as one where
something is wrong: two recordings to compare beats one.

## How it works

1. **Plug in your controller.** It is detected by name (MH4/MH5, PS4 mode,
   MH-XSX - any XInput pad works too). A menu appears if several are
   connected.
2. **5-second countdown**, then **30 seconds of recording.** Move the
   joysticks in circles around the edge, click buttons - whatever is
   relevant for your case.
3. **Write a one-line note** ("Log of normal joystick feeling" / "Log of
   joystick feeling weird"), optionally your nickname and email.
4. **Review exactly what gets sent**, then upload. You get a Log ID to
   paste in the Discord support channel.

## What gets recorded

- Stick / trigger / button values with microsecond timestamps:
  - **XInput pads** (MH4, MH5, MH-XSX): polled at 8000 samples per second
  - **PS4 mode** (054C:05C4): raw USB input reports, stored verbatim
- Disconnects, if one happens mid-recording (that gets recorded too - it's
  useful)
- A system snapshot of the things that make USB flaky: power plan, USB
  selective suspend, fast startup, USB controllers, driver versions,
  controller-related software running
- Where the controller is plugged in: which USB controller (CPU-direct,
  chipset or add-in), how many hubs in between, what else shares that
  controller and hub, the drivers actually loaded on the controller, and its
  per-device power / selective-suspend state
- Whether ViGEmBus, HidHide and USBPcap are running, not just installed
- Every driver loaded on the controller's USB path (the controller, its hubs,
  its USB port), with who made and signed each one, so that non-Microsoft
  drivers can be told from Windows' own. Being on this list only means the
  driver was loaded there, not that it causes a problem
- PC load during the recording (CPU per core, DPC/interrupt time, memory,
  top 5 programs by CPU), sampled 4x per second on the same clock as the
  controller data
- The controller's chip ID (so its factory calibration can be looked up),
  and a random ID DeepLog makes for this PC on first run

## What gets sent (and what doesn't)

Sent: the 30-second controller recording, your note, your nickname/email
if you chose to give them, the system snapshot, and the PC load recording.
USB device serial numbers are masked. The PC ID is random (kept in
`%LOCALAPPDATA%\DeepLog\install_id.txt`); it is not derived from any
hardware or Windows ID. A driver file outside the Windows folder is sent as its
file name only, and your Windows username and PC name are removed from what is
reported about each driver. The driver check reads files already on your PC and
uses no network.

Not sent: your Windows username, files, keystrokes, browsing, or anything
you typed outside the tool. The review screen lists the full contents
before upload, and declining keeps the recording on your PC (under
`%LOCALAPPDATA%\DeepLog`).

## Data format

Each bundle (zip) contains `data.mhc` (binary), `meta.json`, `note.txt`,
`snapshot.json`, and (v2.1+) `load.json`. v2.1 only adds fields and files:
`meta.installId`, `meta.padUid`, `meta.device.uid`; `snapshot.installId`,
`snapshot.driverServices`, `snapshot.padFiltersInStack`, `snapshot.usb`
(`usbHostControllers`, `usbDevices`, `padPlacement`); `load.json` rows are
keyed by `t_us`, the same clock as the `.mhc` records.

v2.2 adds `snapshot.padDriverPath`, and `usb.padPlacement.driverPath` with the
live stack of each device on the path:

- `status`: `complete` (every driver decided, every stack read), `incomplete`
  (see `couldNotTell` and `nodesWithNoStack`), `no-pad` or `error`. Only
  `complete` means nothing is unknown.
- `summary`: one plain sentence.
- `notMicrosoft`, `couldNotTell`, `microsoft`: stack entries such as
  `\Driver\HidHide`. Each driver is in exactly one.
- `drivers`: per driver, `verdict`, `why`, `seenOn`, `signatures`, `file`,
  `serviceState`, and `fileSays` (what the file claims about itself).
- `otherDriverPackages`: driver packages not from Windows' own set. These can
  bind a Microsoft file (Zadig's WinUSB) or a user-mode driver, which the stack
  shows only as Microsoft's `WUDFRd`.

`microsoft` needs proof: a signature Windows accepts, from a signer Microsoft
uses for its own files, on a chain Windows says ends at a Microsoft root. The
"Microsoft Windows Hardware Compatibility Publisher" signature does not count,
because it also signs other companies' drivers.

`padFiltersInStack` keeps its v2.1 shape and scope (the pad and what hangs off
it), but from v2.2 it holds everything there not proved Microsoft's, instead of
matches against a fixed list of names. It is left out when no list could be made
(no pad, or the check failed), so never read a missing field as clean.

The `.mhc` layout: 32-byte header (`MHC1`, version,
proto, record count, unix start time in us, record size), then fixed-size
little-endian records:

- proto 0 (XInput, 24 B): `u32 t_us, u32 packet, i16 lx, i16 ly, i16 rx,
  i16 ry, u8 lt, u8 rt, u16 buttons, u8 phase, u8 connected, u16 pad`
- proto 1 (PS4 raw HID, 72 B): `u32 t_us, u32 seq, byte[64] raw report`

## Bench flags

- `--snapshot` prints the system snapshot (no recording)
- `--headless [--seconds N] [--note TEXT] [--allow-no-pad] [--upload]`
  records without prompts; never uploads unless `--upload` is given
- `--probe-usb VID:PID` prints the placement/stack/power probe and the driver
  report for any present USB device (read-only)
- `--identify-driver NAME|FILE ...` prints the verdict for driver services or
  files; `--identify-driver all` covers every kernel driver service
- `--replay-stacks FILE` runs the driver report on stored stacks
  (`[{"id": "...", "stacks": [["\\Driver\\x", ...], ...]}, ...]`). Files are
  looked up on this PC, so a driver not installed here comes out could-not-tell

## Requirements

- Windows 10/11
- No admin needed (v1 required it for ETW tracing; v2 has no ETW), and no
  WMI (v2.1 reads the device tree through CfgMgr32)
- An MH controller, or any XInput gamepad

## Building from Source

```bash
cd DeepLog
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Related

- [DeepPoll](https://github.com/MariusHeier/deeppoll) - USB polling rate
  analyzer (DeepPoll measures how often the controller talks; DeepLog
  records what it said)

## License

MIT License - See [LICENSE](LICENSE)
