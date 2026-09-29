# AGENTS.md

## Project Overview

**Project:** DevOverlay

DevOverlay is a Windows desktop monitoring application that displays compact, configurable system and development-related metrics in an overlay or taskbar-style UI.

Planned monitoring targets may include:

- CPU usage, temperature, power
- GPU usage, temperature, power, VRAM
- SSD/HDD activity and storage metrics
- Network upload/download activity
- Codex usage
- Claude usage
- FPS
- 1% Low FPS
- Frame time
- Latency

Example compact layout:

```text
CPU 38% 70°C │ GPU 92% 70°C 50W │ FPS 144 118 │ 6.9ms │ LAT 14ms
```

Metrics belonging to the same category should be visually grouped.

The overlay may eventually support:

- Configurable position
- Drag/move support
- Selectable monitoring items
- Configurable popup behavior
  - Click
  - Hover
  - Disabled

Do not assume planned features are already implemented.

Always inspect the current repository before making changes.

---

## Core Workflow

Before modifying code:

1. Read this `AGENTS.md`.
2. Read the latest entry in `docs/DEVLOG.md`.
3. Inspect the repository structure and current implementation.
4. Inspect `git status` and relevant `git diff`.
5. Identify the smallest reasonable scope for the requested task.

Use this development flow:

```text
Understand → Locate → Modify → Validate → Document
```

Prefer incremental changes over large rewrites.

Do not rewrite working systems unless there is a clear technical reason.

---

## Source of Truth

When instructions or information conflict, use this priority:

1. Current user request
2. `AGENTS.md`
3. Current repository state and architecture
4. Existing code conventions
5. `docs/DEVLOG.md`
6. Previous assumptions

The actual repository state is always more authoritative than old DevLog entries.

DevLog provides historical context but does not override current code.

---

## Scope Control

Keep changes focused on the requested task.

Do not:

- Redesign unrelated systems
- Rename unrelated files
- Replace unrelated dependencies
- Perform broad refactoring without a concrete need
- Implement speculative future features

Small cleanup directly related to the current task is acceptable.

If larger technical debt or unrelated issues are discovered, record them in DevLog under `Known Issues` or `Next Steps` instead of expanding the task.

---

## Architecture Principles

Separate responsibilities where practical:

```text
Metric Collection
      ↓
Metric Model / State
      ↓
Formatting / Aggregation
      ↓
UI / Overlay Rendering
```

Hardware monitoring code should not directly control UI rendering.

UI code should consume normalized metric data instead of directly querying hardware APIs.

External integrations should be isolated behind dedicated providers, services, or interfaces where practical.

Examples:

- NVIDIA GPU monitoring
- Windows performance APIs
- Codex usage
- Claude usage
- Network monitoring
- FPS/frame-time collection

This should allow individual monitoring implementations to be replaced without redesigning the UI.

---

## Metric Design

Metrics should preferably have:

- Stable internal identifier
- Human-readable name
- Category
- Current value
- Unit
- Availability state
- Optional timestamp
- Optional formatting rules

Conceptual example:

```text
Metric
├─ Id
├─ Category
├─ Name
├─ Value
├─ Unit
├─ IsAvailable
└─ UpdatedAt
```

Do not represent unavailable data as a valid zero value.

Example:

```text
GPU Temperature = unavailable
```

is not the same as:

```text
GPU Temperature = 0°C
```

---

## Performance

DevOverlay is a monitoring application and should not become a meaningful source of system load.

Avoid:

- Busy polling
- Unnecessarily high polling rates
- Blocking the UI thread
- Excessive process enumeration
- Excessive allocations in hot paths
- Writing logs every frame
- Recreating UI components unnecessarily

Different metrics may use different refresh intervals.

Example:

```text
FPS / frame time     → high frequency
CPU / GPU usage      → medium frequency
Temperature          → medium or low frequency
Storage information  → low frequency
Usage/API quota      → low frequency
```

Do not assume every metric needs the same polling rate.

Prefer centralized or configurable refresh intervals where practical.

---

## Windows Target

DevOverlay primarily targets Windows.

Prefer:

