# CairnStoryTracker Development Notes

Audience: future maintainers / AI coding agents working on this mod.
This document records **runtime-verified architecture conclusions** and the reasons
behind them. It is not a line-by-line source walkthrough.

Stable baseline: **v0.3.18** (v0.4.0 builds on it; owner runtime verification of the
persistence features is still pending — see §14).

---

## 1. Current stable state

Runtime-verified by the project owner (in-game, up to v0.3.18):

- Bear read → `i` marker disappears (same session)
- Mapboard (公告板) fully read → `?` disappears
- Beekeeper / honey cave fully interacted → `?` disappears
- GabRob collectible-backed lore → exactly one `?` (no `?`+`◇`/`*` duplicate)
- No native quick-travel marker pollution (`nativeWarpPointCount` unchanged)
- The severe post-interaction frame hitch is resolved (v0.3.18 perf model)

v0.4.0 (persistent exploration memory) has passed build + minimal startup smoke
test only (mod loads, zero exceptions, no user save entered). Do not claim any
v0.4.0 persistence behavior as runtime-verified until the owner confirms it.

## 2. Product principles

### Completeness first
Classification may be imperfect; content must never disappear because the
classifier was unsure. When semantics are uncertain, prefer a conservative
false-positive (show `i` instead of hiding a potentially important spot).

### One place, one marker
One actual lore POI should render one marker. Group by the level's
`*_Lore` hierarchy, not per readable object.

### Collectible-backed lore
- Collectible classification / eligibility is kept untouched
- The visual marker is merged into the lore marker (no `?`+`*` duplicates)
- Identity is the stable persistent ID, never coordinates or name strings

### Persistent monotonic exploration memory (v0.4.0)
The tracker records **player exploration knowledge**, not a mirror of the
current game save. Design rationale:

- Native save state can **add** completion evidence; it can **never remove**
  completion. If the tracker says completed and the save rolls back, the
  tracker stays completed (rollback must not resurrect explored markers).
- Lore persistence is **member-level**: the file stores each completed
  `scene|path` member key, not group-completed booleans. If a future game
  update adds a new member to a group, the group naturally reappears `?`
  while old members stay completed.
- Collectible persistence uses the **stable `UniquePersistentID`** — never
  coordinates or names.
- **No automatic reset**: no per-site/zone/all reset, no new-game detection,
  no save-slot coupling yet. Manual deletion of `progress.json` IS the reset.
- Old v0.3.x history for nonpersistent readables is unrecoverable (the game
  itself does not remember it); those markers may reappear once after
  upgrading, then persist from v0.4.0 on.

## 3. Data sources

### Collectible (special exploration items)
- Source: `CairnAPI.ItemLocations` (`Enumerate()` / stable `UniquePersistentID`)
- Fields used: `Id`, `Position`, `SceneName`, `Items`, `remaining` / `StocksEmpty`
- The classification families (letters, maps, GabRob, doll parts, flyers,
  journal, crystal shards…) still decide eligibility and lore-merge dedup.
  Since v0.4.0 there is no X/Y UI anymore — the classification feeds marker
  eligibility, not a numeric progress panel.

### ReadInteractionProvider
World readable carrier for provider-backed objects (notes on walls, sign posts).
- Persistent providers: `InteractionCount > 0` is one completion **evidence**
  source (v0.4.0) — alongside persistent tracker memory and session memory;
  it is no longer the reverse authority
- Correlation: `GameEventManager.OnInteractEnterWithReadInteractionProvider`
  records the exact provider key (shared `HierarchyPath` helper — event path
  and scan path MUST produce the same `scene|path` key); the following
  `READ STOP` marks it read **and** stores it in persistent memory
  (see `OnStopReading`)

### FocusInteractionElement
Second reading entry (e.g. the Mapboard poster board).
- Holds `readInteractionData` — members without it are NOT reading interactions
  and are excluded from the lore pool
- Some collectibles surface as FIE + sibling `lootProviderDataContainer`
  (stable PID matches the collectible ItemLocation id)
- Reads are correlated **deferred** (v0.3.18): `READ STOP` records only
  `{ReadDataPointer, playerPos}`; `BuildLoreGroups()`'s FIE pass resolves
  pointer identity + interaction-distance. **Unique match only** marks read
  (and since v0.4.0 also writes persistent memory); zero or multiple matches
  stay unread (never guess)

### StoryEventSensor
Diagnostics only (F8 + `READ`/`STORY` logging). Never use it to decide story
markers: runtime sampling found heavy tutorial/process trigger noise
(`Tuto_Jump_Crag_*` etc.), so sensor state does not reliably represent optional
narrative completion.

