# DeepLog

USB incident logger for MH gamepads. Records kernel-level USB activity to a
ring buffer while you play, so the moment something goes wrong is always
captured - then bundles it with your answers and a system snapshot for
support analysis.

## How it works

1. **Start DeepLog before plugging in** - the connection/enumeration itself
   gets recorded into a separate log.
2. **Play normally.** USB activity is recorded to a circular ring buffer
   (the last ~6-8 minutes), so DeepLog can run for hours without filling
   your disk.
3. **When something happens** (controller disconnects, input dies), press
   ENTER - or DeepLog notices the disconnect itself and stops automatically.
4. Answer three questions about what you saw.
5. Review exactly what gets sent, then upload. You get a Log ID to send to
   Marius on Discord.

## What gets sent (and what doesn't)

Sent: USB **timing** logs (when transfers happened - no data contents, no
keystrokes), your survey answers, and a system snapshot (power settings,
Windows build, USB controller and connected USB device models, driver
versions, controller-related software running).

Not sent: your name, Windows username, files, browsing, or anything you
typed. The tool shows the full snapshot before upload, and declining keeps
the log on your PC.

## Requirements

- Windows 10/11
- Administrator privileges (required for ETW tracing)
- ~2.5 GB free disk space (ring buffer + staging)

## Building from Source

```bash
cd DeepLog
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Related

- [DeepPoll](https://github.com/MariusHeier/deeppoll) - USB polling rate
  analyzer (measurement; DeepLog is for incident logging)

## License

MIT License - See [LICENSE](LICENSE)