- Windows-compatible APIs
- Windows-compatible libraries
- PowerShell commands
- Windows paths and development workflows

Example commands:

```powershell
git status
git diff
git log --oneline -10
```

Do not introduce Linux-only dependencies unless they are necessary and there is no practical Windows alternative.

---

## Error Handling

Monitoring providers should fail independently where practical.

Failure of one provider should not crash the entire application.

Expected failure examples:

- NVIDIA API unavailable
- Unsupported hardware sensor
- Codex or Claude usage source unavailable
- Network adapter removed
- Permission denied
- Monitored game process terminated

Expose unavailable/error states when appropriate.

Do not silently swallow unexpected exceptions.

Log enough context to diagnose unexpected failures without producing excessive logs.

---

## Dependencies

Before adding a dependency:

1. Check whether the project already has a suitable dependency.
2. Confirm Windows support.
3. Consider maintenance status.
4. Consider licensing.
5. Consider runtime/distribution impact.
6. Record important dependency changes in DevLog.

Do not introduce a large framework for a small utility feature.

---

## Code Quality

Prefer:

- Clear names
- Small focused functions/classes
- Explicit responsibilities
- Testable logic
- Minimal global state
- Consistent project conventions

Avoid:

- God classes
- Duplicate monitoring logic
- Unexplained magic numbers
- Tight coupling between UI and hardware APIs
- Large functions mixing collection, formatting, and rendering

Comments should explain **why** something is done when the reason is not obvious.

Do not add comments that merely restate the code.

---

## Configuration

User-selectable behavior should preferably be configuration-driven instead of hard-coded.

Potential configuration includes:

- Enabled metrics
- Metric order
- Metric grouping
- Overlay position
- Refresh intervals
- Popup mode
- Click/hover behavior
- Formatting options

When configuration formats change, consider backward compatibility.

---

## Validation

Every meaningful code change must be validated before it is considered complete.

At minimum, when applicable:

1. Build the project.
2. Run relevant automated tests.
3. Perform a manual test for the changed behavior.
4. Check for newly introduced warnings/errors.

Never claim validation that was not actually performed.

Use explicit validation results in DevLog.

Example:

```text
Build: PASS
Tests: PASS
Manual overlay test: NOT RUN
Reason: Required GPU sensor is unavailable in the current environment.
```

If testing is impossible, record the reason.

---

## Git Workflow

Keep commits focused on one logical change when practical.

Recommended branch names:

```text
feat/<feature-name>
fix/<bug-name>
refactor/<area>
docs/<topic>
chore/<task>
```

Recommended commit messages:

```text
feat: add GPU metric provider
fix: handle unavailable temperature sensors
refactor: separate metric formatting from collection
docs: update development log
chore: update dependencies
```

Before committing:

```powershell
git status
git diff
```

Never commit:

- API keys
- Access tokens
- Passwords
- Personal credentials
- `.env` secrets
- IDE caches
- Build output
- Temporary files
- Debug dumps
- Large generated files unless explicitly required

Do not rewrite Git history or force-push unless explicitly requested.

---

# Development Log Policy

The development log is stored at:

```text
docs/DEVLOG.md
```

`AGENTS.md` contains the rules.

`docs/DEVLOG.md` contains only actual development history.

Every meaningful development session must add or update a DevLog entry.

The purpose of the DevLog is to make handoff between developers or AI agents easy.

A new developer or agent should be able to understand the current state by reading:

```text
AGENTS.md
latest DEVLOG entry
git status
git diff
```

---

## When to Update DevLog

Update `docs/DEVLOG.md` when:

- A feature is implemented
- A bug is fixed
- Architecture changes
- A dependency is added or removed
- An important technical decision is made
- Investigation reveals useful technical information
- Work stops before completion
- A blocker is discovered
- Another developer or agent will need context

Minor typo-only changes do not require a detailed DevLog entry unless they are part of a larger task.

---

## DevLog Entry Rules

Add the newest entry at the top of `docs/DEVLOG.md`.

Do not delete or rewrite previous entries unless correcting clearly incorrect information.

Each entry should include:

