<p align="center">
  <img src="DomanMahjongAI/images/icon.png" width="120" alt="Doman Mahjong Solver icon">
</p>

<h1 align="center">Doman Mahjong Solver (WORK IN PROGRESS)</h1>

<p align="center">
  A helper for <b>Doman Mahjong</b> at the Gold Saucer.<br>
  Hints while you play — or let it play for you.
</p>

<p align="center">
  <a href="https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/releases/latest"><img alt="Release" src="https://img.shields.io/github/v/release/XeldarAlz/FFXIV-DomanMahjongSolver?label=release&color=blue"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-AGPL--3.0--or--later-green"></a>
  <img alt="Platform" src="https://img.shields.io/badge/platform-FFXIV%20%7C%20Dalamud-orange">
</p>

---

## What it does

You sit at a mahjong table. A small window shows up, watches your hand, and suggests the best tile to discard and why. Three modes, one click each:

- **Off** — plugin sleeps.
- **Hints** — shows the best discard + top alternatives with a reason. You click every move. *100% safe.*
- **Auto-play** — plays for you with natural pacing that looks like a person thinking.

## Client compatibility

The mahjong addon ships under different names and memory layouts per region. EU is the reference variant. NA support is in active work; JP and OC need verification.

| Feature | EU (`Emj`) | NA (`EmjL`) | JP | OC |
|---|---|---|---|---|
| Window detection | Yes | Yes | Untested | Untested |
| Hand / score reading | Yes | Yes | Untested | Untested |
| Discard suggestions | Yes | Misaligned ([#30](https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/issues/30)) | Untested | Untested |
| Auto-discard | Yes | Blocked by suggestion miss | Untested | Untested |
| Call prompts (Pon/Chi/Kan) | Yes | Chi miss ([#30](https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/issues/30)) | Untested | Untested |

If you're on JP or OC and willing to help verify: seat at a Doman table, run `/mjauto variant dump`, and attach the file to a new issue. That's the input needed to wire each client up.

## Install

In-game: `/xlsettings` → **Experimental** → paste into **Custom Plugin Repositories**:

```
https://raw.githubusercontent.com/XeldarAlz/FFXIV-DomanMahjongSolver/main/repo/repo.json
```

Tick the checkbox, save. Then `/xlplugins` → search **Doman Mahjong Solver** → Install. Open with `/mjauto` and accept the short notice.

## Using it

`/mjauto` opens the main window. At a live table it fills in scores for all four seats, your current hand, the top 3 discard candidates with short reasoning, and the last action the plugin took. Under **Settings**: delay slider (how long it "thinks" before each click) and a developer-tools toggle.

**If the plugin misclicks a call prompt** (rare, but complex multi-chi menus can confuse it):

- Click the right option yourself in-game — the plugin resumes on the next turn.
- Or from chat: `/mjauto pass <N>` where `<N>` is the button index (0 = leftmost, rightmost is always Pass).

## Problems?
- [Open an issue](https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/issues) with the error text.

## License

**AGPL-3.0-or-later.** Source is open; derivatives must be too.

---

<details>
<summary><b>For developers</b></summary>

### Build & test

```bash
dotnet build DomanMahjongAI.sln
dotnet test  DomanMahjongAI.sln
```

**196 tests** across:

- **Engine** (140 tests) — tiles, shanten, ukeire, yaku, fu, scoring, call-candidate derivation.
- **Policy** (56 tests) — efficiency policy, riichi / call / push-fold evaluators, Bayesian opponent model, ISMCTS with progressive widening, hand simulator, evolutionary weight tuner, Tenhou log parser.

Engine and Policy are Dalamud-free and portable.

### Layout

```
FFXIV-DomanMahjongSolver/
├── DomanMahjongAI/       Plugin entry · UI · dispatch · reader · meld tracker
├── Engine/               Tiles · shanten · ukeire · yaku · fu · scoring
├── Policy/               Efficiency · ISMCTS · opponent model · tuners · Tenhou parser
├── tests/                Engine.Tests · Policy.Tests
├── repo/repo.json        Custom Dalamud repo manifest
└── .github/workflows/    CI · auto-tag · release
```

### Releasing

Bump `<Version>` in `DomanMahjongAI/DomanMahjongAI.csproj` **and** `AssemblyVersion` + `TestingAssemblyVersion` in `repo/repo.json` (all must match). Merge to main → `auto-tag` workflow creates the `vX.Y.Z` tag → `release` workflow builds and uploads `latest.zip`. On first run per version, the release tag sometimes needs a one-time manual re-push (GitHub won't let workflow-pushed tags trigger other workflows).

### Roadmap

The end goal is full intelligent automation across **all clients** (EU, NA, JP, OC) — addon detection, tile reading, hint overlay, auto-discard, and full call acceptance (Pon / Chi / Kan / Riichi / Tsumo / Ron) at parity on every variant.

**Shipped**

- Multi-variant addon resolution (`Emj` + `EmjL`) with auto-detect on load
- Per-variant tile encoding (texture base + byte[1] flip handled for both)
- Hand / score / discard-count readout
- Hints mode with reasoning + top-3 alternatives
- Auto-discard with natural pacing
- Call-prompt acceptance via `FireCallback` opcode 11, option 0 (verified on `Emj`)
- Engine: shanten · ukeire · yaku · fu · scoring (140 tests)
- Policy: efficiency · ISMCTS w/ progressive widening · Bayesian opponent model · evolutionary weight tuner · Tenhou log parser (56 tests)

**In progress**

- NA / `EmjL` AtkValue index parity for hand readout — suggestions reference wrong tile slots ([#30](https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/issues/30))
- NA / `EmjL` call-prompt button layout for Pon / Chi / Kan ([#30](https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/issues/30))

**Planned — remaining scope**

- JP / OC client verification (variant dumps + capture logs needed)
- Self-initiated riichi / tsumo / ron / ankan opcodes — currently speculative; accepts at call prompts work, discard-time declarations may still fail
- Opponent discard pools, dora indicator, dealer, round wind, honba — opponent model currently runs on a partial view
- Multi-chi variant selection — currently always picks the leftmost button
- Open-meld persistence across mid-round plugin reload — scorer currently falls back to tsumogiri until the hand stabilizes

</details>
