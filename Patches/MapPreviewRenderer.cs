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
                // output buffer using FF's screen orientation convention and
                // the heightmap-shading formula from the open-source
                // ff-game-map dev tool (mikh-abc/ff-game-map MapWidget.cpp:
                //   v = 1 - (heightMap[x][y] - heightMin) / (heightMax - heightMin)
                //   if (v < 0.5 && cell is in outer 10%) v = 0.5
                //   color = RGB(v, v, v)  (per-channel float, then * 255)
                //
                // That dev tool reads FF saves and renders maps that match
                // the in-game minimap. Inverting v (high terrain = dark, low
                // terrain = light) produces topographic shaded relief that
                // emphasizes flat farmable land. The border-clamp to 0.5
                // prevents the map's edge mountain ridge from washing out the
                // rest of the scene with pure-black pixels.
                //
                // Axis convention (transpose + Z-flip):
                //   pixel(px, py) shows heightnoise[hx, hz] where:
                //     hx = (H_OUT - 1 - py) scaled to hnW
                //     hz = (W_OUT - 1 - px) scaled to hnH
                int hxBorder = hnW / 10;
                int hzBorder = hnH / 10;
                for (int py = 0; py < OUT_H; py++)
                {
                    int hx = (int)((float)(OUT_H - 1 - py) / (OUT_H - 1) * (hnW - 1));
                    for (int px = 0; px < OUT_W; px++)
                    {
                        int hz = (int)((float)(OUT_W - 1 - px) / (OUT_W - 1) * (hnH - 1));
                        float v = heightNoise[hx, hz];
                        float n = (v - hnMin) / hnRange;
                        n = Mathf.Clamp01(n);
                        // Inverted: high terrain → dark, low terrain → light.
                        float devV = 1f - n;
                        // Border clamp: cells in the outer 10% that would
                        // render dark get pushed up to 0.5 gray so map edges
                        // don't go pure black. Per dev tool's heuristic.
                        bool inBorder = hx < hxBorder || hx > hnW - hxBorder
                                     || hz < hzBorder || hz > hnH - hzBorder;
                        if (devV < 0.5f && inBorder) devV = 0.5f;
                        byte b = (byte)(devV * 255f);
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
                        // World cell → texture pixel mapping per ff-game-map
                        // dev tool's convention (transpose + Z-flip):
                        //   pixel_y = (H_OUT - 1) - worldX_scaled
                        //   pixel_x = (W_OUT - 1) - worldZ_scaled
                        // Same convention used by the heightmap loop above.
                        float sxOut = (float)(OUT_H - 1) / (hnW - 1);  // worldX → pixel Y
                        float szOut = (float)(OUT_W - 1) / (hnH - 1);  // worldZ → pixel X

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
                                    int px = Mathf.Clamp((int)((hnH - 1 - worldZ) * szOut), 0, OUT_W - 1);
                                    for (int lx = 0; lx < mw; lx++)
                                    {
                                        if (!mask[lx, lz]) continue;
                                        int worldX = minX + lx;
                                        if (worldX < 0 || worldX >= hnW) continue;
                                        int py = Mathf.Clamp((int)((hnW - 1 - worldX) * sxOut), 0, OUT_H - 1);
                                        PaintWaterPixel(pixels, py * OUT_W + px);
                                        waterPainted++;
                                    }
                                }
                            }
                            else
                            {
                                // Bbox fallback. Same axis convention as
                                // the polygon path above.
                                int pxLo = Mathf.Clamp((int)((hnH - 1 - maxZ) * szOut), 0, OUT_W - 1);
                                int pxHi = Mathf.Clamp((int)((hnH - 1 - minZ) * szOut), 0, OUT_W - 1);
                                int pyLo = Mathf.Clamp((int)((hnW - 1 - maxX) * sxOut), 0, OUT_H - 1);
                                int pyHi = Mathf.Clamp((int)((hnW - 1 - minX) * sxOut), 0, OUT_H - 1);
                                for (int py = pyLo; py <= pyHi; py++)
                                {
                                    for (int px = pxLo; px <= pxHi; px++)
                                    {
                                        PaintWaterPixel(pixels, py * OUT_W + px);
                                        waterPainted++;
                                    }
                                }
                            }
                        }
                    }
                }

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
        /// at the given pixel index. Color matches the ff-game-map dev tool's
        /// water tone: RGB(128, 194, 255) — light sky blue. 80% blue, 20%
        /// retained heightmap so water still hints at relief underneath.</summary>
        private static void PaintWaterPixel(Color32[] pixels, int idx)
        {
            var prev = pixels[idx];
            const float blend = 0.80f;
            pixels[idx] = new Color32(
                (byte)(prev.r * (1f - blend) + 128 * blend),
                (byte)(prev.g * (1f - blend) + 194 * blend),
                (byte)(prev.b * (1f - blend) + 255 * blend),
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
