# Tallybar

A live, glassy AI-usage gauge drawn directly onto your Windows taskbar.

Tallybar renders a small strip next to the clock showing how much of your AI
coding-plan quota is left — a live sparkline, the current session percentage, and a
countdown to the next reset. No window to open, no tab to check: the number you care
about is just always there.

![License: MIT](https://img.shields.io/badge/license-MIT-6e5aff?style=flat-square)
![Windows 10+](https://img.shields.io/badge/Windows-10%2B-0a0a0c?style=flat-square)
![.NET 10](https://img.shields.io/badge/.NET-10-512bd4?style=flat-square)

## Why on the taskbar?

Tray icons are 16 pixels of guesswork. Network monitors solved this decades ago by
drawing straight onto the taskbar — Tallybar does the same for AI usage limits, so you
can plan a long agent run around your reset window at a glance.

- **Live sparkline** of recent usage, colored by state: green → amber (>75%) → red (>90%).
- **Reset countdown** (`↻ 3h42m`) plus the weekly window percentage.
- **Blends in**: per-pixel-alpha rendering over the taskbar's own material, light/dark
  theme aware, hides automatically over fullscreen apps.
- **Drag to place**: slide the strip anywhere along the taskbar; the position sticks.

## Privacy

Tallybar has no backend and no telemetry. It reuses the OAuth session that
[Claude Code](https://claude.com/claude-code) already maintains locally
(`~/.claude/.credentials.json`), sends it only to Anthropic's own usage endpoint, and
stores nothing of its own. If the token has expired, run `claude` once to refresh it.

## Getting started

Requires Windows 10 (1809+) or Windows 11, and the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build.

```bash
git clone https://github.com/Riyoway/Tallybar
cd Tallybar
dotnet run --project src/Tallybar.Strip
```

The strip appears just left of the clock after the first fetch (a few seconds).

| Action | Effect |
|--------|--------|
| Drag the strip | Move it along the taskbar (persisted) |
| Right-click the tray icon | Reset position · Re-attach · Exit |
| `Tallybar --probe` | Print fetched usage to the console, no UI |

## How it works

Windows 11 removed deskbands, so nothing can truly dock into the taskbar anymore.
Tallybar instead keeps a borderless, per-pixel-alpha layered window glued over the
taskbar surface:

- Anchors to `Shell_TrayWnd` / `TrayNotifyWnd` (stable on both Win10 and Win11,
  regardless of icon alignment) and repositions via a `SetWinEventHook` on the tray
  plus `WM_DISPLAYCHANGE` / `WM_DPICHANGED` / `WM_SETTINGCHANGE`.
- Re-attaches on the `TaskbarCreated` broadcast, so it survives Explorer restarts.
- Per-Monitor-V2 DPI aware; hides when `SHQueryUserNotificationState` reports a
  fullscreen app, mirroring the taskbar's own behavior.
- Usage is polled every 60 s with exponential backoff (to 30 min) on failure; the
  strip renders from a ring buffer and never blocks on the network. Failures keep the
  last known values on screen, marked stale — never a spinner on your taskbar.

## Limitations

- Primary horizontal taskbar only for now; vertical taskbars and secondary monitors
  are on the roadmap, as are more providers, a click-to-open detail popover, and
  packaged releases.
- The overlay shares space with the tray area rather than reserving it — if it covers
  an icon, drag it somewhere emptier.

## License

MIT — see [LICENSE](LICENSE).
