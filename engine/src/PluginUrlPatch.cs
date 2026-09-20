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
        /// "&lt;atom uid&gt;|&lt;slot&gt;" for every slot this engine rewrote, read by
        /// RestoreSuppressPatch. Entries survive the call because the deferred
        /// package-confirmation path creates the script long after this returns.
        /// </summary>
        private static readonly HashSet<string> _swappedSlots =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly List<string> _keyBuf = new List<string>();
        private static readonly List<string> _dropBuf = new List<string>();

        internal static string SlotKey(string atomUid, string slot)
        {
            return atomUid + "|" + slot;
        }

        internal static bool WasSwapped(string atomUid, string slot)
        {
            return _swappedSlots.Contains(SlotKey(atomUid, slot));
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

        private static void Prefix(MVRPluginManager __instance, JSONClass jc)
        {
            if (!Enabled) return;
            try { Rewrite(__instance, jc); }
            catch (Exception ex)
            {
                // A throw here would abort the scene's plugin restore entirely, which is
                // far worse than not swapping. Never let that happen.
                Log.Error("url rewrite failed (scene loaded unchanged): " + ex);
            }
        }

        private static void Rewrite(MVRPluginManager mgr, JSONClass jc)
        {
            // Picks up edits made in the in-game rule builder. Once per frame at most,
            // and only while a plugin list is actually being restored.
            EngineState.MaybeReload();

            RuleSet.Rule[] rules = EngineState.Rules;
            if (rules == null || rules.Length == 0) return;
            if (mgr == null || jc == null) return;

            Atom atom = mgr.containingAtom;
            if (!IsActionableAtom(atom)) return;

            bool isScenePlugins = string.Equals(atom.uid, "CoreControl", StringComparison.Ordinal);
            if (isScenePlugins && !EngineState.ScenePlugins) return;

            JSONNode node = jc["plugins"];
            if (node == null) return;                  // SimpleJSON lazy-creator aware
            JSONClass plugins = node.AsObject;
            if (plugins == null || plugins.Count == 0) return;

            Managers++;
            string atomUid = atom.uid;

            // A re-restore of the same atom must not inherit the previous pass's marks.
            ForgetAtom(atomUid);

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
                             + "   on " + atomUid + " " + slot
                             + (fired == null ? "" : "   [" + fired.name + "]"));
                }
                else if (outcome == RuleSet.OutcomeSwap)
                {
                    plugins[slot] = new JSONData(resolved);
                    _swappedSlots.Add(SlotKey(atomUid, slot));
                    Swapped++;
                    Log.Info("swapped   " + RuleSet.FriendlyName(url)
                             + "   ->   " + RuleSet.FriendlyName(resolved)
                             + "   on " + atomUid + " " + slot
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

        private static void ForgetAtom(string atomUid)
        {
            string prefix = atomUid + "|";
            List<string> stale = null;
            foreach (string key in _swappedSlots)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (stale == null) stale = new List<string>();
                    stale.Add(key);
                }
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) _swappedSlots.Remove(stale[i]);
        }

        internal static string Describe()
        {
            return "urls     : " + Swapped + " swapped, " + Blocked + " blocked, "
                 + Managers + " plugin list(s) read";
        }
    }
}
