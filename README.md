# Quizzler

A Windows tray app that quizzes you on software engineering (C# first) using spaced repetition.
It sits next to the clock, shows how many questions are due, and reminds you when it's time to study.

## How it works

- **Spaced repetition (SM-2).** Questions you get right come back after longer and longer gaps (1 day,
  6 days, then growing). Questions you miss come back in 10 minutes.
- **Multiple choice first, then typed.** A new question is multiple choice. Once you get it right, next time
  you have to type the answer. Get it wrong and it drops back to multiple choice.
- **Four question types:**
  - *Concept*: short factual answers (`sealed`, `O(log n)`, `Liskov Substitution Principle`).
  - *Predict the output*: read a C# snippet and say what it prints.
  - *Pick the code*: choose the snippet that correctly does something.
  - *Predict the result*: small tables are shown as grids next to a SQL query; say what it returns. After
    answering you see the real result, with the rows that affected it highlighted and the rest dimmed.
- **Every answer key is checked by running it.** C# snippets are compiled and run with Roslyn, and SQL queries
  run in an in-memory SQLite database, during tests, so a wrong answer key fails the build.
- **New-per-day cap** (default 10) so a big batch of new questions doesn't swamp you.

## Requirements

- Windows 10 (1809) or later.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build.

## Install

```powershell
powershell -ExecutionPolicy Bypass -File tools/install.ps1
```

This publishes the app to `%LOCALAPPDATA%\Programs\Quizzler`, adds a Start Menu shortcut, turns on
"Start with Windows" and launches it. No admin rights needed. Look for the **Q** icon in the tray (it may
be under the `^` overflow arrow). Re-run the script any time to update; your progress is kept.

To give it to someone who doesn't have .NET installed, add `-SelfContained`.

To remove it: `tools/uninstall.ps1` (add `-RemoveData` to delete your progress too).

## Using the app

- **Tray icon**: the red badge shows how many questions are due. Left-click to start a quiz; right-click
  for Snooze (1 hour / until tomorrow), Settings and Exit.
- **Reminders**: a notification with **Start quiz** and **Snooze 1 hour** buttons, at most once per
  reminder interval and never during quiet hours (default 22:00–08:00).
- **Settings**: theme (Match Windows / Light / Dark), new questions per day, when a new day starts,
  reminder interval, quiet hours, start with Windows.

### Keyboard shortcuts in the quiz window

| Key | Action |
|---|---|
| `1`–`9` | Pick a multiple-choice option |
| `Enter` | Submit / next question (`Ctrl+Enter` in a multi-line answer) |
| `G` | I guessed (counts as right, but comes back sooner) |
| `E` | Too easy (pushes it further out) |
| `O` | Override: my typed answer was actually right |
| `Y` / `N` | Self-graded questions: I got it / I didn't |
| `F` | Flag the question as wrong or unclear |
| `Esc` | Close |

## Command line

The installer also puts a `quizzler` CLI in `%LOCALAPPDATA%\Programs\Quizzler\cli`. From the repo:

```powershell
dotnet run --project src/Quizzler.Cli -- quiz       # study in the terminal
dotnet run --project src/Quizzler.Cli -- stats      # due counts, today's accuracy, per-topic progress
dotnet run --project src/Quizzler.Cli -- validate   # check the question bank and run every snippet
dotnet run --project src/Quizzler.Cli -- import     # import content/ into your database
dotnet run --project src/Quizzler.Cli -- flags      # questions you flagged
dotnet run --project src/Quizzler.Cli -- settings   # view or change settings
```

Every database command takes `--db <path>`.

## Your data

Everything is in one SQLite file: `%LOCALAPPDATA%\Quizzler\quizzler.db` (or `$QUIZZLER_DB`). To move your
progress to another PC, close the app and copy that file. The log is `%LOCALAPPDATA%\Quizzler\quizzler.log`.

## Adding questions

Questions live in `content/<area>/<topic>.json` and follow `schema/questions.schema.json` (VS Code gives
autocomplete and validation). Most are generated with Claude: the authoring rules are in
[CLAUDE.md](CLAUDE.md), so asking Claude Code to "add 10 questions on C# generics" follows them.

```json
{
  "id": "cs-linq-0007",
  "type": "predict-output",
  "topic": "csharp/linq",
  "difficulty": 3,
  "tags": ["linq", "deferred-execution"],
  "prompt": "What does this print?",
  "code": "var list = new List<int> { 1, 2 };\nvar q = list.Select(x => x * 10);\nlist.Add(3);\nConsole.WriteLine(q.Count());",
  "answer": "3",
  "distractors": ["2", "0", "Throws InvalidOperationException"],
  "explanation": "Select is deferred, so the query runs when Count() is called, after 3 was added."
}
```

After adding questions:

```powershell
dotnet test --filter ContentBankTests              # validates and runs every snippet
powershell -ExecutionPolicy Bypass -File tools/install.ps1   # the installed app imports new content on launch
```

Never change an existing question's `id`: your progress is stored against it.

## Project layout

```
src/Quizzler.Core        Models, grading, SM-2 scheduler, session planning, notification policy
src/Quizzler.CodeRunner  Roslyn snippet runner + answer-key verifier
src/Quizzler.Data        SQLite store and migrations
src/Quizzler.Cli         The quizzler command
src/Quizzler.App         WPF tray app (net10.0-windows)
tests/Quizzler.Tests     Unit, integration and question-bank tests
content/                 The question bank
schema/                  JSON Schema for question files
tools/                   install / uninstall / icon scripts
```

```powershell
dotnet build
dotnet test
```

## Roadmap

- More questions across C#, .NET, design, data structures & algorithms, SQL and Git.
- A stats view in the app.
- LeetCode-style exercises: write code, have it run against expected output.
