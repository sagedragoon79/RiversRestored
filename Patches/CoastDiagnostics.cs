using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using TerrainGen;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Coast diagnostics: the settings that gate ocean classification (including
    /// every field of the ocean WaterType), a per-river "ends in the sea" line,
    /// and a per-area report of what the classifier produced. Verbose parts are
    /// gated on <c>VerboseDiagnostics</c>; the ocean and river lines always log.
    /// </summary>
    internal static class CoastDiagnostics
    {
        private static bool _dumpedAssets;

        private static string N(UnityEngine.Object? o) => o != null ? o.name : "null";

        private static string Curve(AnimationCurve? c)
        {
            if (c == null || c.keys == null || c.keys.Length == 0) return "none";
            var sb = new StringBuilder();
            foreach (var k in c.keys) sb.Append($"({k.time:F3},{k.value:F3}) ");
            return sb.ToString().TrimEnd();
        }

        public static void DumpSettings(TerrainGenerator tg, CoastPlan plan)
        {
            var ms = tg.mapSettings;
            var ws = tg.waterSettings;
            var bs = tg.baseSettings;
            float waterNorm = CoastCarver.WaterNorm(tg);
            float cell = ms.width / (float)Mathf.Max(ms.heightmapResolution - 1, 1);

            CoastLog.Msg("===== Coast dump: this generation =====");
            CoastLog.Msg($"theme={(tg.theme != null ? tg.theme.themeID.ToString() : "?")} seed={tg.terrainSettings.seed} " +
                         $"size={ms.size} width={ms.width} depth={ms.depth} height={ms.height} hmRes={ms.heightmapResolution} " +
                         $"texRes={ms.textureResolution} edgeExclusion={ms.edgeExclusion}");
            CoastLog.Msg($"baseSettings.scaling={bs.scaling:F4} noiseScaling={tg.noiseScalingValue:F4} " +
                         $"waterSettings.height={ws.height:F4} => water plane {waterNorm:F5} normalised = {tg.GetWaterHeight():F2} m");
            CoastLog.Msg($"waterSettings: seafloorHeight={ws.seafloorHeight:F4} minOceanMapEdgePoints={ws.minOceanMapEdgePoints} " +
                         $"useDepthCurve={ws.useDepthCurve} depthMap={N(ws.depthMap)} depthMapInfluence={ws.depthMapInfluence:F2}");
            CoastLog.Msg($"waterSettings.waterDepth curve: {Curve(ws.waterDepth)}");
            CoastLog.Msg($"cell size {cell:F3} m; pathing inset 150 m = {150f / cell:F1} cells; camera clamp 205 m = {205f / cell:F1} cells; " +
                         $"border buffer {tg.borderFeatureSettings.borderBuffer} m, spacing {tg.borderFeatureSettings.borderSpacing} m");
            CoastLog.Msg($"plan: {plan}");

            if (!_dumpedAssets)
            {
                _dumpedAssets = true;
                CoastLog.Msg("===== Coast dump: water types (once per session) =====");
                if (ws.lakeTypes != null)
                {
                    for (int i = 0; i < ws.lakeTypes.Count; i++)
                    {
                        var lt = ws.lakeTypes[i];
                        if (lt == null) { CoastLog.Msg($"lakeTypes[{i}] = null"); continue; }
                        CoastLog.Msg($"lakeTypes[{i}] '{lt.name}': shorelinePoints={lt.shorelinePoints} riverEndPoint={lt.riverEndPoint} " +
                                     $"waterMaterial={N(lt.waterMaterial)} foamMaterial={N(lt.foamMaterial)} shorelineHeight={lt.shorelineHeight:F3}");
                    }
                }
                else CoastLog.Msg("lakeTypes = null");

                var ocean = ws.oceanType;
                if (ocean == null)
                {
                    CoastLog.Warning("waterSettings.oceanType is NULL: nothing can be classified as ocean on this map.");
                }
                else
                {
                    CoastLog.Msg($"oceanType '{ocean.name}': shorelinePoints={ocean.shorelinePoints} riverEndPoint={ocean.riverEndPoint} " +
                                 $"shorelineHeight={ocean.shorelineHeight:F3} shorelineSampleRadius={ocean.shorelineSampleRadius} " +
                                 $"blendShorelineDetails={ocean.blendShorelineDetails}");
                    CoastLog.Msg($"oceanType materials: waterMaterial={N(ocean.waterMaterial)} foamMaterial={N(ocean.foamMaterial)}");
                    CoastLog.Msg($"oceanType textures: shoreline={N(ocean.shorelineTexture)}/{N(ocean.shorelineNormal)} " +
                                 $"underwater={N(ocean.underwaterTexture)}/{N(ocean.underwaterNormal)} detailCollection={N(ocean.detailCollection)}");
                    CoastLog.Msg($"oceanType objects: shorelineObjects={ocean.shorelineObjects?.Count ?? -1} waterObjects={ocean.waterObjects?.Count ?? -1} " +
                                 $"underwaterDensity={Curve(ocean.underwaterDensity)} shorelineDensity={Curve(ocean.shorelineDensity)}");
                }
            }

            DumpHeightStats(tg, waterNorm, "pre-carve");
        }

        /// <summary>Min/max/mean of heightNoise plus how many cells on each
        /// heightmap edge already sit at or below the water plane.</summary>
        public static void DumpHeightStats(TerrainGenerator tg, float waterNorm, string label)
        {
            float[,]? hn = tg.Data.heightNoise;
            if (hn == null) { CoastLog.Msg($"heightNoise null ({label})"); return; }
            int resX = hn.GetLength(0), resZ = hn.GetLength(1);
            float min = float.MaxValue, max = float.MinValue; double sum = 0; int below = 0;
            int west = 0, east = 0, south = 0, north = 0;
            for (int z = 0; z < resZ; z++)
            {
                for (int x = 0; x < resX; x++)
                {
                    float v = hn[x, z];
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v;
                    if (v <= waterNorm)
                    {
                        below++;
                        if (x == 0) east++;            // min X displays as east
                        if (x == resX - 1) west++;     // max X displays as west
                        if (z == 0) north++;           // min Z displays as north
                        if (z == resZ - 1) south++;    // max Z displays as south
                    }
                }
            }
            float mapH = tg.mapSettings.height;
            CoastLog.Msg($"heightNoise {label}: {resX}x{resZ} min={min:F4} ({min * mapH:F2} m) max={max:F4} ({max * mapH:F2} m) " +
                         $"mean={sum / (resX * (double)resZ):F4}; cells at/below water={below}; " +
                         $"edge cells at/below water W={west} E={east} S={south} N={north}");
        }

        /// <summary>One line per river in generationData saying where it ends.</summary>
        public static void ReportRivers(TerrainGenerator tg)
        {
            var rivers = tg.Data.rivers;
            if (rivers == null || rivers.Count == 0)
            {
                CoastLog.Msg("Rivers: none in generationData after Stage 38.");
                return;
            }

            var ms = tg.mapSettings;
            int res = ms.heightmapResolution;
            WaterType? ocean = tg.waterSettings.oceanType;
            TerrainGenerator.WaterArea? sea = null;
            foreach (var a in tg.Data.waterAreas)
            {
                if (ocean != null && a.waterType == ocean) { sea = a; break; }
            }

            for (int i = 0; i < rivers.Count; i++)
            {
                var r = rivers[i];
                if (r == null || r.points == null || r.points.Count == 0) continue;
                Vector3 first = r.points[0].pos;
                Vector3 last = r.points[r.points.Count - 1].pos;
                int cx = Mathf.Clamp(Mathf.RoundToInt(last.x / ms.width * (res - 1)), 0, res - 1);
                int cz = Mathf.Clamp(Mathf.RoundToInt(last.z / ms.depth * (res - 1)), 0, res - 1);
                bool inSea = sea.HasValue && InArea(sea.Value, cx, cz);
                CoastLog.Msg($"River {i + 1}: {r.points.Count} points, ({first.x:F0}, {first.z:F0}) -> ({last.x:F0}, {last.z:F0}); " +
                             (inSea ? "ends in the SEA" : "does not end in the sea"));
            }
        }

        private static bool InArea(TerrainGenerator.WaterArea a, int x, int z)
        {
            if (a.points == null) return false;
            int px = x - a.minX, pz = z - a.minZ;
            return px >= 0 && pz >= 0 && px < a.points.GetLength(0) && pz < a.points.GetLength(1) && a.points[px, pz];
        }

        /// <summary>Per-area classification report. The shoreline count is
        /// <c>edge.Length</c>, exactly what the classifier compared against
        /// <c>oceanType.shorelinePoints</c>; the edge-cell count is recomputed
        /// the same way the classifier counts them.</summary>
        public static void ReportWaterAreas(TerrainGenerator tg, string stage, CoastPlan? plan)
        {
            List<TerrainGenerator.WaterArea> areas = tg.Data.waterAreas;
            WaterType? ocean = tg.waterSettings.oceanType;
            float[,]? hn = tg.Data.heightNoise;
            int res = tg.mapSettings.heightmapResolution;
            float mapH = tg.mapSettings.height;
            float waterNorm = CoastCarver.WaterNorm(tg);
            bool verbose = RiversRestoredMod.VerboseDiagnostics?.Value ?? false;

            int oceans = 0;
            for (int i = 0; i < areas.Count; i++)
            {
                var a = areas[i];
                bool isOcean = ocean != null && a.waterType == ocean;
                if (isOcean) oceans++;
                if (!isOcean && !verbose) continue;

                int cells = 0, edgeCells = 0;
                float minH = float.MaxValue, maxH = float.MinValue;
                if (a.points != null)
                {
                    int w = a.points.GetLength(0), h = a.points.GetLength(1);
                    for (int pz = 0; pz < h; pz++)
                    {
                        for (int px = 0; px < w; px++)
                        {
                            if (!a.points[px, pz]) continue;
                            cells++;
                            int gx = a.minX + px, gz = a.minZ + pz;
                            if (gx == 0 || gz == 0 || gx == res - 1 || gz == res - 1) edgeCells++;
                            if (hn != null && gx < hn.GetLength(0) && gz < hn.GetLength(1))
                            {
                                float v = hn[gx, gz];
                                if (v < minH) minH = v;
                                if (v > maxH) maxH = v;
                            }
                        }
                    }
                }
                int shoreline = a.edge?.Length ?? -1;
                string floor = cells > 0 ? $"{minH * mapH:F2}..{maxH * mapH:F2} m" : "n/a";
                string typeName = a.waterType != null ? a.waterType.name : "null";
                string tag = isOcean ? "  <== OCEAN" : "";
                CoastLog.Msg($"  area {i}: {typeName}{tag} bbox x[{a.minX}..{a.maxX}] z[{a.minZ}..{a.maxZ}] " +
                             $"cells={cells} shoreline={shoreline} edgeCells={edgeCells} floor {floor}");
            }

            string thresholds = ocean != null
                ? $"thresholds: shorelinePoints>={ocean.shorelinePoints}, edgeCells>={tg.waterSettings.minOceanMapEdgePoints}"
                : "oceanType is null";
            string coast = plan != null ? $"; coast on {plan.Edge}" : "";
            CoastLog.Msg($"{stage}: {areas.Count} water areas, {oceans} classified as ocean; {thresholds}; " +
                         $"water plane {waterNorm * mapH:F2} m{coast}");

            if (plan != null && oceans == 0)
            {
                CoastLog.Warning("The coast was carved but no area was classified as ocean. Compare the coastal area's " +
                                 "shoreline and edgeCells above with the thresholds; the Ocean Threshold Override should be on.");
            }
        }
    }
}