- Date and time
- Status
- Objective
- Completed work
- Files changed
- Technical decisions, when relevant
- Validation
- Known issues
- Next steps
- Git information

Use exact filenames, class names, functions, commands, or error messages when they help future handoff.

Bad:

```text
Updated GPU stuff.
```

Good:

```text
Implemented NVIDIA GPU utilization polling in
src/Monitoring/NvidiaGpuProvider.cs.

NVML initialization now occurs once when the provider starts
instead of once per polling cycle.
```

Document important reasons, not just actions.

Bad:

```text
Changed refresh rate.
```

Good:

```text
Changed GPU polling interval from 100 ms to 500 ms because
temperature and utilization metrics do not require frame-level
sampling and the previous interval caused unnecessary polling load.
```

---

## DevLog Template

Use the following format for new entries:

```markdown
## YYYY-MM-DD HH:MM — Short task title

**Status:** COMPLETE / IN PROGRESS / BLOCKED

### Objective

Describe what this session attempted to accomplish.

### Completed

- Implemented ...
- Changed ...
- Fixed ...

### Files Changed

- `path/to/file1`
- `path/to/file2`

### Technical Decisions

Include this section only when meaningful decisions were made.

**Decision:**  
Describe the decision.

**Reason:**  
Explain why this approach was selected.

**Alternatives considered:**  
Describe meaningful alternatives if relevant.

**Impact:**  
Describe consequences for future development.

### Validation

- Build: PASS / FAIL / NOT RUN
- Tests: PASS / FAIL / NOT RUN
- Manual test: PASS / FAIL / NOT RUN

Additional details:

- ...

### Known Issues

- None

or:

- Describe current bugs, limitations, uncertainty, or blockers.

### Next Steps

1. Highest-priority next task.
2. Follow-up work.
3. Optional improvement.

### Git

- Branch: `branch-name`
- Commit: `commit-hash commit message`

If not committed:

- Commit: NOT COMMITTED
```

---

## Incomplete Work

Never make incomplete work appear complete.

Use:

```text
Status: IN PROGRESS
```

or:

```text
Status: BLOCKED
```

Record:

- What currently works
- What is incomplete
- Where development should continue
- Any blockers
- Important assumptions
- Relevant errors or failed attempts

Important unfinished work must be recorded in DevLog.

TODO comments in code may provide local context, but important TODOs must also appear in DevLog.

---

## Technical Decisions

Record decisions that affect future architecture or implementation.

Examples:

- UI framework selection
- Hardware monitoring library
- NVIDIA API strategy
- FPS capture strategy
- Storage monitoring API
- IPC architecture
- Configuration format
- Provider/plugin architecture

For significant decisions, document:

```text
Decision:
Reason:
Alternatives considered:
Impact:
```

Do not make major architectural decisions silently.

---

## Handoff Procedure

Before ending a development session:

1. Leave the repository in a coherent state.
2. Run available validation.
3. Inspect `git status` and `git diff`.
4. Update `docs/DEVLOG.md`.
5. Clearly document incomplete work.
6. Record the recommended next task.

The latest DevLog entry should make these answers clear:

```text
What was attempted?
What changed?
Why was it changed?
What currently works?
What does not work?
What should be done next?
```

---

## AI Agent Rules

When working as an AI coding agent:

- Verify repository state before editing.
- Do not assume previous agent reasoning was correct.
- Do not generate large amounts of speculative code.
- Do not replace working implementations without evidence.
- Do not hide errors encountered during development.
- Do not claim tests/builds succeeded unless they were run.
- Do not remove functionality merely to make a build pass.
- Do not expand the task without a clear reason.
- Preserve user-written code unless modification is necessary.

If the task cannot be fully completed, leave the repository in the safest coherent state possible and document the remaining work in DevLog.

---

## Definition of Done

A task is complete when, where applicable:

- Requested behavior is implemented.
- Code follows the existing architecture and conventions.
- Build succeeds.
- Relevant tests pass.
- Important edge cases are handled.
- No unrelated changes were introduced.
- DevLog is updated.
- Remaining limitations are documented.

```text
Code + Validation + Documentation = Done
```