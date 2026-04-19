<p align="center">
  <img src="DomanMahjongAI/images/icon.png" width="128" alt="Doman Mahjong Solver icon">
</p>

<h1 align="center">Doman Mahjong Solver</h1>

<p align="center">
  A helper for Doman Mahjong at the Gold Saucer.<br>
  Get move hints while you play, or let it play for you.
</p>

---

## What this does

You open a Doman Mahjong table. A small window shows up that watches your hand and tells you the best tile to discard and why. That's the default.

If you want, you can switch it to **auto-play** — it'll click for you, with natural pacing that looks like a person thinking. Flip it back to hints, or turn it off entirely, whenever you want.

That's it. Three buttons: **Off / Hints / Auto-play**.

## Install

1. Open in-game: `/xlsettings` → **Experimental** tab.
2. Find **Custom Plugin Repositories** and paste this URL:
   ```
   https://raw.githubusercontent.com/XeldarAlz/FFXIV-MahjongAI/main/repo/repo.json
   ```
3. Tick the checkbox next to it, press **Save and Close**.
4. Open `/xlplugins`, search **Doman Mahjong Solver**, press **Install**.
5. Open the plugin (`/mjauto`). First time you run it, accept the short notice.

## Using it

Open the window with `/mjauto`.

The main window has three big buttons at the top:

- **Off** — the plugin watches nothing. It's asleep.
- **Hints** — shows you the best discard and top alternatives with a short reason. You click every move yourself.
- **Auto-play** — plays the table for you. Click a mode to switch instantly.

When you're at a table, the bottom half of the window fills in:

- Scores for all four seats.
- Your current hand.
- The top 3 discard candidates with a short line of reasoning.
- The last action the plugin took (so you can check it's keeping up).

Open **Settings** inside the window for:

- **Delay slider** — how long the plugin "thinks" before each click. Lower = faster.
- **Developer tools** toggle — ignore unless you're debugging something.

## Stuck on a call prompt?

Sometimes the in-game "pon / chi / pass" window has more than the usual number of buttons (for example, when two different chi combinations are possible). The plugin can misclick in that case.

If that happens:

- Click the correct option yourself in-game. The plugin carries on from the next turn.
- Or from chat: `/mjauto pass 2` clicks the third button (numbering starts at 0). Try `pass 1`, `pass 2`, `pass 3` until you find the right one. The rightmost button is always "Pass".

## Safety

Auto-play uses features that are **against the FFXIV Terms of Service**. Square Enix has and does sanction accounts for using third-party input automation. This plugin is provided as-is, with no guarantees.

Practical protections built in:

- **Off by default.** Nothing happens until you accept the terms notice, and again until you pick a mode other than Off.
- **Hints mode is 100% safe.** It never sends clicks to the game. It only reads your screen and talks to you.
- **Natural pacing.** When auto-play is on, clicks are spaced randomly around 1.2 seconds — you can adjust it.
- **Kill switch.** Flip back to Off and every click stops, mid-game, on the next frame.

Use hints mode unless you've thought carefully about the risk.

## Problems?

- **"Plugin failed to load"** after installing. You may still have a dev copy loaded from an older install. Open `/xlsettings` → Experimental → Dev Plugin Locations and remove anything that points at a local folder for this plugin. Then reinstall.
- **Icon doesn't show** in the plugin list. Dalamud caches the repo; click the refresh icon next to the custom repo entry in Settings → Experimental, or restart the game.
- **Anything else.** Report it at https://github.com/XeldarAlz/FFXIV-MahjongAI/issues with the text of the error message.

## License

AGPL-3.0-or-later. You're free to read, modify, and redistribute the source, as long as your version stays open-source under the same license.

---

<details>
<summary>For developers</summary>

### Build & test

```bash
dotnet build DomanMahjongAI.sln
dotnet test  DomanMahjongAI.sln
```

185 tests across an Engine library (tiles, shanten, ukeire, yaku, fu, scoring) and a Policy library (efficiency policy, riichi / call / push-fold evaluators, Bayesian opponent model, ISMCTS with progressive widening, hand simulator, evolutionary weight tuner, Tenhou log parser). Both libraries are Dalamud-free and portable.

### Layout

```
FFXIV-MahjongAI/
├── DomanMahjongAI/       Plugin entry · UI · dispatch · reader
│   └── images/icon.png   Plugin icon
├── Engine/               Core mahjong primitives
├── Policy/               Decision logic · ISMCTS · tuners · Tenhou parser
├── tests/                129 + 56 tests
├── repo/repo.json        Custom Dalamud repo manifest
├── tools/gen_icon.ps1    Icon regeneration script
└── .github/workflows/    CI + release automation
```

### Release process

Bump `Version` in `DomanMahjongAI/DomanMahjongAI.csproj` and `AssemblyVersion` in `repo/repo.json` to match, push a `vX.Y.Z` tag — the release workflow builds and uploads `latest.zip` automatically.

### Known outstanding work

- Opcodes for riichi / tsumo / ron / kan dispatch are speculative (currently `HookFailed` at runtime until confirmed with in-game capture).
- Opponent discard pools, dora indicator, dealer, round, and honba fields are not yet read from the game addon — the opponent model runs on a partial view until those offsets are mapped.
- Call-prompt button count auto-detection (to always click the rightmost "pass" regardless of how many chi variants are offered) — needs the user to capture a multi-chi event with `/mjauto log on` enabled.

</details>
