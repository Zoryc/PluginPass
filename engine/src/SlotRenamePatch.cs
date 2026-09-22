using System;
using HarmonyLib;

namespace PluginPassEngine
{
    /// <summary>
    /// PATCH 3 - keep a swap mark attached to its slot when VaM renumbers it.
    ///
    /// PATCH 1 records "plugin#0" against the atom while rewriting the JSON. A MERGE restore
    /// (a Plugins preset loaded with Merge on, via MeshVR.PresetManager.LateRestore
    /// setting storable.mergeRestore) then renumbers every slot AFTER that loop -
    /// LateRestoreFromJSON IL_0101 gives the incoming plugins temp uids and IL_0110
    /// gives the existing ones final uids, both through GivePluginsNewUIDs, which is a
    /// plain loop over GivePluginNewUID (IL_0019).
    ///
    /// In the synchronous case that does not matter, because the script was already
    /// created and restored inside the loop. It matters when the replacement package
    /// has not been approved yet: SyncPluginUrlInternal defers to MVRPlugin.UserConfirm,
    /// the load finishes, the slot is renamed, and only when the user clicks Allow does
    /// UserConfirmDenyComplete create "plugin#3_VexMech.Fork". PATCH 2 then looks up
    /// "plugin#3" on that atom, finds nothing, lets Atom.RestoreFromLast run, and the fork
    /// takes the destructive no-match branch - the precise failure this engine exists
    /// to prevent, in the precise situation that produces it most often (a freshly
    /// rebuilt fork is always unapproved, because approval is per package VERSION).
    ///
    /// Patching the single choke point keeps the mark correct through both passes:
    /// plugin#0 -> pluginTemp#N -> plugin#K.
    ///
    /// Not fixable inside PATCH 2 instead: at the RestoreFromLast call site the new
    /// MVRScriptController has its `script` field set but has not yet been added to
    /// MVRPlugin.scriptControllers (that happens later, back in SyncPluginUrlInternal),
    /// so there is no way to walk from the storable back to its MVRPlugin at that
    /// moment.
    /// </summary>
    internal static class SlotRenamePatch
    {
        internal static bool Enabled;
        internal static int Followed;

        internal static void ResetStats() { Followed = 0; }

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(MVRPluginManager), "GivePluginNewUID",
                                   new Type[] { typeof(MVRPlugin), typeof(bool) }),
                new HarmonyMethod(typeof(SlotRenamePatch), "Prefix"),
                new HarmonyMethod(typeof(SlotRenamePatch), "Postfix"));
            Enabled = true;
        }

        // ------------------------------------------------------------------

        // The uid before VaM changes it, handed from prefix to postfix. Harmony's
        // __state is per-call, so this is safe even if the loop re-enters.
        private static void Prefix(MVRPluginManager __instance, MVRPlugin mvrp, out string __state)
        {
            __state = null;
            try
            {
                if (!Enabled || __instance == null || mvrp == null) return;
                if (__instance.containingAtom == null) return;
                __state = mvrp.uid;
            }
            catch { __state = null; }
        }

        private static void Postfix(MVRPluginManager __instance, MVRPlugin mvrp, string __state)
        {
            try
            {
                if (!Enabled || __state == null) return;
                if (__instance == null || mvrp == null) return;

                Atom atom = __instance.containingAtom;
                if (atom == null) return;

                string newUid = mvrp.uid;
                if (newUid == null || newUid == __state) return;

                if (PluginUrlPatch.WasSwapped(atom, __state))
                {
                    PluginUrlPatch.Rename(atom, __state, newUid);
                    Followed++;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("slot rename follow failed: " + ex.Message);
            }
        }

        internal static string Describe()
        {
            return "slots    : " + Followed + " mark(s) followed a renumbering";
        }
    }
}
