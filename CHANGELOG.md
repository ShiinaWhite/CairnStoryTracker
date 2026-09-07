# Changelog

All notable changes to CairnStoryTracker.

## Unreleased

Nothing yet.

## 0.4.0

Product semantics change: the tracker now records **player exploration
knowledge**, not a mirror of the current game save.

- Persistent monotonic exploration memory (`UserData/CairnStoryTracker/progress.json`,
  schema v1): completions are added, never removed — game reloads, early-save
  loads, or world rollbacks do not resurrect explored markers
- Member-level Lore persistence (each completed `scene|path` member is saved;
  group completion stays a runtime-derived state, so future members reappear as
  `?` while old completions stay)
- Stable collectible ID persistence (`UniquePersistentID` as decimal strings)
- Legacy top-left X/Y progress panel removed — the `?` / `i` / `*` markers are
  the progress system (collectible classification / eligibility is kept)
- Native-completed bootstrap: on first run, save-proven completed providers and
  taken collectibles are absorbed into the tracker memory
- Background/deferred safe persistence: debounced writer on a plain-data
  snapshot thread, tmp-file + replace write, corrupt / newer-schema files are
  never overwritten (writes disabled for the session instead)
- No in-game reset: deleting `progress.json` manually is the reset mechanism
- F8: new Persistence section (path / load state / schema / counts / dirty /
  writer state / last save), member `readSource` (native/persistent/session/
  unread) and collectible `completedSource` diagnostics
- Runtime verification by the owner is still pending (only minimal startup was
  smoke-tested: mod load, zero exceptions)

## 0.3.18

Performance stabilization:

- Interaction events (`READ STOP` / item looted / scene loaded) now only mark the
  model dirty — no more delayed full rebuilds right after interacting (removed
  the main-thread frame hitch, confirmed by A/B without the DLL)
- Full refresh is deferred to L1 / eagle-eye / F8 via a single
  `EnsureModelFresh()` entry point (one dirty refresh per open)
- FIE focus reads are correlated **deferred** inside `BuildLoreGroups`
  (no more `FindObjectsOfType<FocusInteractionElement>` at interaction end)
- `LoreGroupVisible` now honors `GroupPending` (covered-collectible remaining no
  longer ignored)
- Replaced all unsafe IL2CPP `Transform → RectTransform` casts with
  `GetComponent<RectTransform>()`
- F8: model refresh timing + RawCategory-aligned diagnostics

## 0.3.17

Marker semantics:

- `?` narrative POI / `i` world info / `*` standalone collectible
  (replaced the missing-glyph `◇`)
- `RawCategory` computed from RAW members (pre-dedup), so collectible-backed
  lore keeps its lore identity (GabRob stays `?`)
- Collectible-backed lore markers are merged into the lore marker (no `?`+`*`
  duplicates); standalone collectibles keep their own `*`
- Group pending = lore unread OR covered collectible remaining

## 0.3.16

- Lore/collectible dedup via stable persistent IDs
  (`lootProviderDataContainer` ↔ ItemLocation)
- Session-read now correctly affects lore group visibility
- `READ STOP` triggers a marker refresh
- Validated: Mapboard, Bear sign, GabRob single-marker behavior

## 0.3.15

- Final FIE visual anchor rule (parent-subtree renderer bounds → self-subtree →
  transform), fixing the mapboard marker offset

## 0.3.x earlier

Broad milestones. The early 0.3.x work predates this public repository, so
these are reconstructed from the development / runtime-investigation records —
the git history here does not cover them:

- Initial per-zone collectible X/Y tracker with Chinese UI
- Quick-travel map markers with pure-visual safety (no native warp pollution)
- L1 survey-view markers
- Lore discovery: `*_Lore` grouping, ReadInteractionProvider /
  FocusInteractionElement carriers, session read tracking
- Native-pollution watchdogs
- F8 diagnostics (zone snapshot, camera/marker/survey state, classification)
