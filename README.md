# SemanticStart

Semantic search for the Windows Start menu. Describe what you want to do — *"free up disk space"*,
*"sandbox for testing untrusted apps"*, *"host a website locally"* — and get the app, setting, or
built-in Windows feature that actually does it.

Windows Start search is lexical: it matches substrings of names. If you don't already know what a
tool is called, you can't find it. SemanticStart builds a local semantic index of your installed
applications **and** built-in Windows features, then serves it from a Start-like overlay. All
inference runs locally; no query ever leaves the machine.

## Status

Working end to end. On this Windows 11 machine it indexes **553 entities** and answers
queries with a **median latency of 2.3 ms (p95 2.9 ms)**, scoring **47/52** on the built-in
relevance corpus. A full rebuild with online enrichment and local LLM synthesis takes ~9 minutes;
querying never touches the network.

## How it works

```
Collectors → Enrichment → LLM synthesis → Embeddings → SQLite + FTS5 + vectors
                                                              │
                              Win+Alt+Space ──► Hybrid retrieval (vector ∥ BM25 → RRF) ──► Overlay
```

**Indexing (offline).** Eight collectors enumerate AppsFolder/MSIX apps, Start shortcuts, uninstall
registry entries, `ms-settings:` pages, Control Panel applets and MMC snap-ins, Windows optional
features, System32 tools, and MSIX command aliases registered under `App Paths` — the last of these
reaches console tools that ship inside installed suites and that Windows deliberately hides from
Start. Each entity is enriched from local documentation (PE version resources, MSIX manifests,
`.lnk` comments, `--help` output) and, optionally, online
sources. A local generative model then writes a one-line description, a list of tasks the user
might want, and synonyms — this is what closes the gap between how people phrase intent and how
vendors name products. The result is embedded with `all-MiniLM-L6-v2` via ONNX Runtime.

**Querying (hot path).** Two arms run per query. The vector arm supplies semantic recall; the
lexical FTS5/BM25 arm supplies precision on literal names. Neither is sufficient alone — pure vector
search fails on short prefixes like `wor`, and pure lexical search cannot answer *"free up disk
space"*. Results are fused with Reciprocal Rank Fusion, then adjusted by literal-name boosts and by
what you actually launch. Results whose evidence is weak are dropped rather than padding the list to
the requested count, so a query nothing answers well shows *"No good matches found"* instead of a
page of near-misses.

## Requirements

- Windows 10 1809 or later, x64
- No administrator rights, no service, no driver, and **no modification of `explorer.exe`**
- CPU-only: no NPU or GPU required

## Building

```powershell
dotnet build SemanticStart.slnx
dotnet test tests\SemanticStart.Tests
```

Portable, self-contained build (no .NET runtime needed on the target machine):

```powershell
dotnet publish src\SemanticStart.App -c Release -r win-x64 --self-contained true -o artifacts\portable
```

## Usage

Run `SemanticStart.App.exe`. It lives in the tray, builds its index on first run (downloading the
~90 MB embedding model once), and opens on **Win+Alt+Space**.

| Key | Action |
|---|---|
| `Win+Alt+Space` | Open the overlay (configurable) |
| `Enter` | Launch |
| `Ctrl+Enter` | Launch as administrator |
| `Ctrl+Shift+Enter` | Open file location |
| `Esc` | Dismiss |

The overlay follows the system light/dark theme and accent colour live, and shows each entry's real
Shell icon (including MSIX/UWP assets) via `IShellItemImageFactory` — the same source Start uses.

**Hotkey fallback.** If `Win+Alt+Space` is already claimed by another app (`RegisterHotKey` fails with
`ERROR_HOTKEY_ALREADY_REGISTERED`), SemanticStart walks a candidate list — `Win+Alt+S`,
`Win+Ctrl+G`, `Win+Alt+X`, `Ctrl+Alt+Space`, `Ctrl+Alt+S`, `Ctrl+Shift+Space` — and registers
the first one that is free. The hotkey that actually won is reported by a tray balloon at startup
and shown under **Hotkey** in Settings (the gear in the lower left of the overlay).

**Changing it.** The hotkey field in Settings records rather than reads: click it and press the
combination you want, and each key appears as its own chip. Escape keeps the current chord. There is
nothing to spell, which matters because key names are not guessable (`PrintScreen`, `Prior`,
`OemQuestion`), and the field cannot display a chord it would refuse to store.

While the field is listening, the global hotkey and the Start-key hook are released and restored
afterwards. Windows delivers a registered hotkey to the owning application as an activation rather
than as key input, so without this the chord most likely to be pressed while editing — the one
already assigned — would open the overlay instead of being recorded.

A chord is rejected, with the reason shown inline, if it has no modifier (Windows would register it
globally and swallow that key everywhere, including inside text boxes), if `Shift` is its only
modifier (that is ordinary typing), or if the shell claims it before any application sees it
(`Win+L`, `Win+G`, `Win+Tab`, `Ctrl+Alt+Delete`) — accepting one of those would appear to work and
then never fire.

**Why this combination.** Bare `Win+<letter>` is impossible: the shell registers every one of them,
so `RegisterHotKey` fails for `Win+S`, `Win+Q` and `Win+F` alike. That leaves `Win+<modifier>+<key>`,
where much of the `Win+Alt+<letter>` family (`D`, `F`, `G`, `R`, `T`, `W`) belongs to Xbox Game Bar on
a stock Windows 11 install, `Win+Shift+S` is Screen Snip, and `Win+Ctrl+S` is Speech Recognition.
`Win+Alt+Space` avoids all of those, matches the established launcher idiom (Spotlight, Alfred,
Raycast, PowerToys Run), and sits in one corner of the keyboard so a single hand can hit it — the
thumb covers Alt and Space while the pinky holds Win, with no finger doing double duty.

