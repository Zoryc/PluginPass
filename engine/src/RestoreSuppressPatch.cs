using System;
using HarmonyLib;

namespace PluginPassEngine
{
    /// <summary>
    /// PATCH 2 - keep the user's own settings, and stop VaM hard-defaulting a swap.
    ///
    /// Rewriting the url in patch 1 means the slot now loads a different class, so its
    /// storable id changes from "plugin#0_Embody" to "plugin#0_EmbodyVR". VaM's
    /// Atom.RestoreFromLast walks lastRestoredData["storables"] looking for an entry
    /// whose "id" equals js.storeId, and its no-match branch is NOT a no-op:
    ///
    ///   IL_00d2  ldloc.0 / brtrue.s IL_0120      // a match was found -> skip the wipe
    ///   IL_00d5  newobj JSONClass::.ctor()       // otherwise: an EMPTY object
    ///   IL_00ec  ldc.i4.1                        // setMissingToDefault: true
    ///   IL_00ed  callvirt JSONStorable::RestoreFromJSON(...)
    ///
    /// So a swapped plugin gets every registered parameter reset to its hard default,
    /// logged nowhere - and because RestoreFromLast runs AFTER Init(), it overwrites
    /// whatever the fork loaded for itself (Embody-family builds call
    /// LoadFromDefaults() at the end of Init). That is the "it always comes up on hard
    /// defaults" report, and on this install it fires on every fork rebuild, because
    /// plugin approval in AddonPackagesUserPrefs is granted per package VERSION and a
    /// freshly built .var is unapproved until you allow it once.
    ///
    /// Skipping RestoreFromLast for slots we rewrote fixes both halves at once: no
    /// wipe, and the replacement keeps its own configuration rather than inheriting
    /// the scene author's. That is the stated intent - "i want my own setting of this
    /// plugin, i dont want to pass the settings from the old one to the new one".
    /// </summary>
    internal static class RestoreSuppressPatch
    {
        internal static bool Enabled;
        internal static int Suppressed;

        internal static void ResetStats() { Suppressed = 0; }

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(Atom), "RestoreFromLast", new Type[] { typeof(JSONStorable) }),
                new HarmonyMethod(typeof(RestoreSuppressPatch), "Prefix"));
            Enabled = true;
        }

        // ------------------------------------------------------------------

        /// <summary>Returning false skips the original method.</summary>
        private static bool Prefix(Atom __instance, JSONStorable js)
        {
            if (!Enabled) return true;
            try
            {
                if (!EngineState.KeepMySettings) return true;
                if (__instance == null || js == null) return true;

                string slot = SlotOf(js);
                if (slot == null) return true;

                if (!PluginUrlPatch.WasSwapped(__instance.uid, slot)) return true;

                Suppressed++;
                Log.Debug("kept own settings: skipped RestoreFromLast for "
                          + __instance.uid + " " + slot + " (" + js.name + ")");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("restore-suppress check failed (restoring normally): " + ex);
                return true;
            }
        }

        /// <summary>
        /// "plugin#3_Ns.Class" -> "plugin#3".
        ///
        /// Parsed from name, not storeId: JSONStorable.get_storeId returns overrideId
        /// when one is set, and inside a subscene the script's id carries a prefix that
        /// the plugin manager's slot names do not. `name` is what CreateScriptController
        /// wrote and is always the bare "&lt;slot&gt;_&lt;Type&gt;" form.
        /// </summary>
        private static string SlotOf(JSONStorable js)
        {
            string name = js.name;
            if (string.IsNullOrEmpty(name)) return null;
            int split = name.IndexOf('_');
            if (split <= 0) return null;
            string slot = name.Substring(0, split);
            // Cheap shape check so non-plugin storables never reach the set lookup.
            return slot.StartsWith("plugin#", StringComparison.Ordinal) ? slot : null;
        }

        internal static string Describe()
        {
            return "settings : " + Suppressed + " slot(s) kept their own configuration";
        }
    }
}
