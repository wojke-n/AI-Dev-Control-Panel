# AI-Dev-Control-Panel
This tool will give an overview over your AI's progress and control it.

It is a Windows desktop app that makes AI-driven development with [Claude Code](https://claude.com/claude-code) easier. A standardized process automates most of the work, and the GUI lets you manage several projects and issues in parallel.

## Requirements

- Windows 10/11 with the [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `git`
- [GitHub CLI](https://cli.github.com/), logged in with `gh auth login`
- [Claude Code](https://claude.com/claude-code), logged in with a Claude subscription

## Build and run

```powershell
dotnet build
dotnet test
dotnet run --project src/AiDevControlPanel.App
```

## Process

1. **Add a project** (+ in the Project panel). The folder must be a git repository connected to GitHub that contains a README. If there is no `CLAUDE.md`, `/init` creates one and it is committed locally.
2. **Add a TSA** (Textual Software Artifact, + in the TSA panel): describe features, bugs or security issues in your own words.
3. The **refiner** turns the TSA into GitHub issues: feature requests, bug reports and security vulnerability reports, with sub-issues and dependencies where needed. It does not request features that already exist, gives the TSA a short title and assigns each issue an internal priority. If something is ambiguous, it asks.
4. **Planning:** Claude plans each issue in plan mode. Its questions appear at the top of the main panel, and the finished plan is posted as a comment on the issue.
5. **Execution:** Claude implements the plan in auto mode, in a separate git worktree per issue. The completion report is posted as a comment, and the app commits the changes locally without pushing them.
6. **Review:** You decide what happens next:
   - **Accept** merges into the default branch, pushes, closes the issue and unblocks dependent issues.
   - **Adjust** posts your change request as a comment and sends it back to the planner.
   - **Restart** discards all progress, so you can edit the issue or the plan and start again.

The **Settings** (gear icon) control whether planning and execution start automatically. **Model** and **Effort** apply to new Claude runs.

## Internal priority

| Priority | Meaning |
|---|---|
| 0 | On hold |
| 1 / 2 | Blocked by other issues (regular / prioritized) |
| 3 / 4 | Not started (regular / prioritized) |
| 5 / 6 | Started (regular / prioritized) |
| 7 / 8 | Interrupted by the usage limit (regular / prioritized) |

## Colors

| | White | Gray | Light yellow | Light blue | Yellow | Dark blue | Orange | Purple | Red | Brown | Green |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **Project** | waiting for the AI | | | needs you | working | | | | | | all done |
| **TSA** | waiting for the refiner | | | question | refining | | issue in progress | | | issues created | all issues done |
| **Issue** | waiting for the AI | blocked | planning | question / plan ready / failed | implementing | review | merging | change requested | rejected | | done |

The title bar shows the current session and weekly usage, for example `Usage: 7% (14:56) | 23% (Mo, 07:38)`. It is refreshed at startup and after every planning and execution phase, at most once per minute.
