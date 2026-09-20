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

        internal static void Publish(string version)
        {
            try
            {
                if (_status == null) return;
                string line = "v=" + version
                            + ";rules=" + EngineState.RuleCount
                            + ";swap=" + PluginUrlPatch.Swapped
                            + ";block=" + PluginUrlPatch.Blocked
                            + ";keep=" + (EngineState.KeepMySettings ? "1" : "0")
                            + ";scene=" + (EngineState.ScenePlugins ? "1" : "0");
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
