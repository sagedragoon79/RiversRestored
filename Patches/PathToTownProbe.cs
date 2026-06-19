using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Debug probe (Ctrl+Shift+F9) — reports whether the cell under the cursor
    /// has a flood-fill path to town, for both gates that RR rivers can break:
    ///
    ///   • BridgesOnly  → DEER / wildlife spawn eligibility
    ///       (AnimalSpawnArea.IsValidForSpawnOrWanderPoint gates on this)
    ///   • WallsBlock   → HUNTER TRAP placement eligibility
    ///       (AnimalManager.CalculateTrappingValues gates trap points on this)
    ///
    /// A region cut off from the town by an RR river fails these even though
    /// villagers can still walk across a bridge (movement is Unity NavMesh, a
    /// separate system). This probe is the authoritative connectivity check —
    /// do NOT diagnose by watching foot traffic.
    ///
    /// Gated behind VerboseDiagnostics. Reflection-only (no hard FF refs).
    /// Findings doc: Knowledge/FF-Modding-Knowledge/game-systems/river-system.md
    /// (section "Wildlife & trapping spawn gating").
    /// </summary>
    internal static class PathToTownProbe
    {
        // Cached reflection handles (resolved once).
        private static bool _resolved;
        private static Type? _gmType;
        private static FieldInfo? _aiPathfinderField;
        private static MethodInfo? _doesPathExistMI;     // (Vector3, FloodFillType, bool)
        private static Type? _floodFillEnumType;
        private static object? _bridgesOnly, _wallsBlock;
        private static Type? _inputManagerType;
        private static MethodInfo? _cursorPosMI;          // InputManager.GetTerrainPositionUnderCursor()
        // Best-effort area-ID extras.
        private static FieldInfo? _gridGraphField;
        private static MethodInfo? _nodeFromWorldMI;      // AIGridGraph.NodeFromWorldPosition(Vector3, bool)
        private static MethodInfo? _getAreaForNodeMI;     // AIGridGraph.GetAreaForNode(node, FloodFillType)

        /// <summary>Call from OnUpdate. Fires only on the Ctrl+Shift+F9 chord.</summary>
        public static void CheckHotkey()
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!(ctrl && shift && Input.GetKeyDown(KeyCode.F9))) return;
            ProbeAtCursor();
        }

        private static void ProbeAtCursor()
        {
            try
            {
                if (!Resolve()) return;

                // 1. GameManager instance + aiPathfinder
                var gm = UnityEngine.Object.FindObjectOfType(_gmType!);
                if (gm == null) { Log("GameManager instance not found."); return; }
                var aiPathfinder = _aiPathfinderField!.GetValue(gm);
                if (aiPathfinder == null) { Log("aiPathfinder is null."); return; }

                // 2. Cursor world position via InputManager.GetTerrainPositionUnderCursor()
                Vector3? cursor = TryGetCursorWorldPos();
                if (cursor == null)
                {
                    Log("No terrain under cursor (hover the map and retry).");
                    return;
                }
                Vector3 pos = cursor.Value;

                // 3. The two gates
                bool deerPath = InvokeDoesPathExist(aiPathfinder, pos, _bridgesOnly!);
                bool trapPath = InvokeDoesPathExist(aiPathfinder, pos, _wallsBlock!);

                Log("════════ Path-to-Town Probe ════════");
                Log($"  cursor = ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                Log($"  DEER  spawn eligible (BridgesOnly): {(deerPath ? "YES — connected to town" : "NO — cut off (no deer here)")}");
                Log($"  TRAP  placement     (WallsBlock):   {(trapPath ? "YES — connected to town" : "NO — cut off (no traps here)")}");

                // 4. Best-effort area IDs (cursor vs town) for extra detail
                TryLogAreaIds(aiPathfinder, gm, pos);

                Log("════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Exception inner = (ex is TargetInvocationException tie && tie.InnerException != null)
                    ? tie.InnerException : ex;
                Log($"probe exception: {inner.GetType().Name}: {inner.Message}");
            }
        }

        private static bool InvokeDoesPathExist(object aiPathfinder, Vector3 pos, object floodFillVal)
        {
            // (Vector3 endPos, AIGridGraph.FloodFillType pathCheckAreaType, bool treatOutsideGridAsError)
            var r = _doesPathExistMI!.Invoke(aiPathfinder, new object[] { pos, floodFillVal, false });
            return r is bool b && b;
        }

        private static Vector3? TryGetCursorWorldPos()
        {
            try
            {
                if (_inputManagerType == null || _cursorPosMI == null) return null;
                var im = UnityEngine.Object.FindObjectOfType(_inputManagerType);
                if (im == null) return null;
                var result = _cursorPosMI.Invoke(im, null); // Vector3? (boxed)
                if (result == null) return null;            // null nullable boxes as null
                return (Vector3)result;
            }
            catch { return null; }
        }

        private static void TryLogAreaIds(object aiPathfinder, object gm, Vector3 pos)
        {
            try
            {
                if (_gridGraphField == null || _nodeFromWorldMI == null || _getAreaForNodeMI == null) return;
                var gridGraph = _gridGraphField.GetValue(aiPathfinder);
                if (gridGraph == null) return;

                var node = _nodeFromWorldMI.Invoke(gridGraph, new object[] { pos, true });
                if (node == null) return;
                int cursorBridges = (int)_getAreaForNodeMI.Invoke(gridGraph, new object[] { node, _bridgesOnly! });
                int cursorWalls = (int)_getAreaForNodeMI.Invoke(gridGraph, new object[] { node, _wallsBlock! });

                // Town reference position (village start as a robust fallback)
                Vector3? townPos = TryGetTownPos(gm);
                string townStr = "n/a";
                if (townPos != null)
                {
                    var tnode = _nodeFromWorldMI.Invoke(gridGraph, new object[] { townPos.Value, true });
                    if (tnode != null)
                    {
                        int townBridges = (int)_getAreaForNodeMI.Invoke(gridGraph, new object[] { tnode, _bridgesOnly! });
                        int townWalls = (int)_getAreaForNodeMI.Invoke(gridGraph, new object[] { tnode, _wallsBlock! });
                        townStr = $"BridgesOnly={townBridges} WallsBlock={townWalls}";
                    }
                }
                Log($"  area IDs — cursor: BridgesOnly={cursorBridges} WallsBlock={cursorWalls}  |  town: {townStr}");
                Log("            (cursor == town area ⇒ connected; -1 ⇒ unpathable cell, e.g. in water)");
            }
            catch { /* extras only — never fail the probe over these */ }
        }

        private static Vector3? TryGetTownPos(object gm)
        {
            try
            {
                // GameManager.villageStartingLocation (Vector3?) — robust town anchor
                var vslProp = _gmType!.GetProperty("villageStartingLocation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var vsl = vslProp?.GetValue(gm);
                if (vsl != null) return (Vector3)vsl;
            }
            catch { }
            return null;
        }

        private static bool Resolve()
        {
            if (_resolved) return _doesPathExistMI != null;
            _resolved = true;
            try
            {
                _gmType = AccessTools.TypeByName("GameManager");
                _aiPathfinderField = _gmType?.GetField("aiPathfinder",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                Type? aiPathfinderType = AccessTools.TypeByName("AIPathfinder")
                    ?? _aiPathfinderField?.FieldType;
                _floodFillEnumType = AccessTools.TypeByName("AIPathfinding.AIGridGraph+FloodFillType");

                if (aiPathfinderType == null || _floodFillEnumType == null)
                {
                    Log("Resolve: AIPathfinder / FloodFillType type not found.");
                    return false;
                }

                _bridgesOnly = Enum.Parse(_floodFillEnumType, "BridgesOnly");
                _wallsBlock = Enum.Parse(_floodFillEnumType, "WallsBlock");

                _doesPathExistMI = aiPathfinderType.GetMethod("DoesGeneralPathExistToTown",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(Vector3), _floodFillEnumType, typeof(bool) }, null);
                if (_doesPathExistMI == null)
                {
                    Log("Resolve: DoesGeneralPathExistToTown(Vector3, FloodFillType, bool) not found.");
                    return false;
                }

                // Cursor helper (optional)
                _inputManagerType = AccessTools.TypeByName("InputManager");
                _cursorPosMI = _inputManagerType?.GetMethod("GetTerrainPositionUnderCursor",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);

                // Area-ID extras (optional)
                _gridGraphField = aiPathfinderType.GetField("gridGraph",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Type? gridGraphType = _gridGraphField?.FieldType
                    ?? AccessTools.TypeByName("AIPathfinding.AIGridGraph");
                if (gridGraphType != null)
                {
                    _nodeFromWorldMI = gridGraphType.GetMethod("NodeFromWorldPosition",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(Vector3), typeof(bool) }, null);
                    Type? nodeType = AccessTools.TypeByName("AIPathfinding.AIGridNode")
                        ?? AccessTools.TypeByName("AIGridNode");
                    if (nodeType != null)
                        _getAreaForNodeMI = gridGraphType.GetMethod("GetAreaForNode",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                            null, new[] { nodeType, _floodFillEnumType }, null);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Resolve exception: {ex.Message}");
                return false;
            }
        }

        private static void Log(string msg) => RiversRestoredMod.Log.Msg($"[RR][Probe] {msg}");
    }
}
