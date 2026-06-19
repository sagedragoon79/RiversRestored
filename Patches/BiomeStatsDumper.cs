using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// One-shot diagnostic dumper for FF's map-type / biome statistics.
    /// Walks UIStartMenu_MapTypeData → MapTypeContainer → Terrain2Theme →
    /// BiomeContainer[] → TerrainBiome, logging the actual numeric values
    /// from the loaded ScriptableObject assets. Writes both to the log and
    /// to UserData/RiversRestored/biome_stats.txt for easy export.
    ///
    /// Gated behind VerboseDiagnostics so it only runs on demand. Fires
    /// once per session from OnUpdate when the main menu / map scene has
    /// the ScriptableObjects loaded.
    /// </summary>
    internal static class BiomeStatsDumper
    {
        private static bool _dumped = false;

        public static void ResetForSceneLoad() { _dumped = false; }

        /// <summary>Try to find UIStartMenu_MapTypeData in loaded resources
        /// and dump its data. Returns true on success (idempotent — only
        /// dumps once per session).</summary>
        public static bool TryDump()
        {
            if (_dumped) return true;

            try
            {
                Type? mtdType = AccessTools.TypeByName("UIStartMenu_MapTypeData");
                if (mtdType == null)
                {
                    Log("UIStartMenu_MapTypeData type not found.");
                    return false;
                }

                // FindObjectsOfTypeAll picks up SOs from loaded resources
                var instances = Resources.FindObjectsOfTypeAll(mtdType);
                if (instances == null || instances.Length == 0)
                {
                    Log("No UIStartMenu_MapTypeData instance loaded yet — waiting.");
                    return false;
                }

                var sb = new StringBuilder();
                sb.AppendLine("===== FF Biome / Map Type Statistics =====");
                sb.AppendLine($"Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"UIStartMenu_MapTypeData instances: {instances.Length}");
                sb.AppendLine();

                var mtd = instances[0];
                Type t = mtd.GetType();

                // Default theme
                var defaultTheme = GetFieldValue(mtd, "_defaultTheme")
                    ?? GetFieldValue(mtd, "defaultTheme");
                sb.AppendLine($"Default theme: {NameOf(defaultTheme)}");

                // All themes list
                var allThemes = GetFieldValue(mtd, "allTerrainThemes") as IList;
                sb.AppendLine($"All terrain themes: {allThemes?.Count ?? -1}");
                if (allThemes != null)
                {
                    foreach (var th in allThemes)
                        sb.AppendLine($"  - {NameOf(th)}");
                }
                sb.AppendLine();

                // Map type containers (the preset → theme mapping)
                var containers = GetFieldValue(mtd, "mapTypeContainers") as IList;
                sb.AppendLine($"Map type containers: {containers?.Count ?? -1}");
                sb.AppendLine();

                if (containers != null)
                {
                    foreach (var c in containers)
                    {
                        DumpMapTypeContainer(c, sb);
                    }
                }

                // Write to log
                string text = sb.ToString();
                foreach (var line in text.Split('\n'))
                    Log(line.TrimEnd('\r'));

                // Write to UserData file
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string outDir = Path.Combine(baseDir, "UserData", "RiversRestored");
                    if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                    string outPath = Path.Combine(outDir, "biome_stats.txt");
                    File.WriteAllText(outPath, text);
                    Log($"Wrote biome stats → {outPath}");
                }
                catch (Exception fex)
                {
                    Log($"File write failed: {fex.Message}");
                }

                _dumped = true;
                return true;
            }
            catch (Exception ex)
            {
                Log($"TryDump exception: {ex}");
                _dumped = true; // don't retry on exception
                return false;
            }
        }

        private static void DumpMapTypeContainer(object c, StringBuilder sb)
        {
            var mapType = GetFieldValue(c, "mapType");
            var theme = GetFieldValue(c, "terrainTheme");
            int minMt = (int)(GetFieldValue(c, "minMountainValue") ?? -1);
            int maxMt = (int)(GetFieldValue(c, "maxMountainValue") ?? -1);
            int minWa = (int)(GetFieldValue(c, "minWaterValue") ?? -1);
            int maxWa = (int)(GetFieldValue(c, "maxWaterValue") ?? -1);

            sb.AppendLine($"━━━ MapType: {mapType} ━━━");
            sb.AppendLine($"  Theme: {NameOf(theme)}");
            sb.AppendLine($"  Mountain range: {minMt} – {maxMt}");
            sb.AppendLine($"  Water range:    {minWa} – {maxWa}");

            if (theme != null)
                DumpTheme(theme, sb);
            sb.AppendLine();
        }

        private static void DumpTheme(object theme, StringBuilder sb)
        {
            Type t = theme.GetType();
            byte themeID = (byte)(GetFieldValue(theme, "themeID") ?? (byte)0);
            float weight = (float)(GetFieldValue(theme, "weightForRandomPick") ?? 0f);
            bool req3 = (bool)(GetFieldValue(theme, "requiresTier3") ?? false);
            string desc = (string)(GetFieldValue(theme, "description") ?? "");
            float treeGrowth = (float)(GetFieldValue(theme, "treeGrowthTime") ?? 0f);
            float chopDelay = (float)(GetFieldValue(theme, "chopGrowthDelay") ?? 0f);
            float groundhog = (float)(GetFieldValue(theme, "groundhogSpawnFrequencyScale") ?? 0f);
            float fox = (float)(GetFieldValue(theme, "foxSpawnFrequencyScale") ?? 0f);

            sb.AppendLine($"  Theme details (themeID={themeID}):");
            sb.AppendLine($"    weightForRandomPick={weight:F2}  requiresTier3={req3}  desc=\"{desc}\"");
            sb.AppendLine($"    treeGrowthTime={treeGrowth}  chopGrowthDelay={chopDelay}");
            sb.AppendLine($"    groundhogSpawnFreq={groundhog}  foxSpawnFreq={fox}");

            // Default biome
            var defaultBiome = GetFieldValue(theme, "defaultBiome");
            sb.AppendLine($"    defaultBiome: {ContainerBiomeName(defaultBiome)}");

            // Biome containers
            var biomeContainers = GetFieldValue(theme, "biomeContainers") as Array;
            sb.AppendLine($"    biomeContainers: {biomeContainers?.Length ?? -1}");

            if (biomeContainers != null)
            {
                for (int i = 0; i < biomeContainers.Length; i++)
                {
                    var bc = biomeContainers.GetValue(i);
                    if (bc == null) continue;
                    var biome = GetFieldValue(bc, "biome");
                    sb.AppendLine($"    [{i}] biome: {NameOf(biome)}");
                    if (biome != null)
                        DumpBiome(biome, sb, "        ");
                }
            }
        }

        private static string ContainerBiomeName(object? bc)
        {
            if (bc == null) return "null";
            var b = GetFieldValue(bc, "biome");
            return NameOf(b);
        }

        private static void DumpBiome(object biome, StringBuilder sb, string indent)
        {
            Type bt = biome.GetType();
            bool enabled = (bool)(GetFieldValue(biome, "enabled") ?? false);
            int priority = (int)(GetFieldValue(biome, "priority") ?? 0);
            var biomeType = GetFieldValue(biome, "biomeType");
            var heightRule = GetFieldValue(biome, "heightRule");
            float shorelineH = (float)(GetFieldValue(biome, "shorelineHeight") ?? 0f);
            float addFertMult = (float)(GetFieldValue(biome, "addFertilityMult") ?? 0f);

            int fertility = (int)(GetFieldValue(biome, "fertility") ?? 0);
            int honey = (int)(GetFieldValue(biome, "honeyProduction") ?? 0);
            int clay = (int)(GetFieldValue(biome, "clay") ?? 0);
            int sand = (int)(GetFieldValue(biome, "sand") ?? 0);
            int fodder = (int)(GetFieldValue(biome, "fodder") ?? 0);
            int goatFodder = (int)(GetFieldValue(biome, "goatFodder") ?? 0);
            int water = (int)(GetFieldValue(biome, "water") ?? 0);
            int animals = (int)(GetFieldValue(biome, "animals") ?? 0);
            int wolfDens = (int)(GetFieldValue(biome, "wolfDens") ?? 0);
            float trapping = (float)(GetFieldValue(biome, "trappingScalar") ?? 0f);
            bool blocksRoads = (bool)(GetFieldValue(biome, "blocksRoads") ?? false);

            sb.AppendLine($"{indent}enabled={enabled} priority={priority} biomeType={biomeType} heightRule={heightRule}");
            sb.AppendLine($"{indent}shorelineHeight={shorelineH:F3} addFertilityMult={addFertMult:F2}");
            sb.AppendLine($"{indent}── Resource Percentages (0-100) ──");
            sb.AppendLine($"{indent}  fertility={fertility,3}  honeyProduction={honey,3}  clay={clay,3}  sand={sand,3}");
            sb.AppendLine($"{indent}  fodder={fodder,3}     goatFodder={goatFodder,3}     water={water,3}");
            sb.AppendLine($"{indent}  animals={animals,3}   wolfDens={wolfDens,3}");
            sb.AppendLine($"{indent}  trappingScalar={trapping:F2} blocksRoads={blocksRoads}");

            // Mineral site curves — evaluate at t=0, 0.5, 1
            sb.AppendLine($"{indent}── Mineral Sites (curve @ t=0 / 0.5 / 1) ──");
            DumpCurve(biome, "numIronSites", sb, indent);
            DumpCurve(biome, "numGoldSites", sb, indent);
            DumpCurve(biome, "numCoalSites", sb, indent);
            DumpCurve(biome, "numClaySites", sb, indent);
            DumpCurve(biome, "numSandSites", sb, indent);
            DumpCurve(biome, "numStoneSites", sb, indent);
            DumpCurve(biome, "numFertilityBonusSites", sb, indent);

            // Tree density
            sb.AppendLine($"{indent}── Vegetation Curves ──");
            DumpCurve(biome, "treeDensity", sb, indent);
            DumpCurve(biome, "grassDensity", sb, indent);
            DumpCurve(biome, "detailDensity", sb, indent);
            DumpCurve(biome, "prefabDensity", sb, indent);
            DumpCurve(biome, "shorelinePrefabDensity", sb, indent);
            DumpCurve(biome, "heightDistribution", sb, indent);
            DumpCurve(biome, "slopeDistribution", sb, indent);
            DumpCurve(biome, "clumpiness", sb, indent);

            // Trees
            var trees = GetFieldValue(biome, "trees");
            if (trees != null)
            {
                var items = GetFieldValue(trees, "items") as IList;
                if (items != null)
                {
                    sb.AppendLine($"{indent}── Trees ({items.Count}) ──");
                    foreach (var te in items)
                    {
                        var prefab = GetFieldValue(te, "prefab") as UnityEngine.Object;
                        int w = (int)(GetFieldValue(te, "weight") ?? 0);
                        sb.AppendLine($"{indent}  {prefab?.name ?? "null"}  weight={w}");
                    }
                }
            }

            // Foragables
            var foraged = GetFieldValue(biome, "foragedItems");
            if (foraged != null)
            {
                var items = GetFieldValue(foraged, "items") as IList;
                if (items != null)
                {
                    sb.AppendLine($"{indent}── Foragables ({items.Count}) ──");
                    foreach (var fi in items)
                    {
                        var itemType = GetFieldValue(fi, "itemType");
                        var seasons = GetFieldValue(fi, "seasons");
                        var locationMask = GetFieldValue(fi, "locationMask");
                        sb.AppendLine($"{indent}  {itemType}  seasons={seasons}  loc={locationMask}");
                        DumpCurve(fi, "quantity", sb, indent + "    ");
                        DumpCurve(fi, "replenishRate", sb, indent + "    ");
                    }
                }
            }
        }

        private static void DumpCurve(object obj, string fieldName, StringBuilder sb, string indent)
        {
            var c = GetFieldValue(obj, fieldName) as AnimationCurve;
            if (c == null || c.keys == null || c.keys.Length == 0)
            {
                sb.AppendLine($"{indent}  {fieldName}: <empty>");
                return;
            }
            float a = c.Evaluate(0f);
            float b = c.Evaluate(0.5f);
            float d = c.Evaluate(1f);
            sb.AppendLine($"{indent}  {fieldName}: {a:F3} / {b:F3} / {d:F3}  (keys={c.keys.Length})");
        }

        private static object? GetFieldValue(object? obj, string name)
        {
            if (obj == null) return null;
            try
            {
                Type t = obj.GetType();
                var f = t.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(obj);
                var p = t.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(obj);
            }
            catch { }
            return null;
        }

        private static string NameOf(object? o)
        {
            if (o == null) return "null";
            if (o is UnityEngine.Object uo) return uo.name;
            return o.ToString() ?? "?";
        }

        private static void Log(string msg) => RiversRestoredMod.Log.Msg($"[RR][BiomeDump] {msg}");
    }
}
