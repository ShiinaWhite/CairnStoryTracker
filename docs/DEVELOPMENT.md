# CairnStoryTracker Development Notes

Audience: future maintainers / AI coding agents working on this mod.
This document records **runtime-verified architecture conclusions** and the reasons
behind them. It is not a line-by-line source walkthrough.

Stable baseline: **v0.3.18**.

---

## 1. Current stable state

Runtime-verified by the project owner (in-game):

- Bear read → `i` marker disappears (same session)
- Mapboard (公告板) fully read → `?` disappears
- Beekeeper / honey cave fully interacted → `?` disappears
- GabRob collectible-backed lore → exactly one `?` (no `?`+`◇`/`*` duplicate)
- No native quick-travel marker pollution (`nativeWarpPointCount` unchanged)
- The severe post-interaction frame hitch is resolved (v0.3.18 perf model)

Do not claim anything beyond this list as runtime-verified.

## 2. Product principles

### Completeness first
Classification may be imperfect; content must never disappear because the
classifier was unsure. When semantics are uncertain, prefer a conservative
false-positive (show `i` instead of hiding a potentially important spot).

### One place, one marker
One actual lore POI should render one marker. Group by the level's
`*_Lore` hierarchy, not per readable object.

### Collectible-backed lore
- Collectible tracking / X/Y is kept untouched
- The visual marker is merged into the lore marker (no `?`+`*` duplicates)
- Identity is the stable persistent ID, never coordinates or name strings

### No custom persistent read state
The mod deliberately does not persist its own "has been read" state. Game saves
can roll back; a mod-side persistent read log would desync from the game save.
Non-persistent readables use **session-only state**: after restart, their markers
reappear. This is design behavior, not a bug.

## 3. Data sources

### Collectible (special exploration items)
- Source: `CairnAPI.ItemLocations` (`Enumerate()` / stable `UniquePersistentID`)
- Fields used: `Id`, `Position`, `SceneName`, `Items`, `remaining` / `StocksEmpty`
- X/Y progress comes **only** from tracked collectibles (current
  classified families: letters, maps, GabRob, doll parts, flyers, journal,
  crystal shards…)

### ReadInteractionProvider
World readable carrier for provider-backed objects (notes on walls, sign posts).
- Persistent providers: `InteractionCount > 0` = read (authoritative, save-backed)
- Non-persistent providers: session state only
- Correlation: `GameEventManager.OnInteractEnterWithReadInteractionProvider`
  records the exact provider key; the following `READ STOP` marks it read
  (see `OnStopReading`)

### FocusInteractionElement
Second reading entry (e.g. the Mapboard poster board).
- Holds `readInteractionData` — members without it are NOT reading interactions
  and are excluded from the lore pool
- Some collectibles surface as FIE + sibling `lootProviderDataContainer`
  (stable PID matches the collectible ItemLocation id)
- Reads are correlated **deferred** (v0.3.18): `READ STOP` records only
  `{ReadDataPointer, playerPos}`; `BuildLoreGroups()`'s FIE pass resolves
  pointer identity + interaction-distance. **Unique match only** marks read;
  zero or multiple matches stay unread (never guess)

### StoryEventSensor
Diagnostics only (F8 + `READ`/`STORY` logging). Never use it to decide story
markers or X/Y: runtime sampling found heavy tutorial/process trigger noise
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

Glyphs:

- Pure collectible: `*`
- Collectible-backed NarrativePoi: `?`
- Collectible-backed WorldInfo: `i`

Collectible data still participates in X/Y; merging is display-only.

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

## 9. Performance model (v0.3.18)

Before: `READ STOP` / `ITEM LOOTED` / scene load scheduled a delayed full
rebuild (`RebuildProgress` + `BuildLoreGroups`, several `FindObjectsOfType`
sweeps + hierarchy reconstruction) on the main thread right after the player
finished interacting — measurable frame hitch, confirmed by A/B (DLL removed →
hitch gone).

Now:

```text
event → MarkModelDirty()   (flag only)
refresh → EnsureModelFresh(reason)
           RebuildProgress()
           BuildLoreGroups()
```

Refresh happens only when the player opens a marker surface (L1 / eagle map)
or presses F8.

One measured dirty-refresh sample at L1 open: `progressMs ≈ 58`, `loreMs ≈ 76`,
`totalMs ≈ 134`. This is one concrete measurement, not a performance guarantee.
Current conclusion: the single L1-open hitch is acceptable; do not re-architect
performance without new evidence.

## 10. F8 diagnostics

F8 is a developer diagnostic tool, not a runtime hot path. One snapshot dumps:
story events, item locations (with tracked/reason), lore groups
(rawMembers/effectiveMembers/covered), RawCategory, LoreUnread,
CoveredRemaining, GroupPending, anchors, FIE collider/renderer spatial data,
native warp point counts, and model refresh timing.

F8 itself is heavy: one measured snapshot ≈ 275 ms — expected debugging cost.

## 11. Frozen / do-not-regress areas

Unless new raw F8/runtime evidence proves a regression, do not refactor:

- FIE visual anchor rule (parent subtree → self subtree → transform)
- Provider anchor (`transform.position`)
- Group centroid / nearest-member anchor
- Quick-travel pure-visual safety (hidden driver + inactive widget + TMP visual)
- Session-only nonpersistent readable design
- RawCategory computed pre-dedup
- Collectible-backed lore merge (stable PID identity)
- GroupPending visibility
- Dirty-on-event / refresh-on-L1 performance model
- X/Y only for reliably persisted special collectibles

## 12. Known issues / low-priority TODO

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

## 13. Validation workflow

For any change, the AI executor must:

1. build
2. install the DLL to the game's `Mods/` folder
3. run the game only to the earliest point where mod load success is visible
4. verify zero Exception / Harmony / IL2CPP errors
5. close the game

Do NOT enter the owner's save; in-game verification is done by the owner.
Never claim marker/readable/save-dependent behavior is "verified" unless the
owner confirmed it in game.
