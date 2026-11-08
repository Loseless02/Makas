# Makas

Tiny, fast video/audio cutter for Windows. Open a file, cut, add fades, export. No projects, no import step.

- 1.3 MB single exe, opens a file in about 1 second
- Hardware-decoded preview (WebView2), instant seek, live waveform
- Split anywhere, remove parts, fade in/out on each part (video to black + audio)
- Live preview of the result: removed parts are skipped, fades play as they will export
- Export with GPU encoders (NVENC / Quick Sync / AMF) or CPU x264/x265
- Stream-copy mode: instant export with no quality loss (cuts snap to keyframes, no fades)

## Run

`dist\Makas.exe` — drag a file onto the window or onto the exe, or `Makas.exe "video.mp4"`.

Needs:
- .NET 10 Desktop Runtime
- WebView2 Runtime (built into Windows 11)
- ffmpeg + ffprobe, either on PATH (`winget install Gyan.FFmpeg`) or next to `Makas.exe`

## Keys

| Key | Action |
| --- | --- |
| Space / K | play / pause |
| S / B, double-click timeline | split at playhead |
| Del / X | remove or restore selected part |
| I / O | cut away everything before / after the playhead |
| F | toggle default fade on selected part |
| ← / → | one frame (Shift: 1 s, Ctrl: 5 s), J / L: 1 s |
| [ / ] | jump to previous / next cut |
| ↑ / ↓ | select previous / next part |
| Ctrl+wheel, + / −, 0 | zoom timeline, fit |
| P | toggle "preview result" |
| M | mute |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Ctrl+O | open |
| Ctrl+E | export (save dialog) |
| Ctrl+Shift+E | quick export next to source as `name_cut.ext` |

On the timeline: drag a cut line to move it, drag the yellow squares at the top corners of a part to set its fades.

## Build

```
cd src
dotnet publish -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ../dist
```

Set `MAKAS_TRACE=1` to log startup timings to `%LOCALAPPDATA%\Makas\trace.log`.
