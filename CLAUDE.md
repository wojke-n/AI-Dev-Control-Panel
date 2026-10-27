# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

AI-Dev-Control-Panel is a Windows desktop app (C#, .NET 10, WPF) that standardizes AI-driven development with Claude Code: projects are onboarded (`/init`), free-text TSAs (Textual Software Artifacts) are refined into GitHub issues, and each issue runs through planning (plan mode) → execution (auto mode) → user review (accept / adjust / restart). The GUI shows projects, TSAs and issues color-coded by state; the title bar shows the subscription usage.

## Commands

```powershell
dotnet build                                   # whole solution (warnings are errors)
dotnet test                                    # all tests
dotnet test --filter "FullyQualifiedName~IssueWorkflowTests.AcceptMergesPushesClosesAndUnblocksDependents"   # single test
dotnet run --project src/AiDevControlPanel.App # run the app

# Run against a separate data folder instead of %LOCALAPPDATA%\AI-Dev-Control-Panel
$env:ADCP_DATA_DIR = "C:\temp\adcp-dev"; dotnet run --project src/AiDevControlPanel.App

# EF Core migration after changing the domain model
dotnet tool restore
dotnet ef migrations add <Name> --project src/AiDevControlPanel.Core --startup-project src/AiDevControlPanel.Core --output-dir Persistence/Migrations

# Regenerate the app icon (draws it in tools/IconGen/Program.cs)
dotnet run --project tools/IconGen -- src/AiDevControlPanel.App/Assets/app.ico
```

Runtime prerequisites: `git`, `gh` (logged in via `gh auth login`) and `claude` (logged in, subscription) on PATH. `ExecutableLocator` re-reads the machine/user PATH, so tools installed after the app started are found.

## Architecture

- `src/AiDevControlPanel.Core` (net10.0, no WPF): all logic, fully unit-testable.
  - `Domain/` – entities (`Project`, `Tsa`, `Issue`, `IssueDependency`), `PriorityRules` (internal priority 0–8, re-derived on every issue save) and `StateAggregation` (state → color of issues, TSAs and projects).
  - `Claude/` – `ClaudeCliRunner` runs one `claude -p --output-format stream-json` turn; `StreamJsonParser` turns lines into events; `UsageLimitDetector`; prompt templates (`Prompts/*.md`) and JSON schemas (`Schemas/*.json`) are embedded resources loaded by `PromptTemplates`.
  - `Git/GitService`, `GitHub/GitHubService` – thin wrappers over the git and gh CLIs (gh request bodies go through stdin with `gh api --input -`).
  - `Usage/` – `OAuthUsageProvider` (undocumented `api/oauth/usage` endpoint with the token from `~/.claude/.credentials.json`), `UsageService` (refresh at most every 60 s, coalesced), `TitleFormatter`.
  - `Persistence/` – EF Core SQLite (`AppRepository`: detached entities, serialized access, `Changed` event) and `SettingsStore` (JSON).
  - `Workflow/` – `ProjectOnboardingService`, `RefinerService`, `IssueWorkflowService`, `BlockingService`, `JobScheduler` + `JobSelector`.
- `src/AiDevControlPanel.App` (WPF, MVVM with CommunityToolkit.Mvvm, DI via Generic Host): `MainViewModel` reloads an `AppSnapshot` on every repository change and reconciles the item view models by id; the main panel shows one `DetailViewModel` (DataTemplates in `MainWindow.xaml`). State colors live in `Themes/StateColors.xaml`.
- `tests/AiDevControlPanel.Core.Tests` – xUnit; workflow tests use a real SQLite file plus fakes for git, gh and claude (`Fakes.cs`, `TestEnvironment.cs`).

### How the workflow runs

- The `JobScheduler` starts at most one job (= one Claude process) per project; projects run in parallel. `JobSelector` order: onboarding → started issues (interrupted 7/8 before 5/6) → TSAs → new issues (only with "Autostart planning"). While any item waits for a usage-limit reset, no job starts anywhere.
- Each phase is one `claude -p` run that must end with `structured_output` (`--json-schema`). Questions are part of that output (`status: "questions"`); `AskUserQuestion` and `ExitPlanMode` are disallowed and `--permission-prompts none` is set. Answers, change requests and continuations resume the same session (`--resume`).
- Phase instructions go into the **user prompt**, never the system prompt: Claude Code snapshots the system prompt of the first request, so later `--append-system-prompt` text would be ignored on resume.
- Every issue works in its own git worktree under `<data>\worktrees\<project>-<id>\issue-<n>` on branch `issue-<n>-<slug>`. Sessions are bound to the working directory, so a session is always resumed in the same worktree. Because worktrees branch from the default branch, onboarding commits `CLAUDE.md` locally.
- The app (not Claude) commits after execution, posts the plan / completion report / change requests as issue comments, and on accept merges into the default branch, pushes and closes the issue. Parent issues with sub-issues are containers: never executed, completed when all sub-issues are done.
- `rate_limit_event` in the stream reports utilization as a fraction (0.05), the OAuth endpoint as percent (5.0).
