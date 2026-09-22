using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace PluginPassEngine
{
    /// <summary>
    /// PATCH 1 - the whole point of the engine.
    ///
    /// MVRPluginManager.LateRestoreFromJSON reads jc["plugins"] and, for every key in
    /// it, calls CreatePluginWithId and then restores that slot's url - which compiles
    /// and Init()s the script. Rewriting that JSON object in a prefix means the
    /// original plugin is never compiled, never constructed and never runs a frame.
    ///
    /// Verified against the shipped IL (Assembly-CSharp, VaM 1.22):
    ///   IL_0082  ldarg.1 / ldstr "plugins" / get_Item / get_AsObject   -> the object we edit
    ///   IL_00a3  get_Keys -> foreach -> CreatePluginWithId(key)
    ///   IL_00d7  pluginURLJSON.RestoreFromJSON(thatSameObject, ...)    -> reads our value
    ///
    /// This is what the in-game script cannot do: it only ever runs after the load,
    /// so the original has already cost a compile and an Init by the time it is
    /// removed. See README "Why it can't intercept before the load".
    /// </summary>
    internal static class PluginUrlPatch
    {
        internal static bool Enabled;
        internal static int Swapped;
        internal static int Blocked;
        internal static int Managers;

        /// <summary>
        /// The slots this engine rewrote, per atom, read by RestoreSuppressPatch.
        /// Entries have to survive the call, because the deferred package-approval path
        /// creates the script long after this returns.
        ///
        /// Keyed on the Atom REFERENCE, not on its uid. The uid is not a stable
        /// identity: SuperController lets the user rename an atom at any moment
        /// (including during the approval window, which is exactly when these marks
        /// matter), and two different scenes routinely contain different atoms both
        /// called "Person". A string key meant a mark could miss the atom it was made
        /// for, or hit one it was not. A reference cannot do either.
        /// </summary>
        private static readonly Dictionary<Atom, HashSet<string>> _swappedSlots =
            new Dictionary<Atom, HashSet<string>>();

        private static readonly List<string> _keyBuf = new List<string>();
        private static readonly List<string> _dropBuf = new List<string>();
        private static readonly List<Atom> _deadBuf = new List<Atom>();

        internal static bool WasSwapped(Atom atom, string slot)
        {
            if (atom == null || string.IsNullOrEmpty(slot)) return false;
            HashSet<string> slots;
            if (!_swappedSlots.TryGetValue(atom, out slots)) return false;
            return slots.Contains(slot);
        }

        private static void Mark(Atom atom, string slot)
        {
            HashSet<string> slots;
            if (!_swappedSlots.TryGetValue(atom, out slots))
            {
                slots = new HashSet<string>(StringComparer.Ordinal);
                _swappedSlots[atom] = slots;
            }
            slots.Add(slot);
        }

        /// <summary>
        /// Follows a mark when VaM renumbers a slot.
        ///
        /// A merge restore calls GivePluginNewUID twice per plugin (a temp pass, then a
        /// final one) AFTER our loop has already recorded "plugin#0". If the replacement
        /// package still needs approval, the script is not created until
        /// UserConfirmDenyComplete resumes the load - by which time the slot is called
        /// something else, the mark no longer matches, suppression does not fire, and
        /// the fork takes VaM's destructive no-match branch. Which is the exact bug the
        /// suppression patch exists to prevent.
        /// </summary>
        internal static void Rename(Atom atom, string oldSlot, string newSlot)
        {
            if (atom == null || string.IsNullOrEmpty(oldSlot) ||
                string.IsNullOrEmpty(newSlot) || oldSlot == newSlot) return;

            HashSet<string> slots;
            if (!_swappedSlots.TryGetValue(atom, out slots)) return;
            if (!slots.Remove(oldSlot)) return;
            slots.Add(newSlot);
            Log.Debug("slot mark followed " + atom.uid + " " + oldSlot + " -> " + newSlot);
        }

        /// <summary>
        /// Drops entries whose Atom has been destroyed. Unity's overloaded == makes a
        /// destroyed object compare equal to null while the dictionary still holds the
        /// managed reference, so without this the table grows for the life of the
        /// process. Called from the same once-per-frame path as the rules reload.
        /// </summary>
        private static void PruneDeadAtoms()
        {
            if (_swappedSlots.Count == 0) return;
            _deadBuf.Clear();
            foreach (KeyValuePair<Atom, HashSet<string>> kv in _swappedSlots)
                if (kv.Key == null) _deadBuf.Add(kv.Key);
            for (int i = 0; i < _deadBuf.Count; i++) _swappedSlots.Remove(_deadBuf[i]);
            _deadBuf.Clear();
        }

        internal static void ResetStats()
        {
            Swapped = 0; Blocked = 0; Managers = 0;
        }

        internal static void ForgetAll()
        {
            _swappedSlots.Clear();
        }

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(MVRPluginManager), "LateRestoreFromJSON",
                                   new Type[] { typeof(JSONClass), typeof(bool), typeof(bool), typeof(bool) }),
                new HarmonyMethod(typeof(PluginUrlPatch), "Prefix"));
            Enabled = true;
        }

        // ------------------------------------------------------------------

        private static void Prefix(MVRPluginManager __instance, JSONClass jc,
                                   bool restorePhysical, bool restoreAppearance)
        {
            if (!Enabled) return;
            try { Rewrite(__instance, jc, restorePhysical, restoreAppearance); }
            catch (Exception ex)
            {
                // A throw here would abort the scene's plugin restore entirely, which is
                // far worse than not swapping. Never let that happen.
                Log.Error("url rewrite failed (scene loaded unchanged): " + ex);
            }
        }

        private static void Rewrite(MVRPluginManager mgr, JSONClass jc,
                                     bool restorePhysical, bool restoreAppearance)
        {
            // The session plugin's "Standard - in-game sweep" setting. Without this the
            // dropdown could not actually stand the engine down: it only restarted the
            // script's sweep while this prefix kept rewriting urls underneath it.
            if (Beacon.StandbyRequested()) return;

            if (mgr == null || jc == null) return;

            // Bail exactly where VaM bails. LateRestoreFromJSON only touches the plugin
            // list when all of these hold (IL_0026..IL_004a); rewriting the JSON and
            // recording a swap mark for a restore VaM is about to skip leaves a mark for
            // a plugin that was never swapped - and a later genuine restore of that slot
            // then gets suppressed and loses its settings.
            if (!restorePhysical || !restoreAppearance) return;
            if (mgr.physicalLocked || mgr.appearanceLocked) return;
            if (mgr.IsCustomPhysicalParamLocked("plugins")) return;
            if (mgr.IsCustomAppearanceParamLocked("plugins")) return;

            Atom atom = mgr.containingAtom;
            if (!IsActionableAtom(atom)) return;

            // Clear this atom's marks as soon as the atom is known, BEFORE any of the
            // remaining bail-outs. Doing it lower down meant a scene whose atom had no
            // plugins - or a session with rules.json deleted - left the previous scene's
            // marks live, so a later legitimate restore of the same uid and slot was
            // silently suppressed and came up on defaults.
            ForgetAtom(atom);
            PruneDeadAtoms();

            // Picks up edits made in the in-game rule builder. Once per frame at most,
            // and only while a plugin list is actually being restored.
            EngineState.MaybeReload();

            RuleSet.Rule[] rules = EngineState.Rules;
            if (rules == null || rules.Length == 0) return;

            bool isScenePlugins = string.Equals(atom.uid, "CoreControl", StringComparison.Ordinal);
            if (isScenePlugins && !EngineState.ScenePlugins) return;

            JSONNode node = jc["plugins"];
            if (node == null) return;                  // SimpleJSON lazy-creator aware
            JSONClass plugins = node.AsObject;
            if (plugins == null || plugins.Count == 0) return;

            Managers++;

            // Materialise the keys: we mutate the object inside the loop and SimpleJSON's
            // key enumerator is the dictionary's own.
            _keyBuf.Clear();
            foreach (string key in plugins.Keys) _keyBuf.Add(key);

            _dropBuf.Clear();

            for (int i = 0; i < _keyBuf.Count; i++)
            {
                string slot = _keyBuf[i];
                JSONNode value = plugins[slot];
                if (value == null) continue;
                string url = value.Value;
                if (string.IsNullOrEmpty(url)) continue;

                string resolved;
                RuleSet.Rule fired;
                int outcome = RuleSet.Evaluate(rules, url, EngineState.KeepOriginalIfMissing,
                                               out resolved, out fired);

                if (outcome == RuleSet.OutcomeBlock)
                {
                    _dropBuf.Add(slot);
                    Blocked++;
                    Log.Info("blocked   " + RuleSet.FriendlyName(url)
                             + "   on " + atom.uid + " " + slot
                             + (fired == null ? "" : "   [" + fired.name + "]"));
                }
                else if (outcome == RuleSet.OutcomeSwap)
                {
                    plugins[slot] = new JSONData(resolved);
                    Mark(atom, slot);
                    Swapped++;
                    Log.Info("swapped   " + RuleSet.FriendlyName(url)
                             + "   ->   " + RuleSet.FriendlyName(resolved)
                             + "   on " + atom.uid + " " + slot
                             + (fired == null ? "" : "   [" + fired.name + "]"));
                }
            }

            for (int i = 0; i < _dropBuf.Count; i++) plugins.Remove(_dropBuf[i]);
            _dropBuf.Clear();
            _keyBuf.Clear();
        }

        /// <summary>
        /// Only atoms that are actually in the scene. There is no global handle to the
        /// session plugin manager anywhere in Assembly-CSharp (the only field of type
        /// MVRPluginManager is MVRScript.manager), so identity is established the way
        /// the script does it - by asking whether this manager's atom is one the scene
        /// controller knows about. A session plugin manager fails that test, which is
        /// what keeps the engine off the user's own session plugin list.
        /// </summary>
        private static bool IsActionableAtom(Atom atom)
        {
            if (atom == null) return false;
            SuperController sc = SuperController.singleton;
            if (sc == null) return false;

            List<Atom> atoms = sc.GetAtoms();
            if (atoms == null) return false;
            for (int i = 0; i < atoms.Count; i++)
                if (ReferenceEquals(atoms[i], atom)) return true;
            return false;
        }

        private static void ForgetAtom(Atom atom)
        {
            if (atom == null) return;
            _swappedSlots.Remove(atom);
        }

        internal static string Describe()
        {
            return "urls     : " + Swapped + " swapped, " + Blocked + " blocked, "
                 + Managers + " plugin list(s) read";
        }
    }
}