**Single instance.** A named mutex ensures only one copy runs. Launching the executable again does
not start a second instance; it signals the running one to show its overlay and exits.

### Using a small local model for descriptions

Description quality is what makes intent search work, and the built-in heuristics can only repeat
what an app already says about itself. Settings has a **Local LLM synthesis** section that hands
that job to a small model running on your machine.

| Mode | Behaviour |
|---|---|
| `Off` | Descriptions come from the built-in heuristics only |
| `Auto` | Probes Foundry Local, Ollama, and LM Studio, and uses the first one it finds |
| `Custom` | Talks to any OpenAI-compatible endpoint you point it at |

Choosing a model is a dropdown, not a config file. SemanticStart discovers what is already installed
(`foundry model ls`, Ollama's `/api/tags`, LM Studio's `/v1/models`) and merges it with a curated
list of small CPU-friendly models — Qwen2.5 0.5B/1.5B, Llama 3.2 1B/3B, Gemma 2 2B, Phi-3.5 Mini —
labelled with size and what each is good for. **Download** fetches a curated model through the
detected runtime, **Test** sends a real chat completion and reports what came back, and if no
runtime is present at all, **Install Foundry Local** runs the winget install for you.

Controls light up only when their dependencies are ready: with no runtime detected, the model picker
and **Download** stay disabled while **Install Foundry Local** does not, so the dialog never offers a
choice that cannot work yet.

Synthesis runs **once, at index time**, never on the query path. Expect roughly 8–15 minutes for a
typical ~470-entity index with a 1–2B CPU model. Every entity has a timeout and falls back to the
heuristic profile, so a slow or missing model degrades quality without ever breaking the build.

Because synthesis happens at index time, **this setting describes the next build, not the index you
are currently searching.** An index whose descriptions were written by a model keeps them after the
setting is switched to `Off`; only a rebuild replaces them. Settings shows what actually wrote the
descriptions on the **Last index generator** line, and `SemanticStart.Cli stats` reports the same
breakdown. Rebuilding with synthesis off when the current index was model-written asks for
confirmation first, because the change is otherwise silent and only another full model run undoes it.

The difference is visible rather than theoretical. On a ~550-entity index, `create and edit files`
returns *Files — "Files is a Microsoft 365 companion app that helps manage and access your files"*
first with model-written descriptions, versus *Notepad, Paint, Recovery Drive* first with the
heuristics, where the same entry reads only *"Open Files."*

Retrieval, however, is unchanged. Across the same 55 corpus queries the two score 50/55 either way,
with MRR 0.821 vs 0.823 and top-1 74% vs 76% — a difference of one case. **A local model does not
measurably help SemanticStart find things; it changes how the results read.** Budget the extra
indexing time and the model download against that, and nothing else.

### The CLI

`SemanticStart.Cli` is a diagnostic front end over the same engine:

```powershell
dotnet run --project src\SemanticStart.Cli -- index [--force]   # build or rebuild the index
dotnet run --project src\SemanticStart.Cli -- search "<query>"  # query it
dotnet run --project src\SemanticStart.Cli -- eval              # run the relevance corpus
dotnet run --project src\SemanticStart.Cli -- stats             # index statistics
```

## Taking over the Windows key

**Windows exposes no supported API for adding local results to Start search.** The only official
extension point is the web search provider model, which is web-results-only and EEA-only. Products
that genuinely replace the Start menu do it by injecting into or patching `explorer.exe`, which
breaks on feature updates and trips security software. SemanticStart does not do this.

Instead there are two activation tiers:

1. **Default:** a global hotkey (`Win+Alt+Space`). Fully supported, always reliable.
2. **Opt-in:** a low-level keyboard hook that detects a *solo* Win press-and-release and opens
   SemanticStart instead of Start.

Tier 2 is best-effort by nature, and its limits are surfaced in Settings rather than hidden:

- It cannot see input while an **elevated** window has focus, so Win falls through to the real Start
  menu there.
- Reserved combinations (`Win+L`, `Win+G`, Ctrl+Alt+Del) are handled below the hook and are never
  intercepted.
- The hook can be dropped by Windows under load; a watchdog re-installs it.

It is **off by default and fails open** — if anything goes wrong, the real Start menu still works.
That is a non-negotiable safety property, and every hook callback is wrapped to guarantee it.

## Privacy

Queries never leave the machine. The only network traffic is the one-time embedding model download
and opt-in enrichment during indexing, which is cached to disk and can be disabled entirely; the
index is fully functional without it. Local LLM synthesis talks only to a server on `localhost`.

## Layout

| Project | Purpose |
|---|---|
| `src/SemanticStart.Core` | Collectors, enrichment, synthesis, embeddings, storage, retrieval |
| `src/SemanticStart.Cli` | Diagnostic CLI (`index`, `search`, `eval`, `stats`) |
| `src/SemanticStart.App` | WPF overlay, activation, tray icon, settings |
| `tests/SemanticStart.Tests` | Unit and regression tests |

The query engine is a standalone library with no UI dependency, so it can also back a Command
Palette or PowerToys Run extension later.

## Not in scope for v1

User files and documents (Windows Search already covers these), generative LLM inference at query
time (hundreds of milliseconds on a path that must feel instant), and any form of Explorer patching.




