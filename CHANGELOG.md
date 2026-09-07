# Changelog

All notable changes to CairnStoryTracker.

## Unreleased

Nothing yet.

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

Broad milestones (details in git history):

- Initial per-zone collectible X/Y tracker with Chinese UI
- Quick-travel map markers with pure-visual safety (no native warp pollution)
- L1 survey-view markers
- Lore discovery: `*_Lore` grouping, ReadInteractionProvider /
  FocusInteractionElement carriers, session read tracking
- Deferred FIE read correlation and native-pollution watchdogs
- F8 diagnostics (zone snapshot, camera/marker/survey state, classification)
