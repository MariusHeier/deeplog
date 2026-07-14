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

## What gets sent (and what doesn't)

Sent: the 30-second controller recording, your note, your nickname/email
if you chose to give them, and the system snapshot.

Not sent: your Windows username, files, keystrokes, browsing, or anything
you typed outside the tool. The review screen lists the full contents
before upload, and declining keeps the recording on your PC (under
`%LOCALAPPDATA%\DeepLog`).

## Data format

Each bundle (zip) contains `data.mhc` (binary), `meta.json`, `note.txt`,
and `snapshot.json`. The `.mhc` layout: 32-byte header (`MHC1`, version,
proto, record count, unix start time in us, record size), then fixed-size
little-endian records:

- proto 0 (XInput, 24 B): `u32 t_us, u32 packet, i16 lx, i16 ly, i16 rx,
  i16 ry, u8 lt, u8 rt, u16 buttons, u8 phase, u8 connected, u16 pad`
- proto 1 (PS4 raw HID, 72 B): `u32 t_us, u32 seq, byte[64] raw report`

## Requirements

- Windows 10/11
- No admin needed (v1 required it for ETW tracing; v2 has no ETW)
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
