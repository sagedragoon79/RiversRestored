using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using TerrainGen;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Stage 2 of the in-game previewer feature. After each gen completes,
    /// reads <c>_generationData.heightNoise</c> + <c>_generationData.waterAreas</c>
    /// and renders a top-down preview image:
    ///   - Grayscale base from heightnoise (low = dark, high = light)
    ///   - Blue overlay where water areas exist
    ///   - Saves as PNG to UserData/RiversRestored/Previews/.
    ///
    /// Gated behind the <see cref="RiversRestoredMod.EnableMapPreviewRender"/>
    /// pref. One render per gen; idempotent.
    /// </summary>
    internal static class MapPreviewRenderer
    {
        // Per-gen flag — reset by RiverSettingsPatch.DoOverride alongside
        // _stage38AlreadyRanThisGen / _stage60AlreadyRanThisGen.
        public static bool RenderedThisGen = false;

        // Output dimensions. 512×512 is a good balance: readable but not
        // excessive on disk. Heightnoise is typically 384×384 (Medium) or
        // 512×512 (Large), so we sample/upscale to 512×512.
        private const int OUT_W = 512;
        private const int OUT_H = 512;

        /// <summary>Render the preview for the supplied terrain generator's
        /// current state. Safe to call multiple times — second-and-later calls
        /// no-op via <see cref="RenderedThisGen"/>. No render if the pref is off.</summary>
        public static void TryRender(TerrainGenerator tg, string source)
        {
            if (!(RiversRestoredMod.EnableMapPreviewRender?.Value ?? false)) return;
            if (RenderedThisGen) return;
            if (tg == null) return;
            try
            {
                var gd = AccessTools.Field(typeof(TerrainGenerator), "_generationData")?.GetValue(tg);
                if (gd == null) { Log("_generationData null — skipping preview."); return; }

                var hnField = gd.GetType().GetField("heightNoise",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var heightNoise = hnField?.GetValue(gd) as float[,];
                if (heightNoise == null) { Log("heightNoise null — skipping preview."); return; }

                int hnW = heightNoise.GetLength(0);
                int hnH = heightNoise.GetLength(1);
                if (hnW <= 0 || hnH <= 0) return;

                // Build the pixel buffer. Texture2D.SetPixels32 wants
                // bottom-left origin; we'll write rows accordingly.
                var pixels = new Color32[OUT_W * OUT_H];

                // Pre-compute heightnoise min/max for normalized grayscale.
                // The raw heightnoise often clusters around [0.3, 0.7];
                // a fixed [0,1] mapping looks washed-out. Stretch to actual range.
                float hnMin = float.MaxValue, hnMax = float.MinValue;
                for (int x = 0; x < hnW; x++)
                {
                    for (int z = 0; z < hnH; z++)
                    {
                        float v = heightNoise[x, z];
                        if (v < hnMin) hnMin = v;
                        if (v > hnMax) hnMax = v;
                    }
                }
                float hnRange = Mathf.Max(0.0001f, hnMax - hnMin);

                // Heightmap grayscale base — sample heightnoise into the
                // output buffer using nearest-neighbor scale-and-flip.
                // Output Y axis is bottom-up (Texture2D); heightnoise z axis
                // mapping per RR's other code: z=0 → screen south (heightmap
                // both-axes-inverted convention used by ApplyRiverFlowBias).
                // For preview purposes we render heightnoise[x,z] directly to
                // pixel (x_pixel, z_pixel) — orientation tweaks can come
                // later once we compare to in-game minimap.
                for (int py = 0; py < OUT_H; py++)
                {
                    int hz = (int)((float)py / (OUT_H - 1) * (hnH - 1));
                    for (int px = 0; px < OUT_W; px++)
                    {
                        int hx = (int)((float)px / (OUT_W - 1) * (hnW - 1));
                        float v = heightNoise[hx, hz];
                        float n = (v - hnMin) / hnRange;
                        n = Mathf.Clamp01(n);
                        // Curved gamma so mid-elevation lands brighter — easier
                        // to read terrain at a glance.
                        float g = Mathf.Pow(n, 0.7f);
                        byte b = (byte)(g * 220f + 20f);  // 20..240, never pure black/white
                        pixels[py * OUT_W + px] = new Color32(b, b, b, 255);
                    }
                }

                // Water overlay — true polygon raster. Each WaterArea has a
                // `points` bool[,] mask sized to its bounding box; cells where
                // the mask is true are inside the polygon. Map each masked
                // world-cell to an output pixel and paint blue. Falls back to
                // bbox raster only if the mask field is missing or null.
                int waterPainted = 0;
                int waterAreaCount = 0;
                var waField = gd.GetType().GetField("waterAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var waterAreas = waField?.GetValue(gd) as IList;
                if (waterAreas != null)
                {
                    waterAreaCount = waterAreas.Count;
                    Type? waType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterArea");
                    var fMinX = waType?.GetField("minX");
                    var fMinZ = waType?.GetField("minZ");
                    var fMaxX = waType?.GetField("maxX");
                    var fMaxZ = waType?.GetField("maxZ");
                    var fPoints = waType?.GetField("points");

                    if (fMinX != null && fMinZ != null && fMaxX != null && fMaxZ != null)
                    {
                        // Heightnoise → output scale factors. Water area
                        // bounds are in heightnoise cell space (same as
                        // _generationData.heightNoise indexing).
                        float sxOut = (float)(OUT_W - 1) / (hnW - 1);
                        float szOut = (float)(OUT_H - 1) / (hnH - 1);

                        foreach (var wa in waterAreas)
                        {
                            if (wa == null) continue;
                            int minX, minZ, maxX, maxZ;
                            try
                            {
                                minX = (int)fMinX.GetValue(wa);
                                minZ = (int)fMinZ.GetValue(wa);
                                maxX = (int)fMaxX.GetValue(wa);
                                maxZ = (int)fMaxZ.GetValue(wa);
                            }
                            catch { continue; }

                            // Try to read the polygon mask. If missing/null,
                            // fall back to bbox raster for this area only.
                            bool[,]? mask = null;
                            try { mask = fPoints?.GetValue(wa) as bool[,]; }
                            catch { /* leave mask null → bbox fallback */ }

                            if (mask != null)
                            {
                                int mw = mask.GetLength(0);
                                int mh = mask.GetLength(1);
                                for (int lz = 0; lz < mh; lz++)
                                {
                                    int worldZ = minZ + lz;
                                    if (worldZ < 0 || worldZ >= hnH) continue;
                                    int py = Mathf.Clamp((int)(worldZ * szOut), 0, OUT_H - 1);
                                    for (int lx = 0; lx < mw; lx++)
                                    {
                                        if (!mask[lx, lz]) continue;
                                        int worldX = minX + lx;
                                        if (worldX < 0 || worldX >= hnW) continue;
                                        int px = Mathf.Clamp((int)(worldX * sxOut), 0, OUT_W - 1);
                                        PaintWaterPixel(pixels, py * OUT_W + px);
                                        waterPainted++;
                                    }
                                }
                            }
                            else
                            {
                                // Bbox fallback (rare — only if mask field
                                // missing). Keeps coverage so we always see
                                // SOMETHING for every water area.
                                int pxMin = Mathf.Clamp((int)(minX * sxOut), 0, OUT_W - 1);
                                int pxMax = Mathf.Clamp((int)(maxX * sxOut), 0, OUT_W - 1);
                                int pyMin = Mathf.Clamp((int)(minZ * szOut), 0, OUT_H - 1);
                                int pyMax = Mathf.Clamp((int)(maxZ * szOut), 0, OUT_H - 1);
                                for (int py = pyMin; py <= pyMax; py++)
                                {
                                    for (int px = pxMin; px <= pxMax; px++)
                                    {
                                        PaintWaterPixel(pixels, py * OUT_W + px);
                                        waterPainted++;
                                    }
                                }
                            }
                        }
                    }
                }

                // Orient to match in-game minimap. Heightnoise indexing
                // produces a buffer that's flipped on both X and Z relative
                // to FF's screen rendering. Reversing the array flips both
                // axes in one pass (180° rotation), aligning preview to the
                // in-game minimap so direction observations are consistent.
                System.Array.Reverse(pixels);

                // Encode + write
                var tex = new Texture2D(OUT_W, OUT_H, TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels);
                tex.Apply();
                byte[] png = ImageConversion.EncodeToPNG(tex);
                UnityEngine.Object.DestroyImmediate(tex);

                string outDir = Path.Combine("UserData", "RiversRestored", "Previews");
                Directory.CreateDirectory(outDir);

                // Filename includes seed (if available) + timestamp. Seed
                // first so dir-listing groups by seed nicely.
                string seedStr = TryGetSeedString(tg);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string filename = string.IsNullOrEmpty(seedStr)
                    ? $"map_{ts}.png"
                    : $"{seedStr}_{ts}.png";
                string outPath = Path.Combine(outDir, filename);

                File.WriteAllBytes(outPath, png);
                RenderedThisGen = true;
                Log($"Wrote preview ({OUT_W}x{OUT_H}, hn={hnW}x{hnH}, " +
                    $"waterAreas={waterAreaCount}, painted={waterPainted}) → {outPath}");
            }
            catch (Exception ex)
            {
                Log($"Render failed ({source}): {ex.Message}");
            }
        }

        /// <summary>Blend a blue water tint over the existing grayscale base
        /// at the given pixel index. 75% blue, 25% retained heightmap so the
        /// water still hints at depth/relief underneath.</summary>
        private static void PaintWaterPixel(Color32[] pixels, int idx)
        {
            var prev = pixels[idx];
            pixels[idx] = new Color32(
                (byte)(prev.r * 0.25f + 30 * 0.75f),
                (byte)(prev.g * 0.25f + 80 * 0.75f),
                (byte)(prev.b * 0.25f + 160 * 0.75f),
                255);
        }

        /// <summary>Best-effort: pull a seed string off the generator for the
        /// filename. If we can't find it, callers fall back to timestamp-only.</summary>
        private static string TryGetSeedString(TerrainGenerator tg)
        {
            try
            {
                var seedField = AccessTools.Field(typeof(TerrainGenerator), "currentMapSeed")
                                 ?? AccessTools.Field(typeof(TerrainGenerator), "_seed")
                                 ?? AccessTools.Field(typeof(TerrainGenerator), "seed");
                if (seedField != null)
                {
                    var v = seedField.GetValue(tg);
                    if (v != null) return v.ToString();
                }

                var gd = AccessTools.Field(typeof(TerrainGenerator), "_generationData")?.GetValue(tg);
                if (gd != null)
                {
                    var sField = gd.GetType().GetField("seed",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (sField != null)
                    {
                        var v = sField.GetValue(gd);
                        if (v != null) return v.ToString();
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static void Log(string msg) =>
            RiversRestoredMod.Log.Msg($"[RR][Preview] {msg}");
    }
}
