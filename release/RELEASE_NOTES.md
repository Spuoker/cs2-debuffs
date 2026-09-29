## cs2-debuffs 1.0.0

A training-debuff roulette for CS2: one random combo per match (sound, color, HUD).
No injection, no hooks: Windows system APIs, the game's own cfg and the official GSI.

### Setup
1. Install **.NET 8 Desktop Runtime (x64)**: https://dotnet.microsoft.com/download/dotnet/8.0 → Windows → .NET Desktop Runtime 8 → x64.
2. Download `cs2-debuffs.zip` below, unpack it anywhere, run `cs2-debuffs.exe`.
3. Optional: [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) for the mono / bass_boost / muffle debuffs.
4. CS2 in "Fullscreen Windowed".
5. Add two binds in the CS2 console (the settings window shows the ready-made command for your key):
   `bind END "exec debuff_key"` — instant apply on the toggle key;
   `bind w "+forward; exec debuff"`, `bind ctrl "+duck; exec debuff"` — a safety net that catches up on its own.
   Without the binds the game won't apply the HUD debuffs (tab/radar/killfeed/crosshair).

Details are in the README.

### What's in it
- **One key for everything**: press it and every debuff in the combo is applied (sound, color, HUD),
  press again and they're removed. Nothing arms itself. The key is rebindable in the settings window,
  and the CS2 bind command is updated right there.
- **FACEIT-safe mode** by default: no windows over the game, the combo lineup arrives as a system notification.
- **Strict mode**, "you turned it on, now live with it": once the debuffs are applied you can't remove
  them until the match ends, can't disable the app and can't kill it. 30 seconds after applying to
  change your mind (hold the key for 2 s).
- Reconnect into the same match — same combo. Self-healing after any crash.
- `cs2-debuffs.exe --selftest` — check the logic without the game.

Nobody gives you any anti-cheat guarantees — see the "Anti-cheat — straight talk" section in the README.
