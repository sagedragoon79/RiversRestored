namespace RiversRestored.Patches
{
    /// <summary>Log helpers for the Coastal Maps feature: same MelonLogger as
    /// the rest of the mod, with a <c>[RR][Coast]</c> prefix.</summary>
    internal static class CoastLog
    {
        public static void Msg(string message) => RiversRestoredMod.Log.Msg("[RR][Coast] " + message);
        public static void Warning(string message) => RiversRestoredMod.Log.Warning("[RR][Coast] " + message);
        public static void Error(string message) => RiversRestoredMod.Log.Error("[RR][Coast] " + message);
    }
}
