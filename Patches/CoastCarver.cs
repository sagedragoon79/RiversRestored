using System;
using UnityEngine;
using TerrainGen;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Writes the coast into <c>generationData.heightNoise</c> right before the
    /// water classification pass. heightNoise is normalised to
    /// <c>mapSettings.height</c> (world metres = value × mapSettings.height) and
    /// the generator treats a cell as water when
    /// <c>heightNoise &lt;= baseSettings.scaling × waterSettings.height × noiseScaling</c>.
    /// Only heightNoise is written: the terrain mesh, textures, water chunks,
    /// shore points, and fish areas are all built from it by later stages.
    /// </summary>
    internal static class CoastCarver
    {
        /// <summary>Height of the beach where it meets the water, in metres above
        /// the water plane. Small on purpose: the game raises ocean shore cells by
        /// its own shorelineHeight later, and a tall step here reads as a cliff.</summary>
        private const float BeachRiseMetres = 0.3f;

        public static float WaterNorm(TerrainGenerator tg) =>
            tg.baseSettings.scaling * tg.waterSettings.height * tg.noiseScalingValue;

        /// <summary>Carves the coast. Idempotent: calling it again only lowers sea
        /// cells that have risen above the target since the previous call and
        /// re-derives the beach from the untouched terrain at its inland edge.
        /// Returns the number of sea cells that were lowered.</summary>
        public static int Carve(TerrainGenerator tg, CoastPlan plan, string label)
        {
            float[,]? hn = tg.Data.heightNoise;
            if (hn == null)
            {
                CoastLog.Warning("heightNoise is null; cannot carve.");
                return 0;
            }

            int resX = hn.GetLength(0);
            int resZ = hn.GetLength(1);
            var ms = tg.mapSettings;
            float cellX = ms.width / (float)Mathf.Max(resX - 1, 1);
            float cellZ = ms.depth / (float)Mathf.Max(resZ - 1, 1);
            float mapH = Mathf.Max(ms.height, 1);

            float waterNorm = WaterNorm(tg);
            float shoreH = waterNorm + BeachRiseMetres / mapH;
            float eps = Mathf.Max(waterNorm * 0.01f, 1e-5f);

            int sea = 0, beach = 0, lowered = 0;
            float deepest = float.MaxValue;
            float shelf = Mathf.Max(plan.ShelfWidth, 1f);
            float beachW = Mathf.Max(plan.BeachWidth, 1f);
            bool alongX = plan.Edge == CoastEdge.North || plan.Edge == CoastEdge.South;

            for (int z = 0; z < resZ; z++)
            {
                for (int x = 0; x < resX; x++)
                {
                    float d, along;
                    switch (plan.Edge)
                    {
                        // In-game display: max X is west (left), max Z is south (bottom).
                        case CoastEdge.West:  d = (resX - 1 - x) * cellX; along = z * cellZ; break;
                        case CoastEdge.East:  d = x * cellX;              along = z * cellZ; break;
                        case CoastEdge.North: d = z * cellZ;              along = x * cellX; break;
                        default:              d = (resZ - 1 - z) * cellZ; along = x * cellX; break; // South
                    }

                    float coast = plan.CoastlineAt(along);
                    if (d >= coast + beachW) continue;

                    if (d < coast)
                    {
                        // Open sea: slope down over the shelf, then flat to the edge.
                        float original = hn[x, z];
                        float t = plan.DepthFactor * Mathf.Clamp01((coast - d) / shelf);
                        float h = Mathf.Min(waterNorm * (1f - t), waterNorm - eps);
                        if (h < original)                  // keep anything already deeper (lakes)
                        {
                            hn[x, z] = h;
                            lowered++;
                        }
                        if (hn[x, z] < deepest) deepest = hn[x, z];
                        sea++;
                    }
                    else
                    {
                        // Beach: smooth ramp from just above water up to the terrain at the
                        // beach's inland edge. Sampling that edge cell (which the carve never
                        // touches) instead of this cell's own height makes the ramp the same
                        // no matter how many times the carve runs, and lets it follow any
                        // later change to the land (the river flow bias).
                        float u = (d - coast) / beachW;
                        float s = u * u * (3f - 2f * u);
                        int ex = x, ez = z;
                        int inland = Mathf.CeilToInt((coast + beachW) / (alongX ? cellZ : cellX));
                        switch (plan.Edge)
                        {
                            case CoastEdge.West:  ex = Mathf.Clamp(resX - 1 - inland, 0, resX - 1); break;
                            case CoastEdge.East:  ex = Mathf.Clamp(inland, 0, resX - 1);            break;
                            case CoastEdge.North: ez = Mathf.Clamp(inland, 0, resZ - 1);            break;
                            default:              ez = Mathf.Clamp(resZ - 1 - inland, 0, resZ - 1); break;
                        }
                        float inlandH = hn[ex, ez];
                        hn[x, z] = Mathf.Lerp(shoreH, inlandH, s);
                        beach++;
                    }
                }
            }

            CoastLog.Msg($"Coast {label} ({plan.Edge}): {sea} sea cells ({lowered} lowered), {beach} beach cells; " +
                         $"water plane {waterNorm:F5} ({waterNorm * mapH:F2} m), deepest {deepest:F5} " +
                         $"({deepest * mapH:F2} m), shore {shoreH:F5} ({shoreH * mapH:F2} m); cell {cellX:F2}x{cellZ:F2} m; {plan}");
            return lowered;
        }

        /// <summary>The shipped ocean WaterType carries shorelinePoints = 900000,
        /// which is the switch that keeps vanilla from ever producing an ocean.
        /// Lower both ocean thresholds to half the heightmap resolution so a
        /// single-edge coast (one full edge of cells) qualifies while no lake can:
        /// a lake never touches 190+ edge cells. Both are plain fields on public
        /// classes; waterSettings is re-read from the save on load, so this runs
        /// every generation.</summary>
        public static void ApplyOceanThresholdOverride(TerrainGenerator tg)
        {
            var ws = tg.waterSettings;
            int res = tg.mapSettings.heightmapResolution;
            uint edgeTarget = (uint)Mathf.Max(16, res / 2);
            int shoreTarget = Mathf.Max(16, res / 2);

            if (ws.minOceanMapEdgePoints > edgeTarget)
            {
                CoastLog.Msg($"Ocean threshold: minOceanMapEdgePoints {ws.minOceanMapEdgePoints} -> {edgeTarget}");
                ws.minOceanMapEdgePoints = edgeTarget;
            }
            if (ws.oceanType != null && ws.oceanType.shorelinePoints > shoreTarget)
            {
                CoastLog.Msg($"Ocean threshold: oceanType.shorelinePoints {ws.oceanType.shorelinePoints} -> {shoreTarget}");
                ws.oceanType.shorelinePoints = shoreTarget;
            }
        }
    }
}
