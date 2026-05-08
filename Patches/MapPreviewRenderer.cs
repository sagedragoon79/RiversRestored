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

                // Pre-compute 5th and 95th percentile heightnoise values via
                // a 256-bin histogram. Using percentiles instead of raw
                // min/max gives mid-elevations more visual range — outliers
                // like the map-edge mountain ridge no longer compress the
                // useful elevation band.
                float rawMin = float.MaxValue, rawMax = float.MinValue;
                for (int x = 0; x < hnW; x++)
                {
                    for (int z = 0; z < hnH; z++)
                    {
                        float v = heightNoise[x, z];
                        if (v < rawMin) rawMin = v;
                        if (v > rawMax) rawMax = v;
                    }
                }
                float rawRange = Mathf.Max(0.0001f, rawMax - rawMin);
                const int HIST_BINS = 256;
                int[] hist = new int[HIST_BINS];
                int totalCells = hnW * hnH;
                for (int x = 0; x < hnW; x++)
                {
                    for (int z = 0; z < hnH; z++)
                    {
                        float v = heightNoise[x, z];
                        int bin = Mathf.Clamp(
                            (int)((v - rawMin) / rawRange * (HIST_BINS - 1)),
                            0, HIST_BINS - 1);
                        hist[bin]++;
                    }
                }
                int target5 = (int)(totalCells * 0.05f);
                int target95 = (int)(totalCells * 0.95f);
                int p5Bin = 0, p95Bin = HIST_BINS - 1, accum = 0;
                for (int b = 0; b < HIST_BINS; b++)
                {
                    accum += hist[b];
                    if (accum >= target5 && p5Bin == 0) p5Bin = b;
                    if (accum >= target95) { p95Bin = b; break; }
                }
                float hnMin = rawMin + (p5Bin / (float)(HIST_BINS - 1)) * rawRange;
                float hnMax = rawMin + (p95Bin / (float)(HIST_BINS - 1)) * rawRange;
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
                // Heightmap render: percentile-normalized elevation → color
                // ramp (green low → tan mid → brown high → white peaks),
                // multiplied by hillshade for 3D feel.
                //
                // Hillshade: virtual sun at azimuth 315° (NW), altitude 45°.
                // For each cell, compute the surface normal from neighbor
                // heightnoise gradients, dot with light direction, multiply
                // base color by the result. Slope exaggeration is needed
                // because heightnoise units are 0..1 (very flat compared to
                // cell width); without it, hillshade is invisible.
                const float SLOPE_EXAGGERATE = 80f;  // empirical — readable shadows on typical seeds
                float lightAlt = 45f * Mathf.Deg2Rad;
                float lightAz = 315f * Mathf.Deg2Rad;
                Vector3 light = new Vector3(
                    Mathf.Sin(lightAz) * Mathf.Cos(lightAlt),
                    Mathf.Sin(lightAlt),
                    Mathf.Cos(lightAz) * Mathf.Cos(lightAlt)).normalized;

                // Axis convention: X-INVERTED, Z-natural. Empirically
                // verified by comparing against Pangu's preview thumbnail
                // and FF's in-game minimap on the New Game screen — both
                // showed river NW→SE while ours initially showed NE→SW
                // (X-axis mirrored). Inverting hx fixes orientation.
                //   pixel(px, py) → heightnoise[hnW-1-px scaled, py scaled]
                int hxBorder = hnW / 10;
                int hzBorder = hnH / 10;
                for (int py = 0; py < OUT_H; py++)
                {
                    int hz = (int)((float)py / (OUT_H - 1) * (hnH - 1));
                    for (int px = 0; px < OUT_W; px++)
                    {
                        int hx = (int)((float)(OUT_W - 1 - px) / (OUT_W - 1) * (hnW - 1));
                        float v = heightNoise[hx, hz];
                        float n = Mathf.Clamp01((v - hnMin) / hnRange);

                        // Hillshade — sample neighbors with edge clamp.
                        int hxL = Mathf.Max(0, hx - 1);
                        int hxR = Mathf.Min(hnW - 1, hx + 1);
                        int hzL = Mathf.Max(0, hz - 1);
                        int hzR = Mathf.Min(hnH - 1, hz + 1);
                        float dhx = (heightNoise[hxR, hz] - heightNoise[hxL, hz]) * 0.5f * SLOPE_EXAGGERATE;
                        float dhz = (heightNoise[hx, hzR] - heightNoise[hx, hzL]) * 0.5f * SLOPE_EXAGGERATE;
                        Vector3 normal = new Vector3(-dhx, 1f, -dhz).normalized;
                        float lambert = Mathf.Max(0.35f, Vector3.Dot(normal, light));
                        // Lambert is now in [0.35, 1.0] — 0.35 floor preserves
                        // detail in self-shadowed areas.

                        // Color ramp by elevation. Linear interpolation
                        // between control colors at fixed elevation stops.
                        Color32 baseColor = ElevationToColor(n);

                        // Border clamp: dark/saturated colors near the map
                        // edge wash out the rest of the scene; lift them
                        // toward neutral gray to keep edges readable without
                        // dominating.
                        bool inBorder = hx < hxBorder || hx > hnW - hxBorder
                                     || hz < hzBorder || hz > hnH - hzBorder;
                        if (inBorder)
                        {
                            // Blend toward mid-gray (128) by 50% in border.
                            baseColor = new Color32(
                                (byte)((baseColor.r + 128) / 2),
                                (byte)((baseColor.g + 128) / 2),
                                (byte)((baseColor.b + 128) / 2),
                                255);
                        }

                        byte r = (byte)(baseColor.r * lambert);
                        byte g = (byte)(baseColor.g * lambert);
                        byte b = (byte)(baseColor.b * lambert);
                        pixels[py * OUT_W + px] = new Color32(r, g, b, 255);
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
                        // Natural mapping (matches the heightmap loop above):
                        //   pixel_x = worldX_scaled
                        //   pixel_y = worldZ_scaled
                        float sxOut = (float)(OUT_W - 1) / (hnW - 1);  // worldX → pixel X
                        float szOut = (float)(OUT_H - 1) / (hnH - 1);  // worldZ → pixel Y

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

                            bool[,]? mask = null;
                            try { mask = fPoints?.GetValue(wa) as bool[,]; }
                            catch { }

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
                                        // X-axis inverted to match heightmap loop convention.
                                        int px = Mathf.Clamp((int)((hnW - 1 - worldX) * sxOut), 0, OUT_W - 1);
                                        PaintWaterPixel(pixels, py * OUT_W + px);
                                        waterPainted++;
                                    }
                                }
                            }
                            else
                            {
                                // X-axis inverted bbox bounds.
                                int pxLo = Mathf.Clamp((int)((hnW - 1 - maxX) * sxOut), 0, OUT_W - 1);
                                int pxHi = Mathf.Clamp((int)((hnW - 1 - minX) * sxOut), 0, OUT_W - 1);
                                int pyLo = Mathf.Clamp((int)(minZ * szOut), 0, OUT_H - 1);
                                int pyHi = Mathf.Clamp((int)(maxZ * szOut), 0, OUT_H - 1);
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

                // ── Resource overlay: minerals (iron/gold/clay/coal/sand/stone) ──
                // Reads MineralManager.Instance at render time. May be null
                // or empty if minerals haven't spawned yet (Stage 70+ resource
                // stage). When that's the case, the overlay is silently
                // skipped — log line below confirms what's available.
                int mineralsRendered = DrawMineralOverlay(pixels, hnW, hnH);
                Log($"Minerals overlay: {mineralsRendered} markers drawn.");

                // Encode + write
                var tex = new Texture2D(OUT_W, OUT_H, TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels);
                tex.Apply();
                byte[] png = ImageConversion.EncodeToPNG(tex);

                // Hand the texture to the in-game overlay (replaces the prior
                // one if any). DontDestroy on the overlay GameObject keeps
                // the texture alive across scene changes. We deliberately
                // DON'T DestroyImmediate here because PreviewOverlay needs
                // it for OnGUI rendering. Old textures are released below.
                if (PreviewOverlay.LatestPreview != null
                    && PreviewOverlay.LatestPreview != tex)
                {
                    UnityEngine.Object.Destroy(PreviewOverlay.LatestPreview);
                }
                PreviewOverlay.LatestPreview = tex;

                string outDir = Path.Combine("UserData", "RiversRestored", "Previews");
                Directory.CreateDirectory(outDir);

                // Filename embeds the gen metadata so screenshots are
                // self-describing without needing a separate text file or
                // font rendering on the image. Format:
                //   <seed>_<preset>_r<riverCount>_w<waterPct>_<timestamp>.png
                // Example: "5CB1426566A_IdyllicValley_r4_w12pct_20260507_195851.png"
                string seedStr = TryGetSeedString(tg);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string presetStr = (RiversRestoredMod.RiverPreset?.Value ?? RiverPresetMode.IdyllicValley).ToString();
                int riverCount = CountRivers(tg);
                int waterPct = totalCells > 0
                    ? (int)Math.Round(100.0 * waterPainted / (OUT_W * OUT_H))
                    : 0;
                string baseName = string.IsNullOrEmpty(seedStr)
                    ? $"map_{presetStr}_r{riverCount}_w{waterPct}pct_{ts}"
                    : $"{seedStr}_{presetStr}_r{riverCount}_w{waterPct}pct_{ts}";
                // Sanitize — strip anything that's not safe for filenames
                foreach (char c in Path.GetInvalidFileNameChars())
                    baseName = baseName.Replace(c, '_');
                string outPath = Path.Combine(outDir, baseName + ".png");

                File.WriteAllBytes(outPath, png);
                RenderedThisGen = true;

                // Set caption for the overlay panel. Compact one-liner with
                // the most useful at-a-glance gen metadata.
                PreviewOverlay.LatestCaption =
                    $"Seed {(string.IsNullOrEmpty(seedStr) ? "?" : seedStr)} · " +
                    $"{presetStr} · {riverCount} river(s) · {waterPct}% water";

                Log($"Wrote preview ({OUT_W}x{OUT_H}, hn={hnW}x{hnH}, " +
                    $"waterAreas={waterAreaCount}, painted={waterPainted}, " +
                    $"minerals={mineralsRendered}) → {outPath}");
            }
            catch (Exception ex)
            {
                Log($"Render failed ({source}): {ex.Message}");
            }
        }

        /// <summary>Find the live MineralManager (if present at render time)
        /// and rasterize each mineral deposit as a small colored marker on
        /// the preview. Mineral colors mirror the ff-game-map dev tool palette
        /// (DataDefines.cpp::mineralColor). Returns the number of markers
        /// painted; zero usually means minerals haven't spawned yet (resource
        /// stage runs after our render hook).</summary>
        private static int DrawMineralOverlay(Color32[] pixels, int hnW, int hnH)
        {
            try
            {
                Type? mmType = AccessTools.TypeByName("MineralManager")
                                ?? AccessTools.TypeByName("Mineral.MineralManager");
                if (mmType == null) return 0;

                var instance = UnityEngine.Object.FindObjectOfType(mmType);
                if (instance == null) return 0;

                IList? minerals = null;
                foreach (var name in new[] { "minerals", "Minerals", "_minerals", "mineralList", "allMinerals" })
                {
                    var f = mmType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) { minerals = f.GetValue(instance) as IList; if (minerals != null) break; }
                    var p = mmType.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null) { minerals = p.GetValue(instance) as IList; if (minerals != null) break; }
                }
                if (minerals == null || minerals.Count == 0) return 0;

                // Natural mapping — matches the heightmap and water raster.
                float sxOut = (float)(OUT_W - 1) / (hnW - 1);
                float szOut = (float)(OUT_H - 1) / (hnH - 1);

                int painted = 0;
                foreach (var m in minerals)
                {
                    if (m == null) continue;
                    Type mt = m.GetType();

                    Vector3 pos = Vector3.zero;
                    bool gotPos = false;
                    var tProp = mt.GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                    if (tProp != null)
                    {
                        var tr = tProp.GetValue(m) as Transform;
                        if (tr != null) { pos = tr.position; gotPos = true; }
                    }
                    if (!gotPos)
                    {
                        var pField = mt.GetField("position", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (pField != null && pField.GetValue(m) is Vector3 v3) { pos = v3; gotPos = true; }
                    }
                    if (!gotPos) continue;

                    string typeStr = "";
                    foreach (var n in new[] { "type", "mineralType", "Type" })
                    {
                        var f = mt.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (f != null) { var v = f.GetValue(m); if (v != null) { typeStr = v.ToString(); break; } }
                        var p = mt.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (p != null) { var v = p.GetValue(m); if (v != null) { typeStr = v.ToString(); break; } }
                    }
                    if (string.IsNullOrEmpty(typeStr)) typeStr = mt.Name;

                    Color32 markerColor = MineralTypeToColor(typeStr);

                    const float CELL_SIZE = 5f;
                    float worldX = pos.x / CELL_SIZE;
                    float worldZ = pos.z / CELL_SIZE;
                    if (worldX < 0 || worldX >= hnW || worldZ < 0 || worldZ >= hnH) continue;

                    // X-axis inverted to match heightmap/water raster convention.
                    int px = Mathf.Clamp((int)((hnW - 1 - worldX) * sxOut), 0, OUT_W - 1);
                    int py = Mathf.Clamp((int)(worldZ * szOut), 0, OUT_H - 1);

                    // 3×3 dot for visibility.
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = px + dx, yy = py + dy;
                            if (xx < 0 || xx >= OUT_W || yy < 0 || yy >= OUT_H) continue;
                            pixels[yy * OUT_W + xx] = markerColor;
                        }
                    painted++;
                }
                return painted;
            }
            catch (Exception ex)
            {
                Log($"DrawMineralOverlay failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Mineral-type-name → color, matching ff-game-map dev tool
        /// palette. Substring match handles FF reporting type as enum value,
        /// qualified string, or class name.</summary>
        private static Color32 MineralTypeToColor(string typeStr)
        {
            typeStr = typeStr.ToLowerInvariant();
            if (typeStr.Contains("iron"))   return new Color32(140, 140, 140, 255);
            if (typeStr.Contains("gold"))   return new Color32(127, 127, 0, 255);
            if (typeStr.Contains("coal"))   return new Color32(80, 80, 80, 255);
            if (typeStr.Contains("clay"))   return new Color32(127, 30, 30, 255);
            if (typeStr.Contains("sand"))   return new Color32(255, 255, 170, 255);
            if (typeStr.Contains("stone"))  return new Color32(200, 200, 200, 255);
            return new Color32(255, 0, 255, 255);  // magenta = unrecognized
        }

        /// <summary>Map normalized elevation [0..1] to a topographic color.
        /// Palette: dark green (low/marsh) → light green (farmland) → tan
        /// (rolling) → brown (hills) → near-white (peaks). Linear lerp
        /// between fixed control colors at fixed elevation stops.</summary>
        private static Color32 ElevationToColor(float n)
        {
            // Control colors (RGB) at elevation stops [0, 0.25, 0.5, 0.75, 1].
            // Tweaked for visual contrast against the sky-blue water and
            // readable hillshade overlay. Avoids pure black/white at extremes
            // so multiplying by lambert never goes flat.
            // 0.00: dark olive  — flat marsh / very low ground
            // 0.25: bright green — farmland / lowland
            // 0.50: tan          — rolling hills / midland
            // 0.75: brown        — mountainsides / highland
            // 1.00: near-white   — peaks / mountain tops
            if (n <= 0.25f)
                return LerpColor(new Color32(80, 100, 60, 255),
                                 new Color32(160, 200, 110, 255),
                                 n * 4f);
            if (n <= 0.50f)
                return LerpColor(new Color32(160, 200, 110, 255),
                                 new Color32(210, 200, 140, 255),
                                 (n - 0.25f) * 4f);
            if (n <= 0.75f)
                return LerpColor(new Color32(210, 200, 140, 255),
                                 new Color32(150, 110, 80, 255),
                                 (n - 0.50f) * 4f);
            return LerpColor(new Color32(150, 110, 80, 255),
                             new Color32(245, 240, 235, 255),
                             (n - 0.75f) * 4f);
        }

        private static Color32 LerpColor(Color32 a, Color32 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Color32(
                (byte)(a.r + (b.r - a.r) * t),
                (byte)(a.g + (b.g - a.g) * t),
                (byte)(a.b + (b.b - a.b) * t),
                255);
        }

        /// <summary>Blend a blue water tint over the existing colored base
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

        /// <summary>Best-effort: count the rivers in <c>_generationData.rivers</c>.
        /// Returns 0 if the list is missing or empty.</summary>
        private static int CountRivers(TerrainGenerator tg)
        {
            try
            {
                var gd = AccessTools.Field(typeof(TerrainGenerator), "_generationData")?.GetValue(tg);
                if (gd == null) return 0;
                var rField = gd.GetType().GetField("rivers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var rivers = rField?.GetValue(gd) as IList;
                return rivers?.Count ?? 0;
            }
            catch { return 0; }
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
