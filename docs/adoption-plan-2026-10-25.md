# Adoption Plan

> **Generated**: 2026-10-25
> **Project phase**: Production (inferred; no `project.stage` set)
> **Engine**: Godot 4.7 (C#)
> **Template version**: v1.0+
> **Workflow tier**: minimal (audit scoped to `design/game-brief.md` + config)

Work through these steps in order. Check off each item as you complete it.
Re-run `/adopt` anytime to check remaining gaps.

Audit result: `design/game-brief.md` has all 6 required fields and all 3
recommended one-liners. No BLOCKING gaps.

---

## Step 1: Fix Blocking Gaps

None.

---

## Step 2: Fix High-Priority Gaps

### 2.1 `engine.rendering` and `engine.physics` unset
Neither `project.yaml` nor `.claude/docs/technical-preferences.md` has a value
(the legacy file still reads `[TO BE CONFIGURED]`). Low practical impact at
`minimal`: ADR engine checks are not on this path.
Fix: `/settings engine.rendering=<value>` and `/settings engine.physics=<value>`
(or add both under `engine:` in `project.yaml`).
**Time**: 5 min
- [x] rendering set (Forward+)
- [x] physics set (Jolt Physics)

---

## Step 3: Bootstrap Infrastructure

Not on the minimal path — the brief's build order is the plan. Next:
`/create-stories` (or `/dev-story` once stories exist).

---

## Step 4: Medium-Priority Gaps

### 4.1 `naming.*` unset
No naming conventions in either config source.
Fix: add `naming:` keys to `project.yaml` (classes, variables, files, constants).
**Time**: 5 min
- [ ] naming configured

### 4.2 `performance.*` unset
No target framerate or frame budget in either config source.
Fix: add `performance:` keys to `project.yaml`.
**Time**: 5 min
- [ ] performance configured

---

## Step 5: Optional Improvements

### 5.1 Installed Godot version not recorded
`docs/engine-reference/godot/VERSION.md` says "Installed at pin time: NOT
DETERMINED", so a pinned API may sit ahead of the installed editor.
Fix: check the installed editor version and record it in `VERSION.md`.
**Time**: 5 min
- [x] installed version recorded (4.7.2.stable.mono, 2026-10-25)

---

## What to Expect from Existing Stories

Existing stories continue to work with all template skills. New format checks
(TR-ID validation, manifest version staleness) auto-pass when the fields are
absent — so nothing breaks. They won't benefit from staleness tracking until
regenerated. Do not regenerate stories that are in progress or done.

---

## Re-run

Run `/adopt` again after completing Step 2 to verify all blocking and high gaps
are resolved. The new run will reflect the current state of the project.
