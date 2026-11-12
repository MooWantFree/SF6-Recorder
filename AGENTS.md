# AGENTS.md

## Build & verify
- Full build (checks + publish to `artifacts\app`): `pwsh -File .\build.ps1`.
- The user authorizes the assistant to close and restart this project's application whenever delivering an updated build. Do not ask the user to close it or request confirmation again.
- Verify each process's executable path belongs to this project's `artifacts` or `src\Sf6Prototype\bin` directory before closing it. Prefer `CloseMainWindow()` and wait for exit so downloads can cancel and pending writes can finish. If it remains unresponsive after a reasonable timeout, terminate only the verified project process. Never terminate unrelated applications or shared browser processes.
- Preserve `artifacts\app\Data` (including SQLite, settings, cookies, and WebView2 state). After a successful build/publish, automatically launch `artifacts\app\Sf6Prototype.exe` on the user's desktop without `--sync-on-start` or `--capture`. Confirm startup without triggering refresh, login, or opponent queries. Restarting the delivered app is authorized; it is not a request for GUI testing.
- Read-only reviews and checks that do not replace the delivered application do not require restarting it. If a build fails, retain the existing application and data; do not launch a partially published build.
- Checks only: `.tools\dotnet\dotnet.exe run --project tests\Sf6Prototype.Checks --configuration Release` (set `DOTNET_CLI_HOME=.local\dotnet-cli`, `NUGET_PACKAGES=.nuget\packages` like build.ps1).
- Compile only: `.tools\dotnet\dotnet.exe build src\Sf6Prototype\Sf6Prototype.csproj -c Release`.

## GUI verification
- Never use the real `artifacts\app\Data` for experiments. Copy it (without `WebView2`, `instance.lock`) to `.local\capture-data` and run
  `src\Sf6Prototype\bin\Release\net10.0-windows\Sf6Prototype.exe --data-dir <copy> --capture [--size 1120x720]`; screenshots `ui-0.png`, `ui-1.png`, `ui-settings.png` land in that directory.
- Check both 1380×880 (default) and the 1120×720 minimum.
- Do not trigger refresh, login or opponent-rank queries during verification: they send requests to the official site with the user's session.

## Conventions
- UI copy is Chinese and minimal; colors/styles come from `Themes/Tokens.xaml` and `Themes/Utility.xaml` (no hard-coded colors in views).
- Replay LP/MR are pre-match values; a ranked match's settlement is the next same-character ranked record minus this one (`BattleStore.Scores`).
