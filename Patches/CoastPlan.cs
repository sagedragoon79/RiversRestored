using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using TerrainGen;

namespace RiversRestored.Patches
{
    /// <summary>
    /// The map-edge plan for one generation: the coast (if any), the border
    /// ring scale, and the playable inset. On a fresh generation it comes from
    /// the preferences and the map seed; on a save load it comes from the sidecar
    /// written when that map was saved, so the replayed generator produces
    /// exactly what the save was built with. Pure and deterministic.
    /// </summary>
    internal sealed class CoastPlan
    {
        public const int SidecarVersion = 2;
        public const float VanillaRingScale = 1f;
        public const float VanillaInset = 150f;

        public bool HasCoast;
        public CoastEdge Edge;
        public int Seed;
        public float CoastlineDistance;
        public float BeachWidth;
        public float ShelfWidth;
        public float DepthFactor;
        public float JitterAmplitude;
        public float JitterWavelength;
        public bool OceanThresholdOverride;
        public float RingScale = VanillaRingScale;
        public float PlayableInset = VanillaInset;
        public string Source = "prefs";

        private readonly float[] _phase = new float[3];

        /// <summary>True when the preferences ask for anything other than a
        /// vanilla map edge.</summary>
        public static bool PrefsWantEdgeChanges()
        {
            if (RiversRestoredMod.CoastalMapsEnabled?.Value ?? false) return true;
            if (Mathf.Abs(PrefRingScale() - VanillaRingScale) > 0.001f) return true;
            if (Mathf.Abs(PrefInset() - VanillaInset) > 0.01f) return true;
            return false;
        }

        public static float PrefRingScale() => Mathf.Clamp(RiversRestoredMod.BorderRingScale?.Value ?? VanillaRingScale, 0f, 1f);
        public static float PrefInset() => Mathf.Clamp(RiversRestoredMod.PlayableInset?.Value ?? VanillaInset, 0f, VanillaInset);

        public static CoastPlan Build(TerrainGenerator tg)
        {
            int seed = tg.terrainSettings.seed;

            CoastEdge edge = RiversRestoredMod.CoastEdgeChoice.Value;
            if (edge == CoastEdge.Random)
            {
                // System.Random is deterministic for a given seed on Mono, and
                // it is separate from the generator's own RNG, so this never
                // disturbs the vanilla random sequence.
                int pick = new System.Random(seed).Next(0, 4);
                edge = (CoastEdge)(pick + 1);
            }

            var plan = new CoastPlan
            {
                HasCoast = RiversRestoredMod.CoastalMapsEnabled?.Value ?? false,
                Edge = edge,
                Seed = seed,
                CoastlineDistance = Mathf.Clamp(RiversRestoredMod.CoastlineDistance.Value, 20f, 800f),
                BeachWidth = Mathf.Clamp(RiversRestoredMod.CoastBeachWidth.Value, 1f, 300f),
                ShelfWidth = Mathf.Clamp(RiversRestoredMod.CoastShelfWidth.Value, 1f, 600f),
                DepthFactor = Mathf.Clamp(RiversRestoredMod.CoastSeabedDepth.Value, 0.02f, 1f),
                JitterAmplitude = Mathf.Clamp(RiversRestoredMod.CoastJitterAmplitude.Value, 0f, 300f),
                JitterWavelength = Mathf.Clamp(RiversRestoredMod.CoastJitterWavelength.Value, 20f, 5000f),
                OceanThresholdOverride = RiversRestoredMod.CoastOceanThresholdOverride.Value,
                RingScale = PrefRingScale(),
                PlayableInset = PrefInset(),
                Source = "prefs",
            };
            plan.InitPhases();
            return plan;
        }

        private void InitPhases()
        {
            var r = new System.Random(unchecked(Seed * 31 + 0x5EA));
            for (int i = 0; i < _phase.Length; i++)
                _phase[i] = (float)(r.NextDouble() * Math.PI * 2.0);
        }

