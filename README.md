# cs2-debuffs — a training-debuff roulette for CS2

One random combo of handicaps per match: sound, color, HUD.
The point is to train silhouette recognition and focus without "sterile" conditions.
No injection, no hooks: only Windows system APIs, CS2's own settings (cfg)
and the official Game State Integration. You apply the whole combo with one key;
the app takes it off when the match ends.

## Setup (once)

A ready-to-run build lives right in this repo, in the **`release`** folder: `cs2-debuffs.exe` and
`data\debuffs.json`. Grab the whole thing (green **Code → Download ZIP** button, then take the
`release` folder out of it) and put it anywhere except `Program Files` — the app writes next to
itself, and Windows won't let it write there. Desktop, `Documents`, a folder of its own on any
drive: all fine.

Everything of its own (logs, state, match journal) the app keeps in that same `data` folder.

0. **.NET 8 Desktop Runtime**: https://dotnet.microsoft.com/download/dotnet/8.0
   — in the **Windows** column of the **.NET Desktop Runtime 8** row, grab **x64** and install it.
   One install, shared by every app on the machine. Forget it and Windows itself will pop up a
   window with the link when you launch.
1. **Equalizer APO** (for the mono / bass_boost / muffle debuffs): https://sourceforge.net/projects/equalizerapo/
   — tick your headphones during the install. If the sound doesn't change (a classic with Realtek):
   Configurator → Troubleshooting options → "Install as SFX/EFX (experimental)" → reboot.
   The app works fine without APO, those three debuffs just never come up in the roll.
   **Non-standard or portable install?** You can point the app at the folder by hand: settings
   window → "Equalizer APO — config.txt folder" → "Browse…". Leave it empty and the app looks the
   path up in the registry itself (`HKLM\SOFTWARE\EqualizerAPO\ConfigPath`). Takes effect after you
   restart the app.
2. **CS2**: display mode — "Fullscreen Windowed" (otherwise the screen effects won't land on top of
   the game).
3. **Two binds in CS2** (in the console, once — they persist). Without them the HUD debuffs
   (tab/radar/killfeed/crosshair) simply can't be applied: only the game itself can turn them on.
   - On the **toggle key** — instant apply. The settings window shows the ready-made command for
     whatever key you picked, with a "Copy" button. For the default key it's:
     `bind END "exec debuff_key"`
   - On your **movement keys** — a safety net that catches up if something drifts out of sync:
     `bind w "+forward; exec debuff"`, `bind ctrl "+duck; exec debuff"`, `bind space "+jump; exec debuff"`
4. Run `cs2-debuffs.exe`. It finds CS2 on its own, drops in the GSI config
   (`gamestate_integration_debuffs.cfg`) and starts spinning the roulette. If the game was already
   running, restart it once so GSI gets picked up.

## Autostart and how it attaches to CS2

The FACEIT client launches CS2 itself, **bypassing Steam's launch options** — so the `%command%`
trick (co-launch through Steam) won't work on FACEIT. The reliable approach, identical for MM,
FACEIT and a plain desktop shortcut, is **autostart on Windows login + watching the `cs2.exe`
process**:

- Turn it on with the **"Start with Windows"** checkbox in the tray (or `cs2-debuffs.exe --autostart on`).
  It drops a shortcut into the Startup folder — visible in Task Manager → "Startup apps", no admin
  rights needed. Turn it off in the same place or with `--autostart off`. If you move the folder,
  switch it on again from the new location.
- After that the app starts with your session and **dozes** (no effects, near-zero CPU) until it
  sees CS2 and a match. Activation inside the match goes through GSI (live → debuffs applied,
  gameover → removed).
- Want the "lives and dies with the game" model (CS2 closes → app closes)? Set
  `"exit_when_cs2_closes": true` in debuffs.json. By default it sits in the tray and waits for the
  next game.

Why not "wired into the game process": tinting only the CS2 window would take injecting into its
process — and that's a ban. Our attachment is honest and external: **process watching** (is CS2
running) + **GSI** (is a match live) + **window focus** (visuals only while you're in the game).
Three signals, and none of them touches the game itself.

To check the state by hand: `cs2-debuffs.exe --status` (tells you whether CS2 is running, whether a
match is live, whether the window is focused, whether autostart is on).

## Playing with it

- The app sits in the tray. At the start of a match it rolls a combo and sends you the lineup as a
  **system notification** — so you know what you're in for. (Uncheck safe mode and you get an
  **on-screen banner** plus a panel on Tab instead.)
- **You are the only one who turns the debuffs on — with one key** (`End` by default). Press it and
  EVERYTHING applies at once: sound, color, tab, radar, killfeed, crosshair. Press again and it all
  comes off.
