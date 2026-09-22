using System;
using UnityEngine;

namespace PluginPassEngine
{
    /// <summary>
    /// The handshake with the in-game session plugin.
    ///
    /// PluginPass.cs runs inside VaM's plugin sandbox, which forbids System.Reflection
    /// outright - so it cannot ask whether this assembly is loaded, cannot reference a
    /// type from it, and cannot call into it. What it CAN do is GameObject.Find, which
    /// is ordinary UnityEngine.
    ///
    /// So the engine plants a GameObject named "PluginPassEngine" and hangs one child
    /// off it whose NAME is the status line. Renaming a GameObject is free, the script
    /// reads it with two public calls, and nothing has to be marshalled across the
    /// sandbox boundary:
    ///
    ///     GameObject b = GameObject.Find("PluginPassEngine");
    ///     string status = b.transform.GetChild(0).name;
    ///     // "v=1.0.0;rules=3;swap=5;block=0;keep=1;scene=1"
    ///
    /// GameObject.Find only sees ACTIVE objects, so both stay active for the whole
    /// session; DontDestroyOnLoad keeps them across scene loads without being found
    /// twice.
    /// </summary>
    internal static class Beacon
    {
        internal const string RootName = "PluginPassEngine";

        /// <summary>
        /// The reply channel. The session plugin parents a child with this name under
        /// the root to say "I am set to Standard - stand down", and destroys it again
        /// when the mode changes. That makes the in-game dropdown mean what it says:
        /// before this existed, picking Standard only restarted the script's own sweep
        /// while the engine kept swapping underneath it, and the UI claimed the engine
        /// was ignored.
        ///
        /// Checking it is a direct-child lookup on an object we already hold, not a
        /// scene search, so it is cheap enough to do on every plugin list restored.
        /// </summary>
        internal const string StandbyName = "standby";

        private static GameObject _root;
        private static Transform _status;
        private static string _last = "";

        internal static void Create(string version)
        {
            try
            {
                if (_root != null) return;

                _root = new GameObject(RootName);
                UnityEngine.Object.DontDestroyOnLoad(_root);

                GameObject child = new GameObject("v=" + version);
                child.transform.SetParent(_root.transform, false);
                _status = child.transform;

                Publish(version);
                Log.Info("handshake object '" + RootName + "' planted for the session plugin");
            }
            catch (Exception ex)
            {
                Log.Error("could not create the handshake object: " + ex.Message);
            }
        }

        /// <summary>
        /// True when the session plugin has asked the engine to stand down.
        /// Direct-child lookup, no scene search.
        /// </summary>
        internal static bool StandbyRequested()
        {
            try
            {
                if (_root == null) return false;
                Transform marker = _root.transform.Find(StandbyName);
                // Active state, not existence: the session plugin toggles the marker
                // rather than creating and destroying it, because Unity defers Destroy
                // to end of frame and Find still returns a doomed object - so a
                // destroy-then-recreate in one frame silently lost the re-plant.
                return marker != null && marker.gameObject.activeSelf;
            }
            catch { return false; }
        }

        internal static void Publish(string version)
        {
            try
            {
                if (_status == null) return;
                // keep= reports what is actually PATCHED, not what the config asked for.
                // If harmony.Patch on Atom.RestoreFromLast threw, the engine still runs
                // (patch 1 is the engine) but swapped slots DO get hard-defaulted - and
                // claiming otherwise would tell the user the one bug this exists to fix
                // is fixed when it is not.
                string line = "v=" + version
                            + ";rules=" + EngineState.RuleCount
                            + ";swap=" + PluginUrlPatch.Swapped
                            + ";block=" + PluginUrlPatch.Blocked
                            + ";keep=" + (RestoreSuppressPatch.Enabled ? "1" : "0")
                            + ";scene=" + (EngineState.ScenePlugins ? "1" : "0")
                            + ";rulepath=" + (EngineState.UsingCustomRulesPath ? "custom" : "default")
                            + ";err=" + (EngineState.LastError.Length > 0 ? "1" : "0");
                if (line == _last) return;
                _last = line;
                _status.gameObject.name = line;
            }
            catch (Exception ex)
            {
                Log.Debug("beacon publish failed: " + ex.Message);
            }
        }

        internal static void Destroy()
        {
            try
            {
                if (_root != null) UnityEngine.Object.Destroy(_root);
            }
            catch { }
            _root = null;
            _status = null;
            _last = "";
        }
    }
}
