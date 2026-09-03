# WinWing Ursa Minor — Input Overlay

A transparent, always-on-top overlay that shows live stick, axis, hat and button input
from a WINWING Ursa Minor (or any other HID joystick) while you play.

Detected on this machine:

```
WINCTRL URSA MINOR Combat Joystick R    VID:PID 4098:BC2A
128 buttons
X, Y, Z         16-bit, 0..65535
RX, RY, Slider  0..4095
Hat             0..7
```

## Design goals

**Lowest possible cost while gaming.** Input arrives as `WM_INPUT` messages, so the process
sleeps completely when the stick is still — there is no polling loop. The repaint timer stops
itself after about half a second of no movement and restarts on the next report. Painting is
plain GDI+ into a small window, capped at 60 FPS and only when a value actually changed.

**Nothing that resembles cheating.** The program:

- reads input through Raw Input, registering **only** the Generic Desktop joystick, gamepad and
  multi-axis usages — keyboard and mouse are never registered, so it cannot observe typing or
  mouse movement even in principle;
- draws into **its own top-level window**. It does not inject a DLL, hook any API, or render
  inside the game — that is the mechanism used by ReShade, RivaTuner and the Discord/Steam
  overlays, and it is what anti-cheat objects to;
- never opens the game process, reads memory, or loads a driver;
- never synthesises input. It is strictly read-only — no `SendInput`, no virtual device.

The only global hooks in the process are two `RegisterHotKey` registrations (documented Win32,
not a keyboard hook) for the lock and hide shortcuts.

> The overlay will not appear over a game running in **exclusive fullscreen**. Use borderless
> windowed. On Windows 11 with "Optimizations for windowed games" enabled, borderless performs
> essentially the same as exclusive fullscreen.

## Build

Open `WinWingOverlay.sln` in Visual Studio 2022, or from a terminal:

```
Build.cmd
```

That produces a single `dist\WinWingOverlay.exe` (framework-dependent; needs the .NET 8 Desktop
Runtime, which is already installed here).

## Run

Double-click `dist\WinWingOverlay.exe`. It starts **locked**: click-through, always on top,
and it never steals focus from the game.

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+L` | Lock / unlock. Unlocked = draggable and resizable, and the border turns cyan. |
| `Ctrl+Alt+O` | Show / hide the overlay. |
| `Ctrl+Alt+M` | Minimal view — **only works while unlocked**, so it cannot fire by accident mid-flight. |
| Mouse wheel (unlocked) | Adjust opacity. |

**Minimal view** drops the button grid, the hat and the RX/RY mini-stick, leaving just X/Y,
Z and Slider. Change what it hides with `minimalHides`.

It is a **crop, not a rescale**. Layout is always computed from the full-view window size, and
minimal simply trims the window to the gauges that survive — so every remaining gauge and label
keeps exactly the size and position it had in full view. Nothing shrinks to fit.

That also means the minimal size is measured, not stored: it is derived from the full-view size
every time, so minimal view cannot be drag-resized. Size the overlay how you like it in full
view and minimal follows. Only the full view writes `width` and `height` to the config.

### If a hotkey does nothing

Global hotkeys are first-come-first-served across the whole system: `RegisterHotKey` fails with
`ERROR_HOTKEY_ALREADY_REGISTERED` if another running program already owns the combination, and
the overlay simply never sees the keypress. **On this machine `Ctrl+Alt+M` is already taken by
another program**, so the config here uses `Ctrl+Alt+N` for minimal view instead.

The overlay checks every registration and falls back to a free combination when one is refused,
telling you via a tray balloon on startup. **The tray menu always shows the combination that is
actually live** — trust it over this table. To choose your own, edit `hotkeys` in the config:

```json
"hotkeys": {
  "toggleLock": "Ctrl+Alt+L",
  "toggleShow": "Ctrl+Alt+O",
  "toggleMinimal": "Ctrl+Alt+N"
}
```

Modifiers are `Ctrl`, `Alt`, `Shift`, `Win` and at least one is required. Key names follow
.NET `Keys` names: letters are `A`–`Z`, number-row digits are `D1`–`D9`, function keys `F1`–`F12`.

### Troubleshooting

Set the `WINWING_TRACE` environment variable to any value and the overlay appends window sizing
events to `%TEMP%\winwing-trace.log`. Useful if the window ever comes up the wrong size.

## Moving and resizing

While unlocked in full view: drag anywhere to move, drag within 7 px of any edge or corner to
resize. In minimal view you can drag to move but not resize, because the size is derived. Then
press `Ctrl+Alt+L` to lock it back down before you fly.

A tray icon gives you Lock/unlock, Show/hide, Reset position, Rescan devices, Open config
folder and Exit. Position, size, opacity and lock state are saved automatically.

## Mapping your controls

```
Run-Diag.cmd
```

This lists the device capabilities and then logs every change as you move things:

```
  BUTTON  27  down
  BUTTON  27  up
  AXIS   Slider  62.4%   raw 2556
  HAT    NE
