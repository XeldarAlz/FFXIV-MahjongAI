<p align="center">
  <img src="DomanMahjongAI/images/icon.png" width="96" alt="Doman Mahjong AI icon">
</p>

<h1 align="center">Doman Mahjong AI</h1>

<p align="center">
  Dalamud plugin that plays FFXIV Doman Mahjong — or whispers the best move in your ear.
</p>

<p align="center">
  <a href="#install"><img src="https://img.shields.io/badge/install-custom%20repo-1b7fb3.svg" alt="Install"></a>
  <img src="https://img.shields.io/badge/tests-185%20passing-2ea043.svg" alt="Tests passing">
  <img src="https://img.shields.io/badge/warnings-0-2ea043.svg" alt="0 warnings">
  <img src="https://img.shields.io/badge/license-AGPL--3.0-blue.svg" alt="AGPL-3.0">
</p>

---

## What it does

Open a Doman Mahjong table at the Gold Saucer. The plugin reads your hand and the table state, runs a mahjong engine with full shanten / ukeire / yaku / fu / score, scores every discard against an opponent danger model, and either:

- **Suggestion mode (default)** — shows the top picks with reasoning. You click.
- **Auto-play mode (opt-in)** — humanized-timing clicks on your behalf.

Everything runs client-side in pure C#. No Python, no GPU, no external service.

## Install

The plugin uses automation, so it cannot ship via the official Dalamud repo. Install via custom-repo:

1. In-game: `/xlsettings` → **Experimental**.
2. Paste into **Custom Plugin Repositories**:
   ```
   https://raw.githubusercontent.com/XeldarAlz/FFXIV-MahjongAI/main/repo/repo.json
   ```
3. Enable the checkbox, **Save and Close**.
4. `/xlplugins` → search **Doman Mahjong AI** → **Install**.
5. `/mjauto` to open. Accept the terms disclosure once.

## Usage

| Command | What it does |
|---|---|
| `/mjauto` | Open the main window |
| `/mjauto on` / `off` | Arm / disarm automation |
| `/mjauto policy eff` / `mcts` | Switch policy tier |
| `/mjauto debug` | Open the developer overlay |

In the main window:

- **Off / Suggestions / Auto-play** pill — three-way mode switch.
- **Live panel** — seat scores, your hand, policy pick, top-3 candidates with shanten + ukeire, and the last auto-action taken.
- **Settings** (collapsed) — policy tier, humanized delay slider, developer tools toggle.

## Safety

The plugin is **off by default** and requires explicit acknowledgement of a terms-of-service disclosure before automation can be enabled. Beyond that:

- **Suggestion-only** mode shows picks without ever dispatching inputs.
- **Humanized timing** — log-normal delay, ~1.2s median, 400ms floor, 2.5s cap, adjustable in Settings.
- **Kill switch** — flipping the mode pill to Off halts the loop mid-hand.
- **Dispatch guards** — every FireCallback is wrapped; invalid states return `HookFailed` silently, never crash.

Third-party automation is against the FFXIV Terms of Service. Use at your own risk.

## What's under the hood

```
┌── Dalamud plugin (net10-windows) ──────────────────────────────┐
│  AddonEmjReader ──► StateSnapshot ──► Policy ──► InputDispatch │
│     ▲                                              │           │
│     │ AutoPlayLoop watches state, humanized delay  ▼           │
│     └──────────── FireCallback to game             MainWindow  │
└────────────────────────────────────────────────────────────────┘
       │                                       │
       ▼                                       ▼
   Engine (net8.0)                        Policy (net8.0)
   Tiles · shanten · ukeire               EfficiencyPolicy · DiscardScorer
   yaku · fu · score · wall               RiichiEvaluator · CallEvaluator
   decomposer · StateSnapshot             PushFoldEvaluator · PlacementAdjuster
                                          OpponentModel · IsmctsPolicy · Determinizer
                                          Simulator · WeightTuner · TenhouLog
```

Both class libraries are **Dalamud-free and fully unit-tested** (185 tests, 0 warnings). They compile stand-alone and can be dropped into any C# project.

## Features

**Implemented**

- Engine: shanten (standard + chitoi + kokushi), ukeire, all common yaku, fu, score, payments, dora
- Efficiency policy: weighted discard scorer with shanten / ukeire / dora / yakuhai / deal-in-cost
- Riichi + call + push/fold evaluators
- Placement-aware weight adjustment (oorasu pressure)
- Heuristic opponent model: tenpai probability, hand marginal, danger map (genbutsu + suji + kabe)
- ISMCTS policy with determinization, UCB1, progressive widening, opponent-sim rollouts
- Hand simulator with tsumo + ron + riichi detection for self-play
- Weight tuners: coordinate descent + (μ/μ, λ)-ES with Gaussian proposals
- Tenhou log parser (136→34 tile mapping, event tag decoding) and replay harness

**Still owed (needs live-game capture)**

- Dispatch patterns for riichi / kan / tsumo / ron (opcodes currently speculative, stubs return `HookFailed` until confirmed)
- AddonEmj RE for opponent discard pools, dora indicator, dealer, round, honba, riichi sticks — until mapped, the opponent model operates on partial data

## Developer notes

### Build & test

```bash
dotnet build DomanMahjongAI.sln
dotnet test  DomanMahjongAI.sln
```

### Layout

```
FFXIV-MahjongAI/
├── DomanMahjongAI/       Plugin entry · UI · dispatch · reader
│   └── images/icon.png   Plugin icon (64×64)
├── Engine/               Core mahjong primitives
├── Policy/               Decision logic · ISMCTS · tuners · Tenhou parser
├── tests/
│   ├── Engine.Tests/     129 tests
│   └── Policy.Tests/      56 tests
├── repo/repo.json        Custom Dalamud repo manifest
├── tools/gen_icon.ps1    Regenerates the plugin icon from source
└── .github/workflows/    CI + release
```

### Cutting a release

1. Bump `Version` in `DomanMahjongAI/DomanMahjongAI.csproj` and `repo/repo.json`.
2. `git push --tags` with a `vX.Y.Z` tag. The release workflow builds and uploads `latest.zip`; `repo.json` pins `releases/latest/download/latest.zip`, so no further manual edits.

### Regenerating the icon

```bash
powershell -ExecutionPolicy Bypass -File tools/gen_icon.ps1
```

## License

AGPL-3.0-or-later.

## Acknowledgements

- Structural exemplar: [ffxiv-rota](https://github.com/XeldarAlz/ffxiv-rota).
- Heuristic design drawn from public Tenhou / Riichi literature and the Suphx / Mortal papers, without ML-track dependencies.
