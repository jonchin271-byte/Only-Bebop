# Only Bebop

Climb **Only Up!**'s tower as **Deadlock**'s **Bebop**, using Deadlock's movement (dash, double jump, mantle),
Bebop's **Hook** as a grapple and his **Uppercut** as a launch. Solo.

Only Bebop is a small program of its own that plays from your copies of both games:

- **Deadlock** (required, the host Melty starts it from): Bebop's model, animations and voice, the hero roster
  cards, his ability icons and the movement and ability numbers are read from your Deadlock install.
  Only Bebop never starts Deadlock and never connects to Valve's servers, so it can't touch your account.
- **Only Up!** (required, found through your Steam library): the level you climb, its props and its music are
  read from your Only Up! install. Only Up! was delisted from Steam in 2023, so you need to own it already.

Nothing from either game is included in this download. The first launch reads what it needs into
`%LOCALAPPDATA%\OnlyBebop\cache` on your PC; later launches start straight away.

## Controls (Deadlock's defaults)

| Key | Action |
|---|---|
| WASD / mouse | move / look |
| Space | jump; again in the air to double jump; hold into a ledge to mantle |
| Shift | dash (uses stamina) |
| Ctrl | sprint |
| 1 | Uppercut: launch yourself upward |
| 3 or Mouse 4 | Hook: aim at a ledge and reel yourself in (over the lip) |
| Esc | menu |

No checkpoints: a fall keeps you wherever you land, like Only Up!.

## Status (v0.1.0)

- Movement, Hook, Uppercut, mantle, level loading and Steam detection pass the headless bench
  (`OnlyBebop.exe -- --selftest`, 12 checks).
- Reading the real Deadlock and Only Up! files has **not yet been tested on a real install**; see
  `design/sheets` for the cells still marked unverified.

## Building

Design lives in `design/sheets/*.json` (the source of truth). `tools/preflight.py` checks them,
`tools/gen.py` generates `game/Generated/Sheets.g.cs` and `extract/plan.json`, and `tools/build.sh` builds the
Windows release (needs .NET 8 + 10 SDKs and Godot 4.5.1 .NET with export templates).
