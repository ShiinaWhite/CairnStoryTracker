using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CairnAPI;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.UI;
using Il2CppTMPro;
using Il2CppTGBTools.Localization;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(CairnStoryTracker.Core), "CairnStoryTracker", "0.3.18", "runtime-recon")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnStoryTracker
{
    /// <summary>
    /// V1: zone-grouped collectible progress, shown while the eagle-eye map is open.
    /// F8 diagnostics kept from V0; story events remain diagnostics-only (not counted).
    /// </summary>
    public class Core : MelonMod
    {
        private const float SubRetrySeconds = 5f;

        // ------------------------------------------------------------ V0 state
        private StoryEventManager _subscribedManager;
        private System.Action<Il2Cpp.StoryEventSensor> _managedStoryEnd;
        private Il2CppSystem.Action<Il2Cpp.StoryEventSensor> _il2cppStoryEnd;
        private bool _lootSubscribed;
        private float _nextSubAttemptAt;
        private bool _loggedWaiting;
        private bool _snapshotBusy;

        // ------------------------------------------------------------ V1 state
        private sealed class ZoneProgress
        {
            public string Key;
            public string Display;
            public int Total;
            public int Done;
            public bool Current;
            public readonly HashSet<string> Scenes = new HashSet<string>();
        }

        // Collectible ID families: deliberate small allowlist of story/exploration
        // item series (recon: InventoryItemStringIdEnum, 316 entries).
        private static readonly KeyValuePair<string, string>[] Families =
        {
            new KeyValuePair<string, string>("ITEM_GABROB", "GabRob"),
            new KeyValuePair<string, string>("ITEM_WEATHER_DOLL", "WeatherDoll"),
            new KeyValuePair<string, string>("ITEM_CRYSTAL_SHARDS", "CrystalShards"),
            new KeyValuePair<string, string>("ITEM_LETTER", "Letter"),
            new KeyValuePair<string, string>("ITEM_RODANNA_LETTER", "Letter"),
            new KeyValuePair<string, string>("ITEM_FLYER", "Flyer"),
            new KeyValuePair<string, string>("ITEM_GAME_JOURNAL", "Journal"),
            new KeyValuePair<string, string>("ITEM_MAP_", "Map"),
        };

        private GameObject _uiCanvas;
        private GameObject _uiRoot;
        private RectTransform _uiRootRect;
        private TextMeshProUGUI _uiText;
        private bool _uiBroken;
        private EagleEyeUI _eagleEye;
        private float _nextEagleLookupAt;
        private bool _eagleWasShown;

        // F8 feedback toast: transient "F8 快照已生成" so the hotkey press is visible in-game.
        private GameObject _toastCanvas;
        private GameObject _toastText;
        private float _toastUntil = -1f;

        // ------------------------------------------------------- V2 read diagnostics
        // The honey-cave wall map is none of FocusInteractionElement / LootProvider /
        // StoryEventSensor / ReadablesInventory. Metadata reverse lookup found the real
        // carrier family: ReadInteractionProvider (holds ReadInteractionDataBase, may
        // giveItemAfterReading, InteractionCount on the base = "has been read" source).
        // GameEventManager exposes STATIC read events, subscribed once for the mod
        // lifetime: OnInteractEnterWithReadInteractionProvider, OnStartReading,
        // OnStopReading.
        private bool _readEventSubscribed;
        private bool _readingNow;
        private string _currentReadDataName;
        private string _currentReadWrapperType;
        private string _currentReadIl2cppType;
        private string _currentReadLayout;
        private bool _currentReadHasDialog;
        private Il2Cpp.ReadInteractionDataBase _currentReadData; // live ref for pointer correlation
        private string _currentProviderDesc;
        private float _lastReadEventAt = -1f;
        private Il2CppSystem.Action<Il2Cpp.ReadInteractionDataBase> _il2cppStartReading;
        private Il2CppSystem.Action<Il2Cpp.ReadInteractionDataBase> _il2cppStopReading;
        private Il2CppSystem.Action<Il2Cpp.ReadInteractionProvider> _il2cppEnterProvider;

        private readonly List<ZoneProgress> _zones = new List<ZoneProgress>();
        private readonly Dictionary<ulong, KeyValuePair<bool, string>> _classification =
            new Dictionary<ulong, KeyValuePair<bool, string>>();

        // Official localized zone names resolved through the game's LocalizationManager,
        // cached per group key ("02_Crag" -> official current-language name).
        private readonly Dictionary<string, string> _officialZoneNames = new Dictionary<string, string>();

        // 暂定中文映射：仅在运行时官方本地化不可用时才会显示（主路径是官方中文）。
        private static readonly Dictionary<string, string> ZoneNameFallbackZh = new Dictionary<string, string>
        {
            ["01_FirstRidge"] = "第一道山脊",
            ["02_Crag"] = "峭壁",
            ["03_Chimney"] = "烟囱",
        };

        private Il2Cpp.ZoneSceneData _currentZone;
        private int _trackedTotal;
        private int _trackedDone;
        private bool _modelDirty = true; // events only flag; refresh happens on L1 open / F8

        private sealed class PendingFocusRead
        {
            public IntPtr ReadDataPointer;
            public Vector3 PlayerPos;
        }

        private readonly List<PendingFocusRead> _pendingFocusReads = new List<PendingFocusRead>();

        // ------------------------------------------------------------ V2 map markers
        // v0.3.0 lesson: the eagle-eye list UI enumerates every ACTIVE
        // FreeRoamWarpPoint in the scene (FindObjectsOfType-style scan), not just
        // FreeRoamManager.WarpPoints — an unregistered point with a disabled
        // component still produced a selectable "[none string]" travel row.
        //
        // v0.3.1 design (pure visual marker):
        //  - driver GameObject is INACTIVE from birth (excluded from active-object
        //    scans; AddComponent on an inactive GO defers Awake/OnEnable) and holds a
        //    disabled, never-registered FreeRoamWarpPoint that only stores a world pos.
        //  - a clone of the game's world-pin widget (serialized refs intact) is kept
        //    INACTIVE under the pins layer; we call Update() on it manually each frame
        //    so the native projection computes a position, then copy that position
        //    onto our own visual "?" (RectTransform + TMP only, raycast off).
        //  - no ACTIVE FreeRoamWarpPoint / FreeRoamWarpPointWidget of ours ever exists
        //    in the scene, so the native list, its location count and its selection
        //    can never see them.
        private sealed class MapMarker
        {
            public ulong Id;           // collectible identity (0 for lore groups)
            public string GroupKey;    // lore group identity
            public string Glyph;
            public GameObject DriverGo;
            public FreeRoamWarpPoint Point;
            public GameObject WidgetGo;
            public FreeRoamWarpPointWidget Widget;
            public GameObject VisualGo;
        }

        private sealed class EligibleSpot
        {
            public ulong Id;
            public Vector3 Pos;
            public string Scene;
        }

        private readonly List<MapMarker> _markers = new List<MapMarker>();
        private readonly List<EligibleSpot> _eligible = new List<EligibleSpot>();
        private readonly Dictionary<ulong, string> _markerReason = new Dictionary<ulong, string>();
        private FreeRoamEagleEyeWarpPointListUI _pinListUi;
        private float _nextPinListLookupAt;
        private bool _markersSynced;
        private bool _lastSyncHadList;
        private int _markerSpawned;
        private int _markerFailed;
        private int _nativeWarpCountBefore = -1;
        private int _nativeWarpCountAfter = -1;
        private int _nativeWidgetCountBefore = -1;
        private int _nativeWidgetCountAfter = -1;

        // ------------------------------------------------------- V3 lore categories
        // Runtime-proven model: narrative readables live under "*_Lore" hierarchy in
        // groups. A group with >=2 members is a NarrativePoi (Beekeeper=9, Mapboard=6);
        // a single-member group is WorldInfo (AdamsSign, Bear_RedSign) — count only
        // decides icon/category, never eligibility. All members stay visible.
        private enum LoreCategory { NarrativePoi, WorldInfo }

        private sealed class LoreMember
        {
            public bool IsProvider;              // ReadInteractionProvider vs FocusInteractionElement
            public GameObject Go;
            public string Scene;
            public string Path;
            public Vector3 Pos;
            public string PositionSource;        // v0.3.15: where Pos came from
            public bool IsPersistent;
            public int InteractionCount;
            public string ReadDataName;
            public string Key;                   // scene|path — session-read identity
            public bool CoveredByCollectible;    // v0.3.16: stable PID match with a tracked ItemLocation
            public ulong CollectibleId;
            public bool CollectibleRemaining;
            public IntPtr ReadDataPointer;       // v0.3.18: deferred FIE read correlation
            public Vector3 InteractionPos;       // interaction transform (NOT the visual anchor)
        }

        private sealed class LoreGroup
        {
            public string Identity;              // scene|loreRoot|poiGroup
            public string LoreRoot;
            public string PoiGroup;
            public string Scene;
            public LoreCategory Category;          // effective (post-dedup) — diagnostics
            public LoreCategory RawCategory;       // v0.3.17: display semantics from RAW members
            public int LoreUnread;
            public int CoveredRemaining;
            public bool GroupPending;
            public readonly List<LoreMember> Members = new List<LoreMember>();
            public Vector3 Anchor;
            public string AnchorSource;
            public string AnchorMember;          // diagnostics
            public bool HasGroupTransform;       // diagnostics only — logic nodes are NOT POI positions
            public Vector3 GroupTransformPos;    // diagnostics only
        }

        private readonly List<LoreGroup> _loreGroups = new List<LoreGroup>();
        private readonly HashSet<string> _sessionReadKeys = new HashSet<string>(); // memory only, never persisted
        private readonly Dictionary<ulong, (string groupIdentity, LoreCategory rawCategory)> _collectibleCoveredByLore =
            new Dictionary<ulong, (string, LoreCategory)>(); // stable PID -> merged into lore marker

        private MelonPreferences_Category _prefsCat;
        private MelonPreferences_Entry<bool> _prefShowNarrative;
        private MelonPreferences_Entry<bool> _prefShowWorldInfo;
        private MelonPreferences_Entry<bool> _prefShowCollectible;

        // ------------------------------------------------- V2 survey markers (observation view)
        // "勘察岩壁" = the game's observation camera mode. Detection: CameraManager's
        // current camera type via the game's own CairnCamera.IsObservationView helper.
        // Projection: the observation view is driven by Cinemachine on the main camera,
        // so Camera.main.WorldToScreenPoint maps world -> screen directly. The "?" is a
        // plain RectTransform + TMP on our own overlay canvas: no warp point, no widget,
        // no list, no interaction — pure display, updated every frame while open.
        private sealed class SurveyMarker
        {
            public ulong Id;           // collectible identity (0 for lore groups)
            public string GroupKey;    // lore group identity
            public Vector3 WorldPos;
            public GameObject Go;
            public RectTransform Rt;
            public string Glyph;
        }

        private readonly List<SurveyMarker> _surveyMarkers = new List<SurveyMarker>();
        private GameObject _surveyCanvas;
        private Canvas _surveyCanvasComp;
        private bool _surveyOpen;
        private int _surveyVisible;
        private int _surveyBehind;
        private int _surveyOffscreen;
        private int _surveyFailed;
        private readonly Dictionary<ulong, string> _surveyReason = new Dictionary<ulong, string>();

        public override void OnInitializeMelon()
        {
            _managedStoryEnd = OnStoryEventEnd;
            SubscribeLoot();
            SubscribeReadEvents();
            try
            {
                _prefsCat = MelonPreferences.CreateCategory("CairnStoryTracker");
                _prefShowNarrative = _prefsCat.CreateEntry("ShowNarrativePoi", true, "显示叙事探索点");
                _prefShowWorldInfo = _prefsCat.CreateEntry("ShowWorldInfo", true, "显示世界信息");
                _prefShowCollectible = _prefsCat.CreateEntry("ShowCollectible", true, "显示特殊收集物");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("preferences init failed (defaults assumed visible): " + e.Message);
                _prefShowNarrative = _prefShowWorldInfo = _prefShowCollectible = null;
            }
            MelonLogger.Msg("V1 ready. Eagle-eye shows collectible progress; F8 dumps a zone snapshot.");
        }

        public override void OnDeinitializeMelon()
        {
            try
            {
                if (_subscribedManager != null && _il2cppStoryEnd != null)
                    _subscribedManager.remove_OnStoryEventEnd(_il2cppStoryEnd);
            }
            catch { /* manager may already be destroyed */ }
            _subscribedManager = null;

            try
            {
                if (_lootSubscribed)
                {
                    ItemLocations.OnLooted -= OnItemLooted;
                    _lootSubscribed = false;
                }
            }
            catch { }

            TearDownPanel();
            TeardownMarkers();
            TeardownSurvey();
            UnsubscribeReadEvents();
        }

        // ------------------------------------------------------- V3 lore groups

        private static bool IsGameplayScene(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) &&
                   (sceneName.Contains("Gameplay") || sceneName.Contains("gameplay"));
        }

        /// <summary>
        /// FIE visual anchor (v0.3.15, runtime-proven): the poster/visual mesh usually
        /// lives in the FIE's direct parent subtree (Mapboard: Object_NN/Cairn_PosterKami_*),
        /// sometimes in the FIE's own subtree (Skeleton: Message). The FIE transform itself
        /// is only the interaction trigger spot (Mapboard: 10m in front of the board).
        /// Priority: parent-subtree renderer bounds center -> self-subtree -> transform.
        /// </summary>
        private static bool TryGetFieVisualAnchor(FocusInteractionElement fie, string poiGroup,
            out Vector3 pos, out string source)
        {
            // Step 1: direct parent subtree — but not when the FIE hangs directly under
            // the poiGroup root (that subtree would sweep sibling visuals of the whole group)
            var parent = fie.transform.parent;
            if (parent != null && parent.name != poiGroup)
            {
                if (TryCombinedRendererCenter(parent, out pos))
                {
                    source = "FIEParentRendererBounds";
                    return true;
                }
            }

            // Fallback 1: FIE's own subtree
            if (TryCombinedRendererCenter(fie.transform, out pos))
            {
                source = "FIESelfRendererBounds";
                return true;
            }

            // Fallback 2: interaction transform
            pos = fie.transform.position;
            source = "FIETransformFallback";
            return false;
        }

        private static bool TryCombinedRendererCenter(Transform root, out Vector3 center)
        {
            center = default;
            try
            {
                var rens = root.GetComponentsInChildren<Renderer>(true);
                if (rens == null || rens.Length == 0) return false;
                var bounds = new Bounds();
                bool any = false;
                foreach (var r in rens)
                {
                    try
                    {
                        if (r == null) continue;
                        if (!any) { bounds = r.bounds; any = true; }
                        else bounds.Encapsulate(r.bounds);
                    }
                    catch { }
                }
                if (!any) return false;
                center = bounds.center;
                return true;
            }
            catch { return false; }
        }

        private static string MemberKey(string scene, string path) { return scene + "|" + path; }

        private bool MemberIsRead(LoreMember m)
        {
            // persistent providers: native count is authoritative
            if (m.IsProvider && m.IsPersistent)
            {
                try { if (m.InteractionCount > 0) return true; } catch { }
            }
            // everything else: session-read memory (unknown state stays unread = visible)
            return _sessionReadKeys.Contains(m.Key);
        }

        private Vector3 GetMemberPos(LoreMember m) { return m.Pos; }

        /// <summary>
        /// Scan both carrier families (include inactive, loaded gameplay scenes only),
        /// group by scene+loreRoot+poiGroup, classify by member count, compute anchors.
        /// FocusInteractionElement joins only when readInteractionData is present
        /// (proven reading interaction); providers always (their read data loads late).
        /// </summary>
        private void BuildLoreGroups()
        {
            _loreGroups.Clear();
            var groups = new Dictionary<string, LoreGroup>();

            void AddMember(LoreMember m, string loreRoot, string poiGroup)
            {
                var id = m.Scene + "|" + loreRoot + "|" + poiGroup;
                if (!groups.TryGetValue(id, out var g))
                {
                    g = new LoreGroup
                    {
                        Identity = id,
                        LoreRoot = loreRoot,
                        PoiGroup = poiGroup,
                        Scene = m.Scene,
                        Category = LoreCategory.WorldInfo,
                    };
                    groups[id] = g;
                }
                g.Members.Add(m);
            }

            // collectible identity map: LootProvider persistentId -> still has loot?
            var collectibleRemaining = new Dictionary<ulong, bool>();
            try
            {
                foreach (var lp in UnityEngine.Object.FindObjectsOfType<Il2Cpp.LootProvider>(true))
                {
                    try
                    {
                        if (lp == null) continue;
                        var pid = lp.UniquePersistentID;
                        if (pid == 0) continue;
                        bool remaining = true;
                        try { remaining = !lp.StocksEmpty; } catch { }
                        collectibleRemaining[pid] = remaining;
                    }
                    catch { }
                }
            }
            catch { }

            // A) ReadInteractionProvider
            try
            {
                foreach (var rp in UnityEngine.Object.FindObjectsOfType<Il2Cpp.ReadInteractionProvider>(true))
                {
                    try
                    {
                        if (rp == null) continue;
                        var scene = "unknown";
                        try { scene = rp.gameObject.scene.name; } catch { }
                        if (!IsGameplayScene(scene)) continue;

                        var segs = new List<string> { rp.gameObject.name };
                        var t = rp.transform.parent;
                        for (int d = 0; d < 24 && t != null; d++, t = t.parent)
                        {
                            segs.Insert(0, t.name);
                        }
                        string loreRoot = null, poiGroup = null;
                        Transform groupT = null;
                        for (int i = 0; i < segs.Count; i++)
                        {
                            if (segs[i].EndsWith("_Lore", StringComparison.OrdinalIgnoreCase))
                            {
                                loreRoot = segs[i];
                                // diagnostics: locate the poi-group logic node transform
                                var tt = rp.transform;
                                int up = segs.Count - 1 - (i + 1);
                                for (int k = 0; k < up && tt != null; k++) tt = tt.parent;
                                groupT = tt;
                                if (i + 1 < segs.Count - 1) poiGroup = segs[i + 1];
                                else if (i + 1 == segs.Count - 1) poiGroup = "<root>";
                                break;
                            }
                        }
                        if (loreRoot == null) continue; // underLore safety boundary

                        var path = string.Join("/", segs);
                        bool persistent = false; int count = -1;
                        try { persistent = rp.IsPersistent; } catch { }
                        try { count = rp.InteractionCount; } catch { }

                        var pid = rp.UniquePersistentID;
                        var covered = pid != 0 &&
                                      _classification.TryGetValue(pid, out var pc) && pc.Key;
                        var coveredId = covered ? pid : 0;
                        var coveredRem = covered && collectibleRemaining.TryGetValue(pid, out var r1) && r1;

                        AddMember(new LoreMember
                        {
                            IsProvider = true,
                            Go = rp.gameObject,
                            Scene = scene,
                            Path = path,
                            Pos = rp.transform.position,
                            PositionSource = "ProviderTransform",
                            IsPersistent = persistent,
                            InteractionCount = count,
                            ReadDataName = null,
                            Key = MemberKey(scene, path),
                            CoveredByCollectible = covered,
                            CollectibleId = coveredId,
                            CollectibleRemaining = coveredRem,
                        }, loreRoot, poiGroup);

                        // stash group-transform diagnostics (never used for anchors)
                        if (groups.TryGetValue(scene + "|" + loreRoot + "|" + poiGroup, out var gDiag) && !gDiag.HasGroupTransform)
                        {
                            gDiag.HasGroupTransform = groupT != null;
                            if (groupT != null) gDiag.GroupTransformPos = groupT.position;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception e) { MelonLogger.Warning("lore provider scan failed: " + e.Message); }

            // B) FocusInteractionElement — only with serialized read data (proven readers)
            try
            {
                foreach (var fie in UnityEngine.Object.FindObjectsOfType<FocusInteractionElement>(true))
                {
                    try
                    {
                        if (fie == null) continue;
                        Il2Cpp.ReadInteractionDataBase rd = null;
                        try { rd = fie.readInteractionData; } catch { }
                        if (rd == null) continue; // not a proven reading interaction

                        var scene = "unknown";
                        try { scene = fie.gameObject.scene.name; } catch { }
                        if (!IsGameplayScene(scene)) continue;

                        var segs = new List<string> { fie.gameObject.name };
                        var t = fie.transform.parent;
                        for (int d = 0; d < 24 && t != null; d++, t = t.parent) segs.Insert(0, t.name);
                        string loreRoot = null, poiGroup = null;
                        for (int i = 0; i < segs.Count; i++)
                        {
                            if (segs[i].EndsWith("_Lore", StringComparison.OrdinalIgnoreCase))
                            {
                                loreRoot = segs[i];
                                if (i + 1 < segs.Count - 1) poiGroup = segs[i + 1];
                                else if (i + 1 == segs.Count - 1) poiGroup = "<root>";
                                break;
                            }
                        }
                        if (loreRoot == null) continue;

                        var path = string.Join("/", segs);

                        // v0.3.15: FIE member position = visual anchor (parent/self renderer
                        // bounds center), falling back to the interaction transform.
                        TryGetFieVisualAnchor(fie, poiGroup, out var pos, out var posSource);

                        ulong fpid = 0;
                        try
                        {
                            var container = fie.lootProviderDataContainer;
                            if (container != null) fpid = container.UniquePersistentID;
                        }
                        catch { }
                        var fcovered = fpid != 0 &&
                                       _classification.TryGetValue(fpid, out var fc) && fc.Key;
                        var fcoveredId = fcovered ? fpid : 0;
                        var fcoveredRem = fcovered && collectibleRemaining.TryGetValue(fpid, out var r2) && r2;

                        AddMember(new LoreMember
                        {
                            IsProvider = false,
                            Go = fie.gameObject,
                            Scene = scene,
                            Path = path,
                            Pos = pos,
                            PositionSource = posSource,
                            IsPersistent = false,
                            InteractionCount = -1,
                            ReadDataName = rd.name,
                            Key = MemberKey(scene, path),
                            CoveredByCollectible = fcovered,
                            CollectibleId = fcoveredId,
                            CollectibleRemaining = fcoveredRem,
                            ReadDataPointer = rd.Pointer,
                            InteractionPos = fie.transform.position,
                        }, loreRoot, poiGroup);
                    }
                    catch { }
                }
            }
            catch (Exception e) { MelonLogger.Warning("lore focus-element scan failed: " + e.Message); }

            // v0.3.18: resolve deferred focus reads using the FIE scan we just did.
            // Conservative rule kept: only a UNIQUE pointer+distance match marks read.
            if (_pendingFocusReads.Count > 0)
            {
                var fieMembers = new List<LoreMember>();
                foreach (var g in groups.Values)
                    foreach (var m in g.Members)
                        if (!m.IsProvider) fieMembers.Add(m);

                foreach (var p in _pendingFocusReads)
                {
                    LoreMember match = null;
                    int matchCount = 0;
                    foreach (var m in fieMembers)
                    {
                        if (m.ReadDataPointer != p.ReadDataPointer) continue;
                        if ((m.InteractionPos - p.PlayerPos).sqrMagnitude > NearbyRadius * NearbyRadius) continue;
                        matchCount++;
                        match = m;
                    }
                    if (matchCount == 1 && match != null)
                    {
                        _sessionReadKeys.Add(match.Key);
                        MelonLogger.Msg("SESSION READ (focus, deferred): " + match.Key);
                    }
                    else
                    {
                        MelonLogger.Msg("SESSION READ deferred-skip (focus matches=" + matchCount + ") — left visible");
                    }
                }
                _pendingFocusReads.Clear();
            }

            // v0.3.17: covered (collectible-backed) members are merged into their lore
            // group's marker; the standalone collectible marker is suppressed for them.
            _collectibleCoveredByLore.Clear();

            foreach (var g in groups.Values)
            {
                // RawLoreCategory: based on RAW members (before collectible dedup) —
                // GabRob rawMembers=2 stays NarrativePoi even though both are covered.
                g.RawCategory = g.Members.Count >= 2 ? LoreCategory.NarrativePoi : LoreCategory.WorldInfo;

                var effective = g.Members.Where(m => !m.CoveredByCollectible).ToList();
                g.Category = effective.Count >= 2 ? LoreCategory.NarrativePoi : LoreCategory.WorldInfo;

                g.LoreUnread = effective.Count(m => !MemberIsRead(m));
                g.CoveredRemaining = g.Members.Count(m => m.CoveredByCollectible && m.CollectibleRemaining);
                g.GroupPending = g.LoreUnread > 0 || g.CoveredRemaining > 0;

                foreach (var m in g.Members)
                    if (m.CoveredByCollectible)
                        _collectibleCoveredByLore[m.CollectibleId] = (g.Identity, g.RawCategory);

                // anchor = real member position nearest the centroid (v0.3.12).
                // GroupTransform logic nodes are layout containers stacked at one spot —
                // captured for diagnostics only, never used for marker positioning.
                if (g.Members.Count == 0)
                {
                    g.AnchorSource = "anchorFailed";
                    continue;
                }
                var centroid = Vector3.zero;
                foreach (var m in g.Members) centroid += m.Pos;
                centroid /= g.Members.Count;

                LoreMember best = null;
                var bestD = float.MaxValue;
                foreach (var m in g.Members)
                {
                    var d = (m.Pos - centroid).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = m; }
                }
                g.Anchor = best.Pos;
                g.AnchorSource = "MemberNearestCentroid";
                g.AnchorMember = best.Path + " (" + (best.IsProvider ? "Provider" : "FocusElement") + ")";
            }

            _loreGroups.AddRange(groups.Values);
            _loreGroups.Sort((a, b) => string.Compare(a.Identity, b.Identity, StringComparison.OrdinalIgnoreCase));
        }

        private bool LoreGroupVisible(LoreGroup g)
        {
            // v0.3.18: single source of truth — GroupPending already covers effective
            // lore unread AND covered-collectible remaining (v0.3.17 missed-jump fix).
            return g != null && g.GroupPending;
        }

        private static string LoreGlyph(LoreCategory c)
        {
            return c == LoreCategory.NarrativePoi ? "?" : "i";
        }

        private bool PrefShowCollectible()
        {
            try { return _prefShowCollectible == null || _prefShowCollectible.Value; }
            catch { return true; }
        }

        private bool PrefVisible(LoreCategory c)
        {
            try
            {
                return c switch
                {
                    LoreCategory.NarrativePoi => _prefShowNarrative == null || _prefShowNarrative.Value,
                    LoreCategory.WorldInfo => _prefShowWorldInfo == null || _prefShowWorldInfo.Value,
                    _ => _prefShowCollectible == null || _prefShowCollectible.Value,
                };
            }
            catch { return true; }
        }

        // ------------------------------------------------------- read event tracking

        private void SubscribeReadEvents()
        {
            try
            {
                _managedStartReading = OnInitReading;
                _managedStopReading = OnStopReading;
                _managedEnterProvider = OnEnterReadProvider;
                _il2cppStartReading = _managedStartReading;
                _il2cppStopReading = _managedStopReading;
                _il2cppEnterProvider = _managedEnterProvider;
                GameEventManager.add_OnStartReading(_il2cppStartReading);
                GameEventManager.add_OnStopReading(_il2cppStopReading);
                GameEventManager.add_OnInteractEnterWithReadInteractionProvider(_il2cppEnterProvider);
                _readEventSubscribed = true;
                MelonLogger.Msg("SUBSCRIBED: GameEventManager read events (start/stop/enter-provider)");
            }
            catch (Exception e)
            {
                _readEventSubscribed = false;
                MelonLogger.Warning("read event subscribe failed: " + e.Message);
            }
        }

        private void UnsubscribeReadEvents()
        {
            try
            {
                if (!_readEventSubscribed) return;
                if (_il2cppStartReading != null) GameEventManager.remove_OnStartReading(_il2cppStartReading);
                if (_il2cppStopReading != null) GameEventManager.remove_OnStopReading(_il2cppStopReading);
                if (_il2cppEnterProvider != null) GameEventManager.remove_OnInteractEnterWithReadInteractionProvider(_il2cppEnterProvider);
            }
            catch { }
            _readEventSubscribed = false;
        }

        private System.Action<Il2Cpp.ReadInteractionDataBase> _managedStartReading;
        private System.Action<Il2Cpp.ReadInteractionDataBase> _managedStopReading;
        private System.Action<Il2Cpp.ReadInteractionProvider> _managedEnterProvider;
        private string _pendingProviderKey; // provider member key awaiting its READ STOP

        private void OnInitReading(Il2Cpp.ReadInteractionDataBase data)
        {
            try
            {
                _readingNow = true;
                _lastReadEventAt = Time.unscaledTime;
                _currentReadData = data;
                _currentReadDataName = data != null ? data.name : null;
                _currentReadWrapperType = data != null ? data.GetType().Name : null;
                try { _currentReadIl2cppType = data != null ? data.GetIl2CppType().Name : null; }
                catch { _currentReadIl2cppType = null; }
                _currentReadLayout = null;
                _currentReadHasDialog = false;
                try
                {
                    var concrete = data.TryCast<Il2Cpp.ReadInteractionData>();
                    if (concrete != null)
                    {
                        _currentReadLayout = concrete.layoutName;
                        _currentReadHasDialog = concrete.dialogPath != null;
                    }
                }
                catch { }
                MelonLogger.Msg("READ START: \"" + (_currentReadDataName ?? "?") +
                                "\" wrapper=" + (_currentReadWrapperType ?? "?") +
                                " il2cpp=" + (_currentReadIl2cppType ?? "?") +
                                (_currentReadLayout != null ? " layout=" + _currentReadLayout : "") +
                                " hasDialog=" + _currentReadHasDialog);
            }
            catch (Exception e) { MelonLogger.Warning("read-start handler error: " + e.Message); }
        }

        private void OnStopReading(Il2Cpp.ReadInteractionDataBase data)
        {
            try
            {
                _readingNow = false;
                _lastReadEventAt = Time.unscaledTime;
                MarkModelDirty(); // v0.3.18: refresh only on next marker surface open
                string n = data != null ? data.name : "?";
                string t = null;
                try { t = data != null ? data.GetIl2CppType().Name : null; } catch { }

                // session-read marking (memory only)
                if (_pendingProviderKey != null)
                {
                    _sessionReadKeys.Add(_pendingProviderKey);
                    MelonLogger.Msg("SESSION READ (provider): " + _pendingProviderKey);
                    _pendingProviderKey = null;
                }
                else if (data != null)
                {
                    // v0.3.18 perf: no FIE scan here. Record a lightweight pending read;
                    // correlation is resolved inside the next BuildLoreGroups FIE pass.
                    try
                    {
                        _pendingFocusReads.Add(new PendingFocusRead
                        {
                            ReadDataPointer = data.Pointer,
                            PlayerPos = GetPlayerPos(out _),
                        });
                    }
                    catch { }
                    MarkModelDirty();
                }

                MelonLogger.Msg("READ STOP: " + n + (t != null ? " il2cpp=" + t : ""));
            }
            catch (Exception e) { MelonLogger.Warning("read-stop handler error: " + e.Message); }
        }

        private void OnEnterReadProvider(Il2Cpp.ReadInteractionProvider provider)
        {
            try
            {
                if (provider == null) return;
                string path = "?";
                try
                {
                    var sb = new System.Text.StringBuilder(provider.gameObject.name);
                    var t = provider.transform.parent;
                    for (int d = 0; d < 6 && t != null; d++, t = t.parent) sb.Insert(0, t.name + "/");
                    path = sb.ToString();
                }
                catch { }
                _currentProviderDesc = provider.gameObject.name + " | path=" + path +
                                      " | pos=" + Fmt(provider.transform.position);
                try
                {
                    var scene = provider.gameObject.scene.name;
                    _pendingProviderKey = MemberKey(scene, path);
                }
                catch { }
                MelonLogger.Msg("READ PROVIDER ENTER: " + _currentProviderDesc);
            }
            catch (Exception e) { MelonLogger.Warning("read-provider handler error: " + e.Message); }
        }

        
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // new zone / menu scene: allow immediate (re)subscription checks and
            // schedule a progress refresh (streaming composition changed).
            _nextSubAttemptAt = 0f;
            if (!_lootSubscribed) SubscribeLoot();
            MarkModelDirty();
        }

        public override void OnUpdate()
        {
            HandleHotkey();
            TryEnsureStorySubscription();
            TickEagleEye();
            if (_eagleWasShown) DriveMarkers();
            TickSurvey();
            TickToast();
        }

        // ---------------------------------------------------------------- hotkey

        private void HandleHotkey()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb == null || kb.f8Key == null || !kb.f8Key.wasPressedThisFrame) return;
                if (_snapshotBusy) return;
                _snapshotBusy = true;
                try
                {
                    DumpSnapshot();
                    ShowSnapshotToast();
                }
                catch (Exception e) { MelonLogger.Error("snapshot failed: " + e); }
                finally { _snapshotBusy = false; }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("hotkey check failed: " + e.Message);
            }
        }

        /// <summary>Brief bottom-center "F8 快照已生成" toast (no sound, no animation).</summary>
        private void ShowSnapshotToast()
        {
            try
            {
                TeardownToast();
                _toastCanvas = new GameObject("CairnStoryTracker.ToastCanvas");
                var canvas = _toastCanvas.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 600;
                var scaler = _toastCanvas.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                var textGo = new GameObject("CairnStoryTracker.Toast");
                textGo.AddComponent<RectTransform>();
                textGo.transform.SetParent(_toastCanvas.transform, false);
                var rt = (RectTransform)textGo.transform;
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, 40f);
                rt.sizeDelta = new Vector2(400f, 60f);

                var tmp = textGo.AddComponent<TextMeshProUGUI>();
                tmp.font = Label.GameFont;
                tmp.fontSize = 28f;
                tmp.color = new Color(1f, 1f, 1f, 0.9f);
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.enableWordWrapping = false;
                tmp.raycastTarget = false;
                tmp.text = "F8 快照已生成";
                _toastText = textGo;
                _toastUntil = Time.unscaledTime + 2f;
            }
            catch (Exception e)
            {
                TeardownToast();
                MelonLogger.Warning("snapshot toast failed (harmless): " + e.Message);
            }
        }

        private void TickToast()
        {
            if (_toastUntil < 0f || Time.unscaledTime < _toastUntil) return;
            TeardownToast();
        }

        private void TeardownToast()
        {
            try { if (_toastText != null) UnityEngine.Object.Destroy(_toastText); } catch { }
            try { if (_toastCanvas != null) UnityEngine.Object.Destroy(_toastCanvas); } catch { }
            _toastText = null;
            _toastCanvas = null;
            _toastUntil = -1f;
        }

        // ------------------------------------------------------------ story events (V0 diagnostics only)

        private void TryEnsureStorySubscription()
        {
            if (Time.unscaledTime < _nextSubAttemptAt) return;
            _nextSubAttemptAt = Time.unscaledTime + SubRetrySeconds;

            try
            {
                var mgr = GetStoryEventManager(out var how);
                if (mgr == null)
                {
                    if (!_loggedWaiting)
                    {
                        MelonLogger.Msg("StoryEventManager not found yet (waiting); lookup=" + how);
                        _loggedWaiting = true;
                    }
                    return;
                }

                if (_subscribedManager != null && mgr.Pointer == _subscribedManager.Pointer) return;

                if (_subscribedManager != null)
                {
                    try { if (_il2cppStoryEnd != null) _subscribedManager.remove_OnStoryEventEnd(_il2cppStoryEnd); }
                    catch (Exception e) { MelonLogger.Warning("unsubscribing old StoryEventManager failed: " + e.Message); }
                    MelonLogger.Msg("StoryEventManager instance changed; resubscribing.");
                }

                _il2cppStoryEnd = _managedStoryEnd; // implicit System.Action -> Il2CppSystem.Action
                mgr.add_OnStoryEventEnd(_il2cppStoryEnd);
                _subscribedManager = mgr;
                _loggedWaiting = false;
                MelonLogger.Msg("SUBSCRIBED: StoryEventManager.OnStoryEventEnd (lookup=" + how + ")");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("story subscription check failed: " + e.Message);
            }
        }

        private StoryEventManager GetStoryEventManager(out string how)
        {
            how = "MoSingleton.Instance";
            try
            {
                var i = StoryEventManager.Instance;
                if (i != null) return i;
            }
            catch (Exception e)
            {
                how = "MoSingleton.Instance threw: " + e.GetType().Name;
            }

            how += " -> FindObjectOfType";
            try { return UnityEngine.Object.FindObjectOfType<StoryEventManager>(); }
            catch { return null; }
        }

        private void OnStoryEventEnd(Il2Cpp.StoryEventSensor sensor)
        {
            try
            {
                string label = SensorLabel(sensor, out _);
                var ms = ManagerState(sensor);
                MelonLogger.Msg("STORY COMPLETED: " + label +
                                " | sensor(done=" + Safe(() => sensor.Done, false) +
                                " triggered=" + Safe(() => sensor.Triggered, false) +
                                " prereq=" + Safe(() => sensor.PrerequisitesMet, false) + ")" +
                                (ms.Ok ? " | mgr(done=" + ms.Done + " trigger=" + ms.Triggered + " reg=" + ms.Registered + ")" : " | mgr=unknown"));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("story-end handler error: " + e.Message);
            }
        }

        // ------------------------------------------------------------ loot events

        private void SubscribeLoot()
        {
            try
            {
                ItemLocations.OnLooted -= OnItemLooted; // no-op if not subscribed; guards double-add
                ItemLocations.OnLooted += OnItemLooted;
                _lootSubscribed = true;
                MelonLogger.Msg("SUBSCRIBED: CairnAPI.ItemLocations.OnLooted");
            }
            catch (Exception e)
            {
                _lootSubscribed = false;
                MelonLogger.Warning("ItemLocations.OnLooted subscribe failed (will retry on scene load): " + e.Message);
            }
        }

        private void OnItemLooted(ulong locationId, int itemIndex, Il2Cpp.InventoryItemStringIdEnum itemId)
        {
            try
            {
                bool tracked = _classification.TryGetValue(locationId, out var cls) && cls.Key;
                string reason = _classification.TryGetValue(locationId, out var c2) ? c2.Value : "unknown";
                MelonLogger.Msg("ITEM LOOTED: " + itemId + " @ location " + locationId +
                                " (index " + itemIndex + ")" + (tracked ? " [tracked:" + reason + "]" : ""));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("looted handler error: " + e.Message);
            }
            MarkModelDirty();
        }

        // ------------------------------------------------------- progress (V1 core)

        private void MarkModelDirty()
        {
            _modelDirty = true;
        }

        /// <summary>
        /// v0.3.18 perf: events (read stop / loot / scene load) only flag the model
        /// dirty; the full scans run once, here, when the player actually opens a
        /// marker surface (L1 / eagle map) or presses F8.
        /// </summary>
        private void EnsureModelFresh(string reason)
        {
            if (!_modelDirty) return;
            _modelDirty = false;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            RebuildProgress();
            long progressMs = sw.ElapsedMilliseconds;
            BuildLoreGroups();
            long loreMs = sw.ElapsedMilliseconds - progressMs;
            MelonLogger.Msg("MODEL REFRESH reason=" + reason +
                            " progressMs=" + progressMs + " loreMs=" + loreMs +
                            " totalMs=" + sw.ElapsedMilliseconds);
        }

        private void RebuildProgress()
        {
            _zones.Clear();
            _classification.Clear();
            _eligible.Clear();
            _markerReason.Clear();
            _trackedTotal = 0;
            _trackedDone = 0;

            try
            {
                _currentZone = null;
                try
                {
                    var sm = StreamingManager.Instance;
                    if (sm != null) _currentZone = sm.currentZone;
                }
                catch { }

                var byId = new Dictionary<ulong, Il2Cpp.LootProvider>();
                try
                {
                    foreach (var p in UnityEngine.Object.FindObjectsOfType<Il2Cpp.LootProvider>())
                        try { byId[p.UniquePersistentID] = p; } catch { }
                }
                catch { }

                var groups = new Dictionary<string, ZoneProgress>();
                var locs = ItemLocations.Enumerate();

                foreach (var loc in locs)
                {
                    Il2Cpp.LootProvider lp = null;
                    byId.TryGetValue(loc.Id, out lp);

                    var cls = Classify(loc, lp);
                    _classification[loc.Id] = cls;
                    if (!cls.Key) continue;

                    _trackedTotal++;
                    bool emptied = false;
                    try { emptied = lp != null && lp.StocksEmpty; } catch { }
                    if (emptied) _trackedDone++;

                    var scene = Safe(() => loc.SceneName, null) ?? "unknown";
                    var key = GroupKey(scene);

                    if (!groups.TryGetValue(key, out var zp))
                    {
                        zp = new ZoneProgress { Key = key, Display = ResolveZoneDisplay(key, scene) };
                        groups[key] = zp;
                    }
                    zp.Total++;
                    if (emptied) zp.Done++;
                    zp.Scenes.Add(scene);

                    // V2: tracked + not yet emptied => eligible for a map "?" marker.
                    // Same classification source as the zone X/Y above (consistency rule).
                    if (emptied)
                    {
                        _markerReason[loc.Id] = "notRemaining";
                    }
                    else
                    {
                        _markerReason[loc.Id] = "remaining";
                        _eligible.Add(new EligibleSpot { Id = loc.Id, Pos = loc.Position, Scene = scene });
                    }
                }

                _zones.AddRange(groups.Values);
                foreach (var zp in _zones)
                {
                    zp.Current = zp.Scenes.Any(s => IsCurrentZoneScene(s));
                }
                _zones.Sort((a, b) =>
                {
                    if (a.Current != b.Current) return a.Current ? -1 : 1;
                    return string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception e)
            {
                MelonLogger.Warning("progress rebuild failed: " + e.Message);
            }
        }

        private bool IsCurrentZoneScene(string sceneName)
        {
            if (_currentZone == null || string.IsNullOrEmpty(sceneName)) return false;
            // F8 runtime evidence: ContainsSceneNamed over-matches (it also accepted
            // neighbour zones while streaming). The streaming zone's own name
            // ("02_Crag") is exactly our group key format, so match on it directly.
            try
            {
                var zn = _currentZone.name;
                if (!string.IsNullOrEmpty(zn))
                    return string.Equals(GroupKey(sceneName), zn, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return false;
        }

        private KeyValuePair<bool, string> Classify(CairnAPI.ItemLocation loc, Il2Cpp.LootProvider lp)
        {
            try
            {
                if (lp != null && lp.source == Il2Cpp.LootProviderSource.ReadInteraction)
                    return new KeyValuePair<bool, string>(true, "ReadInteraction");
            }
            catch { }

            try
            {
                var items = loc.Items;
                if (items != null)
                {
                    bool readable = false;
                    foreach (var e in items)
                    {
                        string name = null;
                        try { name = e.ToString(); } catch { }

                        if (!string.IsNullOrEmpty(name))
                        {
                            foreach (var fam in Families)
                            {
                                if (name.Contains(fam.Key))
                                    return new KeyValuePair<bool, string>(true, fam.Value);
                            }
                        }

                        if (!readable)
                        {
                            try
                            {
                                var item = GetItem(e);
                                if (item != null && item.TryCast<Il2Cpp.ReadableItem>() != null)
                                    readable = true;
                            }
                            catch { }
                        }
                    }
                    if (readable) return new KeyValuePair<bool, string>(true, "ReadableItem");
                }
            }
            catch { }

            return new KeyValuePair<bool, string>(false, "OrdinaryResource");
        }

        private Il2Cpp.InventoryItemsLibrary _library;

        private Il2Cpp.InventoryItem GetItem(Il2Cpp.InventoryItemStringIdEnum e)
        {
            if (_library == null)
            {
                var im = InventoryManager.Instance;
                if (im == null) return null;
                _library = im.library;
                if (_library == null) return null;
            }
            return _library.GetItem(new Il2Cpp.InventoryItemStringId((int)e));
        }

        // ------------------------------------------------------------ V2 map markers

        /// <summary>
        /// Rebuild eagle-eye "?" markers from _eligible (tracked && remaining).
        /// Everything we create is inactive or non-interactive (see class comment):
        /// the native fast-travel list can never enumerate our objects.
        /// </summary>
        private void SyncMarkers()
        {
            TeardownMarkers();

            if (_pinListUi == null && Time.unscaledTime >= _nextPinListLookupAt)
            {
                _nextPinListLookupAt = Time.unscaledTime + 3f;
                try { _pinListUi = UnityEngine.Object.FindObjectOfType<FreeRoamEagleEyeWarpPointListUI>(); } catch { }
            }

            if (_pinListUi == null)
            {
                foreach (var e in _eligible) _markerReason[e.Id] = "notLoaded";
                _markersSynced = true;
                _lastSyncHadList = false;
                return;
            }
            _lastSyncHadList = true;

            FreeRoamWarpPointWidget example = null;
            Transform exampleParent = null;
            try
            {
                example = _pinListUi.warpPointWorldPinExample;
                if (example != null) exampleParent = example.transform.parent;
            }
            catch { }

            if (example == null)
            {
                foreach (var e in _eligible) _markerReason[e.Id] = "createFailed";
                _markerFailed = _eligible.Count;
                _markersSynced = true;
                return;
            }

            // baseline native counts before we create anything (F8 pollution check)
            _nativeWarpCountBefore = SafeCountNative<FreeRoamWarpPoint>();
            _nativeWidgetCountBefore = SafeCountNative<FreeRoamWarpPointWidget>();

            void AddMarker(ulong id, string groupKey, Vector3 pos, string glyph)
            {
                GameObject driverGo = null;
                GameObject widgetGo = null;
                GameObject visualGo = null;
                try
                {
                    // 1) hidden driver: inactive from birth, holds the world position only
                    driverGo = new GameObject("CairnStoryTracker.MarkerDriver");
                    driverGo.SetActive(false); // AddComponent below stays dormant (no Awake/OnEnable)
                    var wp = driverGo.AddComponent<FreeRoamWarpPoint>();
                    wp.enabled = false;        // extra belt-and-braces; never registered anywhere
                    driverGo.transform.position = pos;

                    // 2) hidden driver widget: real prefab clone (serialized refs intact),
                    //    parked inactive under the pins layer; Update() is invoked manually.
                    var widget = UnityEngine.Object.Instantiate(example);
                    widgetGo = widget.gameObject;
                    if (exampleParent != null) widget.transform.SetParent(exampleParent, false);
                    widgetGo.SetActive(false);
                    widget.Set(false, wp);

                    // 3) pure visual glyph — active, non-interactive, same coordinate space
                    visualGo = new GameObject("CairnStoryTracker.MarkerVisual");
                    visualGo.AddComponent<RectTransform>();
                    visualGo.transform.SetParent(exampleParent, false);
                    var vrt = (RectTransform)visualGo.transform;
                    vrt.anchorMin = new Vector2(0.5f, 0.5f);
                    vrt.anchorMax = new Vector2(0.5f, 0.5f);
                    vrt.pivot = new Vector2(0.5f, 0.5f);
                    vrt.sizeDelta = new Vector2(90f, 90f);
                    var tmp = visualGo.AddComponent<TextMeshProUGUI>();
                    tmp.font = Label.GameFont;
                    tmp.fontSize = 44f;
                    tmp.color = Color.white;
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.enableWordWrapping = false;
                    tmp.raycastTarget = false; // never blocks map input; no Button/Selectable/EventTrigger
                    tmp.text = glyph;

                    _markers.Add(new MapMarker
                    {
                        Id = id,
                        GroupKey = groupKey,
                        Glyph = glyph,
                        DriverGo = driverGo,
                        Point = wp,
                        WidgetGo = widgetGo,
                        Widget = widget,
                        VisualGo = visualGo,
                    });
                    _markerSpawned++;
                }
                catch (Exception ex)
                {
                    _markerFailed++;
                    if (id != 0) _markerReason[id] = "createFailed";
                    try { if (visualGo != null) UnityEngine.Object.Destroy(visualGo); } catch { }
                    try { if (widgetGo != null) UnityEngine.Object.Destroy(widgetGo); } catch { }
                    try { if (driverGo != null) UnityEngine.Object.Destroy(driverGo); } catch { }
                    MelonLogger.Warning("marker spawn failed: " + ex.Message);
                }
            }

            // collectibles (existing behavior, glyph ◇)
            if (PrefShowCollectible())
                foreach (var e in _eligible)
                    if (!_collectibleCoveredByLore.ContainsKey(e.Id)) // collectible-backed: merged into lore marker
                        AddMarker(e.Id, null, e.Pos, "*");

            // lore groups: one marker per visible group (? narrative / i world info)
            if (PrefVisible(LoreCategory.NarrativePoi))
                foreach (var g in _loreGroups)
                    if (g.RawCategory == LoreCategory.NarrativePoi && LoreGroupVisible(g))
                        AddMarker(0, g.Identity, g.Anchor, "?");

            if (PrefVisible(LoreCategory.WorldInfo))
                foreach (var g in _loreGroups)
                    if (g.RawCategory == LoreCategory.WorldInfo && LoreGroupVisible(g))
                        AddMarker(0, g.Identity, g.Anchor, "i");

            _nativeWarpCountAfter = SafeCountNative<FreeRoamWarpPoint>();
            _nativeWidgetCountAfter = SafeCountNative<FreeRoamWarpPointWidget>();
            _markersSynced = true;
        }

        /// <summary>Per-frame: run the hidden driver widget's native projection and copy the result to the visual "?".</summary>
        private void DriveMarkers()
        {
            for (int i = 0; i < _markers.Count; i++)
            {
                var m = _markers[i];
                try
                {
                    if (m.Widget != null) m.Widget.Update(); // direct call works while the GO is inactive
                    if (m.VisualGo != null && m.Widget != null)
                        m.VisualGo.transform.position = m.Widget.transform.position;
                }
                catch { /* a dead driver/-widget is fixed by the next resync */ }
            }
        }

        private static int SafeCountNative<T>() where T : UnityEngine.Object
        {
            try { return UnityEngine.Object.FindObjectsOfType<T>().Length; }
            catch { return -1; }
        }

        private void TeardownMarkers()
        {
            foreach (var m in _markers)
            {
                try { if (m.VisualGo != null) UnityEngine.Object.Destroy(m.VisualGo); } catch { }
                try { if (m.WidgetGo != null) UnityEngine.Object.Destroy(m.WidgetGo); } catch { }
                try { if (m.DriverGo != null) UnityEngine.Object.Destroy(m.DriverGo); } catch { }
            }
            _markers.Clear();
            _markerSpawned = 0;
            _markerFailed = 0;
            _markersSynced = false;
        }

        // ------------------------------------------------------- V2 survey markers

        private void TickSurvey()
        {
            // v0.3.4: the L1 survey belongs to the Eagle camera family (confirmed at
            // runtime: type=WalkingEagleEyePlusView, isEagle=True). EagleEyeUI.Shown is
            // true for the WHOLE eagle-eye experience (survey included), so the
            // fast-travel sub-window must be excluded via EagleEyeUI.isInFreeRoamView
            // (exact interop property name; the 2D warp map lives in that sub-window).
            bool open = false;
            try
            {
                var cm = CameraManager.Instance;
                if (cm != null)
                {
                    var t = cm.currentCameraType;
                    open = CairnCamera.IsEagleEyeViewAny(t) && !FreeRoamWindowShown();
                }
            }
            catch { }

            if (open && !_surveyOpen)
            {
                EnsureModelFresh("survey-open");
                EnsureSurveyCanvas();
                SpawnSurveyMarkers();
                UpdateSurveyMarkers();
            }
            else if (!open && _surveyOpen)
            {
                TeardownSurvey();
            }

            _surveyOpen = open;
            if (open) UpdateSurveyMarkers(); // camera moves: per-frame screen positions
        }

        private void RespawnSurvey()
        {
            if (!_surveyOpen) return;
            EnsureModelFresh("survey-respawn");
            EnsureSurveyCanvas();
            SpawnSurveyMarkers();
            UpdateSurveyMarkers();
        }

        private void EnsureSurveyCanvas()
        {
            if (_surveyCanvas != null) return;
            try
            {
                _surveyCanvas = new GameObject("CairnStoryTracker.SurveyCanvas");
                _surveyCanvas.AddComponent<Canvas>();
                _surveyCanvasComp = _surveyCanvas.GetComponent<Canvas>();
                _surveyCanvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
                _surveyCanvasComp.sortingOrder = 450;
                var scaler = _surveyCanvas.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
            }
            catch (Exception e)
            {
                TeardownSurvey();
                _surveyFailed = _eligible.Count;
                MelonLogger.Warning("survey canvas creation failed: " + e.Message);
            }
        }

        private void SpawnSurveyMarkers()
        {
            foreach (var m in _surveyMarkers)
                try { if (m.Go != null) UnityEngine.Object.Destroy(m.Go); } catch { }
            _surveyMarkers.Clear();
            _surveyFailed = 0;

            if (_surveyCanvas == null)
            {
                EnsureSurveyCanvas();
                if (_surveyCanvas == null)
                {
                    _surveyFailed = _eligible.Count + _loreGroups.Count;
                    foreach (var e in _eligible) _surveyReason[e.Id] = "createFailed";
                    return;
                }
            }

            // sources: collectibles (◇) + visible lore groups (? narrative / i world info)
            void AddMarker(ulong id, string groupKey, Vector3 pos, string glyph)
            {
                try
                {
                    var go = new GameObject("CairnStoryTracker.SurveyQ");
                    go.AddComponent<RectTransform>();
                    go.transform.SetParent(_surveyCanvas.transform, false);
                    var rt = (RectTransform)go.transform;
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.sizeDelta = new Vector2(70f, 70f);
                    var tmp = go.AddComponent<TextMeshProUGUI>();
                    tmp.font = Label.GameFont;
                    tmp.fontSize = 40f;
                    tmp.color = Color.white;
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.enableWordWrapping = false;
                    tmp.raycastTarget = false; // pure display: no Button/Selectable/EventTrigger
                    tmp.text = glyph;
                    go.SetActive(false); // shown only when projected on-screen & in front
                    _surveyMarkers.Add(new SurveyMarker { Id = id, GroupKey = groupKey, WorldPos = pos, Go = go, Rt = rt, Glyph = glyph });
                }
                catch (Exception ex)
                {
                    _surveyFailed++;
                    if (id != 0) _surveyReason[id] = "createFailed";
                    MelonLogger.Warning("survey marker spawn failed: " + ex.Message);
                }
            }

            if (PrefShowCollectible())
                foreach (var e in _eligible)
                    if (!_collectibleCoveredByLore.ContainsKey(e.Id)) // collectible-backed: merged into lore marker
                        AddMarker(e.Id, null, e.Pos, "*");

            if (PrefVisible(LoreCategory.NarrativePoi))
                foreach (var g in _loreGroups)
                    if (g.RawCategory == LoreCategory.NarrativePoi && LoreGroupVisible(g))
                        AddMarker(0, g.Identity, g.Anchor, "?");

            if (PrefVisible(LoreCategory.WorldInfo))
                foreach (var g in _loreGroups)
                    if (g.RawCategory == LoreCategory.WorldInfo && LoreGroupVisible(g))
                        AddMarker(0, g.Identity, g.Anchor, "i");
        }

        private void UpdateSurveyMarkers()
        {
            if (!_surveyOpen || _surveyCanvas == null) return;
            Camera cam = null;
            try { cam = Camera.main; } catch { }

            _surveyVisible = 0;
            _surveyBehind = 0;
            _surveyOffscreen = 0;
            int failed = 0;

            foreach (var m in _surveyMarkers)
            {
                try
                {
                    if (cam == null)
                    {
                        if (m.Go != null) m.Go.SetActive(false);
                        failed++;
                        continue;
                    }

                    var sp = cam.WorldToScreenPoint(m.WorldPos);
                    if (sp.z <= 0f)
                    {
                        if (m.Go != null) m.Go.SetActive(false);
                        _surveyBehind++;
                        if (m.Id != 0) _surveyReason[m.Id] = "behind";
                        continue;
                    }

                    if (sp.x < 0f || sp.y < 0f || sp.x > UnityEngine.Screen.width || sp.y > UnityEngine.Screen.height)
                    {
                        if (m.Go != null) m.Go.SetActive(false);
                        _surveyOffscreen++;
                        if (m.Id != 0) _surveyReason[m.Id] = "offscreen";
                        continue;
                    }

                    if (m.Rt != null)
                    {
                        m.Rt.anchoredPosition =
                            (new Vector2(sp.x, sp.y) - new Vector2(UnityEngine.Screen.width * 0.5f, UnityEngine.Screen.height * 0.5f)) *
                            _surveyCanvasComp.scaleFactor;
                    }
                    if (m.Go != null) m.Go.SetActive(true);
                    _surveyVisible++;
                    if (m.Id != 0) _surveyReason[m.Id] = "visible";
                }
                catch
                {
                    // next frame retries; a resync recreates dead markers
                }
            }
            _surveyFailed = failed;
        }

        private void TeardownSurvey()
        {
            foreach (var m in _surveyMarkers)
                try { if (m.Go != null) UnityEngine.Object.Destroy(m.Go); } catch { }
            _surveyMarkers.Clear();
            try { if (_surveyCanvas != null) UnityEngine.Object.Destroy(_surveyCanvas); } catch { }
            _surveyCanvas = null;
            _surveyCanvasComp = null;
            _surveyVisible = 0;
            _surveyBehind = 0;
            _surveyOffscreen = 0;
            _surveyFailed = 0;
        }

        private bool EagleMapShown()
        {
            // _eagleEye is refreshed by TickEagleEye (runs earlier in OnUpdate).
            try { return _eagleEye != null && _eagleEye.Shown; }
            catch { return false; }
        }

        /// <summary>True only while the fast-travel (Free Roam) sub-window of the eagle-eye is open;
        /// false during the plain L1 survey view. Exact interop property: EagleEyeUI.isInFreeRoamView.</summary>
        private bool FreeRoamWindowShown()
        {
            try { return _eagleEye != null && _eagleEye.isInFreeRoamView; }
            catch { return false; }
        }

        // ------------------------------------------------------------------- UI
        // Minimal own layout: OverlayCanvas (CairnAPI helper) + one multiline TMP
        // label with word wrap disabled and an explicit width. The FieldsPanel
        // field grid collapsed to one character per line outside the debug menu,
        // so it is not used for this text.

        private void TearDownPanel()
        {
            try { if (_uiRoot != null) UnityEngine.Object.Destroy(_uiRoot); } catch { }
            try { if (_uiCanvas != null) UnityEngine.Object.Destroy(_uiCanvas); } catch { }
            _uiRoot = null;
            _uiRootRect = null;
            _uiText = null;
            _uiCanvas = null;
        }

        private bool EnsurePanel()
        {
            if (_uiBroken) return false;
            if (_uiText != null) return true;
            try
            {
                // Own overlay canvas; scaler constants mirror CairnAPI's HudPanel.Build.
                _uiCanvas = new GameObject("CairnStoryTracker.Canvas");
                var canvas = _uiCanvas.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 500;
                var scaler = _uiCanvas.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                var root = new GameObject("CairnStoryTracker.Progress");
                root.AddComponent<RectTransform>();
                root.transform.SetParent(_uiCanvas.transform, false);
                var rt = (RectTransform)root.transform;
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(24f, -24f);
                rt.sizeDelta = new Vector2(380f, 44f);

                var backing = root.AddComponent<Image>();
                backing.color = new Color(0f, 0f, 0f, 0.55f);
                backing.raycastTarget = false;

                var textGo = new GameObject("Text");
                textGo.AddComponent<RectTransform>();
                textGo.transform.SetParent(rt, false);
                var trt = (RectTransform)textGo.transform;
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(12f, 8f);
                trt.offsetMax = new Vector2(-12f, -8f);

                _uiText = textGo.AddComponent<TextMeshProUGUI>();
                _uiText.font = Label.GameFont; // game font asset; has CJK glyphs
                _uiText.fontSize = 26f;
                _uiText.color = Color.white;
                _uiText.alignment = TextAlignmentOptions.TopLeft;
                _uiText.enableWordWrapping = false; // never wrap per character
                _uiText.raycastTarget = false;      // never block map input

                _uiRoot = root;
                _uiRootRect = rt;
                return true;
            }
            catch (Exception e)
            {
                TearDownPanel();
                _uiBroken = true;
                MelonLogger.Warning("progress panel creation failed (UI disabled until next eagle-eye open): " + e.Message);
                return false;
            }
        }

        private void UpdatePanel()
        {
            if (_uiBroken || _uiText == null) return;
            try
            {
                string text;
                if (_zones.Count == 0)
                {
                    text = "探索收集  0 / 0";
                }
                else if (_zones.Count == 1)
                {
                    var z = _zones[0];
                    text = z.Display + "\n探索收集  " + z.Done + " / " + z.Total;
                }
                else
                {
                    var sb = new System.Text.StringBuilder("探索收集");
                    foreach (var z in _zones)
                        sb.Append('\n').Append(z.Current ? ">> " : "   ")
                          .Append(z.Display).Append("   ")
                          .Append(z.Done).Append(" / ").Append(z.Total);
                    text = sb.ToString();
                }

                _uiText.text = text;

                int lines = text.Split('\n').Length;
                _uiRootRect.sizeDelta = new Vector2(380f, 12f + 36f * lines);
            }
            catch (Exception e)
            {
                TearDownPanel(); // recreate on next eagle-eye open; not permanently broken
                _uiBroken = true;
                MelonLogger.Warning("progress panel update failed: " + e.Message);
            }
        }

        private void TickEagleEye()
        {
            try
            {
                if (_eagleEye == null)
                {
                    if (Time.unscaledTime < _nextEagleLookupAt) return;
                    _nextEagleLookupAt = Time.unscaledTime + 3f;
                    try { _eagleEye = UnityEngine.Object.FindObjectOfType<EagleEyeUI>(); } catch { }
                    if (_eagleEye == null) return;
                }

                bool shown;
                try { shown = _eagleEye.Shown; }
                catch
                {
                    _eagleEye = null; // destroyed by a scene change; look it up again
                    return;
                }

                if (shown && !_eagleWasShown)
                {
                    _uiBroken = false; // allow one fresh attempt per map open
                    EnsureModelFresh("eagle-open");
                    if (EnsurePanel()) UpdatePanel();
                    SyncMarkers();
                }
                else if (!shown && _eagleWasShown)
                {
                    TearDownPanel();
                    TeardownMarkers();
                }

                // The eagle-eye list UI only becomes findable once the map canvas is
                // active; if it wasn't there on the rising edge, retry while shown.
                if (shown && _markersSynced && !_lastSyncHadList && _eligible.Count > 0 &&
                    Time.unscaledTime >= _nextPinListLookupAt)
                {
                    _nextPinListLookupAt = Time.unscaledTime + 1.5f;
                    _pinListUi = null;
                    SyncMarkers();
                }

                _eagleWasShown = shown;
            }
            catch { }
        }

        // --------------------------------------------------------------- snapshot

        private void DumpSnapshot()
        {
            MelonLogger.Msg("========== F8 SNAPSHOT BEGIN ==========");

            string activeScene = "?";
            try { activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
            string stray = "unknown";
            try
            {
                var z = CairnSceneManager.Instance.currentStrayZone;
                if (z != null && !string.IsNullOrEmpty(z.name)) stray = z.name;
            }
            catch { }
            string streamingZone = "unknown";
            try
            {
                var sm = StreamingManager.Instance;
                if (sm != null && sm.currentZone != null && !string.IsNullOrEmpty(sm.currentZone.name))
                    streamingZone = sm.currentZone.name;
            }
            catch { }
            MelonLogger.Msg("Zone: activeScene=" + activeScene + " | strayZone=" + stray + " | streamingZone=" + streamingZone);

            // Camera diagnostics: identifies which type the L1 survey view actually uses.
            try
            {
                string type = "unknown", current = "null", main = "null";
                bool isObs = false, isEagle = false, isEaglePath = false;
                var cm = CameraManager.Instance;
                if (cm != null)
                {
                    var t = cm.currentCameraType;
                    type = Safe(() => t.ToString(), "unknown");
                    isObs = Safe(() => CairnCamera.IsObservationView(t), false);
                    isEagle = Safe(() => CairnCamera.IsEagleEyeViewAny(t), false);
                    isEaglePath = Safe(() => CairnCamera.IsEagleEyeViewPath(t), false);
                    var cc = cm.CurrentCairnCamera;
                    if (cc != null)
                    {
                        string proxy = "?", uname = "?";
                        try { proxy = cc.GetType().Name; } catch { }
                        try { uname = cc.name; } catch { }
                        current = proxy + "/" + uname;
                    }
                }
                var mc = Safe(() => UnityEngine.Camera.main, null);
                if (mc != null)
                {
                    string mname = "?";
                    try { mname = mc.name; } catch { }
                    main = mname;
                }
                MelonLogger.Msg("Camera: type=" + type + " current=" + current + " main=" + main +
                                " isObservation=" + isObs + " isEagle=" + isEagle + " isEaglePath=" + isEaglePath +
                                " surveyOpen=" + _surveyOpen +
                                " shown=" + EagleMapShown() + " freeRoamView=" + FreeRoamWindowShown());
            }
            catch (Exception e)
            {
                MelonLogger.Msg("Camera: diagnostics failed: " + e.Message);
            }

            EnsureModelFresh("f8"); // refresh classification so F8 always reflects the live world
            if (_eagleWasShown) SyncMarkers(); // and marker state when the map is open
            if (_surveyOpen)
            {
                SpawnSurveyMarkers();
                UpdateSurveyMarkers();
            }
            else
            {
                foreach (var e in _eligible) _surveyReason[e.Id] = "surveyClosed";
            }

            DumpStory();
            DumpItems();
            DumpNearbyNarrative();
            DumpClassification();
            DumpFieSpatial();
            DumpGroupVisualDiagnostic();

            if (_zones.Count > 0)
            {
                var lines = _zones.Select(z => (z.Current ? "[current] " : "") + z.Display + " " + z.Done + "/" + z.Total);
                MelonLogger.Msg("Tracked by zone: " + string.Join(" | ", lines) +
                                " | trackedTotal=" + _trackedDone + "/" + _trackedTotal);
            }
            MelonLogger.Msg("UI: " + (_uiBroken ? "failed last attempt (retries next map open)" : _uiText == null ? "not created yet" : _eagleWasShown ? "visible" : "hidden (eagle-eye closed)"));

            MelonLogger.Msg("========== F8 SNAPSHOT END ==========");
        }

        private void DumpStory()
        {
            MelonLogger.Msg("=== Story Events ===");

            var mgr = GetStoryEventManager(out var how);
            if (mgr == null)
            {
                MelonLogger.Msg("StoryEventManager: NOT FOUND (lookup=" + how + ")");
                return;
            }

            var sensors = mgr.allSensors;
            int n = 0;
            try { n = sensors.Count; }
            catch (Exception e)
            {
                MelonLogger.Msg("allSensors unavailable: " + e.Message);
                return;
            }

            int done = 0, triggered = 0, unfinished = 0, mgrDone = 0, agree = 0, compared = 0, suspicious = 0;

            for (int i = 0; i < n; i++)
            {
                Il2Cpp.StoryEventSensor s = null;
                try { s = sensors[i]; } catch { continue; }
                if (s == null) continue;

                string label = SensorLabel(s, out var sceneName);
                bool susp = IsSuspicious(label);
                if (susp) suspicious++;

                bool d = Safe(() => s.Done, false);
                bool t = Safe(() => s.Triggered, false);
                bool p = Safe(() => s.PrerequisitesMet, false);
                bool r = Safe(() => s.CanTriggerMultipleTimes, false);

                var ms = ManagerState(s);
                Vector3 pos = SensorPos(s);

                MelonLogger.Msg(
                    "[STORY] id=" + label + (susp ? "  [DBG?]" : "") + "\n" +
                    "        done=" + d + " triggered=" + t + " prereq=" + p + " repeat=" + r +
                    " | mgr(done/trig/reg)=" + (ms.Ok ? ms.Done + "/" + ms.Triggered + "/" + ms.Registered : "unknown") + "\n" +
                    "        pos=" + Fmt(pos) + " scene=" + sceneName);

                if (d) done++;
                if (t) triggered++;
                if (!d) unfinished++;
                if (ms.Ok)
                {
                    compared++;
                    if (ms.Done) mgrDone++;
                    if (ms.Done == d) agree++;
                }
            }

            MelonLogger.Msg("Story summary: " + n + " total | sensorDone=" + done + " triggered=" + triggered +
                            " unfinished(sensorDone=false)=" + unfinished + " | mgrDone=" + mgrDone +
                            " agree(sensorDone==mgrDone)=" + agree + "/" + compared +
                            " | suspiciousNames=" + suspicious +
                            "  (story events are diagnostics only; not counted in progress)");

            try
            {
                bool avail = Beats.Available;
                int snap = -1;
                if (avail) snap = Beats.Snapshot().Count;
                MelonLogger.Msg("CairnAPI.Beats check: Available=" + avail + " SnapshotCount=" + snap);
            }
            catch (Exception e)
            {
                MelonLogger.Msg("CairnAPI.Beats check failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        private void DumpItems()
        {
            MelonLogger.Msg("=== Item Locations ===");

            try
            {
                var byId = new Dictionary<ulong, Il2Cpp.LootProvider>();
                int providersInScene = 0;
                try
                {
                    var providers = UnityEngine.Object.FindObjectsOfType<Il2Cpp.LootProvider>();
                    foreach (var p in providers)
                    {
                        try
                        {
                            byId[p.UniquePersistentID] = p;
                            providersInScene++;
                        }
                        catch { }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Msg("LootProvider scene scan failed: " + e.Message);
                }

                var locs = ItemLocations.Enumerate();
                int empty = 0, remaining = 0, unresolved = 0, tracked = 0;

                foreach (var loc in locs)
                {
                    string src = "unknown";
                    string rem = "unknown";
                    int remCount = -1;

                    Il2Cpp.LootProvider lp = null;
                    if (byId.TryGetValue(loc.Id, out lp))
                    {
                        try { src = lp.source.ToString(); } catch { }
                        try
                        {
                            remCount = lp.remainingLoots.Length;
                            rem = (!lp.StocksEmpty).ToString();
                        }
                        catch { }
                    }
                    else
                    {
                        unresolved++;
                    }

                    bool isTracked = false;
                    string reason = "unknown";
                    if (_classification.TryGetValue(loc.Id, out var cls))
                    {
                        isTracked = cls.Key;
                        reason = cls.Value;
                    }

                    string items = "[]";
                    try
                    {
                        if (loc.Items != null)
                            items = string.Join(",", loc.Items.Select(x => x.ToString()));
                    }
                    catch { }

                    bool canLoot = false;
                    try { canLoot = loc.CanLoot; } catch { }

                    string markerTxt = "";
                    if (isTracked)
                    {
                        var mr = _markerReason.TryGetValue(loc.Id, out var r) ? r : "unknown";
                        bool up = _markersSynced && mr == "remaining" &&
                                  _markers.Exists(m => m.Id == loc.Id);
                        markerTxt = " marker=" + (up ? "True" : "False") + " markerReason=" +
                                    (mr == "remaining" && !_eagleWasShown ? mr + "(eagleClosed)" : mr);

                        var sr = _surveyReason.TryGetValue(loc.Id, out var srVal) ? srVal : "unknown";
                        markerTxt += " surveyMarker=" + (_surveyOpen && sr == "visible" ? "True" : "False") +
                                     " surveyReason=" + (sr == "unknown" || !_surveyOpen ? "surveyClosed" : sr);
                    }

                    MelonLogger.Msg(
                        "[ITEM] id=" + loc.Id + " name=" + SafeName(loc) + "\n" +
                        "       scene=" + Safe(() => loc.SceneName, "?") + " source=" + src + " items=[" + items + "]\n" +
                        "       remaining=" + rem + (remCount >= 0 ? "(" + remCount + ")" : "") +
                        " canLoot=" + canLoot + " tracked=" + (isTracked ? "True" : "False") + " reason=" + reason +
                        markerTxt + " pos=" + Fmt(loc.Position));

                    if (rem == "True") remaining++;
                    else if (rem == "False") empty++;
                    if (isTracked) tracked++;
                }

                MelonLogger.Msg("Item summary: total=" + locs.Count + " remaining=" + remaining + " empty=" + empty +
                                " providerUnresolved=" + unresolved + " providersInScene=" + providersInScene +
                                " tracked=" + tracked);

                if (_eagleWasShown)
                {
                    // markers were synced against the live map this snapshot.
                    // Pollution verdict uses ONLY the warp-point delta: the requirement is
                    // "no new native warp points / no fast-travel list entries". Widget
                    // count deltas are informational only — the game itself spawns its own
                    // pins right after the map opens, which raced our baseline (v0.3.2 F8
                    // showed widget 1->2 with warp points flat 47->47; no real pollution).
                    bool warpChanged = _nativeWarpCountBefore >= 0 && _nativeWarpCountAfter >= 0 &&
                                       _nativeWarpCountBefore != _nativeWarpCountAfter;
                    MelonLogger.Msg("Map markers: eligible=" + _eligible.Count +
                                    " spawned=" + _markerSpawned + " failed=" + _markerFailed +
                                    " storyVisualMarkerCount=" + _markers.Count +
                                    " | nativeWarpPointCount=" + _nativeWarpCountAfter +
                                    " (before=" + _nativeWarpCountBefore + ")" +
                                    " nativeChanged=" + (warpChanged ? "True(!)" : "False") +
                                    " | info: nativeWidgetCount=" + _nativeWidgetCountAfter +
                                    " (before=" + _nativeWidgetCountBefore + ")");
                }
                else
                {
                    MelonLogger.Msg("Map markers: eligible=" + _eligible.Count +
                                    " spawned=0 failed=0 (markers only exist while the eagle-eye map is open)");
                }

                MelonLogger.Msg("Survey markers: surveyOpen=" + _surveyOpen +
                                " eligible=" + _eligible.Count +
                                " visible=" + _surveyVisible +
                                " behindCamera=" + _surveyBehind +
                                " offscreen=" + _surveyOffscreen +
                                " failed=" + _surveyFailed +
                                (!_surveyOpen ? " (survey markers only exist in observation view)" : ""));
            }
            catch (Exception e)
            {
                MelonLogger.Error("item snapshot failed: " + e);
            }
        }

        // ------------------------------------------------- nearby narrative diagnostics
        // Read-only probe for "non-loot interactable story objects" the regular F8
        // sections cannot see. FocusInteractionElement is the world component behind
        // readables/notes/maps (carries readInteractionData and/or a LootProvider
        // container; deactivates objects on interact — which is why already-read
        // world objects can vanish from the LootProvider enumerate). Diagnostic only:
        // nothing here feeds tracked/X/Y/markers.

        private const float NearbyRadius = 30f;

        private void DumpNearbyNarrative()
        {
            MelonLogger.Msg("=== Nearby Narrative Candidates ===");

            string posSource;
            Vector3 origin = GetPlayerPos(out posSource);
            bool haveOrigin = posSource != "none";
            MelonLogger.Msg("playerPos=" + Fmt(origin) + " positionSource=" + posSource);
            if (!haveOrigin)
            {
                MelonLogger.Msg("no player position available; nearby block skipped");
                return;
            }

            var rows = new List<(float dist, string line)>();

            // FocusInteractionElement: notes / maps / world readables / interactions.
            // includeInactive=true: already-read world objects get deactivated by the game
            // (honey-cave sample: 0 hits while standing next to the map), so the plain
            // scan could not see them. Enum values are cast to the item enum by value.
            try
            {
                foreach (var fie in UnityEngine.Object.FindObjectsOfType<FocusInteractionElement>(true))
                {
                    try
                    {
                        if (fie == null) continue;
                        var pos = fie.transform.position;
                        var dist = Vector3.Distance(origin, pos);
                        if (dist > NearbyRadius) continue;

                        string scene = "unknown", name = "-", mode = "?", path = "?";
                        bool activeSelf = false, activeHier = false, enabled = false;
                        bool hasRead = false, hasLoot = false;
                        bool deactCanvas = false, hasDeactGo = false, hasDisableList = false;
                        string locKey = null, persistent = null, deactGoName = null;
                        int disableListCount = -1;
                        try { scene = fie.gameObject.scene.name; } catch { }
                        try { name = fie.gameObject.name; } catch { }
                        try { activeSelf = fie.gameObject.activeSelf; } catch { }
                        try { activeHier = fie.gameObject.activeInHierarchy; } catch { }
                        try { enabled = fie.enabled; } catch { }
                        try { mode = fie.interactionMode.ToString(); } catch { }
                        try { hasRead = fie.readInteractionData != null; } catch { }
                        try { hasLoot = fie.lootProviderDataContainer != null; } catch { }
                        try { locKey = fie.locKey.ToString(); } catch { }
                        try { deactCanvas = fie.deactivateCanvasGameObjectOnInteract; } catch { }
                        try
                        {
                            var g = fie.gameObjectToDeactivate;
                            hasDeactGo = g != null;
                            if (g != null) deactGoName = g.name;
                        }
                        catch { }
                        try
                        {
                            disableListCount = fie.focusInteractionElementToDisableOnPerformInteractionFromReadHud.Count;
                            hasDisableList = disableListCount > 0;
                        }
                        catch { }
                        try
                        {
                            var lp = fie.lootProviderDataContainer;
                            if (lp != null) persistent = lp.UniquePersistentID.ToString();
                        }
                        catch { }
                        try
                        {
                            var sb = new System.Text.StringBuilder(name);
                            var t = fie.transform.parent;
                            for (int d = 0; d < 6 && t != null; d++, t = t.parent)
                            {
                                sb.Insert(0, t.name + "/");
                            }
                            path = sb.ToString();
                        }
                        catch { }

                        rows.Add((dist,
                            "[NARRATIVE] type=FocusInteractionElement name=" + name +
                            " scene=" + scene + " dist=" + dist.ToString("F1", CultureInfo.InvariantCulture) + "m" +
                            " pos=" + Fmt(pos) + "\n" +
                            "             activeSelf=" + activeSelf + " activeInHierarchy=" + activeHier +
                            " enabled=" + enabled + " mode=" + mode +
                            " readData=" + hasRead + " lootContainer=" + hasLoot +
                            (persistent != null ? " persistentId=" + persistent : "") +
                            (locKey != null ? " locKey=" + locKey : "") + "\n" +
                            "             deactivateCanvasOnInteract=" + deactCanvas +
                            " gameObjectToDeactivate=" + (hasDeactGo ? deactGoName : "none") +
                            " disableListCount=" + disableListCount +
                            " path=" + path));
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Msg("FocusInteractionElement scan failed: " + e.Message);
            }

            // StoryEventSensors within radius (summary only; not promoted to markers)
            try
            {
                var mgr = GetStoryEventManager(out _);
                if (mgr != null)
                {
                    var sensors = mgr.allSensors;
                    int n = sensors.Count;
                    for (int i = 0; i < n; i++)
                    {
                        Il2Cpp.StoryEventSensor s = null;
                        try { s = sensors[i]; } catch { continue; }
                        if (s == null) continue;
                        try
                        {
                            var pos = SensorPos(s);
                            var dist = Vector3.Distance(origin, pos);
                            if (dist > NearbyRadius) continue;

                            string label = SensorLabel(s, out var sceneName);
                            rows.Add((dist,
                                "[STORY-NEAR] id=" + label + " scene=" + sceneName +
                                " dist=" + dist.ToString("F1", CultureInfo.InvariantCulture) + "m" +
                                " pos=" + Fmt(pos) + "\n" +
                                "             done=" + Safe(() => s.Done, false) +
                                " triggered=" + Safe(() => s.Triggered, false) +
                                " prereq=" + Safe(() => s.PrerequisitesMet, false) +
                                " repeat=" + Safe(() => s.CanTriggerMultipleTimes, false)));
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Msg("nearby story sensor scan failed: " + e.Message);
            }

            foreach (var r in rows.OrderBy(r => r.dist))
                MelonLogger.Msg(r.line);
            MelonLogger.Msg("Narrative summary: " + rows.Count + " candidates within " +
                            NearbyRadius.ToString("F0", CultureInfo.InvariantCulture) + "m (origin=" + posSource + ")");

            // Read Carriers Nearby: the actual world component behind pure-read objects
            // (notes/maps). Found via metadata reverse lookup of ReadInteractionDataBase
            // holders; include-inactive because read objects may be deactivated.
            try
            {
                var carriers = UnityEngine.Object.FindObjectsOfType<Il2Cpp.ReadInteractionProvider>(true);
                var cRows = new List<(float dist, string line)>();
                foreach (var rp in carriers)
                {
                    try
                    {
                        if (rp == null) continue;
                        var pos = rp.transform.position;
                        var dist = Vector3.Distance(origin, pos);
                        if (dist > NearbyRadius) continue;

                        string scene = "unknown", name = "-", path = "?";
                        bool activeSelf = false, activeHier = false, enabled = false;
                        string dataName = "none", locKey = null;
                        ulong pid = 0; int count = -1; bool persistent = false;
                        string giveItem = null;
                        try { scene = rp.gameObject.scene.name; } catch { }
                        try { name = rp.gameObject.name; } catch { }
                        try { activeSelf = rp.gameObject.activeSelf; } catch { }
                        try { activeHier = rp.gameObject.activeInHierarchy; } catch { }
                        try { enabled = rp.enabled; } catch { }
                        try { var d = rp.readInteractionData; dataName = d != null ? d.name : "none"; } catch { }
                        try { locKey = rp.locKey.ToString(); } catch { }
                        try { pid = rp.UniquePersistentID; } catch { }
                        try { count = rp.InteractionCount; } catch { }
                        try { persistent = rp.IsPersistent; } catch { }
                        try
                        {
                            var loot = rp.giveItemAfterReading; // interop: plain Loot struct, not nullable
                            var iid = loot.itemId.Value;
                            if (iid != 0) giveItem = ((Il2Cpp.InventoryItemStringIdEnum)iid).ToString();
                        }
                        catch { }
                        try
                        {
                            var sb = new System.Text.StringBuilder(name);
                            var t = rp.transform.parent;
                            for (int d = 0; d < 6 && t != null; d++, t = t.parent) sb.Insert(0, t.name + "/");
                            path = sb.ToString();
                        }
                        catch { }

                        cRows.Add((dist,
                            "[READ-CARRIER] name=" + name + " scene=" + scene +
                            " dist=" + dist.ToString("F1", CultureInfo.InvariantCulture) + "m" +
                            " pos=" + Fmt(pos) + "\n" +
                            "               activeSelf=" + activeSelf + " activeInHierarchy=" + activeHier +
                            " enabled=" + enabled + " readData=" + dataName +
                            " interactionCount=" + count + " isPersistent=" + persistent +
                            " persistentId=" + pid + "\n" +
                            "               locKey=" + (locKey ?? "?") +
                            " giveItemAfterReading=" + (giveItem ?? "none") +
                            " path=" + path));
                    }
                    catch { }
                }
                foreach (var r in cRows.OrderBy(r => r.dist).Take(15))
                    MelonLogger.Msg(r.line);
                MelonLogger.Msg("Read carriers: " + cRows.Count + " within " +
                                NearbyRadius.ToString("F0", CultureInfo.InvariantCulture) + "m" +
                                (cRows.Count > 15 ? " (closest 15 shown)" : ""));
            }
            catch (Exception e)
            {
                MelonLogger.Msg("Read carrier scan failed: " + e.Message);
            }

            // Current read state (from static GameEventManager read events).
            MelonLogger.Msg("=== Current Read ===");
            MelonLogger.Msg("open=" + _readingNow +
                            " dataName=" + (_currentReadDataName ?? "none") +
                            " wrapperType=" + (_currentReadWrapperType ?? "-") +
                            " il2cppType=" + (_currentReadIl2cppType ?? "-") +
                            " layout=" + (_currentReadLayout ?? "-") +
                            " hasDialog=" + _currentReadHasDialog +
                            (_lastReadEventAt > 0f
                                ? " lastEvent=" + (Time.unscaledTime - _lastReadEventAt).ToString("F0", CultureInfo.InvariantCulture) + "s ago"
                                : "") +
                            (_currentProviderDesc != null ? "\nlastProvider=" + _currentProviderDesc : ""));

            DumpReadingCarrierCorrelation();
            DumpReadCarrierSurvey();

            // Asset-side lookup: loaded ReadInteractionDataBase assets matching story keywords.
            try
            {
                var assets = UnityEngine.Resources.FindObjectsOfTypeAll<Il2Cpp.ReadInteractionDataBase>();
                var keywords = new[] { "beekeeper", "honey", "map", "rodanna", "letter", "agents", "note" };
                var hits = new List<string>();
                int total = 0;
                foreach (var a in assets)
                {
                    try
                    {
                        if (a == null) continue;
                        total++;
                        var n = a.name.ToLowerInvariant();
                        foreach (var k in keywords)
                        {
                            if (n.Contains(k)) { hits.Add(a.name + " (" + a.GetType().Name + ")"); break; }
                        }
                    }
                    catch { }
                }
                MelonLogger.Msg("Read data assets loaded: " + total +
                                " | keyword hits (" + hits.Count + "): " + string.Join(" | ", hits.Take(12)));
            }
            catch (Exception e)
            {
                MelonLogger.Msg("read data asset lookup failed: " + e.Message);
            }

            // Readables the player already owns: answers "was the map/note already collected?"
            // IDs are ints in the save data; resolve them to enum names via the generated
            // enum (same numeric space), never string matching.
            try
            {
                var im = InventoryManager.Instance;
                var inv = im != null ? im.ReadablesInventoryData : null;
                var items = inv != null ? inv.itemsData : null;
                if (items == null)
                {
                    MelonLogger.Msg("Readables owned: unknown (inventory not available)");
                }
                else
                {
                    var names = new List<string>();
                    bool beekeeper = false, rodanna = false;
                    foreach (var it in items)
                    {
                        try
                        {
                            if (it == null) continue;
                            var e = (Il2Cpp.InventoryItemStringIdEnum)it.itemId.Value;
                            var n = e.ToString();
                            names.Add(n);
                            if (n == "ITEM_MAP_BEEKEEPER") beekeeper = true;
                            if (n == "ITEM_RODANNA_LETTER") rodanna = true;
                        }
                        catch { }
                    }
                    var shown = string.Join(", ", names.Take(25));
                    MelonLogger.Msg("Readables owned: count=" + names.Count +
                                    (names.Count > 25 ? " (first 25 shown)" : "") +
                                    (names.Count > 0 ? " [" + shown + "]" : "") +
                                    " beekeeperMapOwned=" + beekeeper +
                                    " rodannaLetterOwned=" + rodanna);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Msg("Readables owned scan failed: " + e.Message);
            }
        }

        /// <summary>
        /// During an active read, scan every known read-carrier family near the player
        /// (include inactive) and correlate each candidate's read data against the
        /// currently open ReadInteractionDataBase by il2cpp pointer identity. Covers
        /// ReadInteractionProvider, FocusInteractionElement, and LootProvider-readable
        /// paths — the honey-cave poster/kami reads had NO provider-enter event, so the
        /// carrier family must be discovered, not assumed.
        /// </summary>
        private void DumpReadingCarrierCorrelation()
        {
            if (!_readingNow || _currentReadData == null)
            {
                MelonLogger.Msg("=== Reading Carrier Correlation ===");
                MelonLogger.Msg("skipped (no active read)");
                return;
            }

            MelonLogger.Msg("=== Reading Carrier Correlation ===");
            MelonLogger.Msg("target=\"" + (_currentReadDataName ?? "?") +
                            "\" il2cppType=" + (_currentReadIl2cppType ?? "?") +
                            " ptr=0x" + _currentReadData.Pointer.ToString(IntPtr.Size == 8 ? "X16" : "X8"));

            string posSource;
            Vector3 origin = GetPlayerPos(out posSource);
            if (posSource == "none")
            {
                MelonLogger.Msg("no player position; correlation skipped");
                return;
            }

            IntPtr targetPtr = _currentReadData.Pointer;
            int matches = 0, candidates = 0;

            // 1) ReadInteractionProvider family
            try
            {
                foreach (var rp in UnityEngine.Object.FindObjectsOfType<Il2Cpp.ReadInteractionProvider>(true))
                {
                    try
                    {
                        if (rp == null) continue;
                        var pos = rp.transform.position;
                        if (Vector3.Distance(origin, pos) > NearbyRadius) continue;
                        candidates++;

                        string dataName = "none";
                        bool match = false;
                        try
                        {
                            var d = rp.readInteractionData;
                            if (d != null)
                            {
                                dataName = d.name;
                                match = d.Pointer == targetPtr;
                            }
                        }
                        catch { }

                        string path = rp.gameObject.name;
                        try
                        {
                            var sb = new System.Text.StringBuilder(path);
                            var t = rp.transform.parent;
                            for (int d2 = 0; d2 < 6 && t != null; d2++, t = t.parent) sb.Insert(0, t.name + "/");
                            path = sb.ToString();
                        }
                        catch { }

                        MelonLogger.Msg("[CARRIER] type=ReadInteractionProvider name=" + rp.gameObject.name +
                                        " dist=" + Vector3.Distance(origin, pos).ToString("F1", CultureInfo.InvariantCulture) + "m" +
                                        " pos=" + Fmt(pos) +
                                        (match ? " match=currentRead" : "") + "\n" +
                                        "         path=" + path +
                                        " readData=" + dataName +
                                        " count=" + Safe(() => rp.InteractionCount, -1) +
                                        " active=" + Safe(() => rp.gameObject.activeInHierarchy, false));
                        if (match) matches++;
                    }
                    catch { }
                }
            }
            catch (Exception e) { MelonLogger.Msg("provider correlation failed: " + e.Message); }

            // 2) FocusInteractionElement family (holds readInteractionData too)
            try
            {
                foreach (var fie in UnityEngine.Object.FindObjectsOfType<FocusInteractionElement>(true))
                {
                    try
                    {
                        if (fie == null) continue;
                        var pos = fie.transform.position;
                        if (Vector3.Distance(origin, pos) > NearbyRadius) continue;
                        candidates++;

                        string dataName = "none";
                        bool match = false;
                        try
                        {
                            var d = fie.readInteractionData;
                            if (d != null)
                            {
                                dataName = d.name;
                                match = d.Pointer == targetPtr;
                            }
                        }
                        catch { }

                        string path = fie.gameObject.name;
                        try
                        {
                            var sb = new System.Text.StringBuilder(path);
                            var t = fie.transform.parent;
                            for (int d2 = 0; d2 < 6 && t != null; d2++, t = t.parent) sb.Insert(0, t.name + "/");
                            path = sb.ToString();
                        }
                        catch { }

                        MelonLogger.Msg("[CARRIER] type=FocusInteractionElement name=" + fie.gameObject.name +
                                        " dist=" + Vector3.Distance(origin, pos).ToString("F1", CultureInfo.InvariantCulture) + "m" +
                                        " pos=" + Fmt(pos) +
                                        (match ? " match=currentRead" : "") + "\n" +
                                        "         path=" + path +
                                        " readData=" + dataName +
                                        " activeSelf=" + Safe(() => fie.gameObject.activeSelf, false) +
                                        " activeHier=" + Safe(() => fie.gameObject.activeInHierarchy, false) +
                                        " enabled=" + Safe(() => fie.enabled, false));
                        if (match) matches++;
                    }
                    catch { }
                }
            }
            catch (Exception e) { MelonLogger.Msg("focus-element correlation failed: " + e.Message); }

            // 3) LootProvider -> ReadableItem path (loot that IS a readable)
            try
            {
                foreach (var lp in UnityEngine.Object.FindObjectsOfType<Il2Cpp.LootProvider>(true))
                {
                    try
                    {
                        if (lp == null) continue;
                        var pos = lp.transform.position;
                        if (Vector3.Distance(origin, pos) > NearbyRadius) continue;

                        var lootItems = lp.remainingLoots;
                        if (lootItems == null) continue;
                        for (int i = 0; i < lootItems.Length; i++)
                        {
                            try
                            {
                                var item = lp.GetLootItem(i);
                                var readable = item != null ? item.TryCast<Il2Cpp.ReadableItem>() : null;
                                if (readable == null) continue;
                                string guid = null;
                                try { guid = readable.readData.AssetGUID; } catch { }
                                candidates++;
                                MelonLogger.Msg("[CARRIER] type=LootProvider/ReadableItem provider=" + lp.gameObject.name +
                                                " dist=" + Vector3.Distance(origin, pos).ToString("F1", CultureInfo.InvariantCulture) + "m" +
                                                " pos=" + Fmt(pos) + "\n" +
                                                "         itemType=" + readable.Type +
                                                " readDataRef=" + (string.IsNullOrEmpty(guid) ? "none" : guid));
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception e) { MelonLogger.Msg("loot-readable correlation failed: " + e.Message); }

            // 4) active Read HUD (confirms the read UI instance and its tracked data)
            try
            {
                foreach (var hud in UnityEngine.Object.FindObjectsOfType<ReadInteractionHud>(true))
                {
                    try
                    {
                        if (hud == null) continue;
                        string tracked = "none";
                        try { tracked = hud.trackedMultiInteraction != null ? hud.trackedMultiInteraction.name : "none"; } catch { }
                        MelonLogger.Msg("[CARRIER] type=ReadInteractionHud activeSelf=" +
                                        Safe(() => hud.gameObject.activeSelf, false) +
                                        " trackedMulti=" + tracked +
                                        " layoutIndex=" + Safe(() => hud.multiLayoutIndex, -1));
                    }
                    catch { }
                }
            }
            catch { }

            MelonLogger.Msg("Correlation summary: candidates=" + candidates + " matched=" + matches +
                            (matches == 0 ? "  (NO carrier matched — unknown pipeline, inspect [CARRIER] list above)" : ""));
        }

        /// <summary>
        /// Whole-loaded-scene survey of ReadInteractionProviders (diagnostic only).
        /// Validates whether "*_Lore" hierarchy membership separates narrative readables
        /// from functional ones, and whether Lore root -> first child grouping maps to
        /// author-defined POIs. No distance limit; loaded scenes only; include inactive.
        /// </summary>
        private void DumpReadCarrierSurvey()
        {
            MelonLogger.Msg("=== Read Carrier Survey ===");

            var items = new List<(string scene, string name, string path, bool underLore,
                                  string loreRoot, string poiGroup, bool persistent, int count,
                                  string giveItem, string readData, string refGuid, string locKey,
                                  Vector3 pos, bool activeSelf, bool activeHier, bool enabled)>();
            try
            {
                foreach (var rp in UnityEngine.Object.FindObjectsOfType<Il2Cpp.ReadInteractionProvider>(true))
                {
                    try
                    {
                        if (rp == null) continue;

                        // hierarchy path (root -> ... -> provider)
                        var segs = new List<string> { rp.gameObject.name };
                        var t = rp.transform.parent;
                        for (int d = 0; d < 24 && t != null; d++, t = t.parent) segs.Insert(0, t.name);
                        var path = string.Join("/", segs);

                        // Lore analysis: any segment ending with "_Lore"; the segment right
                        // after it is the author's POI group.
                        string loreRoot = null, poiGroup = null;
                        for (int i = 0; i < segs.Count; i++)
                        {
                            if (segs[i].EndsWith("_Lore", StringComparison.OrdinalIgnoreCase))
                            {
                                loreRoot = segs[i];
                                if (i + 1 < segs.Count - 1) poiGroup = segs[i + 1];
                                else if (i + 1 == segs.Count - 1) poiGroup = "<root>";
                                break;
                            }
                        }

                        string scene = "unknown";
                        try { scene = rp.gameObject.scene.name; } catch { }
                        // gameplay scenes only (skip bootstrap/base scenes if any leak in)
                        if (!scene.Contains("Gameplay") && !scene.Contains("gameplay")) continue;

                        bool activeSelf = false, activeHier = false, enabled = false, persistent = false;
                        int count = -1;
                        string giveItem = null, readData = "none", refGuid = null, locKey = null;
                        try { activeSelf = rp.gameObject.activeSelf; } catch { }
                        try { activeHier = rp.gameObject.activeInHierarchy; } catch { }
                        try { enabled = rp.enabled; } catch { }
                        try { persistent = rp.IsPersistent; } catch { }
                        try { count = rp.InteractionCount; } catch { }
                        try
                        {
                            var loot = rp.giveItemAfterReading;
                            var iid = loot.itemId.Value;
                            if (iid != 0) giveItem = ((Il2Cpp.InventoryItemStringIdEnum)iid).ToString();
                        }
                        catch { }
                        try { var d = rp.readInteractionData; readData = d != null ? d.name : "none"; } catch { }
                        try { refGuid = rp.readInteractionDataReference.AssetGUID; } catch { }
                        try { locKey = rp.locKey.ToString(); } catch { }

                        items.Add((scene, rp.gameObject.name, path,
                            loreRoot != null, loreRoot ?? "-", poiGroup ?? "-",
                            persistent, count, giveItem ?? "none", readData, refGuid ?? "-", locKey ?? "?",
                            rp.transform.position, activeSelf, activeHier, enabled));
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Msg("survey scan failed: " + e.Message);
                return;
            }

            int lore = 0, nonLore = 0;
            int lP = 0, lNp = 0, lGi = 0, lNi = 0;
            int nP = 0, nNp = 0, nGi = 0, nNi = 0;
            foreach (var it in items)
            {
                if (it.underLore)
                {
                    lore++;
                    if (it.persistent) lP++; else lNp++;
                    if (it.giveItem != "none") lGi++; else lNi++;
                }
                else
                {
                    nonLore++;
                    if (it.persistent) nP++; else nNp++;
                    if (it.giveItem != "none") nGi++; else nNi++;
                }
            }

            var scenes = items.Select(i => i.scene).Distinct().OrderBy(x => x).ToList();
            MelonLogger.Msg("Read carrier survey: total=" + items.Count + " underLore=" + lore + " nonLore=" + nonLore +
                            " scenes=[" + string.Join(",", scenes) + "]");
            MelonLogger.Msg("Lore: persistent=" + lP + " nonPersistent=" + lNp + " givesItem=" + lGi + " noItem=" + lNi);
            MelonLogger.Msg("Non-Lore: persistent=" + nP + " nonPersistent=" + nNp + " givesItem=" + nGi + " noItem=" + nNi);

            // Lore POI groups: loreRoot/poiGroup -> members
            var groups = items.Where(i => i.underLore)
                .GroupBy(i => i.loreRoot + "/" + i.poiGroup)
                .OrderBy(g => g.Key);
            MelonLogger.Msg("Lore POI groups: " + groups.Count());
            foreach (var g in groups)
            {
                var l = g.ToList();
                int p = l.Count(x => x.persistent);
                int gi = l.Count(x => x.giveItem != "none");
                MelonLogger.Msg("[Lore POI] " + g.Key +
                                " providers=" + l.Count + " persistent=" + p +
                                " nonPersistent=" + (l.Count - p) + " givesItem=" + gi +
                                " scenes=" + string.Join(",", l.Select(x => x.scene).Distinct()));
                foreach (var x in l)
                    MelonLogger.Msg("  - " + x.name +
                                    (x.persistent ? " [persistent count=" + x.count + "]" : "") +
                                    (x.giveItem != "none" ? " [gives " + x.giveItem + "]" : ""));
            }

            // Non-Lore members: full detail (the precision control group)
            var nl = items.Where(i => !i.underLore).OrderBy(i => i.scene).ThenBy(i => i.path).ToList();
            if (nl.Count == 0)
            {
                MelonLogger.Msg("Non-Lore members: none");
            }
            else
            {
                MelonLogger.Msg("Non-Lore members: " + nl.Count);
                foreach (var x in nl)
                    MelonLogger.Msg("[NON-LORE] name=" + x.name + " scene=" + x.scene +
                                    " path=" + x.path + "\n" +
                                    "           persistent=" + x.persistent + " interactionCount=" + x.count +
                                    " giveItem=" + x.giveItem + " readData=" + x.readData +
                                    " refGuid=" + x.refGuid + " locKey=" + x.locKey +
                                    " activeSelf=" + x.activeSelf + " enabled=" + x.enabled +
                                    " pos=" + Fmt(x.pos));
            }
        }

        /// <summary>Player position: pawn controllers first, camera fallback for diagnostics.</summary>
        private Vector3 GetPlayerPos(out string source)
        {
            try
            {
                var cm = CameraManager.Instance;
                var cc = cm != null ? cm.CurrentCairnCamera : null;
                if (cc != null)
                {
                    try
                    {
                        var w = cc.walkingController;
                        if (w != null) { source = "Pawn(walking)"; return w.transform.position; }
                    }
                    catch { }
                    try
                    {
                        var c = cc.climbingController;
                        if (c != null) { source = "Pawn(climbing)"; return c.transform.position; }
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                var mc = UnityEngine.Camera.main;
                if (mc != null) { source = "CameraFallback"; return mc.transform.position; }
            }
            catch { }
            source = "none";
            return default;
        }

        // ------------------------------------------------------- V3 FIE spatial diagnostics
        // v0.3.12 finding: Mapboard "?" sits at the FIE transform (ground/board-edge),
        // below the poster visuals. Before choosing a new anchor rule we need real
        // Collider/Renderer bounds for every Lore FIE member. Diagnostic only — the
        // formal anchor still uses transform.position this version.

        private static string BoundsStr(Bounds b)
        {
            return "center=" + Fmt(b.center) + " size=" + Fmt(b.size);
        }

        private static string ComponentPath(Component c)
        {
            var sb = new System.Text.StringBuilder(c.gameObject.name);
            var t = c.transform.parent;
            for (int d = 0; d < 8 && t != null; d++, t = t.parent) sb.Insert(0, t.name + "/");
            return sb.ToString() + " [" + c.GetType().Name + "]";
        }

        private void DumpFieSpatial()
        {
            MelonLogger.Msg("=== FIE Spatial Anchors ===");

            bool bearPrinted = false;
            foreach (var g in _loreGroups)
            {
                // Bear provider spatial cross-check (Provider transform proven good)
                foreach (var m in g.Members)
                {
                    if (!m.IsProvider || bearPrinted) continue;
                    try
                    {
                        if (m.Go == null || !m.Path.Contains("Bear_RedSign")) continue;
                        MelonLogger.Msg("[PROVIDER-SPATIAL] name=" + m.Path +
                                        "\n  transformPos=" + Fmt(m.Pos));
                        var cols = m.Go.GetComponentsInChildren<Collider>(true);
                        for (int i = 0; i < cols.Length && i < 4; i++)
                            MelonLogger.Msg("  collider[" + i + "] " + ComponentPath(cols[i]) +
                                            " enabled=" + cols[i].enabled + " isTrigger=" + cols[i].isTrigger +
                                            " " + BoundsStr(cols[i].bounds));
                        var rens = m.Go.GetComponentsInChildren<Renderer>(true);
                        for (int i = 0; i < rens.Length && i < 4; i++)
                            MelonLogger.Msg("  renderer[" + i + "] " + ComponentPath(rens[i]) +
                                            " enabled=" + rens[i].enabled +
                                            " " + BoundsStr(rens[i].bounds));
                        bearPrinted = true;
                    }
                    catch { }
                }

                foreach (var m in g.Members)
                {
                    if (m.IsProvider) continue; // FIE only
                    try
                    {
                        if (m.Go == null)
                        {
                            MelonLogger.Msg("[FIE-SPATIAL] member=" + m.Path + " GO=NULL");
                            continue;
                        }

                        MelonLogger.Msg("[FIE-SPATIAL] member=" + m.Path +
                                        "\n  transformPos=" + Fmt(m.Pos) +
                                        " group=" + g.LoreRoot + "/" + g.PoiGroup +
                                        " readData=" + (m.ReadDataName ?? "none"));

                        var cols = m.Go.GetComponentsInChildren<Collider>(true);
                        MelonLogger.Msg("  colliders=" + cols.Length);
                        for (int i = 0; i < cols.Length && i < 6; i++)
                        {
                            var c = cols[i];
                            MelonLogger.Msg("    collider[" + i + "] " + ComponentPath(c) +
                                            " enabled=" + c.enabled + " isTrigger=" + c.isTrigger +
                                            " " + BoundsStr(c.bounds));
                        }

                        // parent chain (up to 3 levels): self-only colliders there
                        var pt = m.Go.transform.parent;
                        for (int up = 1; up <= 3 && pt != null; up++, pt = pt.parent)
                        {
                            var pcols = pt.GetComponents<Collider>();
                            for (int i = 0; i < pcols.Length && i < 2; i++)
                                MelonLogger.Msg("    parent" + up + "(" + pt.name + ") collider[" + i + "] " +
                                                pcols[i].GetType().Name + " enabled=" + pcols[i].enabled +
                                                " isTrigger=" + pcols[i].isTrigger + " " + BoundsStr(pcols[i].bounds));
                        }

                        var rens = m.Go.GetComponentsInChildren<Renderer>(true);
                        MelonLogger.Msg("  renderers=" + rens.Length);
                        for (int i = 0; i < rens.Length && i < 6; i++)
                        {
                            var r = rens[i];
                            MelonLogger.Msg("    renderer[" + i + "] " + ComponentPath(r) +
                                            " enabled=" + r.enabled + " " + BoundsStr(r.bounds));
                        }
                    }
                    catch { }
                }
            }
            MelonLogger.Msg("FIE spatial done.");
        }

        // ------------------------------------------------------- V3 group visual diagnostic
        // Last structural probe for the FIE visual anchor: (1) FIE's own camera/focus
        // Transform members (singleCameraTransform / fipCamera — exact interop names),
        // (2) the whole poiGroup subtree's Renderers with combined bounds and centers
        // centroid. Diagnostic only; formal anchor unchanged.

        private static Transform FindPoiGroupTransform(LoreMember m, string poiGroup)
        {
            if (m.Go == null || poiGroup == "<root>" || poiGroup == null) return null;
            var t = m.Go.transform.parent;
            while (t != null)
            {
                if (t.name == poiGroup) return t;
                t = t.parent;
            }
            return null;
        }

        private static string FocusTransformDesc(FocusInteractionElement fie)
        {
            try
            {
                var s = fie.singleCameraTransform;
                if (s != null) return "singleCameraTransform=" + s.name + " pos=" + Fmt(s.position);
            }
            catch { }
            try
            {
                var f = fie.fipCamera;
                if (f != null) return "fipCamera=" + f.name + " pos=" + Fmt(f.position);
            }
            catch { }
            return "none";
        }

        private void DumpGroupVisualDiagnostic()
        {
            MelonLogger.Msg("=== FIE Focus Transforms ===");
            foreach (var g in _loreGroups)
            {
                foreach (var m in g.Members)
                {
                    if (m.IsProvider || m.Go == null) continue;
                    try
                    {
                        var fie = m.Go.GetComponent<FocusInteractionElement>();
                        MelonLogger.Msg("[FIE-FOCUS] member=" + m.Path +
                                        "\n  transformPos=" + Fmt(m.Pos) +
                                        " focus=" + (fie != null ? FocusTransformDesc(fie) : "component-missing"));
                    }
                    catch { }
                }
            }

            MelonLogger.Msg("=== Group Visual Diagnostic ===");
            bool bearDone = false, skeletonDone = false;
            foreach (var g in _loreGroups)
            {
                Transform poiT = null;
                foreach (var m in g.Members)
                {
                    poiT = FindPoiGroupTransform(m, g.PoiGroup);
                    if (poiT != null) break;
                }
                if (poiT == null)
                {
                    MelonLogger.Msg("[GROUP-RENDERER] group=" + g.LoreRoot + "/" + g.PoiGroup +
                                    " poiGroup GO not found — skipped");
                    continue;
                }

                try
                {
                    var rens = poiT.GetComponentsInChildren<Renderer>(true);
                    var combined = new Bounds();
                    var centers = Vector3.zero;
                    int valid = 0, lodCount = 0, suspiciousCount = 0;
                    var lines = new List<string>();

                    foreach (var r in rens)
                    {
                        try
                        {
                            if (r == null) continue;
                            var b = r.bounds;
                            if (valid == 0) combined = b; else combined.Encapsulate(b);
                            centers += b.center;
                            valid++;

                            bool isLod = r.name.Contains("LOD") || (r.transform.parent != null && r.transform.parent.name.Contains("LOD"));
                            bool big = b.size.x > 50f || b.size.y > 50f || b.size.z > 50f;
                            if (isLod) lodCount++;
                            if (big) suspiciousCount++;

                            var path = ComponentPath(r);
                            lines.Add("    [" + (valid - 1) + "]" + (isLod ? " [LOD]" : "") +
                                      (big ? " [SUSPICIOUS-LARGE]" : "") +
                                      " " + path + " enabled=" + r.enabled + " " + BoundsStr(b));
                        }
                        catch { }
                    }

                    MelonLogger.Msg("[GROUP-RENDERER] group=" + g.LoreRoot + "/" + g.PoiGroup +
                                    " poiNode=" + poiT.name + " renderers=" + valid +
                                    " lod=" + lodCount + " suspiciousLarge=" + suspiciousCount);
                    if (valid > 0)
                    {
                        MelonLogger.Msg("    combinedCenter=" + Fmt(combined.center) +
                                        " combinedSize=" + Fmt(combined.size) +
                                        " rendererCentersCentroid=" + Fmt(centers / valid));
                    }
                    // detail: LOD/suspicious first, then the rest, capped at 25 lines
                    foreach (var l in lines.Where(l => l.Contains("[LOD]") || l.Contains("[SUSPICIOUS-LARGE]")).Take(10))
                        MelonLogger.Msg(l);
                    foreach (var l in lines.Where(l => !l.Contains("[LOD]") && !l.Contains("[SUSPICIOUS-LARGE]")).Take(15))
                        MelonLogger.Msg(l);
                    if (lines.Count > 25)
                        MelonLogger.Msg("    ... (" + (lines.Count - 25) + " more omitted)");

                    // light cross-checks
                    if (!bearDone && g.Members.Any(m => m.Path.Contains("Bear_RedSign")))
                    {
                        var p = g.Members.First(m => m.Path.Contains("Bear_RedSign"));
                        MelonLogger.Msg("  [CROSS] Bear provider transformPos=" + Fmt(p.Pos));
                        bearDone = true;
                    }
                    if (!skeletonDone && g.Members.Any(m => m.Path.Contains("Skeleton")))
                    {
                        var sm = g.Members.First(m => m.Path.Contains("Skeleton"));
                        string focus = "n/a";
                        try
                        {
                            var fie = sm.Go != null ? sm.Go.GetComponent<FocusInteractionElement>() : null;
                            if (fie != null) focus = FocusTransformDesc(fie);
                        }
                        catch { }
                        MelonLogger.Msg("  [CROSS] Skeleton FIE transformPos=" + Fmt(sm.Pos) + " focus=" + focus);
                        skeletonDone = true;
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Msg("[GROUP-RENDERER] scan failed: " + e.Message);
                }
            }
            MelonLogger.Msg("Group visual diagnostic done.");
        }

        // ------------------------------------------------------- V3 classification report

        private void DumpClassification()
        {
            MelonLogger.Msg("=== Story Marker Classification ===");

            int nPoi = 0, nInfo = 0, visiblePoi = 0, visibleInfo = 0;
            foreach (var g in _loreGroups)
            {
                int unread = 0;
                foreach (var m in g.Members)
                    if (!m.CoveredByCollectible && !MemberIsRead(m)) unread++;

                bool visible = LoreGroupVisible(g) && PrefVisible(g.RawCategory);
                string tag = g.RawCategory == LoreCategory.NarrativePoi ? "NarrativePoi" : "WorldInfo";
                if (g.RawCategory == LoreCategory.NarrativePoi) { nPoi++; if (visible) visiblePoi++; }
                else { nInfo++; if (visible) visibleInfo++; }

                MelonLogger.Msg("[" + tag + "] group=" + g.LoreRoot + "/" + g.PoiGroup +
                                " scene=" + g.Scene +
                                "\nrawMembers=" + g.Members.Count +
                                " effectiveMembers=" + g.Members.Count(m => !m.CoveredByCollectible) +
                                " coveredByCollectible=" + g.Members.Count(m => m.CoveredByCollectible) +
                                " unread=" + unread +
                                "\nrawCategory=" + g.RawCategory +
                                " loreUnread=" + g.LoreUnread +
                                " coveredCollectibleRemaining=" + g.CoveredRemaining +
                                " groupPending=" + g.GroupPending +
                                " displayIcon=" + (g.RawCategory == LoreCategory.NarrativePoi ? "?" : "i") +
                                "\ncentroid=" + Fmt(CentroidOf(g)) +
                                " anchorSource=" + g.AnchorSource +
                                " anchorMember=" + (g.AnchorMember ?? "-") +
                                " anchorPos=" + Fmt(g.Anchor) +
                                (g.HasGroupTransform
                                    ? "\ngroupTransformPos=" + Fmt(g.GroupTransformPos) + " (diagnostic only, not used for anchors)"
                                    : "") +
                                " visible=" + (LoreGroupVisible(g) ? "True" : "False") +
                                " prefVisible=" + PrefVisible(g.Category) +
                                "\n  members:");
                foreach (var m in g.Members)
                    MelonLogger.Msg("  - " + (m.IsProvider ? "Provider" : "FocusElement") + " " + m.Path +
                                    " read=" + (MemberIsRead(m) ? "True" : "False") +
                                    " memberPos=" + Fmt(m.Pos) +
                                    " positionSource=" + (m.PositionSource ?? "ProviderTransform") +
                                    (m.CoveredByCollectible
                                        ? " coveredByCollectible=True collectibleId=" + m.CollectibleId +
                                           " collectibleRemaining=" + m.CollectibleRemaining
                                        : "") +
                                    (m.IsProvider ? " persistent=" + m.IsPersistent + " count=" + m.InteractionCount : "") +
                                    (m.ReadDataName != null ? " readData=" + m.ReadDataName : ""));
            }

            int colEligible = _eligible.Count;
            int colRemaining = 0;
            try
            {
                foreach (var e in _eligible)
                {
                    if (_classification.TryGetValue(e.Id, out var cls) && cls.Key)
                    {
                        // remaining state from last rebuild (kept simple for the report)
                    }
                }
                colRemaining = colEligible;
            }
            catch { }

            int mergedCount = _collectibleCoveredByLore.Count;
            MelonLogger.Msg("Classification summary: NarrativePoi=" + nPoi + " WorldInfo=" + nInfo +
                            " Collectible=" + colEligible +
                            " (standalone=" + (colEligible - mergedCount) + " mergedIntoLore=" + mergedCount + ")" +
                            " visibleLore=" + (visiblePoi + visibleInfo) +
                            " (poiVisible=" + visiblePoi + " infoVisible=" + visibleInfo + ")" +
                            " sessionReadCount=" + _sessionReadKeys.Count +
                            " prefs(n/i/c)=" + PrefVisible(LoreCategory.NarrativePoi) + "/" +
                            PrefVisible(LoreCategory.WorldInfo) + "/" + PrefShowCollectible());
        }

        // ---------------------------------------------------------------- helpers

        private static Vector3 CentroidOf(LoreGroup g)
        {
            var c = Vector3.zero;
            if (g.Members.Count == 0) return c;
            foreach (var m in g.Members) c += m.Pos;
            return c / g.Members.Count;
        }

        private struct MgrState
        {
            public bool Ok;
            public bool Done;
            public bool Triggered;
            public bool Registered;
        }

        private MgrState ManagerState(Il2Cpp.StoryEventSensor s)
        {
            var st = new MgrState();
            try
            {
                var setup = s.setup;
                if (setup == null) return st;
                var gdm = GameDataManager.Instance;
                if (gdm == null) return st;
                var gdata = gdm.gameData;
                if (gdata == null) return st;
                var smd = gdata.storyEventManagerData;
                if (smd == null) return st;

                var id = setup.id;
                st.Done = smd.IsDone(id);
                st.Triggered = smd.HasTrigger(id);
                st.Registered = smd.IsRegistered(id);
                st.Ok = true;
            }
            catch { }
            return st;
        }

        private string SensorLabel(Il2Cpp.StoryEventSensor s, out string sceneName)
        {
            sceneName = "unknown";
            try { sceneName = s.gameObject.scene.name; } catch { }

            try
            {
                var setup = s.setup;
                if (setup != null)
                {
                    var en = StoryEventSensorStringIdEnumHelper.ToEnum(setup.id);
                    var str = StoryEventSensorStringIdEnumHelper.FastToString(en);
                    if (!string.IsNullOrEmpty(str)) return str;
                }
            }
            catch { }

            try
            {
                var gn = s.gameObject.name;
                if (!string.IsNullOrEmpty(gn)) return gn + "(gameObject)";
            }
            catch { }

            return "unknown";
        }

        private Vector3 SensorPos(Il2Cpp.StoryEventSensor s)
        {
            try
            {
                var cols = s.Colliders;
                if (cols != null && cols.Length > 0)
                {
                    var c = cols[0];
                    if (c != null) return c.bounds.center;
                }
            }
            catch { }
            try { return s.transform.position; }
            catch { return default; }
        }

        private static string SafeName(CairnAPI.ItemLocation loc)
        {
            try { return string.IsNullOrEmpty(loc.Name) ? "-" : loc.Name; }
            catch { return "-"; }
        }

        private static T Safe<T>(Func<T> f, T def)
        {
            try { return f(); } catch { return def; }
        }

        private static readonly string[] SuspiciousTokens =
            { "debug", "gym", "test", "placeholder", "dummy", "cheat" };

        private static bool IsSuspicious(string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            var l = label.ToLowerInvariant();
            foreach (var t in SuspiciousTokens)
                if (l.Contains(t)) return true;
            return false;
        }

        private static string Fmt(Vector3 p)
        {
            return "(" + p.x.ToString("F1", CultureInfo.InvariantCulture) +
                   ", " + p.y.ToString("F1", CultureInfo.InvariantCulture) +
                   ", " + p.z.ToString("F1", CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>"01_FirstRidge_Gameplay" -> "01_FirstRidge" (grouping key).</summary>
        private static string GroupKey(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return "unknown";
            var k = sceneName;
            foreach (var suf in new[] { "_Gameplay", "_GameplayDup", "_Art", "_ART", "_Audio", "_AUDIO", "_Camera", "_CAMERA", "_Holds", "_HOLDS", "_LOD" })
            {
                if (k.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                {
                    k = k.Substring(0, k.Length - suf.Length);
                    break;
                }
            }
            return k;
        }

        /// <summary>"01_FirstRidge" -> "First Ridge" (last-resort display when no official name is available).</summary>
        private static string DisplayName(string key)
        {
            var m = Regex.Match(key, @"^(\d+)_(.+)$");
            var s = m.Success ? m.Groups[2].Value : key;
            s = Regex.Replace(s, "([a-z])([A-Z])", "$1 $2");
            return string.IsNullOrEmpty(s) ? key : s;
        }

        /// <summary>
        /// Official localized zone name (Priority 1: game's own LocalizationManager via
        /// ZoneSceneData.zoneNameLocKey). Falls back to a small internal Chinese mapping
        /// (Priority 2) and finally to the prettified internal name.
        /// </summary>
        private string ResolveZoneDisplay(string groupKey, string sceneName)
        {
            if (_officialZoneNames.TryGetValue(groupKey, out var cached)) return cached;

            string official = null;
            try
            {
                ZoneSceneData zsd = null;
                try
                {
                    if (_currentZone != null &&
                        string.Equals(_currentZone.name, groupKey, StringComparison.OrdinalIgnoreCase))
                        zsd = _currentZone;
                }
                catch { }

                if (zsd == null)
                {
                    Il2Cpp.WorldZoneData world = null;
                    try { world = StreamingManager.Instance.World; } catch { }
                    if (world == null) { try { world = CairnAPI.World.Current; } catch { } }

                    if (world != null)
                    {
                        try { zsd = world.GetZoneSceneData(sceneName); } catch { }
                        if (zsd == null) { try { zsd = world.GetZoneSceneData(groupKey); } catch { } }
                    }
                }

                if (zsd != null)
                {
                    var lm = LocalizationManager.Instance;
                    if (lm != null)
                    {
                        var s = lm.Get(zsd.zoneNameLocKey);
                        if (!string.IsNullOrWhiteSpace(s)) official = s.Trim();
                    }
                }
            }
            catch { }

            if (official != null)
            {
                _officialZoneNames[groupKey] = official;
                return official;
            }

            if (ZoneNameFallbackZh.TryGetValue(groupKey, out var zh)) return zh;
            return DisplayName(groupKey);
        }
    }
}