- Nothing arms itself: not on a match, not on a round. Forget to press and the game is clean.
- A combo can come out empty — that's a control game, by design.
- At the end of a match the app removes its own part automatically and reminds you to tap the key so
  the game gives your tab and radar back.

## One combo holds for the whole match, and is rerolled only for a new one

- One combo per match. **Reconnect or crash back into the same match — same combo** (everything is
  reapplied once you're back in live). A new combo is rolled when a new match starts (warmup) or
  when you **join a new map** (including dropping into a casual game or a match in progress, or a
  map rotation on a community server). Disconnecting to the menu and gameover don't change the combo.
- You can check the lineup any time: tray → **"Status…"** (always works), or tray → "Show combo on
  screen" (the banner — only if safe mode is off).

## One key for everything

The key (`toggle_hotkey`, `End` by default) **applies and removes every debuff at once**. To rebind:
window → "General" → "Key" → **Assign** → press any key (Esc cancels, "Off" removes it entirely).
The new key works immediately, no restart.

Why you need the in-game binds: tab, radar, killfeed, player IDs and the crosshair live inside CS2,
and nothing outside can switch them — only a command in the game itself. The app doesn't press keys
for you; that's exactly what gets people banned. So it writes two files and the game executes them:

- **`debuff_key.cfg`** — bound to the toggle key (`bind END "exec debuff_key"`). It holds whatever
  should happen on the **next** press. The game reads the file the moment you press, before the app
  can rewrite it — which is why the HUD flips **instantly**, with no lag.
- **`debuff.cfg`** — bound to your movement keys. It holds the **current** state, so any step just
  pulls the game up to whatever the app has already turned on. A safety net for when the app
  restarted, a press didn't land, or the game didn't execute something.

| What you press | What happens |
|---|---|
| The key, in a match | EVERY debuff in the combo applied: sound, color, HUD |
| The key again | everything removed |
| First step in the menu / after the match | HUD reset — fixes the game if something stayed on |
| Empty combo | "clean game" notification, nothing changes |

## FACEIT-safe mode — ON BY DEFAULT

Out of the box the app runs in the safe profile. Window → "General" → **"FACEIT-safe mode"** —
uncheck it if you want the banner and the panel on Tab and you only play MM.

The mode drops everything that looks suspicious to strict kernel-level anti-cheats even though it
isn't cheating:

- **it draws no window over the game at all** — no combo banner, no panel on Tab
  (normal overlays hide themselves from screen capture, and that is exactly an ESP cheat's trick —
  a red flag for an AC);

You don't **lose** the lineup: it arrives as a Windows **system notification** (a shell window, not
ours — to an AC it looks like a Discord popup), and tray → "Status…" always has it. The debuffs
themselves (sound, color, cfg) work as usual.

> **Straight up: this guarantees nothing.** FACEIT's anti-cheat decides, not us. The app doesn't
> inject into CS2, doesn't read its memory and doesn't send input into it — but an AC judges by
> profile, not by intent. The safest option for FACEIT is to close the app altogether (tray →
> "Exit"); a "disabled mod" is still a running process. The call and the risk are yours.

## Turning the mod off, or uninstalling it

- **Turn it off completely:** the "Mod enabled" checkbox in the settings window, or the
  **"Mod enabled"** item in the tray. A disabled mod applies nothing and resets the cfg debuffs
  (your bind will bring tab/radar/killfeed back).
- **Uninstall:** the **"Uninstall mod…"** button in the settings window (or tray →
  "Uninstall mod…"). It removes autostart, deletes `gamestate_integration_debuffs.cfg` and
  `debuff.cfg` from the CS2 folder, clears the mute/APO and puts sound and the game's settings back
  to normal. Deleting the program folder is on you — it opens in Explorer.

## Strict mode — "you turned it on, now live with it"

Off by default. Window → "General" → **"Strict mode"** or tray → "Strict mode" (it asks for
confirmation).