        /// <summary>World distance from the coastal heightmap edge to the
        /// shoreline at position <paramref name="along"/> (world units along
        /// the edge). Straight when jitter amplitude is 0.</summary>
        public float CoastlineAt(float along)
        {
            if (JitterAmplitude <= 0f) return CoastlineDistance;
            float k = Mathf.PI * 2f / JitterWavelength;
            float j = 0.60f * Mathf.Sin(along * k + _phase[0])
                    + 0.30f * Mathf.Sin(along * k * 2.7f + _phase[1])
                    + 0.10f * Mathf.Sin(along * k * 7.3f + _phase[2]);
            return CoastlineDistance + JitterAmplitude * j;
        }

        // ── Sidecar (key=value text, invariant culture) ────────────────────

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        public IEnumerable<string> ToSidecarLines(string modVersion)
        {
            yield return $"# Rivers Restored map-edge sidecar v{SidecarVersion}";
            yield return $"modVersion={modVersion}";
            yield return $"seed={Seed}";
            yield return $"coast={HasCoast}";
            yield return $"edge={Edge}";
            yield return $"coastlineDistance={F(CoastlineDistance)}";
            yield return $"beachWidth={F(BeachWidth)}";
            yield return $"shelfWidth={F(ShelfWidth)}";
            yield return $"depthFactor={F(DepthFactor)}";
            yield return $"jitterAmplitude={F(JitterAmplitude)}";
            yield return $"jitterWavelength={F(JitterWavelength)}";
            yield return $"oceanThresholdOverride={OceanThresholdOverride}";
            yield return $"ringScale={F(RingScale)}";
            yield return $"playableInset={F(PlayableInset)}";
        }

        /// <summary>Parses a sidecar. Version 1 files (Rivers Restored 1.7.0 and the
        /// Coastal Kingdom prototype) have no <c>coast</c>, <c>ringScale</c>, or
        /// <c>playableInset</c> keys; they describe coast maps generated with the
        /// vanilla ring and inset, and fall back to exactly that.</summary>
        public static CoastPlan? FromSidecarLines(string[] lines, out string error)
        {
            error = "";
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            if (!kv.TryGetValue("seed", out string? seedText) || !int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
            {
                error = "missing or invalid seed";
                return null;
            }

            bool hasCoast = !kv.TryGetValue("coast", out string? coastText) || !bool.TryParse(coastText, out bool c) || c;

            CoastEdge edge = CoastEdge.West;
            bool edgeOk = kv.TryGetValue("edge", out string? edgeText) && Enum.TryParse(edgeText, true, out edge) && edge != CoastEdge.Random;
            if (hasCoast && !edgeOk)
            {
                error = "missing or invalid edge";
                return null;
            }

            float Get(string key, float fallback) =>
                kv.TryGetValue(key, out string? t) && float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

            var plan = new CoastPlan
            {
                HasCoast = hasCoast,
                Edge = edgeOk ? edge : CoastEdge.West,
                Seed = seed,
                CoastlineDistance = Get("coastlineDistance", 300f),
                BeachWidth = Get("beachWidth", 40f),
                ShelfWidth = Get("shelfWidth", 90f),
                DepthFactor = Get("depthFactor", 0.5f),
                JitterAmplitude = Get("jitterAmplitude", 90f),
                JitterWavelength = Get("jitterWavelength", 700f),
                OceanThresholdOverride = !kv.TryGetValue("oceanThresholdOverride", out string? o) || !bool.TryParse(o, out bool ov) || ov,
                RingScale = Mathf.Clamp(Get("ringScale", VanillaRingScale), 0f, 1f),
                PlayableInset = Mathf.Clamp(Get("playableInset", VanillaInset), 0f, VanillaInset),
                Source = "sidecar",
            };
            plan.InitPhases();
            return plan;
        }

        public override string ToString() =>
            (HasCoast
                ? $"coast edge={Edge} coastline={CoastlineDistance:F0}m beach={BeachWidth:F0}m shelf={ShelfWidth:F0}m " +
                  $"depth={DepthFactor:F2} jitter={JitterAmplitude:F0}m/{JitterWavelength:F0}m override={OceanThresholdOverride}"
                : "no coast") +
            $" ring={RingScale:F2} inset={PlayableInset:F0}m seed={Seed} ({Source})";
    }
}
