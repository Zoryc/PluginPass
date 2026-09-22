using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PluginPassEngine
{
    /// <summary>
    /// Everything the patches need to read, in one place, plus the rules file loader.
    ///
    /// The rules file is the SAME file the in-game session plugin reads and writes -
    /// Saves/PluginData/PluginPass/rules.json. That is the whole handshake for
    /// configuration: edit rules in the VaM UI, and the engine picks them up on the
    /// next scene load. There is no second copy to keep in sync.
    /// </summary>
    internal static class EngineState
    {
        internal const string RulesRelativePath = "Saves/PluginData/PluginPass/rules.json";

        internal static RuleSet.Rule[] Rules = new RuleSet.Rule[0];
        internal static bool KeepMySettings = true;
        internal static bool ScenePlugins = true;
        internal static bool KeepOriginalIfMissing = true;

        internal static string RulesPath { get; private set; }
        internal static string LastError = "";
        internal static int RuleCount { get { return Rules == null ? 0 : Rules.Length; } }

        /// <summary>
        /// True when the RulesPath config points somewhere other than the file the
        /// in-game plugin edits. The session plugin's Settings panel otherwise claims
        /// "both read the same rules file" and prints only its own path, which would
        /// be a lie in exactly the case the user needs to know about.
        /// </summary>
        internal static bool UsingCustomRulesPath { get; private set; }

        private static DateTime _stamp = DateTime.MinValue;
        private static long _size = -1;
        private static int _lastCheckFrame = -1;

        /// <summary>VaM root, i.e. the folder holding VaM.exe and VaM_Data.</summary>
        internal static string VamRoot()
        {
            try
            {
                DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                return parent == null ? "." : parent.FullName;
            }
            catch { return "."; }
        }

        internal static void Init(string overridePath)
        {
            string defaultPath = Path.Combine(VamRoot(),
                                              RulesRelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (string.IsNullOrEmpty(overridePath))
            {
                RulesPath = defaultPath;
            }
            else
            {
                // A relative override resolves against the VaM install, not against the
                // process working directory - which for VaM.exe is not reliably the game
                // folder, and would silently point at a file that does not exist.
                RulesPath = Path.IsPathRooted(overridePath)
                    ? overridePath
                    : Path.Combine(VamRoot(), overridePath.Replace('/', Path.DirectorySeparatorChar));
            }

            UsingCustomRulesPath = !string.Equals(
                Path.GetFullPath(RulesPath), Path.GetFullPath(defaultPath),
                StringComparison.OrdinalIgnoreCase);

            if (UsingCustomRulesPath)
                Log.Warn("RulesPath override in use - the in-game rule editor writes "
                         + defaultPath + " and this engine reads " + RulesPath);

            Reload(true);
        }

        /// <summary>
        /// Re-reads rules.json when it has changed on disk. Called from the url patch,
        /// gated to once per frame: a scene load fires the patch once per atom with a
        /// plugin list, and a file stat per atom would be wasteful for no gain. Nothing
        /// polls - when no scene is loading, this is never called at all.
        /// </summary>
        internal static void MaybeReload()
        {
            int frame = Time.frameCount;
            if (frame == _lastCheckFrame) return;
            _lastCheckFrame = frame;

            // Drop the "does this target exist" answers as well. They are keyed on a
            // url, and a fork rebuilt at the SAME version number changes what is on
            // disk without changing any url - so a cache that only cleared when
            // rules.json changed left a freshly built fork looking permanently missing
            // until VaM restarted. FileManagerSecure.FileExists is a dictionary lookup,
            // and this runs at most once per frame while a scene is loading.
            RuleSet.ClearCaches();
            Reload(false);
        }

        internal static void Reload(bool force)
        {
            try
            {
                if (string.IsNullOrEmpty(RulesPath)) return;

                if (!File.Exists(RulesPath))
                {
                    if (Rules.Length > 0 || force)
                    {
                        Rules = new RuleSet.Rule[0];
                        RuleSet.ClearCaches();
                        _stamp = DateTime.MinValue;
                        _size = -1;
                        if (force) Log.Info("no rules file at " + RulesPath + " - nothing will be swapped");
                    }
                    return;
                }

                FileInfo fi = new FileInfo(RulesPath);
                DateTime stamp = fi.LastWriteTimeUtc;
                long size = fi.Length;
                if (!force && stamp == _stamp && size == _size) return;

                string text = File.ReadAllText(RulesPath);

                List<string> problems = new List<string>();
                RuleSet.Rule[] parsed = RuleSet.Parse(text, problems);

                _stamp = stamp;
                _size = size;
                Rules = parsed;
                RuleSet.ClearCaches();

                LastError = problems.Count == 0 ? "" : string.Join(" | ", problems.ToArray());
                for (int i = 0; i < problems.Count; i++) Log.Warn(problems[i] + " - rule skipped");

                Log.Info("rules loaded: " + parsed.Length + " active"
                         + (problems.Count > 0 ? ", " + problems.Count + " skipped" : "")
                         + "   (" + RulesPath + ")");
            }
            catch (Exception ex)
            {
                // Keep whatever was already loaded rather than silently disarming.
                LastError = ex.Message;
                Log.Error("could not read " + RulesPath + ": " + ex.Message);
            }
        }
    }
}