**30 seconds to change your mind.** The lock doesn't snap shut when the match starts — it snaps
**30 seconds after you apply the debuffs with the key**. During that grace period you can **hold the
key for 2 seconds** and strict mode drops **for this match** (it's back in force in the next one),
or you can switch it off entirely in the tray/settings. A short press during the grace period
doesn't remove the debuffs, it just tells you to hold — so you can't kill it by accident. After 30 s
you get a "strict mode on" notification, and from then until the match ends:

- **you can't remove them**: the key answers "strict mode", the debuffs stay until the match is over;
- **you can't disable the mod**: the "Mod enabled" checkbox in the tray and in the window is locked;
- **you can't exit or uninstall**: "Exit", "Uninstall mod…" and "Clean up stuck effects" refuse with
  a notification;
- **settings are locked**: the window opens read-only, with a red bar across the top;
- **you can't kill it**: two processes are running — the main one and its watchdog partner — and they
  guard each other. Kill one in Task Manager and the other brings it back in ~1–15 seconds
  **with the same combo** (not a fresh roll).

It lets go at the **end of the match** or when you **leave the match to the menu** — after that
everything is available again, including turning strict mode itself off. CS2 closing also counts as
the end of a match.

- **Self-healing**: however the app dies, on its next start it clears a stuck mute and cleans out its
  Equalizer APO lines by itself.
- Fuse: if the main process crashes on startup more than 5 times in 2 minutes, the watchdog gives up
  and cleans up after itself.

Straight about the limits: this doesn't make the app unkillable. Take both processes down at once
(or from an admin account) and it dies. It's self-discipline, not protection from you-the-
administrator; there are no kernel drivers here.
**Don't turn strict mode on for FACEIT**: a pair of processes resurrecting each other is a
suspicious profile.

Emergency manual cleanup if something got stuck: `cs2-debuffs.exe --cleanup`
(or the "Clean up stuck effects" item in the tray).

## Settings

**The window opens by itself on a normal launch** (double-click the exe); after that it's a
**double-click on the tray icon** or tray → "Settings…". Autostart with Windows brings the app up
quietly in the tray, without the window.

In the window: the master "Mod enabled" checkbox, **strict mode**, key assignment, enabling or
disabling any debuff and its chance, overall difficulty, the banner, fade smoothness, autostart,
effect strength, conflict rules and the "Uninstall mod…" button. Chances and the lineup apply to the
**next** game (the current combo is left alone). Saving writes `debuffs.json`.

## Debuff list

| Slot | id | What it does |
|---|---|---|
| sound | no_sound | full game mute (takes the whole sound slot) |
| sound | mono | no stereo — positional audio is dead |
| sound | bass_boost | a rumble; footsteps drown in it |
| sound | muffle | underwater sound |
| sound | white_noise | masking pink noise |
| voice | no_voice | voice_modenable false |
| color | grayscale / colorblind / invert | black and white, colorblindness (random type), negative |
| color | contrast_up / brightness_down / washed_out | crushed contrast, darkness, "milk" |
| color | rainbow | live palette cycling — colors drift around the wheel |
| HUD | no_tab / no_killfeed / no_radar / no_ids / no_crosshair | CS2 cfg convars, applied by your own `exec debuff` bind |

`no_radar` and `no_sound` **never come up together** — that would leave you zero info about
positions, and that isn't training. The other conflict rules and chances are in the settings window
(or by hand in `debuffs.json`, see [src/DEVELOPMENT.md](src/DEVELOPMENT.md)).

## Requirements

- Windows 10 (2004+) / 11, 64-bit.
- CS2 in "Fullscreen Windowed".
- For the mono / bass_boost / muffle sound debuffs — [Equalizer APO](https://sourceforge.net/projects/equalizerapo/).
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).

First launch of the downloaded exe: Windows SmartScreen will complain about an unsigned app —
"More info" → "Run anyway". That's normal for a self-built exe.

## Anti-cheat — straight talk

Nothing is injected into the game and nothing is read out of its memory. It all happens from the
outside: Windows system APIs (sound through audio sessions/APO, color through Magnification), CS2's
own settings via cfg (legal convars, no `sv_cheats`) and the official GSI. Keys are polled with
`GetAsyncKeyState`, no hooks. To VAC this is the same class of program as OBS or Discord.

**Safe mode is on by default** — the app draws nothing over the game whatsoever, and the watchdog
pair only exists in strict mode (off by default): by profile it's an ordinary background
application. Uncheck it and you get the banner and the panel on Tab (layered click-through windows,
like the Discord/Steam overlay) — and those are worth keeping for MM only: strict kernel-level
anti-cheats (FACEIT/ESEA) don't like windows over the game, and they especially don't like ones
hidden from screen capture.

**Nobody gives you any guarantees.** The app honestly doesn't cheat, but an anti-cheat judges the
mechanism, not the intent. For FACEIT the safest option is to close the app completely (tray →
"Exit"). The decision and the risk are the user's.

## Development

Folders in the root: `src` — sources and the build, `app` — the running copy with its logs and
state, `release` — what ships to the user.

Everything about building from source, how the code is laid out, the self-test, console flags and
distribution is in a separate file, [src/DEVELOPMENT.md](src/DEVELOPMENT.md). You don't need it to
play.

## License

MIT — see [LICENSE](LICENSE). Do whatever you want; the author is not responsible for anything.
