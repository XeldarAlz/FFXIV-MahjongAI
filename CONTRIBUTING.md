# Contributing

Thanks for taking an interest. This is a small solo project, but PRs are welcome and I'll review them.

## Quick start

```bash
git clone https://github.com/XeldarAlz/FFXIV-MahjongAI.git
cd FFXIV-MahjongAI
dotnet restore DomanMahjongAI.sln
dotnet build   DomanMahjongAI.sln
dotnet test    DomanMahjongAI.sln
```

You need .NET 10 SDK. The plugin itself requires Dalamud at runtime; CI pulls a Dalamud dev build automatically and that's enough to compile. See `.github/workflows/ci.yml` if you want to reproduce CI locally.

## Project layout

- `DomanMahjongAI/` — Dalamud plugin: UI, dispatch, game reader. The only part that depends on Dalamud.
- `Engine/` — Core mahjong primitives (tiles, shanten, ukeire, yaku, fu, scoring). Dalamud-free and portable.
- `Policy/` — Decision logic: efficiency policy, riichi / call / push-fold evaluators, Bayesian opponent model, ISMCTS, hand simulator, evolutionary tuner, Tenhou log parser. Also Dalamud-free.
- `tests/` — xUnit tests for Engine and Policy. Anything in those two libraries should be covered.
- `repo/repo.json` — Custom Dalamud repo manifest.

Rule of thumb: if logic can live in `Engine` or `Policy`, put it there and test it. Keep `DomanMahjongAI/` focused on glue — reading addons, dispatching clicks, drawing windows.

## Before you open a PR

1. `dotnet build` cleanly.
2. `dotnet test` passes. If you changed engine or policy behavior, add or update tests.
3. Keep the diff focused. One concern per PR is easier to review than five.
4. Match the existing style — the code leans terse and direct. No heavy abstractions "for later."
5. If your change affects what a user sees or types (commands, window layout, install steps), update the README.

## Good first issues

Check the issue tracker for anything labeled `good first issue`. If nothing's there, the "Known outstanding work" section of the README lists gaps that are fair game — especially opcodes for riichi/tsumo/ron/kan dispatch and the remaining addon fields (dora indicator, dealer, round, honba).

## Releasing (maintainers)

Bump `Version` in `DomanMahjongAI/DomanMahjongAI.csproj` and `AssemblyVersion` in `repo/repo.json` to match, push a `vX.Y.Z` tag. The release workflow builds and uploads `latest.zip`.

## Reporting bugs

Use the bug report template. Include plugin version, mode, and repro steps. Logs from `/mjauto log on` help a lot for runtime issues.

## Security

Please don't file public issues for security problems — see [SECURITY.md](SECURITY.md).

## Code of conduct

See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Be decent.

## License

By contributing, you agree your contributions are licensed under AGPL-3.0-or-later, the same as the project.
