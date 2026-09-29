# cs2-debuffs — development

Everything you need to build the project, understand how it's put together and ship a build.
A player doesn't need this file: [README.md](../README.md) is enough for them.

**Short version:** C# / .NET 8, WinForms, a single project. You need
[.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0). Build a release with
`./build-release.ps1`; check the logic without the game with `cs2-debuffs.exe --selftest`.

## Config — debuffs.json (by hand)

Every chance, conflict and link lives there (or in the settings window). No rebuild needed, just
restart the app. The main knobs:

- `general.global_chance_multiplier` — a global multiplier for all chances.
  **Start at 0.7** (more clean games), then raise it.
- `effects.*` — per-effect parameters (frequencies, grime opacity and so on),
  or the "Effect strength" section of the settings window.
- `fade_in_ms` / `fade_out_ms` — how smoothly effects fade in and out
  (220/380 ms by default; 0 is instant). The mute goes through volume,
  APO through an animated filter. No clicks or pops.
- `show_combo_banner` / `banner_on_round_start` — the lineup banner: at the start of a match and/or a round.
- `exit_when_cs2_closes` — close the app when CS2 closes (false by default — it stays in the tray).

## Checking it without the game

```
cs2-debuffs.exe --selftest         # self-test of the match logic (touches nothing in the system)
cs2-debuffs.exe --roll 1000        # combo distribution over 1000 games
cs2-debuffs.exe --apply grayscale 5   # turn an effect on for 5 seconds
cs2-debuffs.exe --status           # what was found, what's available
```

`--selftest` runs the match state machine through scenarios taken from real games: side switch,
match restart on duel/DM servers, leaving to the menu from every phase, reconnect (including
straight into halftime), toggling by hand with the key, all three start modes, process resurrection.
Effects, windows and the CS2 folder are left untouched. Exit code 0 means all green.

## How it's put together (for anyone digging into the code)

- `MatchBrain.cs` — the **match state machine**, the one place where "what should be on right now"
  is decided. Pure logic with no I/O, which is why the self-test can cover it.
- `GsiServer.cs` — only reports facts from the game (phase, map, menu, round); it decides nothing.
- `Program.cs` → `Reconcile()` — the **one place** where the world is brought in line with the state
  machine's decision: `debuff.cfg` (written only when the contents change) and the set of effects.
- `AudioModule.cs` / `DisplayModule.cs` — the effects themselves (sound, color). `CfgWriter.cs` — the
  cfg debuffs.
- `Program.Tray.cs`, `Program.Cli.cs`, `Program.Watchdog.cs`, `Autostart.cs` — tray, console flags,
  watchdog, autostart.

## What lands on the user's disk

Next to the exe — exactly one folder:

```
cs2-debuffs.exe
data\debuffs.json     config (chances, effect strength, settings)
data\logs\            app.log, watchdog.log
data\state\           markers: current combo, map, "applied", launch flags
data\journal.jsonl    match journal
```

The app creates nothing outside that folder (apart from its own files in the CS2 cfg folder and the
autostart shortcut). The old layout, where everything sat right next to the exe, migrates into `data`
by itself on the first launch — `Program.MigrateLegacyLayout`.

## Journal

`data\journal.jsonl`: every combo and every match with its score.
Use it to look for correlations between debuffs and results.

## Dependencies

| What | Why | How to install |
|---|---|---|
| **.NET 8 Desktop Runtime** | running the app | [download](https://dotnet.microsoft.com/download/dotnet/8.0) → Windows → .NET Desktop Runtime 8 → x64, install |
| **.NET SDK 8** | only to **build** from source | [download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **NAudio** (NuGet) | white-noise synthesis, audio plumbing | pulled in automatically by `dotnet build` (declared in `.csproj`) |
| **Equalizer APO** | 3 sound debuffs: mono / bass_boost / muffle | a separate third-party program, not part of the repo — [install it with its own installer](https://sourceforge.net/projects/equalizerapo/) |

Equalizer APO is **optional**: the app works in full without it, mono/bass_boost/muffle just never
come up in the roll (the roulette excludes unavailable debuffs by itself). Its path is looked up in
the registry, and for a non-standard install you set it by hand in the settings.

Nothing else: no engines, no third-party libraries beyond NAudio — just Windows calls.

## Building from source

You need [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0). Then:

```
git clone <repo>
cd cs2-debuffs
./build-release.ps1
```

The script drops a ready-to-run build into `release\`: `cs2-debuffs.exe` and `data\debuffs.json`.
That folder **is committed** — the repository is the distribution, and the user downloads the whole
thing. The script doesn't touch the working `app\` folder. The default config is also embedded as a
resource inside the exe (`ConfigLoader.EnsureExists`): if a user deletes `debuffs.json`, the app
recreates it.

## How to distribute it

1. `./src/build-release.ps1` — updates `release\`; commit it together with the code.
2. The user installs .NET 8 Desktop Runtime once (see "Setup" in README.md), takes the `release`
   folder from the repo and runs `cs2-debuffs.exe`.
3. What's left for them: install Equalizer APO (if they want the sound debuffs), switch CS2 to
   borderless, add the bind. Autostart with Windows switches itself on the first time the app runs.

Personal `data\logs`, `data\state` and `data\journal.jsonl` don't ship: they're in `.gitignore` and
get created on the first launch.

Open source: it all lives in one repository — sources in `src\`, the ready build in `release\`. The
user doesn't need the sources, and doesn't have to hunt for separate "releases" either.
