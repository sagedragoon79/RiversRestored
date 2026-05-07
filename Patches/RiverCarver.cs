using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using TerrainGen;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Manual river carving using Farthest Frontier's PUBLIC terrain API
    /// (the same API that the Pangu mod uses). Bypasses Stage 60's per-river
    /// NRE that reflection-injection couldn't get past.
    ///
    /// API chain (per Pangu's decompiled source):
    ///   1. terrainManager.SetHeight(hX, hZ, h)      — per-cell height write
    ///   2. terrain.SmoothHeightsNotify(...)         — rebuild affected mesh chunks
    ///   3. _generationData.heightNoise[hX,hZ] = h/mapH  — dual-write for persistence
    ///
    /// All three are needed:
    ///   - SetHeight without SmoothHeightsNotify    →  data changes, mesh doesn't update
    ///   - SmoothHeightsNotify without heightNoise  →  carve doesn't survive regen
    ///   - Both alone don't tell building/AI/water systems "this is a river"
    ///     (but Stage 60's partial execution registers the WaterPath visual,
    ///      which gives us the water mesh + minimap rendering automatically)
    /// </summary>
    internal static class RiverCarver
    {
        private static bool _dumpedShape = false;
        private static bool _carved = false;
        private static bool _dumpedAPIShape = false;
        private static bool _dumpedLayerShape = false;
        private static bool _dumpedTerrainData = false;

        // Cached terrain API references. Resolved once via FindObjectOfType
        // and reused across every CarveAllRivers call until ResetGuard()
        // clears them (e.g., when reloading a different save). Pre-cache,
        // CarveAllRivers ran two scene-wide FindObjectOfType lookups every
        // frame until _carved=true — on slow loads (60s+) that's thousands
        // of redundant scans burning CPU during the load screen.
        private static Type? _cachedTerrainManagerType;
        private static Type? _cachedTerrain2Type;
        private static UnityEngine.Object? _cachedTerrainManagerInstance;
        private static UnityEngine.Object? _cachedTerrain2Instance;

        public static void ResetGuard()
        {
            _carved = false;
            _dumpedShape = false;
            _dumpedAPIShape = false;
            _dumpedLayerShape = false;
            _dumpedTerrainData = false;
            // Drop cached scene references — a new load will have fresh
            // terrain instances even though the Type lookups stay valid.
            _cachedTerrainManagerInstance = null;
            _cachedTerrain2Instance = null;
        }

        /// <summary>
        /// Find the live ControlTexture/channel for a generator-side splat
        /// texture index. Walks Terrain2.Data.TextureLayers and matches by
        /// Texture2D reference equality on diffuse + normal. Returns
        /// (controlIdx, channel) via out params if found, or -1 if not.
        /// </summary>
        private static int ResolveLiveLayer(System.Collections.Generic.IList<object> combinedLayers,
                                              IList splatTexturesList,
                                              int splatIdx, out int channel)
        {
            channel = -1;
            try
            {
                if (splatIdx < 0 || splatIdx >= splatTexturesList.Count) return -1;
                var splatEntry = splatTexturesList[splatIdx];
                if (splatEntry == null) return -1;

                UnityEngine.Texture? splatDiffuse = ExtractTexture(splatEntry, new[]
                    { "diffuse", "Diffuse", "texture", "Texture", "albedo", "Albedo", "diffuseMap" });
                UnityEngine.Texture? splatNormal = ExtractTexture(splatEntry, new[]
                    { "normal", "Normal", "normalMap", "NormalMap", "normalTexture" });

                if (splatDiffuse == null) return -1;

                for (int i = 0; i < combinedLayers.Count; i++)
                {
                    var layer = combinedLayers[i];
                    if (layer == null) continue;
                    UnityEngine.Texture? lDiff = ExtractTexture(layer, new[]
                        { "diffuse", "Diffuse", "texture", "Texture", "albedo", "Albedo", "diffuseMap" });
                    UnityEngine.Texture? lNorm = ExtractTexture(layer, new[]
                        { "normal", "Normal", "normalMap", "NormalMap", "normalTexture" });
                    if (lDiff == splatDiffuse &&
                        (splatNormal == null || lNorm == splatNormal))
                    {
                        channel = i % 4;
                        return i / 4;
                    }
                }
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning($"[RR][Carve] ResolveLiveLayer failed: {ex.Message}");
            }
            return -1;
        }

        /// <summary>Try a list of candidate field/property names to extract a Texture from an object.</summary>
        private static UnityEngine.Texture? ExtractTexture(object obj, string[] candidateNames)
        {
            Type t = obj.GetType();
            foreach (var name in candidateNames)
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && typeof(UnityEngine.Texture).IsAssignableFrom(f.FieldType))
                {
                    var v = f.GetValue(obj) as UnityEngine.Texture;
                    if (v != null) return v;
                }
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && typeof(UnityEngine.Texture).IsAssignableFrom(p.PropertyType))
                {
                    var v = p.GetValue(obj, null) as UnityEngine.Texture;
                    if (v != null) return v;
                }
            }
            return null;
        }

        /// <summary>Dump TextureLayers + CustomLayers content with diffuse/normal names,
        /// plus the relevant splatTexturesList entries for matching.</summary>
        private static void DumpCombinedLayers(IList? textureLayers, IList? customLayers,
                                                  IList splatTexturesList,
                                                  int riverUnderwaterTex, int riverShorelineTex)
        {
            try
            {
                Log("===== [LayerShape] TextureLayers content =====");
                DumpLayerList(textureLayers, "TL");
                Log("===== [LayerShape] CustomLayers content =====");
                DumpLayerList(customLayers, "CL");

                // Also dump full member structure of CustomLayers[0] in case
                // its fields are different from Terrain2Layer
                if (customLayers != null && customLayers.Count > 0 && customLayers[0] != null)
                {
                    Log("===== [LayerShape] CustomLayer[0] FULL DUMP =====");
                    DumpAllMembers(customLayers[0]!);
                    Log("===== [LayerShape] end CustomLayer dump =====");
                }

                Log($"===== [LayerShape] splatTexturesList[{riverUnderwaterTex}] (riverUnderwater) =====");
                if (riverUnderwaterTex >= 0 && riverUnderwaterTex < splatTexturesList.Count)
                    DumpListEntry(splatTexturesList, riverUnderwaterTex);
                Log($"===== [LayerShape] splatTexturesList[{riverShorelineTex}] (riverShoreline) =====");
                if (riverShorelineTex >= 0 && riverShorelineTex < splatTexturesList.Count)
                    DumpListEntry(splatTexturesList, riverShorelineTex);
                Log("===== [LayerShape] end =====");
            }
            catch (Exception ex)
            {
                Log($"DumpCombinedLayers failed: {ex.Message}");
            }
        }

        private static void DumpLayerList(IList? layers, string prefix)
        {
            if (layers == null) { Log($"  ({prefix}) null"); return; }
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                if (l == null) { Log($"  ({prefix})[{i}] = null"); continue; }
                string diffName = (ExtractTexture(l, new[] { "diffuse", "Diffuse", "texture", "Texture", "albedo", "diffuseMap" }) as UnityEngine.Object)?.name ?? "?";
                string normName = (ExtractTexture(l, new[] { "normal", "Normal", "normalMap" }) as UnityEngine.Object)?.name ?? "?";
                Log($"  ({prefix})[{i}] {l.GetType().Name}  diffuse={diffName}  normal={normName}");
            }
        }

        /// <summary>
        /// Dump every field and property of an object with their values.
        /// Useful when we don't know the schema.
        /// </summary>
        private static void DumpAllMembers(object obj)
        {
            try
            {
                Type t = obj.GetType();
                Log($"  Type: {t.FullName}");

                // Fields (declared + inherited up to non-Unity base)
                Type? walker = t;
                while (walker != null && walker != typeof(object) &&
                       walker != typeof(UnityEngine.MonoBehaviour) &&
                       walker != typeof(UnityEngine.ScriptableObject) &&
                       walker != typeof(UnityEngine.Object))
                {
                    foreach (var f in walker.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                        BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        object? v = null;
                        try { v = f.GetValue(obj); } catch { }
                        string repr = ReprValue(v);
                        Log($"    field {walker.Name}::{f.Name} ({f.FieldType.Name}) = {repr}");
                    }
                    walker = walker.BaseType;
                }

                // Properties (top-level only — most informative ones)
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object? v = null;
                    try { v = p.GetValue(obj, null); } catch { continue; }
                    string repr = ReprValue(v);
                    Log($"    prop  {t.Name}::{p.Name} ({p.PropertyType.Name}) = {repr}");
                }
            }
            catch (Exception ex)
            {
                Log($"  DumpAllMembers exception: {ex.Message}");
            }
        }

        private static string ReprValue(object? v)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object uo) return $"UO[{uo.name}]";
            if (v is System.Collections.ICollection col) return $"Collection(Count={col.Count})";
            string s = v.ToString() ?? "";
            if (s.Length > 80) s = s.Substring(0, 80) + "…";
            return s;
        }

        private static void DumpListEntry(IList list, int idx)
        {
            var e = list[idx];
            if (e == null) { Log("  null"); return; }
            Type t = e.GetType();
            Log($"  {t.FullName}");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object? v = null;
                try { v = f.GetValue(e); } catch { }
                string name = (v as UnityEngine.Object)?.name ?? v?.ToString() ?? "null";
                if (name.Length > 60) name = name.Substring(0, 60) + "…";
                Log($"    {f.Name} ({f.FieldType.Name}) = {name}");
            }
        }

        public static void CarveAllRivers(TerrainGenerator __instance)
        {
            try
            {
                if (!RiversRestoredMod.RiversEnabled.Value) return;
                if (_carved) return;
                // CRITICAL: this check is NOT a duplicate of OnUpdate's gate.
                // CarveAllRivers is also called directly from
                // RiverSettingsPatch.LateCarvePostfix — a Harmony postfix that
                // fires on FF's late-stage terrain methods. Those same methods
                // run during save reload as part of FF's terrain reconstruction,
                // so without this guard the carver runs during reload and
                // overwrites/breaks the saved water state (lakes appear missing
                // after reload because our carve writes cascade through the
                // load pipeline).
                if (RiverSettingsPatch.IsLoadingSavedMap(__instance)) return;
                // Save-load belt-and-suspenders: skip if persistence has
                // marked a restore in flight, so the carver doesn't fight
                // the persistence layer's spawn-from-disk path.
                if (RiverPersistence.RestorePending) return;
                if (RiverPersistence.RestoredThisLoad) return;

                // ── 0) Fast pre-check: if rivers list isn't populated yet,
                // bail silently. CarveAllRivers gets called every frame
                // during gen until _carved flips true; without this gate,
                // every frame ran the full reflection prologue + logged it,
                // producing ~350 redundant prologue blocks per gen and
                // freezing the main thread on log I/O. Defer all heavy
                // work until rivers actually exist.
                {
                    var gdFieldFast = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                    var gdFast = gdFieldFast?.GetValue(__instance);
                    if (gdFast == null) return;
                    var riversFieldFast = gdFast.GetType().GetField("rivers",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var riversFast = riversFieldFast?.GetValue(gdFast) as IList;
                    if (riversFast == null || riversFast.Count == 0) return;
                }

                // ── 1) Locate FF's terrain API instances ─────────────────
                // TerrainManagerBase: handles per-cell height read/write
                // Terrain2:           handles mesh rebuild after height changes
                //
                // Cached on first successful resolution and reused. Pre-cache,
                // FindObjectOfType ran every frame during the load window
                // (potentially seconds × 60fps = thousands of scans).
                if (_cachedTerrainManagerType == null)
                    _cachedTerrainManagerType = AccessTools.TypeByName("TerrainManagerBase");
                if (_cachedTerrain2Type == null)
                    _cachedTerrain2Type = AccessTools.TypeByName("LibTerrain2.Terrain2")
                                          ?? AccessTools.TypeByName("Terrain2");

                if (_cachedTerrainManagerType == null || _cachedTerrain2Type == null)
                {
                    Log($"Type not found: TerrainManagerBase={_cachedTerrainManagerType != null} Terrain2={_cachedTerrain2Type != null}");
                    return;
                }

                if (_cachedTerrainManagerInstance == null)
                    _cachedTerrainManagerInstance = UnityEngine.Object.FindObjectOfType(_cachedTerrainManagerType);
                if (_cachedTerrain2Instance == null)
                    _cachedTerrain2Instance = UnityEngine.Object.FindObjectOfType(_cachedTerrain2Type);

                var tm = _cachedTerrainManagerInstance;
                var t2 = _cachedTerrain2Instance;
                if (tm == null || t2 == null)
                {
                    // Not yet alive — caller (OnUpdate) will retry. Don't
                    // cache nulls; loop back through FindObjectOfType next frame.
                    return;
                }
                // Local aliases for the cached Type refs (downstream code reads
                // these freely; keeping the names matches the pre-cache version
                // so no other edits in this method are required).
                var tmType = _cachedTerrainManagerType;
                var terrain2Type = _cachedTerrain2Type;

                if (!_dumpedAPIShape)
                {
                    _dumpedAPIShape = true;
                    Log($"Found terrain API: TerrainManagerBase={tm.GetType().FullName}  " +
                        $"Terrain2={t2.GetType().FullName}");
                }

                // ── Resolve Terrain2.Data.ControlTextures + Terrain2Control API ──
                // This is the LIVE splat data (separate from _generationData.splatMaps).
                // Pangu writes here so its lakes survive save/reload visually.
                var dataMember = (object?)terrain2Type.GetProperty("Data",
                                    BindingFlags.Public | BindingFlags.Instance)
                                 ?? terrain2Type.GetField("Data",
                                    BindingFlags.Public | BindingFlags.Instance);
                object? terrainData = null;
                if (dataMember is PropertyInfo dp) terrainData = dp.GetValue(t2);
                else if (dataMember is FieldInfo df) terrainData = df.GetValue(t2);

                IList? controlTextures = null;
                int ctSize = 0;
                MethodInfo? setPixelMI = null;
                MethodInfo? uploadMI = null;
                if (terrainData != null)
                {
                    Type tdType = terrainData.GetType();
                    var ctMember = (object?)tdType.GetProperty("ControlTextures",
                                        BindingFlags.Public | BindingFlags.Instance)
                                     ?? tdType.GetField("ControlTextures",
                                        BindingFlags.Public | BindingFlags.Instance);
                    if (ctMember is PropertyInfo cp) controlTextures = cp.GetValue(terrainData) as IList;
                    else if (ctMember is FieldInfo cf) controlTextures = cf.GetValue(terrainData) as IList;

                    var ctsMember = (object?)tdType.GetProperty("ControlTextureSize",
                                        BindingFlags.Public | BindingFlags.Instance)
                                     ?? tdType.GetField("ControlTextureSize",
                                        BindingFlags.Public | BindingFlags.Instance);
                    if (ctsMember is PropertyInfo csp) ctSize = (int)(csp.GetValue(terrainData) ?? 0);
                    else if (ctsMember is FieldInfo csf) ctSize = (int)(csf.GetValue(terrainData) ?? 0);

                    if (controlTextures != null && controlTextures.Count > 0)
                    {
                        Type t2cType = controlTextures[0]!.GetType();
                        setPixelMI = t2cType.GetMethod("SetPixelComponent",
                            BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(int), typeof(int), typeof(int), typeof(float) }, null);
                        uploadMI = t2cType.GetMethod("Upload",
                            BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(bool) }, null);
                    }
                }
                Log($"  ControlTextures: count={controlTextures?.Count ?? -1}  size={ctSize}  " +
                    $"setPixel={setPixelMI != null}  upload={uploadMI != null}");

                // One-shot: dump terrain.Data's full member list so we find
                // whichever collection holds the live layers. Verbose-only.
                if (!_dumpedTerrainData && terrainData != null &&
                    (RiversRestoredMod.VerboseDiagnostics?.Value ?? false))
                {
                    _dumpedTerrainData = true;
                    Log("===== [TerrainDataDump] terrain.Data FULL MEMBERS =====");
                    DumpAllMembers(terrainData);
                    Log("===== [TerrainDataDump] end =====");
                }

                // ── 2) Resolve method handles ────────────────────────────
                var setHeightMI = tmType.GetMethod("SetHeight",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(int), typeof(int), typeof(float) }, null);
                var getHeightMI = tmType.GetMethod("GetHeight",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(float), typeof(float) }, null);
                var smoothNotifyMI = terrain2Type.GetMethod("SmoothHeightsNotify",
                    BindingFlags.Public | BindingFlags.Instance);
                if (setHeightMI == null || smoothNotifyMI == null)
                {
                    Log($"API methods missing: SetHeight={setHeightMI != null}  " +
                        $"SmoothHeightsNotify={smoothNotifyMI != null}");
                    return;
                }

                // ── 3) Read mapSettings for coordinate conversion ────────
                var mapSettingsField = AccessTools.Field(typeof(TerrainGenerator), "mapSettings");
                var mapSettings = mapSettingsField?.GetValue(__instance);
                if (mapSettings == null) { Log("mapSettings null"); return; }
                var msType = mapSettings.GetType();
                int hmRes = (int)msType.GetField("heightmapResolution").GetValue(mapSettings);
                int mapW = (int)msType.GetField("width").GetValue(mapSettings);
                int mapD = (int)msType.GetField("depth").GetValue(mapSettings);
                int mapH = (int)msType.GetField("height").GetValue(mapSettings);
                Log($"mapSettings: heightmapRes={hmRes}  width={mapW}  depth={mapD}  height={mapH}");

                // ── 4) Determine water level + trench height ────────────
                float waterHeight = 0f;
                try
                {
                    var ghMI = typeof(TerrainGenerator).GetMethod("GetWaterHeight",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (ghMI != null) waterHeight = (float)ghMI.Invoke(__instance, null);
                }
                catch { }
                var effective = RiversRestoredMod.GetEffectiveValues();
                float trenchDepth = effective.TrenchDepth;
                float trenchHeight = waterHeight - trenchDepth;
                Log($"Carve target: waterHeight={waterHeight:F2}  trenchHeight={trenchHeight:F2}  width=±3 cells");

                // ── 5) Get rivers list ───────────────────────────────────
                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(__instance);
                if (gd == null) { Log("_generationData null"); return; }

                var riversField = gd.GetType().GetField("rivers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var rivers = riversField?.GetValue(gd) as IList;
                if (rivers == null || rivers.Count == 0) { Log("no rivers to carve"); return; }
                Log($"CarveAllRivers: {rivers.Count} river(s) to process");

                // heightNoise dual-write target
                var heightNoiseField = gd.GetType().GetField("heightNoise",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var heightNoise = heightNoiseField?.GetValue(gd) as float[,];

                // splatMaps + texture indices for proper riverbed painting
                // (without this, save/reload re-derives splats from heights
                //  and applies a wrong "shoreline" splat that renders yellow)
                var splatMapsField = gd.GetType().GetField("splatMaps",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var splatMaps = splatMapsField?.GetValue(gd) as float[,,];
                var underwaterIdxField = gd.GetType().GetField("riverUnderwaterTexture",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var shorelineIdxField = gd.GetType().GetField("riverShorelineTexture",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                int riverUnderwaterTex = underwaterIdxField != null ? (int)underwaterIdxField.GetValue(gd) : -1;
                int riverShorelineTex = shorelineIdxField != null ? (int)shorelineIdxField.GetValue(gd) : -1;
                int splatChannels = splatMaps?.GetLength(2) ?? 0;
                Log($"  Splat: underwaterTex={riverUnderwaterTex} shorelineTex={riverShorelineTex} channels={splatChannels}");

                // ── Splat layer mapping (real Pangu pattern) ─────────────
                // splatTexturesList[N] indices are GENERATION-side (24 entries).
                // terrain.Data.TextureLayers is RUNTIME-side (count = ControlTextures.Count * 4).
                // To paint, we must match by Texture2D reference: find which
                // live layer slot's .diffuse + .normal equal splatTexturesList[N]'s.
                int underwaterCtrlIdx = -1, underwaterCtrlChan = -1;
                int shorelineCtrlIdx = -1, shorelineCtrlChan = -1;
                if (terrainData != null)
                {
                    Type tdType = terrainData.GetType();
                    var layersMember = (object?)tdType.GetProperty("TextureLayers",
                                            BindingFlags.Public | BindingFlags.Instance)
                                         ?? tdType.GetField("TextureLayers",
                                            BindingFlags.Public | BindingFlags.Instance);
                    IList? textureLayers = null;
                    if (layersMember is PropertyInfo lp) textureLayers = lp.GetValue(terrainData) as IList;
                    else if (layersMember is FieldInfo lf) textureLayers = lf.GetValue(terrainData) as IList;

                    // ALSO check CustomLayers — that's where river textures live
                    var customLayersMember = (object?)tdType.GetProperty("CustomLayers",
                                                BindingFlags.Public | BindingFlags.Instance)
                                             ?? tdType.GetField("customLayers",
                                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    IList? customLayers = null;
                    if (customLayersMember is PropertyInfo clp) customLayers = clp.GetValue(terrainData) as IList;
                    else if (customLayersMember is FieldInfo clf) customLayers = clf.GetValue(terrainData) as IList;

                    // Concatenated layer list = TextureLayers ∪ CustomLayers.
                    // The combined index maps to (controlIdx, channel) via /4, %4.
                    // (or the layout might be Custom-first; we'll try TextureLayers
                    // first since that's Pangu's convention.)
                    var combinedLayers = new System.Collections.Generic.List<object>();
                    if (textureLayers != null)
                        foreach (var l in textureLayers) combinedLayers.Add(l!);
                    if (customLayers != null)
                        foreach (var l in customLayers) combinedLayers.Add(l!);
                    Log($"  Combined layers: TextureLayers={textureLayers?.Count ?? 0} + " +
                        $"CustomLayers={customLayers?.Count ?? 0} = {combinedLayers.Count}");

                    // Read splatTexturesList from generationData (the catalog)
                    var stlField = gd.GetType().GetField("splatTexturesList",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var stl = stlField?.GetValue(gd) as IList;

                    if (combinedLayers.Count > 0 && stl != null)
                    {
                        // Diagnostic: dump CustomLayers content once. Verbose-only.
                        if (!_dumpedLayerShape &&
                            (RiversRestoredMod.VerboseDiagnostics?.Value ?? false))
                        {
                            _dumpedLayerShape = true;
                            DumpCombinedLayers(textureLayers, customLayers, stl, riverUnderwaterTex, riverShorelineTex);
                        }

                        // Match by Texture2D reference across BOTH lists concatenated.
                        // Pangu's formula assumes layerIdx/4 = controlIdx, layerIdx%4 = channel.
                        underwaterCtrlIdx = ResolveLiveLayer(combinedLayers, stl, riverUnderwaterTex, out underwaterCtrlChan);
                        shorelineCtrlIdx  = ResolveLiveLayer(combinedLayers, stl, riverShorelineTex, out shorelineCtrlChan);
                    }
                }
                Log($"  Live layer match: underwater→ctrlIdx={underwaterCtrlIdx} chan={underwaterCtrlChan}  " +
                    $"shoreline→ctrlIdx={shorelineCtrlIdx} chan={shorelineCtrlChan}");
                if (underwaterCtrlIdx < 0 && shorelineCtrlIdx < 0)
                    Log("  ⚠ Neither riverbed texture is loaded into a live layer slot — splat painting will be skipped (yellow may persist).");

                // Track which Terrain2Control instances we've written to,
                // so we can batch-Upload at the end (Pangu pattern).
                var touchedControls = new System.Collections.Generic.HashSet<object>();

                // ── 6) Carve each river ──────────────────────────────────
                // Two radii for a banked profile:
                //   innerRadius — full-depth trench (slam to trenchHeight)
                //   outerRadius — blend zone (smoothstep back up to original)
                // Plus path jitter: subdivide each segment with sinusoidal
                // perpendicular offset for a meandering look.
                int innerRadius = effective.InnerRadius;
                int outerRadius = effective.OuterRadius;
                float jitterAmp = effective.JitterAmplitude;
                float jitterFreq = effective.JitterFrequency;
                Log($"  Bank profile: innerRadius={innerRadius} outerRadius={outerRadius}  " +
                    $"jitter amp={jitterAmp} freq={jitterFreq}");
                int allMinX = int.MaxValue, allMinZ = int.MaxValue;
                int allMaxX = int.MinValue, allMaxZ = int.MinValue;
                int totalCells = 0;

                if (!_dumpedShape && rivers.Count > 0 && rivers[0] != null &&
                    (RiversRestoredMod.VerboseDiagnostics?.Value ?? false))
                {
                    DumpShape(rivers[0]);
                    _dumpedShape = true;
                }

                for (int i = 0; i < rivers.Count; i++)
                {
                    var r = rivers[i];
                    if (r == null) continue;
                    var pointsField = r.GetType().GetField("points",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var points = pointsField?.GetValue(r) as IList;
                    if (points == null || points.Count < 2)
                    {
                        Log($"  river[{i}]: skipped ({points?.Count ?? 0} points)");
                        continue;
                    }

                    int riverCells = 0;
                    // Build a jittered sub-path: for each Voronoi segment,
                    // interpolate N sub-points and apply perpendicular offset
                    // via sin wave + offset-per-river so each river meanders
                    // differently. Stage 38's path is the spine; this adds
                    // organic wiggles between control points.
                    var jitteredPath = BuildJitteredPath(points, jitterAmp, jitterFreq, riverSeed: i * 31 + 17);

                    for (int p = 0; p < jitteredPath.Count - 1; p++)
                    {
                        var p0 = jitteredPath[p];
                        var p1 = jitteredPath[p + 1];

                        int hx0 = WorldToHmX(p0.x, mapW, hmRes);
                        int hz0 = WorldToHmZ(p0.y, mapD, hmRes);
                        int hx1 = WorldToHmX(p1.x, mapW, hmRes);
                        int hz1 = WorldToHmZ(p1.y, mapD, hmRes);

                        riverCells += BresenhamCarve(setHeightMI, getHeightMI, tm,
                            heightNoise, mapH, mapW, mapD,
                            hx0, hz0, hx1, hz1, innerRadius, outerRadius,
                            trenchHeight, waterHeight, hmRes,
                            splatMaps, splatChannels, riverUnderwaterTex, riverShorelineTex,
                            controlTextures, ctSize, setPixelMI,
                            underwaterCtrlIdx, underwaterCtrlChan,
                            shorelineCtrlIdx, shorelineCtrlChan,
                            touchedControls,
                            ref allMinX, ref allMinZ, ref allMaxX, ref allMaxZ);
                    }
                    Log($"  river[{i}]: {points.Count} points → {riverCells} cells slammed");
                    totalCells += riverCells;
                }

                // ── 7) Trigger mesh rebuild for the bounding rect ─────────
                if (totalCells > 0 && allMinX < int.MaxValue)
                {
                    // Pad by outer radius + smoothing reach so chunk edges resolve cleanly
                    int smoothPasses = effective.SmoothPasses;
                    int pad = outerRadius + smoothPasses + 2;
                    int rMinX = Math.Max(0, allMinX - pad);
                    int rMinZ = Math.Max(0, allMinZ - pad);
                    int rMaxX = Math.Min(hmRes - 1, allMaxX + pad);
                    int rMaxZ = Math.Min(hmRes - 1, allMaxZ + pad);

                    // ── 7a) Iterative box-blur smoothing ─────────────────
                    // Soften any remaining hard edges from the carve by
                    // averaging each cell with its 3x3 neighbors over N passes.
                    // Water-level cells are protected (won't be raised).
                    if (smoothPasses > 0 && heightNoise != null)
                    {
                        Log($"  → Smoothing {smoothPasses} pass(es) over [{rMinX},{rMinZ} .. {rMaxX},{rMaxZ}]");
                        // Convert waterHeight to normalized for water-protection check
                        float waterNormProtect = (waterHeight - 0.1f) / mapH;
                        float waterNormSurface = waterHeight / (float)mapH;
                        int smoothedCells = SmoothBoxBlur(setHeightMI, tm, heightNoise,
                            rMinX, rMinZ, rMaxX, rMaxZ, hmRes, mapH,
                            smoothPasses, waterNormProtect, waterNormSurface);
                        Log($"  → Smoothing modified {smoothedCells} cells");
                    }

                    Log($"  → SmoothHeightsNotify rect=[{rMinX},{rMinZ} .. {rMaxX},{rMaxZ}]");
                    try
                    {
                        // Pangu calls SmoothHeightsNotify TWICE for lakes (smoothing pass)
                        smoothNotifyMI.Invoke(t2, new object[] { tm, rMinX, rMinZ, rMaxX, rMaxZ, true });
                        smoothNotifyMI.Invoke(t2, new object[] { tm, rMinX, rMinZ, rMaxX, rMaxZ, true });
                        Log("  → SmoothHeightsNotify completed");
                    }
                    catch (Exception ex)
                    {
                        Exception inner = (ex is TargetInvocationException tie && tie.InnerException != null)
                            ? tie.InnerException : ex;
                        Log($"  → SmoothHeightsNotify FAILED: {inner.GetType().Name}: {inner.Message}");
                    }
                }

                // ── 8) Batch-Upload all touched ControlTextures (Pangu pattern) ──
                if (uploadMI != null && touchedControls.Count > 0)
                {
                    Log($"  → Uploading {touchedControls.Count} ControlTexture(s)…");
                    int uploaded = 0;
                    foreach (var ctrl in touchedControls)
                    {
                        try
                        {
                            uploadMI.Invoke(ctrl, new object[] { false });
                            uploaded++;
                        }
                        catch (Exception ex)
                        {
                            Log($"  → Upload({ctrl.GetType().Name}) failed: {ex.Message}");
                        }
                    }
                    Log($"  → Uploaded {uploaded}/{touchedControls.Count} controls");
                }

                _carved = true;
                Log($"CarveAllRivers: DONE ({totalCells} cells across {rivers.Count} rivers)");

                // (WaterArea registration + Pangu-style merge happens earlier
                // — see RiverSettingsPatch.InjectStage38Postfix → builder
                // walk-and-stamp loop. Doing it here would be too late:
                // tree/rock/animal placement runs between Stage 38 and our
                // LateCarvePostfix, so resources would already be on river
                // cells. v0.2: no separate post-pass merge — merge is inline
                // per stamp via AddWaterAreaWithPanguMerge.)
            }
            catch (Exception ex)
            {
                Log($"CarveAllRivers EXCEPTION: {ex}");
            }
        }

        // ── Bresenham line walk + per-step banked footprint ─────────────────
        private static int BresenhamCarve(MethodInfo setHeightMI, MethodInfo? getHeightMI, object tm,
                                            float[,]? heightNoise, int mapH, int mapW, int mapD,
                                            int x0, int z0, int x1, int z1,
                                            int innerRadius, int outerRadius,
                                            float trenchHeight, float waterHeight, int hmRes,
                                            float[,,]? splatMaps, int splatChannels,
                                            int underwaterTex, int shorelineTex,
                                            IList? controlTextures, int ctSize, MethodInfo? setPixelMI,
                                            int uwCtrlIdx, int uwChan, int shCtrlIdx, int shChan,
                                            System.Collections.Generic.HashSet<object> touchedControls,
                                            ref int minX, ref int minZ, ref int maxX, ref int maxZ)
        {
            int touched = 0;
            int dx = Math.Abs(x1 - x0), dz = Math.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1;
            int err = dx - dz;
            int x = x0, z = z0;

            while (true)
            {
                touched += DropDisc(setHeightMI, getHeightMI, tm, heightNoise, mapH, mapW, mapD,
                                     x, z, innerRadius, outerRadius, trenchHeight, waterHeight, hmRes,
                                     splatMaps, splatChannels, underwaterTex, shorelineTex,
                                     controlTextures, ctSize, setPixelMI,
                                     uwCtrlIdx, uwChan, shCtrlIdx, shChan, touchedControls,
                                     ref minX, ref minZ, ref maxX, ref maxZ);
                if (x == x1 && z == z1) break;
                int e2 = err * 2;
                if (e2 > -dz) { err -= dz; x += sx; }
                if (e2 <  dx) { err += dx; z += sz; }
            }
            return touched;
        }

        /// <summary>
        /// Banked carve footprint:
        ///   distance ≤ innerRadius  →  slam to trenchHeight (full depth)
        ///   distance ≤ outerRadius  →  linear ramp from waterHeight up to
        ///                              original terrain height (banks rise
        ///                              cleanly out of the water)
        ///   distance > outerRadius  →  untouched
        /// Never raises terrain — only modifies if new height < current.
        ///
        /// Note: blend zone starts at waterHeight (not trenchHeight) because
        /// cells ramping below waterHeight outside the inner radius look like
        /// extra flanking water on fresh-gen (Stage 50's water plane happens
        /// to extend over them) but DON'T survive save/reload — Stage 50's
        /// saved waterAreas polygons don't dynamically extend to cover cells
        /// we lower. Result on reload: heightmap is below water but no water
        /// plane covers them → exposed muddy strips beside the river. Pinning
        /// the blend bottom to waterHeight gives matching gen/reload visuals.
        /// </summary>
        private static int DropDisc(MethodInfo setHeightMI, MethodInfo? getHeightMI, object tm,
                                      float[,]? heightNoise, int mapH, int mapW, int mapD,
                                      int cx, int cz, int innerRadius, int outerRadius,
                                      float trenchHeight, float waterHeight, int hmRes,
                                      float[,,]? splatMaps, int splatChannels,
                                      int underwaterTex, int shorelineTex,
                                      IList? controlTextures, int ctSize, MethodInfo? setPixelMI,
                                      int uwCtrlIdx, int uwChan, int shCtrlIdx, int shChan,
                                      System.Collections.Generic.HashSet<object> touchedControls,
                                      ref int minX, ref int minZ, ref int maxX, ref int maxZ)
        {
            int touched = 0;
            int blendBand = Math.Max(1, outerRadius - innerRadius);
            float invBlend = 1f / blendBand;

            for (int dz = -outerRadius; dz <= outerRadius; dz++)
            {
                int z = cz + dz;
                if (z < 0 || z >= hmRes) continue;
                for (int dx = -outerRadius; dx <= outerRadius; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= hmRes) continue;

                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist > outerRadius) continue;

                    // Read original height from heightNoise (the canonical
                    // source — normalized 0..1, multiply by mapH for world units).
                    // Falls back to trenchHeight if heightNoise unavailable.
                    float origHeight = trenchHeight;
                    if (heightNoise != null && mapH > 0)
                        origHeight = heightNoise[x, z] * mapH;

                    float h;
                    if (dist <= innerRadius)
                    {
                        // Inner trench: slam to trench height (unconditional).
                        // If terrain is already lower (lake floor), keep it lower —
                        // don't push lakes up.
                        h = Mathf.Min(trenchHeight, origHeight);
                    }
                    else
                    {
                        // Bank slope: linear from waterHeight (at innerRadius
                        // edge) up to origHeight (at outerRadius edge). The
                        // discontinuity from trenchHeight to waterHeight at
                        // the inner-edge boundary gets smoothed away by the
                        // SmoothBoxBlur passes.
                        float t = (dist - innerRadius) * invBlend; // 0..1
                        h = Mathf.Lerp(waterHeight, origHeight, t);
                        // If origHeight is below waterHeight (already a lake
                        // floor), keep it as-is — don't push lake floors up.
                        if (h > origHeight) h = origHeight;
                    }

                    // Skip if no change (avoid spurious mesh rebuilds)
                    if (h >= origHeight) continue;

                    setHeightMI.Invoke(tm, new object[] { x, z, h });
                    if (heightNoise != null && mapH > 0)
                    {
                        float normalized = h / mapH;
                        if (heightNoise[x, z] > normalized)
                            heightNoise[x, z] = normalized;
                    }

                    // ── Splat painting (inner trench only) ──────────────────
                    // We paint ONLY the inner-radius cells with the underwater
                    // texture. The water-path ribbon covers them, so even at
                    // full saturation it looks fine.
                    //
                    // We DO NOT paint the bank zone with shoreline texture.
                    // FF's ControlTextures don't persist our high-saturation
                    // writes across save/reload (the load pipeline resets
                    // them to biome defaults). On fresh gen, painting bank
                    // cells with shoreline at 1.0 alpha + zeroing other
                    // channels produces a fluorescent-yellow band visible on
                    // gen but absent on reload — bad mismatch. Leaving the
                    // bank's biome texture untouched gives matching gen and
                    // reload visuals (clean bank, biome-natural).
                    bool isInner = dist <= innerRadius;
                    if (isInner && splatMaps != null && splatChannels > 0
                        && underwaterTex >= 0 && underwaterTex < splatChannels)
                    {
                        for (int c = 0; c < splatChannels; c++)
                            splatMaps[x, z, c] = 0f;
                        splatMaps[x, z, underwaterTex] = 1f;
                    }
                    if (isInner && controlTextures != null && controlTextures.Count > 0
                        && setPixelMI != null && ctSize > 0
                        && uwCtrlIdx >= 0 && uwCtrlIdx < controlTextures.Count)
                    {
                        int ctlX = (int)((float)x / (hmRes - 1) * (ctSize - 1));
                        int ctlZ = (int)((float)z / (hmRes - 1) * (ctSize - 1));
                        if (ctlX >= 0 && ctlX < ctSize && ctlZ >= 0 && ctlZ < ctSize)
                        {
                            var targetCtrl = controlTextures[uwCtrlIdx]!;
                            try
                            {
                                setPixelMI.Invoke(targetCtrl,
                                    new object[] { ctlX, ctlZ, uwChan, 1.0f });
                                touchedControls.Add(targetCtrl);

                                // Zero other channels so the underwater texture
                                // is solid (riverbed should be wet/rocky-looking
                                // — biome bleed-through under water looks weird).
                                for (int ci = 0; ci < controlTextures.Count; ci++)
                                {
                                    var ctrl = controlTextures[ci]!;
                                    for (int ch = 0; ch < 4; ch++)
                                    {
                                        if (ci == uwCtrlIdx && ch == uwChan) continue;
                                        setPixelMI.Invoke(ctrl,
                                            new object[] { ctlX, ctlZ, ch, 0.0f });
                                    }
                                    touchedControls.Add(ctrl);
                                }
                            }
                            catch { /* skip on per-cell failure, keep batch alive */ }
                        }
                    }

                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                    touched++;
                }
            }
            return touched;
        }

        /// <summary>
        /// Build a jittered sub-path: for each consecutive pair of control
        /// points, subdivide into N sub-points and apply a perpendicular
        /// sin-wave offset. The result is a meandering version of the
        /// Voronoi-generated spine.
        /// </summary>
        private static System.Collections.Generic.List<Vector2>
            BuildJitteredPath(IList controlPoints, float amplitude, float frequency, int riverSeed)
        {
            var result = new System.Collections.Generic.List<Vector2>();
            int subdivPerSegment = 8; // higher = smoother wiggle
            float phase = (riverSeed * 0.1f) % (2f * Mathf.PI);

            for (int p = 0; p < controlPoints.Count - 1; p++)
            {
                if (!TryGetPointXZ(controlPoints[p],     out float x0, out float z0)) continue;
                if (!TryGetPointXZ(controlPoints[p + 1], out float x1, out float z1)) continue;

                float dx = x1 - x0, dz = z1 - z0;
                float len = Mathf.Sqrt(dx * dx + dz * dz);
                if (len < 0.01f) continue;
                // Perpendicular (left-hand)
                float px = -dz / len;
                float pz =  dx / len;

                int steps = Math.Max(1, subdivPerSegment);
                for (int s = 0; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    float baseX = Mathf.Lerp(x0, x1, t);
                    float baseZ = Mathf.Lerp(z0, z1, t);

                    // Sin offset, fading to zero at endpoints so segments connect cleanly
                    float fade = Mathf.Sin(t * Mathf.PI); // 0 at t=0 and 1, 1 at t=0.5
                    float offset = Mathf.Sin(t * frequency * 2f * Mathf.PI + phase + p) * amplitude * fade;

                    result.Add(new Vector2(baseX + px * offset, baseZ + pz * offset));
                }
            }
            return result;
        }

        /// <summary>
        /// Iterative 3×3 box-blur smoothing on the heightNoise array within
        /// the given rect. Each pass averages every interior cell with its
        /// 8 neighbors and writes the result back via SetHeight + heightNoise.
        ///
        /// Two-sided water protection (both prevent gen/reload visual mismatch):
        ///   waterNormProtect: cells already at/below water surface are NEVER
        ///     raised. Prevents smoothing from filling in river trenches.
        ///   waterNormSurface: cells already ABOVE water are never lowered
        ///     BELOW it. Prevents smoothing from creating new sub-water cells
        ///     out of bank slope cells — those sub-water cells would look
        ///     like wide flanking water on fresh-gen but disappear on save/
        ///     reload (the saved water-plane polygon doesn't extend to cover
        ///     them, so the carved heightmap stays exposed).
        /// </summary>
        private static int SmoothBoxBlur(MethodInfo setHeightMI, object tm, float[,] heightNoise,
                                           int minX, int minZ, int maxX, int maxZ, int hmRes, int mapH,
                                           int passes, float waterNormProtect, float waterNormSurface)
        {
            int totalChanged = 0;
            int w = maxX - minX + 1;
            int h = maxZ - minZ + 1;
            if (w < 3 || h < 3) return 0;

            // Reusable snapshot buffer
            float[,] snap = new float[w, h];

            for (int pass = 0; pass < passes; pass++)
            {
                // 1) Capture current heightNoise state into local buffer
                for (int z = minZ; z <= maxZ; z++)
                    for (int x = minX; x <= maxX; x++)
                        snap[x - minX, z - minZ] = heightNoise[x, z];

                // 2) For each interior cell, compute 3×3 average and write back
                int passChanged = 0;
                for (int z = minZ + 1; z < maxZ; z++)
                {
                    for (int x = minX + 1; x < maxX; x++)
                    {
                        int sx = x - minX, sz = z - minZ;
                        float current = snap[sx, sz];

                        // Compute 3×3 average from snapshot
                        float sum = 0f;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                                sum += snap[sx + dx, sz + dz];
                        float avg = sum / 9f;

                        // Trench protection: never raise cells at/below water level
                        if (current <= waterNormProtect && avg > current)
                            avg = current;

                        // Bank protection (soft): cells starting above water can
                        // drop, but not more than 0.5m below the water surface.
                        // Rigid clamping at waterSurface (the previous behavior)
                        // froze the trench-bank boundary cells in place, leaving
                        // a visible 2m stair-step where the trench depth meets
                        // the bank rise. Allowing 0.5m of dip lets smoothing
                        // soften that step over multiple passes — and the dip
                        // is small enough that the WaterPath ribbon (which
                        // lives at waterHeight and spans cp.width across) still
                        // covers it visually on both gen and reload.
                        float dipFloor = waterNormSurface - (0.5f / (float)mapH);
                        if (current >= waterNormSurface && avg < dipFloor)
                            avg = dipFloor;

                        // Skip negligible changes
                        if (Mathf.Abs(avg - current) < 1e-5f) continue;

                        heightNoise[x, z] = avg;
                        try { setHeightMI.Invoke(tm, new object[] { x, z, avg * mapH }); }
                        catch { }
                        passChanged++;
                    }
                }
                totalChanged += passChanged;
            }
            return totalChanged;
        }

        // ── World→Heightmap coordinate conversion (Pangu pattern) ─────────
        private static int WorldToHmX(float wx, int mapW, int hmRes)
            => Mathf.Clamp(Mathf.RoundToInt(wx / mapW * (hmRes - 1)), 0, hmRes - 1);
        private static int WorldToHmZ(float wz, int mapD, int hmRes)
            => Mathf.Clamp(Mathf.RoundToInt(wz / mapD * (hmRes - 1)), 0, hmRes - 1);

        // ── ControlPoint world-XZ extraction ──────────────────────────────
        private static bool TryGetPointXZ(object pt, out float x, out float z)
        {
            x = 0; z = 0;
            try
            {
                Type t = pt.GetType();
                foreach (var name in new[] { "position", "pos", "worldPos", "worldPosition" })
                {
                    var f = t.GetField(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f == null) continue;
                    var v = f.GetValue(pt);
                    if (v is Vector3 v3) { x = v3.x; z = v3.z; return true; }
                    if (v is Vector2 v2) { x = v2.x; z = v2.y; return true; }
                }
                var fx = t.GetField("x", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fz = t.GetField("z", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fx != null && fz != null && fx.FieldType == typeof(float) && fz.FieldType == typeof(float))
                {
                    x = (float)fx.GetValue(pt);
                    z = (float)fz.GetValue(pt);
                    return true;
                }
            }
            catch { }
            return false;
        }

        // ── One-time TerrainRiver / ControlPoint shape dump ───────────────
        private static void DumpShape(object river)
        {
            try
            {
                Type rt = river.GetType();
                Log($"===== [Carver shape] TerrainRiver = {rt.FullName} =====");
                foreach (var f in rt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    Log($"  {f.Name} ({f.FieldType.Name})");
                var pointsField = rt.GetField("points",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var points = pointsField?.GetValue(river) as IList;
                if (points != null && points.Count > 0)
                {
                    Type pt = points[0].GetType();
                    Log($"===== [Carver shape] ControlPoint = {pt.FullName} =====");
                    foreach (var f in pt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        object? v = null;
                        try { v = f.GetValue(points[0]); } catch { }
                        Log($"  {f.Name} ({f.FieldType.Name}) = {v}");
                    }
                }
                Log("===== [Carver shape] end =====");
            }
            catch (Exception ex) { Log($"DumpShape failed: {ex.Message}"); }
        }

        private static void Log(string msg) => RiversRestoredMod.Log.Msg($"[RR][Carve] {msg}");
    }
}
