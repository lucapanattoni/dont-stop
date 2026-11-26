# 💗 Don't Stop! 💗

A tiny Windows tray app that keeps your laptop running when you close the lid and stops it sleeping when idle. It's one ~14 KB exe with no install, no background service, and no admin rights.

It's made so coding agents can keep working while you close the laptop and move around. That's especially useful if you keep prompting them from your phone with remote control.

The icon is grey when off and pink when on.

## Features

- **Ignore lid close:** Closing the lid turns the screen off, but the computer keeps running.
- **Block idle sleep:** The computer won't go to sleep on its idle timer. The screen can still turn off as normal.
- **Heat cutoff:** While the lid is closed with lid-ignore on, the laptop hibernates if it gets too hot.

## Usage

- **Right-click** the icon for the menu: *Ignore lid close*, *Block idle sleep*, *Exit*.
- **Left-click** the icon to turn both on, or both off if either is on.
- **Hover** over the icon to see the current state.

Your toggles are remembered between runs. It doesn't start with Windows unless you add it yourself: press `Win+R`, type `shell:startup`, and put a shortcut to `DontStop.exe` in the folder that opens.

## Download / build

Get `DontStop.exe` from the [Releases](../../releases) page, or build it yourself. You only need Windows, because the C# compiler ships with .NET Framework 4.x:

```
build.cmd
```

## How it works

- **Idle sleep** is blocked with `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`, the same thing video players do. It changes no settings, and Windows drops the request as soon as the app exits.
- **Lid close** is ignored by setting your power plan's *Lid close action* to *Do nothing*, on both battery and plugged in. Your original values are saved first and put back when you turn it off or exit.
  - If you switch power plans while it's on, the old plan is restored and the new one is set.
  - **Crash recovery:** If the app was killed while lid-ignore was on, the next launch restores your original lid setting and starts with lid-ignore off.
- **Heat cutoff:** Every 30 seconds, and only while lid-ignore is on and the lid is closed, it reads the ACPI thermal zones (`Thermal Zone Information` performance counters, no driver needed). After two readings in a row over the limit, it hibernates the laptop, or shuts it down if hibernate is disabled. When it wakes, both toggles are off and a notification says why.

Settings and state are stored in `HKCU\Software\DontStop`.

## Configuration

Edit the `Cfg` class at the top of [`DontStop.cs`](DontStop.cs) and run `build.cmd`:

| Setting | Default |
|---|---|
| `BoardZone` / `BoardLimit` | `\_TZ.TZS0`, 70 °C |
| `ChassisZone` / `ChassisLimit` | `\_TZ.TZS1`, 50 °C |
| `CheckMs` | 30000 |
| `Pink` | `#FF0086` |

Thermal zone names differ between laptops. To list yours:

```powershell
Get-CimInstance Win32_PerfFormattedData_Counters_ThermalZoneInformation | Select Name, @{n='C';e={$_.HighPrecisionTemperature/10-273.15}}
```

## License

Public domain, under [The Unlicense](LICENSE). Do whatever you want with it.