## 4. Lore grouping

- Boundary: any hierarchy segment ending in `_Lore` (`Crag_Lore`, `Chimney_Lore`…)
- POI identity: `scene + loreRoot + first child under the lore node`
  (e.g. `Crag_Lore/Beekeeper`, `Crag_Lore/02_Crag_Mapboard_Crag`)

Important: `*_Lore` is the **world-readable content boundary** — nonLore=0
was observed in every investigated loaded gameplay scene so far. Treat that as
a strong working assumption / boundary, NOT an exhaustive proof across the
whole game. It is also NOT a reliable Narrative-vs-WorldInfo semantic tag —
both kinds live under it.

## 5. RawCategory (conservative display classifier)

```text
rawMembers >= 2 => NarrativePoi => ?
rawMembers == 1 => WorldInfo    => i
```

This is **display semantics only**, not an eligibility filter (members are never
dropped because of it).

Critical ordering rule: `RawCategory` MUST be computed **before**
collectible-backed member dedup. GabRob has rawMembers=2 but
effectiveMembers=0 (both covered); computing category after dedup would
wrongly demote it.

## 6. Collectible-backed lore

Identity: `UniquePersistentID` of the member's carrier
(Provider itself / FIE's `lootProviderDataContainer`) matched against the
tracked collectible ItemLocation id.

Formal semantics:

```text
GroupPending =
    LoreUnread > 0
    || CoveredCollectibleRemaining > 0
```

`LoreGroupVisible(g)` returns `g.GroupPending` — **single source of truth**
(v0.3.18 fix; the previous loop ignored covered-remaining and ignored
session-read for non-persistent providers).

Since v0.4.0 `CollectibleRemaining` uses the tracker's final completion
semantics: if the persistent memory contains the ID, the collectible counts as
completed even when the live world claims it is still there.

Glyphs:

- Pure collectible: `*`
- Collectible-backed NarrativePoi: `?`
- Collectible-backed WorldInfo: `i`

Merging is display-only; classification is unchanged.

## 7. Marker anchoring (FROZEN — historical reasons documented to prevent regression)

### ReadInteractionProvider
`provider.transform.position` — runtime-verified correct (Bear red sign:
transform == collider center == visual board).

### FocusInteractionElement
Do NOT use `fie.transform.position` directly. Mapboard runtime proved the FIE
transform is the interaction spot on the ground, ~10 m from the poster surface.

Formal rule (in priority order):

1. FIE **direct-parent subtree** renderer bounds center
2. fallback: FIE **own subtree** renderer bounds center (Skeleton case: the
   `Message` visual lives under the FIE itself)
3. fallback: FIE `transform.position`

### Group anchor
- Centroid of member anchors
- Then the real member nearest to that centroid

Never use the whole lore-group renderer combined bounds: the Skeleton group
contains an abnormal giant renderer (combined size 445×669×360 m) that puts
the combined center hundreds of meters away.

This anchoring passed in-game acceptance; treat it as **frozen** unless new raw
evidence shows a regression.

## 8. Quick-travel marker safety (FROZEN)

History: an early attempt created a real `FreeRoamWarpPoint` — even unregistered
and disabled, the eagle-eye list enumerated active-scene points and showed a
selectable `[none string]` fast-travel row (v0.3.0 incident).

Current design:

- Hidden driver GameObject: inactive from birth + disabled, never registered —
  used only to hold the world position
- Inactive clone of the native world-pin widget: `Update()` invoked manually to
  get the game's own map projection
- The visible marker is an independent non-interactive TMP glyph
  (`?` / `i` / `*`), `raycastTarget=false`, no Button/Selectable/EventTrigger

Principle: **pure visual only**. `nativeWarpPointCount` before/after is logged
by F8 as the pollution watchdog. Do not "simplify" this back into registering a
`FreeRoamWarpPoint`.

## 9. Performance model (v0.3.18) + persistence writer (v0.4.0)

Events (`READ STOP` / `ITEM LOOTED` / scene load) only flag the model dirty;
the full scans run once when the player opens a marker surface (L1 / eagle
map) or presses F8:

```text
event → MarkModelDirty()   (flag only)
refresh → EnsureModelFresh(reason)
           RebuildProgress()
           BuildLoreGroups()
```

One measured dirty-refresh sample at L1 open: `progressMs ≈ 58`, `loreMs ≈ 76`,
`totalMs ≈ 134`. This is one concrete measurement, not a performance guarantee.
Current conclusion: the single L1-open hitch is acceptable; do not re-architect
performance without new evidence.

Persistence I/O never runs inside an interaction hot path:

```text
completion event → HashSet add (in-memory, immediate)
                 → MarkProgressDirty() (flag + debounce timestamp only)
OnUpdate: TickProgressPersistence()
          → after a short debounce, snapshot the HashSets (plain .NET data)
          → one background writer serializes + writes tmp + moves over
            progress.json
shutdown → OnDeinitializeMelon does one final synchronous flush
```

The background thread touches ONLY the plain-data snapshot (strings/ulongs).
One writer at a time; if a new revision arrives mid-write, the dirty flag
survives and the next tick schedules the follow-up save.

## 10. F8 diagnostics

F8 is a developer diagnostic tool, not a runtime hot path. One snapshot dumps:
story events, item locations (with tracked/reason/`completedSource`), lore
groups (rawMembers/effectiveMembers/covered, member `readSource`
native/persistent/session/unread), RawCategory, LoreUnread, CoveredRemaining,
GroupPending, anchors, FIE collider/renderer spatial data, native warp point
counts, model refresh timing, and — since v0.4.0 — a **Persistence** section:
file path, load state, schema versions, persistent lore/collectible counts,
dirty flag, writer busy state, last save result.

F8 itself is heavy: one measured snapshot ≈ 275 ms — expected debugging cost.

## 11. Persistence file (v0.4.0)

`<game>/UserData/CairnStoryTracker/progress.json`, schema v1:

```json
{
  "schemaVersion": 1,
  "completedLoreMembers": ["02_Crag_Gameplay|SceneRoot/Crag_Lore/…"],
  "completedCollectibles": ["8020028534801096432"]
}
```

- Lore: one entry per completed **member key** (`scene|path`, built by the
  shared `HierarchyPath` helper) — never group-completed booleans
- Collectibles: decimal **strings** (avoids 64-bit JSON integer issues);
  in-memory `HashSet<ulong>`
- NOT stored (always recomputed from the live world model): marker screen
  positions, anchors, categories, glyphs, zone progress, player position
- Writes: debounced (~2 s), background thread, tmp file + `File.Move(overwrite)`
- Missing file = empty memory (fresh start), created on first save
- Corrupt file: warning, file left untouched, writes disabled for the session
  (`PERSISTENCE LOAD FAILED` in the log) — never auto-reset
- `schemaVersion > 1`: same protective behavior, format never guessed
- Hand-rolled serializer/parser (unit-tested offline for round-trip and
  corrupt-input rejection); no JSON library dependency

## 12. Frozen / do-not-regress areas

Unless new raw F8/runtime evidence proves a regression, do not refactor:

- FIE visual anchor rule (parent subtree → self subtree → transform)
- Provider anchor (`transform.position`)
- Group centroid / nearest-member anchor
- Quick-travel pure-visual safety (hidden driver + inactive widget + TMP visual)
- RawCategory computed pre-dedup
- Collectible-backed lore merge (stable PID identity)
- GroupPending visibility
- Deferred FIE unique-match-only correlation
- Dirty-on-event / refresh-on-L1 performance model
- Persistence write path: no synchronous file I/O in interaction handlers

## 13. Known issues / low-priority TODO

- **F8 standalone count diagnostic**: can report
  `standalone=-1` with `mergedIntoLore=2`. The summary subtracts covered lore
  *member* count instead of unique collectible IDs. Actual marker behavior is
  correct. Correct fix: count unique `CollectibleId`s. Diagnostic-only, low
  priority — do not fix casually.
- **Tiny interaction hitch**: v0.3.18 removed the severe post-interaction hitch;
  the owner still occasionally feels ~1 frame of jitter after interactions.
  No evidence it comes from this mod. If investigated later: A/B test disabling
  the plain READ START/STOP logging first; do not touch the perf architecture.
- **L1 refresh cost**: ~134 ms per dirty refresh. Acceptable today. If it grows
  with new zones, investigate incremental caching / scene-scoped scans /
  avoiding repeated hierarchy reconstruction. Not scheduled.

## 14. Validation workflow

For any change, the AI executor must:

1. build
2. install the DLL to the game's `Mods/` folder
3. run the game only to the earliest point where mod load success is visible
4. verify zero Exception / Harmony / IL2CPP errors
5. close the game

Do NOT enter the owner's save; in-game verification is done by the owner.
Never claim marker/readable/save-dependent behavior is "verified" unless the
owner confirmed it in game. v0.4.0 persistence behavior (bootstrap absorption,
rollback survival, file contents) is still awaiting that owner verification.