```

Work through the grip and base, note the numbers, then label them in the config file.

## Configuration

`%APPDATA%\WinWingOverlay\config.json`

| Key | Meaning |
| --- | --- |
| `x`, `y`, `width`, `height` | Window rectangle. Managed for you while you drag. |
| `opacity` | 0.15 – 1.0. |
| `locked` | Start locked (click-through). |
| `vendorId` / `productId` | Which device to show. `16536` is WINWING; `0` means "any". |
| `buttonCount` | Buttons drawn in the grid. `0` auto-sizes to the 128 the stick declares — set it to `32` or so if you only use the first block and want bigger cells. |
| `showButtons` | Hide the button grid entirely for a compact axes-only readout. |
| `showAxisReadouts` | Percentage text under each bar. |
| `maxFps` | Repaint ceiling, 15 – 144. Lower it if you want it even cheaper. |
| `buttonLabels` | Friendly names, e.g. `{ "1": "Trigger", "5": "Pickle" }`. |
| `gaugeOrder` | Left-to-right gauge order. Default `["XY","Z","Slider","RXRY","HAT"]`. |
| `invertAxes` | Axes drawn upside down. Defaults to `["Slider"]`. |
| `minimalHides` | What minimal view hides. Defaults to `["Buttons","HAT","RXRY"]`. |
| `hotkeys` | See "If a hotkey does nothing" above. |
| `minimal` | Start in minimal view. |

Restart the overlay after editing the file.

## Layout

Reading left to right: the X/Y stick box, vertical bars for Z and Slider, the RX/RY mini-stick,
then the hat rosette. Anything the device does not report is simply left out, so this works
unchanged if you plug in a different stick. The button grid sits underneath.

Reorder any of it with `gaugeOrder`. Tokens are `XY`, `RXRY`, `HAT`, and any axis name
(`Z`, `RZ`, `Slider`, `Dial`, `Wheel`) which draws as a vertical bar. A bar axis your device
reports but the list omits gets appended on the right rather than silently vanishing.

### Inverted axes

`invertAxes` flips a gauge without touching the input itself — the overlay still reports what
the hardware sends, it just draws it the other way up so it matches an axis you have inverted
in game. The Slider ships inverted for exactly that reason: physically up reads as 0 % from
the hardware, and the overlay shows it as full.

## Source map

| File | Role |
| --- | --- |
| `Native.cs` | Win32 P/Invoke — window styles, Raw Input, hotkeys. |
| `Hid.cs` | `HIDP_*` structures and `hid.dll` imports. |
| `JoystickDevice.cs` | Device enumeration, capability parsing, report decoding. |
| `RawInputManager.cs` | Raw Input registration and `WM_INPUT` dispatch. |
| `OverlayForm.cs` | The window: click-through, hit-testing, hotkeys, tray, frame timing. |
| `OverlayRenderer.cs` | All drawing. |
| `OverlayConfig.cs` | JSON settings. |
| `Hotkey.cs` | Parses "Ctrl+Alt+M" into modifiers and a virtual key. |
| `DiagRunner.cs` | `--diag` console mode. |
