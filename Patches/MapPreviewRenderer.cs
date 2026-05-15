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

        // Counts from the most recent successful render. Exposed so the
        // caption builder (PreviewGenWorker.BuildRichCaption) can show
        // the SAME numbers we used for the rendered preview, instead of
        // computing its own (which double-counts overlapping water area
        // bboxes and over-reports water%).
        public static int LastRiverCount = 0;
        public static int LastWaterPct = 0;

        // Output dimensions. 768×768 — sweet spot for the 425px in-game
        // overlay panel: sharp without being wasteful. Heightnoise is
        // typically 384×384 (Medium) or 512×512 (Large), so we upscale.
        private const int OUT_W = 768;
        private const int OUT_H = 768;

        // World units per heightnoise cell. Used to convert TerrainArea
        // polygon vertices (Vector3 world space) into hn-cell space for
        // biome rasterization. Same constant the mineral overlay uses.
        private const float CELL_SIZE = 5f;

        /// <summary>Render the preview for the supplied terrain generator's
        /// current state. Safe to call multiple times — second-and-later calls
        /// no-op via <see cref="RenderedThisGen"/>. No render if the pref is off.</summary>
        public static void TryRender(TerrainGenerator tg, string source)
        {
            if (!(RiversRestoredMod.EnableMapPreviewRender?.Value ?? false)) return;
            // CRITICAL: only render when the preview pipeline is actively
            // requesting a gen. LateCarvePostfix (the caller) is hooked on
            // FF's terrain gen stage carriers (Stage 40/50/60/70/97), which
            // fire during BOTH preview gen AND actual gameplay terrain gen.
            // Without this gate, clicking "Start" → FF unloads our preview
            // Map scene → loads a fresh Map scene for gameplay → runs the
            // full terrain pipeline → our stage hooks fire → TryRender writes
            // a spurious PNG (with seed='' because the preview context is
            // gone) and the preview overlay re-engages with a progress bar
            // ("preview goes black and starts generating another preview").
            // IsPreviewActive is set true only by PreviewGenWorker around
            // its tgc.GenSliced_Generate call; HardCancel clears it after
            // the Map scene unload completes. False here = gameplay gen
            // (or any other non-preview path) → no preview render.
            if (!PreviewGenWorker.IsPreviewActive) return;
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

                // Biome color map — rasterize TerrainArea polygons to a
                // per-hn-cell Color32 grid using each area's TerrainBiome.editorColor.
                // Returns null if biome data isn't available yet (Stage 40 hasn't
                // run, or theme/biome assignment failed). When null we fall back
                // to pure elevation coloring like before.
                Color32[]? biomeMap = BuildBiomeColorMap(gd, hnW, hnH);

                // Per-preset terrain tint — subtle per-channel multiplier
                // applied to land pixels at the end of the heightmap loop.
                // Without this, AridHighlands and IdyllicValley render nearly
                // identical because they share most biome assets and only
                // differ in distribution. The tint nudges the overall color
                // cast so the map's intent reads at a glance.
                Vector3 presetTint = GetPresetTint(
                    RiversRestoredMod.RiverPreset?.Value ?? RiverPresetMode.IdyllicValley);

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

                // Axis convention: BOTH inverted (X-inverted + Z-inverted).
                // Empirical sequence:
                //   - Natural (both): user said "wrongly flipped and rotated"
                //   - X-inverted only: still X-mirrored vs Pangu
                //   - Z-inverted only: only rotates the image, not aligned
                //   - Both inverted (this): equivalent to 180° rotation;
                //     matches FlowBias's "both axes inverted" convention
                //     also observed earlier this session for those formulas.
                //   pixel(px, py) → heightnoise[hnW-1-px scaled, hnH-1-py scaled]
                int hxBorder = hnW / 10;
                int hzBorder = hnH / 10;
                for (int py = 0; py < OUT_H; py++)
                {
                    int hz = (int)((float)(OUT_H - 1 - py) / (OUT_H - 1) * (hnH - 1));
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
                        float lambert = Mathf.Max(0.60f, Vector3.Dot(normal, light));
                        // Lambert is now in [0.60, 1.0] — softer shadow
                        // floor so terrain stays readable under hillshade
                        // without the heavy dark-side cast.

                        // Color ramp by elevation. Linear interpolation
                        // between control colors at fixed elevation stops.
                        Color32 elevColor = ElevationToColor(n);

                        // Biome tint — blend the rasterized biome.editorColor
                        // with elevation ramp 60/40 in favor of biome when
                        // available; elevation still adds high-altitude tan/
                        // brown/white to mountain peaks regardless of biome.
                        // High elevations (n > 0.7) lean back toward elevation
                        // ramp so mountain peaks stay rocky/snowy even inside
                        // a forest biome.
                        Color32 baseColor = elevColor;
                        if (biomeMap != null)
                        {
                            Color32 bc = biomeMap[hz * hnW + hx];
                            if (bc.a > 0)
                            {
                                float biomeWeight = n < 0.7f
                                    ? 0.60f
                                    : Mathf.Lerp(0.60f, 0.10f, (n - 0.7f) / 0.3f);
                                baseColor = LerpColor(elevColor, bc, biomeWeight);
                            }
                        }

                        // Border = snow-capped mountain ridge surrounding the
                        // playable area. FF generates a high mountain wall
                        // around every map; rendering it as flat mid-gray
                        // washed out and read as a UI artifact. Treat it as
                        // actual snow instead: cool-white tint with granular
                        // sparkle noise. Hillshade still applies so the
                        // ridges show shape; snowiness ramps with proximity
                        // to the edge so it's not a hard cutoff.
                        bool inBorder = hx < hxBorder || hx > hnW - hxBorder
                                     || hz < hzBorder || hz > hnH - hzBorder;
                        if (inBorder)
                        {
                            // Distance-into-border [0..1]: 1 at the very edge,
                            // 0 at the inner border boundary. Use the smaller
                            // of the four edge distances normalized by hxBorder.
                            int distEdgeX = Math.Min(hx, hnW - 1 - hx);
                            int distEdgeZ = Math.Min(hz, hnH - 1 - hz);
                            int distEdge = Math.Min(distEdgeX, distEdgeZ);
                            float t = 1f - Mathf.Clamp01((float)distEdge / hxBorder);

                            // Snow base — cool white with very slight blue
                            // tint, NOT pure white (avoids glare against the
                            // dark UI).
                            Color32 snow = new Color32(232, 236, 245, 255);
                            // Sparkle: deterministic per-cell hash. ~⅛ of
                            // cells get a brighter pixel for snow-glint
                            // effect; rest stay base snow tone.
                            int snowHash = (hx * 928371 + hz * 6571) & 0xFF;
                            if (snowHash < 32)
                                snow = new Color32(250, 252, 255, 255);
                            else if (snowHash > 220)
                                // Slight shadow flecks — uneven snow surface.
                                snow = new Color32(195, 205, 220, 255);

                            // Lerp from base biome/elevation color → snow
                            // proportional to t. At the very edge, fully snow;
                            // toward the inner boundary, mostly the underlying
                            // terrain color so the transition reads as
                            // "treeline → snow" rather than a hard ring.
                            baseColor = LerpColor(baseColor, snow, t);
                        }

                        // Subtle contour lines — every 0.08 normalized-elevation
                        // step. Detect by comparing neighbor's quantized step;
                        // when they differ this pixel is on the contour.
                        // Darken by ~20% — readable, more visible at higher
                        // resolution.
                        const float CONTOUR_STEP = 0.08f;
                        int myStep = (int)(n / CONTOUR_STEP);
                        float nRight = Mathf.Clamp01((heightNoise[hxR, hz] - hnMin) / hnRange);
                        float nDown  = Mathf.Clamp01((heightNoise[hx, hzR] - hnMin) / hnRange);
                        bool onContour = (int)(nRight / CONTOUR_STEP) != myStep
                                      || (int)(nDown  / CONTOUR_STEP) != myStep;
                        float contourMul = onContour ? 0.80f : 1f;

                        // Slope-based rocky tint — steep cells (high gradient
                        // magnitude regardless of absolute elevation) shift
                        // toward warm brown/gray. Adds variation in the
                        // mid-elevation band where the elevation ramp alone
                        // is uniform tan/green.
                        float slopeMag = Mathf.Sqrt(dhx * dhx + dhz * dhz) / SLOPE_EXAGGERATE;
                        float rockShift = Mathf.Clamp01(slopeMag * 6f);
                        Color32 rockColor = new Color32(150, 130, 105, 255);
                        baseColor = LerpColor(baseColor, rockColor, rockShift * 0.35f);

                        // Per-pixel land noise — small deterministic ± value
                        // per channel keeps flat patches from reading as
                        // single-color paint. Uses the same hash trick as
                        // the water texture pass; magnitude smaller (±5).
                        int lhash = (px * 73856093) ^ (py * 19349663);
                        float lnoise = ((lhash & 0xF) - 7.5f) * 0.7f;  // ≈ ±5

                        // Apply per-preset tint as a per-channel multiplier
                        // (each channel mul is ≈ 0.92..1.08, so total color
                        // shift is subtle but the cast distinguishes presets).
                        float fr = baseColor.r * lambert * contourMul * presetTint.x + lnoise;
                        float fg = baseColor.g * lambert * contourMul * presetTint.y + lnoise * 0.9f;
                        float fb = baseColor.b * lambert * contourMul * presetTint.z + lnoise * 0.7f;
                        byte r = (byte)Mathf.Clamp(fr, 0f, 255f);
                        byte g = (byte)Mathf.Clamp(fg, 0f, 255f);
                        byte b = (byte)Mathf.Clamp(fb, 0f, 255f);
                        pixels[py * OUT_W + px] = new Color32(r, g, b, 255);
                    }
                }

                // Water overlay — true polygon raster. Each WaterArea has a
                // `points` bool[,] mask sized to its bounding box; cells where
                // the mask is true are inside the polygon. Map each masked
                // world-cell to an output pixel and paint blue. Falls back to
                // bbox raster only if the mask field is missing or null.
                // Track which output pixels are water. Used by the shoreline
                // pass to highlight pixels that border water but aren't water
                // themselves. Bool[] keeps the pass O(W*H) instead of needing
                // to retest pixel colors (which would false-positive on
                // pre-water blue tones).
                // 0 = land, 1 = river, 2 = lake/pond. Used by shoreline + water-
                // texture passes to apply different visual treatment per kind.
                byte[] waterKind = new byte[OUT_W * OUT_H];

                int waterPainted = 0;
                int waterAreaCount = 0;
                int riverAreaCount = 0;
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
                    var fWaterType = waType?.GetField("waterType");
                    Type? wtType = AccessTools.TypeByName("TerrainGen.WaterType");
                    var fRiverEnd = wtType?.GetField("riverEndPoint");

                    if (fMinX != null && fMinZ != null && fMaxX != null && fMaxZ != null)
                    {
                        // Natural mapping (matches the heightmap loop above):
                        //   pixel_x = worldX_scaled
                        //   pixel_y = worldZ_scaled
                        float sxOut = (float)(OUT_W - 1) / (hnW - 1);  // worldX → pixel X
                        float szOut = (float)(OUT_H - 1) / (hnH - 1);  // worldZ → pixel Y
                        // When upscaling (sxOut/szOut > 1), painting one
                        // pixel per mask cell leaves gaps because some
                        // pixels never get a mask cell mapped to them.
                        // Paint a block sized to cover the upscale factor
                        // so coverage stays continuous on smaller maps.
                        int blockW = Mathf.Max(1, Mathf.CeilToInt(sxOut));
                        int blockH = Mathf.Max(1, Mathf.CeilToInt(szOut));

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

                            // Discriminate river vs lake by reading the
                            // attached WaterType.riverEndPoint flag. RR's own
                            // MarkAllWaterTypesAsRiverEnd patch flips this on
                            // every WaterType — but only WaterAreas that
                            // *originated* from river paths carry a WaterType
                            // whose name says River; lakes/ponds carry
                            // WaterType_LakeSmall / Pond / etc. So a name
                            // substring check is more reliable than the flag.
                            bool isRiver = false;
                            try
                            {
                                if (fWaterType != null)
                                {
                                    var wt = fWaterType.GetValue(wa) as UnityEngine.Object;
                                    if (wt != null)
                                    {
                                        string nm = wt.name ?? "";
                                        isRiver = nm.IndexOf("river", StringComparison.OrdinalIgnoreCase) >= 0;
                                    }
                                }
                            }
                            catch { }
                            if (isRiver) riverAreaCount++;

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
                                    // Both axes inverted to match heightmap loop convention.
                                    int py = Mathf.Clamp((int)((hnH - 1 - worldZ) * szOut), 0, OUT_H - 1);
                                    for (int lx = 0; lx < mw; lx++)
                                    {
                                        if (!mask[lx, lz]) continue;
                                        int worldX = minX + lx;
                                        if (worldX < 0 || worldX >= hnW) continue;
                                        int px = Mathf.Clamp((int)((hnW - 1 - worldX) * sxOut), 0, OUT_W - 1);
                                        // Paint block to avoid grid gaps when upscaling.
                                        for (int byy = 0; byy < blockH; byy++)
                                        {
                                            int yy = py + byy;
                                            if (yy < 0 || yy >= OUT_H) continue;
                                            for (int bxx = 0; bxx < blockW; bxx++)
                                            {
                                                int xx = px + bxx;
                                                if (xx < 0 || xx >= OUT_W) continue;
                                                int idx = yy * OUT_W + xx;
                                                PaintWaterPixel(pixels, idx, isRiver);
                                                waterKind[idx] = (byte)(isRiver ? 1 : 2);
                                                waterPainted++;
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Both axes inverted bbox bounds.
                                int pxLo = Mathf.Clamp((int)((hnW - 1 - maxX) * sxOut), 0, OUT_W - 1);
                                int pxHi = Mathf.Clamp((int)((hnW - 1 - minX) * sxOut), 0, OUT_W - 1);
                                int pyLo = Mathf.Clamp((int)((hnH - 1 - maxZ) * szOut), 0, OUT_H - 1);
                                int pyHi = Mathf.Clamp((int)((hnH - 1 - minZ) * szOut), 0, OUT_H - 1);
                                for (int py = pyLo; py <= pyHi; py++)
                                {
                                    for (int px = pxLo; px <= pxHi; px++)
                                    {
                                        int idx = py * OUT_W + px;
                                        PaintWaterPixel(pixels, idx, isRiver);
                                        waterKind[idx] = (byte)(isRiver ? 1 : 2);
                                        waterPainted++;
                                    }
                                }
                            }
                        }
                    }
                }

                // ── Water depth + texture pass: makes water bodies feel
                // less flat. Two effects combined per water pixel:
                //   (1) Depth-from-shore gradient — pixels in the interior
                //       of a body darken; pixels near the shore stay lighter.
                //       Computed via a 5-pixel radial sample of waterKind:
                //       count of water neighbors → 0..max → darken factor.
                //   (2) Per-pixel deterministic noise — small ±value derived
                //       from coordinates adds micro-texture so flat blue
                //       reads as water surface, not paint.
                // Lakes apply both strongly (deep, textured); rivers apply
                // depth lightly (channel water reads as flowing/shallow) but
                // share the same noise.
                {
                    const int RADIUS = 4;            // 9×9 sample window
                    const int MAX_WATER_NEIGHBORS = (RADIUS * 2 + 1) * (RADIUS * 2 + 1);
                    for (int y = 0; y < OUT_H; y++)
                    {
                        int rowBase = y * OUT_W;
                        for (int x = 0; x < OUT_W; x++)
                        {
                            int idx = rowBase + x;
                            byte kind = waterKind[idx];
                            if (kind == 0) continue;

                            // Count water neighbors in window — inlined to
                            // keep the inner loop tight; clamps at edges.
                            int yLo = Math.Max(0, y - RADIUS);
                            int yHi = Math.Min(OUT_H - 1, y + RADIUS);
                            int xLo = Math.Max(0, x - RADIUS);
                            int xHi = Math.Min(OUT_W - 1, x + RADIUS);
                            int waterCount = 0;
                            for (int yy = yLo; yy <= yHi; yy++)
                            {
                                int rb = yy * OUT_W;
                                for (int xx = xLo; xx <= xHi; xx++)
                                    if (waterKind[rb + xx] != 0) waterCount++;
                            }
                            // depth ∈ [0, 1] — 0 = shore, 1 = fully surrounded
                            float depth = (float)waterCount / MAX_WATER_NEIGHBORS;

                            // Layered water texture for richer surface feel:
                            //  (1) Fine per-pixel hash — ±10 on B (more on
                            //      blue so it reads as water shimmer rather
                            //      than gray static).
                            //  (2) Coarse low-frequency hash (sample every
                            //      8 pixels) — gives ~8-pixel "patches" of
                            //      slightly varied tone, mimicking ripple
                            //      groupings on a still surface.
                            //  (3) Sine-wave ripple band — broad horizontal
                            //      bands of subtle brightness, frequency
                            //      higher for rivers (flowing) than lakes
                            //      (placid).
                            // Bank shadow into water — water pixels with
                            // land to their NW (sun direction) darken to
                            // suggest the bank casts a shadow ONTO the water.
                            // This is the key cue that water is BELOW land,
                            // not raised above it. Sample 3 pixels NW; the
                            // closer the land, the deeper the shadow.
                            float bankShadow = 1f;
                            for (int s = 1; s <= 3; s++)
                            {
                                int sx = x - s;
                                int sy = y - s;
                                if (sx < 0 || sy < 0) break;
                                if (waterKind[sy * OUT_W + sx] == 0)
                                {
                                    // Closer = darker. s=1 → 0.62, s=2 → 0.74, s=3 → 0.86
                                    bankShadow = Mathf.Min(bankShadow, 0.50f + s * 0.12f);
                                }
                            }

                            int fineHash = (x * 73856093) ^ (y * 19349663);
                            float fineNoise = ((fineHash & 0x1F) - 15.5f) * 0.7f;  // ≈ ±10

                            int coarseHash = ((x >> 3) * 83492791) ^ ((y >> 3) * 12582917);
                            float coarseNoise = ((coarseHash & 0x1F) - 15.5f) * 0.8f;  // ≈ ±12

                            // Higher freq for rivers so they read as moving
                            // water; lakes get gentler longer waves.
                            float ripFreq = (kind == 1) ? 0.18f : 0.07f;
                            float ripple = Mathf.Sin(x * ripFreq + y * ripFreq * 0.6f) * 4f;

                            var prev = pixels[idx];
                            // Lakes darken more (up to 35%) toward center.
                            // Rivers darken lightly (up to 15%).
                            float depthMul = (kind == 2)
                                ? Mathf.Lerp(1.00f, 0.65f, depth)
                                : Mathf.Lerp(1.00f, 0.85f, depth);
                            // Combine depth + bank shadow — bank shadow
                            // takes precedence (i.e. multiplies cumulatively).
                            float waterMul = depthMul * bankShadow;
                            float totalNoise = fineNoise + coarseNoise + ripple;
                            float pr = prev.r * waterMul + totalNoise * 0.4f;
                            float pg = prev.g * waterMul + totalNoise * 0.7f;
                            float pb = prev.b * waterMul + totalNoise * 1.1f;
                            pixels[idx] = new Color32(
                                (byte)Mathf.Clamp(pr, 0f, 255f),
                                (byte)Mathf.Clamp(pg, 0f, 255f),
                                (byte)Mathf.Clamp(pb, 0f, 255f),
                                255);
                        }
                    }
                }

                // ── Shoreline pass: subtle dark-earth ring on land pixels
                // adjacent to water. The earlier warm-sand halo created a
                // "lit plateau rim" illusion that made water read as raised.
                // A darker tone instead suggests damp earth at the water's
                // edge and reinforces the perception that water is BELOW
                // the surrounding land. Single-pixel ring; cheap.
                {
                    Color32 shorelineTone = new Color32(60, 48, 36, 255);  // damp earth
                    for (int y = 1; y < OUT_H - 1; y++)
                    {
                        int rowBase = y * OUT_W;
                        for (int x = 1; x < OUT_W - 1; x++)
                        {
                            int idx = rowBase + x;
                            if (waterKind[idx] != 0) continue;
                            bool nearWater =
                                   waterKind[idx - 1]      != 0
                                || waterKind[idx + 1]      != 0
                                || waterKind[idx - OUT_W]  != 0
                                || waterKind[idx + OUT_W]  != 0;
                            if (!nearWater) continue;
                            var prev = pixels[idx];
                            const float blend = 0.30f;
                            pixels[idx] = new Color32(
                                (byte)(prev.r * (1f - blend) + shorelineTone.r * blend),
                                (byte)(prev.g * (1f - blend) + shorelineTone.g * blend),
                                (byte)(prev.b * (1f - blend) + shorelineTone.b * blend),
                                255);
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
                //   <seed>_<preset>_<size>_r<rivers>_w<water>pct_d<RTVT>_<timestamp>.png
                // Example: "6BCDBA3C462_AlpineValleys_M_r2_w14pct_dRTVT_20260509_140529.png"
                //
                // Difficulty letters are RR's caption shorthand:
                //   P=Pioneer (Easy)  T=Trailblazer (Normal)
                //   V=Vanquisher (Hard)  X=VeryHard
                // Order is Resources / Wildlife / Maladies / Raiders,
                // matching the on-screen caption layout.
                string seedStr = TryGetSeedString(tg);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string presetStr = (RiversRestoredMod.RiverPreset?.Value ?? RiverPresetMode.IdyllicValley).ToString();
                string sizeStr = ReadMapSizeLetter();
                string diffStr = ReadDifficultyLetters();
                int riverCount = CountRivers(tg);
                int waterPct = totalCells > 0
                    ? (int)Math.Round(100.0 * waterPainted / (OUT_W * OUT_H))
                    : 0;
                // Stash for caption builders to read.
                LastRiverCount = riverCount;
                LastWaterPct = waterPct;
                string seedPrefix = string.IsNullOrEmpty(seedStr) ? "map" : seedStr;
                string baseName = $"{seedPrefix}_{presetStr}_{sizeStr}_r{riverCount}_w{waterPct}pct_d{diffStr}_{ts}";
                // Sanitize — strip anything that's not safe for filenames
                foreach (char c in Path.GetInvalidFileNameChars())
                    baseName = baseName.Replace(c, '_');
                string outPath = Path.Combine(outDir, baseName + ".png");

                File.WriteAllBytes(outPath, png);
                RenderedThisGen = true;
                PrunePreviewsDirectory(outDir, MAX_STORED_PREVIEWS);

                // Set caption for the overlay panel. Compact one-liner with
                // the most useful at-a-glance gen metadata.
                // Default split caption (PreviewGenWorker overrides with
                // richer metadata after this returns when the user clicks
                // PREVIEW; this default fires when the gen came from
                // gameplay or Pangu's preview path).
                string seedDisp = string.IsNullOrEmpty(seedStr) ? "?" : seedStr;
                PreviewOverlay.LatestCaptionLeft =
                    $"Seed {seedDisp} · {presetStr}\n" +
                    $"{riverCount} river(s) · {waterPct}% water";
                PreviewOverlay.LatestCaptionMid = "";
                PreviewOverlay.LatestCaptionRight = "";
                PreviewOverlay.LatestCaption = "";

                Log($"Wrote preview seed='{seedStr}' ({OUT_W}x{OUT_H}, hn={hnW}x{hnH}, " +
                    $"waterAreas={waterAreaCount} (river={riverAreaCount}), " +
                    $"painted={waterPainted}, biomeMap={(biomeMap != null ? "yes" : "no")}, " +
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

                    // Both axes inverted to match heightmap/water raster convention.
                    int px = Mathf.Clamp((int)((hnW - 1 - worldX) * sxOut), 0, OUT_W - 1);
                    int py = Mathf.Clamp((int)((hnH - 1 - worldZ) * szOut), 0, OUT_H - 1);

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

        /// <summary>Build a per-hn-cell biome color grid by rasterizing each
        /// TerrainArea polygon and writing its biome.editorColor where the
        /// polygon contains the cell's world-space center. Returns null when
        /// areas/biome data isn't ready (e.g. before Stage 40 PaintBiomes
        /// runs). Caller falls back to pure elevation coloring in that case.
        ///
        /// TerrainArea inherits TerrainPoly which has a public bool Contains(
        /// float x, float z) point-in-polygon method — we reuse it via
        /// reflection. Each area's bounding box is computed from its points
        /// array to avoid testing every cell against every area.</summary>
        private static Color32[]? BuildBiomeColorMap(object gd, int hnW, int hnH)
        {
            try
            {
                var areasField = gd.GetType().GetField("areas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var areas = areasField?.GetValue(gd) as IList;
                if (areas == null || areas.Count == 0) return null;

                Type? taType = AccessTools.TypeByName("TerrainGen.TerrainArea");
                Type? tpType = AccessTools.TypeByName("TerrainGen.TerrainPoly");
                if (taType == null || tpType == null) return null;

                var fBiome = taType.GetField("biome",
                    BindingFlags.Public | BindingFlags.Instance);
                var fPoints = tpType.GetField("points",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var miContains = tpType.GetMethod("Contains",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(float), typeof(float) }, null);
                if (fBiome == null || fPoints == null || miContains == null) return null;

                Type? tbType = AccessTools.TypeByName("TerrainGen.TerrainBiome");
                var fEditorColor = tbType?.GetField("editorColor",
                    BindingFlags.Public | BindingFlags.Instance);
                if (fEditorColor == null) return null;

                var fTreeDensity = taType.GetField("treeDensity",
                    BindingFlags.Public | BindingFlags.Instance);

                var map = new Color32[hnW * hnH];
                int paintedCells = 0;
                int areasUsed = 0;
                int areaIndex = 0;
                object[] containsArgs = new object[2];

                foreach (var area in areas)
                {
                    areaIndex++;
                    if (area == null) continue;
                    var biome = fBiome.GetValue(area);
                    if (biome == null) continue;
                    Color ec;
                    try { ec = (Color)fEditorColor.GetValue(biome); }
                    catch { continue; }
                    // Skip the down-stream code's old col= line; pre-process
                    // jitter + treeDensity below.

                    // Per-area color variation — kept VERY subtle (±2% per
                    // channel). Earlier ±5% values made the map read as
                    // discrete colored patches instead of smooth biomes;
                    // post-rasterization box blur softens what's left.
                    int areaHash = unchecked((int)(areaIndex * 2654435761u));
                    float jr = ((areaHash       & 0xFF) / 255f - 0.5f) * 0.04f;
                    float jg = ((areaHash >> 8  & 0xFF) / 255f - 0.5f) * 0.04f;
                    float jb = ((areaHash >> 16 & 0xFF) / 255f - 0.5f) * 0.04f;

                    // Tree density darkens biome color — heavily-treed areas
                    // read as denser forest green, sparse as lighter pasture.
                    // Dialed back from 28% to 12% so polygon boundaries
                    // between forest and grass biomes don't read as hard
                    // edges.
                    float treeDarken = 1f;
                    if (fTreeDensity != null)
                    {
                        try
                        {
                            float td = Mathf.Clamp01((float)fTreeDensity.GetValue(area));
                            treeDarken = Mathf.Lerp(1.0f, 0.88f, td);
                        }
                        catch { }
                    }

                    Color32 col = new Color32(
                        (byte)Mathf.Clamp((ec.r + jr) * treeDarken * 255f, 0f, 255f),
                        (byte)Mathf.Clamp((ec.g + jg) * treeDarken * 255f, 0f, 255f),
                        (byte)Mathf.Clamp((ec.b + jb) * treeDarken * 255f, 0f, 255f),
                        255);

                    var pts = fPoints.GetValue(area) as Vector3[];
                    if (pts == null || pts.Length < 3) continue;

                    // Bounding box in cell space (worldX/CELL_SIZE = hx etc.)
                    float minX = float.MaxValue, maxX = float.MinValue;
                    float minZ = float.MaxValue, maxZ = float.MinValue;
                    for (int i = 0; i < pts.Length; i++)
                    {
                        if (pts[i].x < minX) minX = pts[i].x;
                        if (pts[i].x > maxX) maxX = pts[i].x;
                        if (pts[i].z < minZ) minZ = pts[i].z;
                        if (pts[i].z > maxZ) maxZ = pts[i].z;
                    }
                    int cxLo = Mathf.Max(0, Mathf.FloorToInt(minX / CELL_SIZE));
                    int cxHi = Mathf.Min(hnW - 1, Mathf.CeilToInt(maxX / CELL_SIZE));
                    int czLo = Mathf.Max(0, Mathf.FloorToInt(minZ / CELL_SIZE));
                    int czHi = Mathf.Min(hnH - 1, Mathf.CeilToInt(maxZ / CELL_SIZE));
                    if (cxLo > cxHi || czLo > czHi) continue;

                    areasUsed++;
                    for (int hz = czLo; hz <= czHi; hz++)
                    {
                        for (int hx = cxLo; hx <= cxHi; hx++)
                        {
                            int idx = hz * hnW + hx;
                            if (map[idx].a > 0) continue;  // first writer wins
                            containsArgs[0] = hx * CELL_SIZE;
                            containsArgs[1] = hz * CELL_SIZE;
                            bool inside;
                            try { inside = (bool)miContains.Invoke(area, containsArgs); }
                            catch { continue; }
                            if (inside) { map[idx] = col; paintedCells++; }
                        }
                    }
                }
                Log($"BiomeMap: {areasUsed}/{areas.Count} areas, {paintedCells}/{hnW * hnH} cells painted.");

                // ── Smoothing pass: box blur the biome color map so polygon
                // boundaries between adjacent biomes read as gradients
                // instead of hard edges. Two passes of a 3-cell-radius box
                // blur (≈5 px wide) approximates a Gaussian and matches the
                // soft style Pangu's preview produces. We stay in ARGB byte
                // space — accumulate as int sums to avoid Color32→float
                // conversion costs.
                if (paintedCells > 0)
                {
                    map = BoxBlurColorMap(map, hnW, hnH, radius: 2);
                    map = BoxBlurColorMap(map, hnW, hnH, radius: 2);
                }
                return paintedCells > 0 ? map : null;
            }
            catch (Exception ex)
            {
                Log($"BuildBiomeColorMap failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Two-pass separable box blur of an hnW×hnH Color32 map.
        /// Used to soften biome polygon boundaries so the rendered map reads
        /// as gradients, not discrete patches. Skips cells where alpha=0
        /// (no biome assigned) so unpainted regions stay null and don't
        /// drag biome colors into them.</summary>
        private static Color32[] BoxBlurColorMap(Color32[] src, int w, int h, int radius)
        {
            var tmp = new Color32[w * h];
            // Horizontal pass
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    int rSum = 0, gSum = 0, bSum = 0, n = 0;
                    int xLo = Math.Max(0, x - radius);
                    int xHi = Math.Min(w - 1, x + radius);
                    for (int xx = xLo; xx <= xHi; xx++)
                    {
                        var c = src[rowBase + xx];
                        if (c.a == 0) continue;
                        rSum += c.r; gSum += c.g; bSum += c.b; n++;
                    }
                    tmp[rowBase + x] = (n > 0)
                        ? new Color32((byte)(rSum / n), (byte)(gSum / n), (byte)(bSum / n), 255)
                        : new Color32(0, 0, 0, 0);
                }
            }
            // Vertical pass
            var dst = new Color32[w * h];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int rSum = 0, gSum = 0, bSum = 0, n = 0;
                    int yLo = Math.Max(0, y - radius);
                    int yHi = Math.Min(h - 1, y + radius);
                    for (int yy = yLo; yy <= yHi; yy++)
                    {
                        var c = tmp[yy * w + x];
                        if (c.a == 0) continue;
                        rSum += c.r; gSum += c.g; bSum += c.b; n++;
                    }
                    dst[y * w + x] = (n > 0)
                        ? new Color32((byte)(rSum / n), (byte)(gSum / n), (byte)(bSum / n), 255)
                        : new Color32(0, 0, 0, 0);
                }
            }
            return dst;
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

        /// <summary>Per-preset RGB multiplier applied to land pixels at the
        /// end of the heightmap loop. Each preset gets a subtle color cast
        /// so the rendered map reads its biome intent at a glance even when
        /// underlying TerrainBiome assignments are similar (which they often
        /// are between presets that share assets — IdyllicValley vs
        /// AridHighlands both pull from forest/grass biomes).
        ///
        /// Multipliers stay in ≈[0.85, 1.10] so the cast is visible but the
        /// underlying biome/elevation colors still dominate.</summary>
        private static Vector3 GetPresetTint(RiverPresetMode mode)
        {
            switch (mode)
            {
                case RiverPresetMode.IdyllicValley:
                    // Lush green — boost green slightly, slight cool cast.
                    return new Vector3(0.95f, 1.05f, 0.95f);
                case RiverPresetMode.LowlandLakes:
                    // Wetlands — cooler, slight blue lift.
                    return new Vector3(0.92f, 1.00f, 1.08f);
                case RiverPresetMode.AridHighlands:
                    // Warm, dry — boost red/yellow, suppress green and blue.
                    return new Vector3(1.10f, 0.95f, 0.82f);
                case RiverPresetMode.Plains:
                    // Pale yellow-tan — boost red and green, suppress blue.
                    return new Vector3(1.06f, 1.04f, 0.88f);
                case RiverPresetMode.AlpineValleys:
                    // Cooler, grayer — slight blue lift, suppress warm channels.
                    return new Vector3(0.92f, 0.96f, 1.06f);
                default:
                    return Vector3.one;
            }
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

        /// <summary>Write an opaque water-tone pixel. We deliberately do NOT
        /// blend with the underlying hillshaded terrain — RiverCarver carves
        /// the Unity Terrain but doesn't modify _generationData.heightNoise,
        /// so the heightnoise still shows the *un-carved* shape. Blending
        /// would leak that pre-river ridge/valley pattern through and make
        /// rivers read as raised plateaus.
        ///
        /// Two tones:
        ///   River — bright cyan-tinted blue (130, 200, 255), reads as
        ///           fresh flowing water.
        ///   Lake  — deeper navy (50, 110, 195), saturated still-water body.
        ///
        /// The water depth-and-texture pass that runs after this then darkens
        /// pixels by distance-from-shore and adds noise micro-texture.</summary>
        private static void PaintWaterPixel(Color32[] pixels, int idx, bool isRiver)
        {
            pixels[idx] = isRiver
                ? new Color32(130, 200, 255, 255)
                : new Color32(50,  110, 195, 255);
        }

        // Cap on saved preview PNGs in UserData/RiversRestored/Previews/.
        // Auto-prune oldest beyond this count after each save. PNGs are
        // ~150-300KB each, so 25 keeps the directory under ~10MB even
        // with rapid auto-regen iteration.
        private const int MAX_STORED_PREVIEWS = 25;

        /// <summary>Delete all but the N most-recent .png files in the
        /// given directory. Sorts by LastWriteTimeUtc descending and
        /// deletes everything past index N. Silent on errors — pruning
        /// failure shouldn't break the gen flow.</summary>
        private static void PrunePreviewsDirectory(string dir, int keepCount)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                var files = Directory.GetFiles(dir, "*.png");
                if (files.Length <= keepCount) return;

                Array.Sort(files, (a, b) =>
                    File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                int deleted = 0;
                for (int i = keepCount; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); deleted++; }
                    catch { /* best effort — skip locked/permission failures */ }
                }
                if (deleted > 0)
                    Log($"Pruned {deleted} old preview PNG(s); kept newest {keepCount}.");
            }
            catch (Exception ex)
            {
                Log($"PrunePreviewsDirectory failed: {ex.Message}");
            }
        }

        /// <summary>Read the current map size as a single letter
        /// (S/M/L) from SettingsManager.mapSizeValue. Same property/
        /// backing-field fallback PreviewGenWorker uses. Returns "?" on
        /// any failure — filename gets the placeholder, no exception.</summary>
        private static string ReadMapSizeLetter()
        {
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType == null) return "?";
                object? v = null;
                try
                {
                    var prop = smType.GetProperty("mapSizeValue",
                        BindingFlags.Public | BindingFlags.Static);
                    v = prop?.GetValue(null);
                }
                catch { }
                if (v == null)
                {
                    var f = smType.GetField("_mapSizeValue",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    v = f?.GetValue(null);
                }
                if (v == null) return "?";
                int enumVal = v is Enum e ? Convert.ToInt32(e) : 1;
                // FF enum: Large=0, Medium=1, Small=2.
                return enumVal switch
                {
                    0 => "L",
                    1 => "M",
                    2 => "S",
                    _ => "?",
                };
            }
            catch { return "?"; }
        }

        /// <summary>Read the four difficulty values from SettingsManager
        /// and concatenate their UI letters (P/T/V/X) in the order
        /// Resources / Wildlife / Maladies / Raiders. Wildlife and
        /// Raiders are stored one tier higher than the UI label
        /// suggests — same offset PreviewGenWorker.ReadDifficulty applies.</summary>
        private static string ReadDifficultyLetters()
        {
            try
            {
                string r = ReadOneDifficulty("startingResourcesDifficultyValue", 0);
                string w = ReadOneDifficulty("animalDifficultyValue", -1);
                string m = ReadOneDifficulty("diseaseDifficultyValue", 0);
                string a = ReadOneDifficulty("raiderDifficultyValue", -1);
                return $"{r}{w}{m}{a}";
            }
            catch { return "????"; }
        }

        private static string ReadOneDifficulty(string fieldName, int offset)
        {
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType == null) return "?";
                object? val = null;
                try
                {
                    var prop = smType.GetProperty(fieldName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    val = prop?.GetValue(null);
                }
                catch { }
                if (val == null)
                {
                    var fld = smType.GetField(fieldName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                              ?? smType.GetField("_" + fieldName,
                                  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    val = fld?.GetValue(null);
                }
                if (val == null) return "?";
                if (offset != 0 && val is Enum e)
                {
                    int raw = Convert.ToInt32(e);
                    int adjusted = Math.Max(0, Math.Min(3, raw + offset));
                    val = Enum.ToObject(val.GetType(), adjusted);
                }
                return (val.ToString() ?? "?") switch
                {
                    "Easy" => "P",
                    "Normal" => "T",
                    "Hard" => "V",
                    "VeryHard" => "X",
                    _ => "?",
                };
            }
            catch { return "?"; }
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
