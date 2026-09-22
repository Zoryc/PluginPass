using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PluginPassEngine
{
    /// <summary>
    /// PluginPass Engine - the out-of-sandbox half of PluginPass.
    ///
    /// The in-game session plugin can only act AFTER a scene has loaded: VaM's plugin
    /// sandbox forbids System.Reflection, so no Harmony, so no way to get in front of
    /// the load. Everything a blocked plugin costs before it is removed - the compile,
    /// the Init(), the storables it registered - is spent regardless.
    ///
    /// Out here none of that applies. Three patches do the whole job:
    ///
    ///   1. MVRPluginManager.LateRestoreFromJSON - rewrite jc["plugins"] before VaM
    ///      reads it, so the original is never compiled and the replacement is what
    ///      the scene asked for all along.
    ///   2. Atom.RestoreFromLast - skip it for slots we rewrote, so the replacement
    ///      keeps its own configuration instead of being hard-defaulted by VaM's
    ///      no-match branch.
    ///   3. MVRPluginManager.GivePluginNewUID - follow a slot mark when a merge
    ///      restore renumbers the slot out from under it.
    ///
    /// The session plugin stays installed and stays useful: it is the rule editor, and
    /// it is the fallback when this assembly is not loaded. It finds us through a
    /// GameObject (see Beacon) because that is the only channel the sandbox allows.
    /// </summary>
    [BepInPlugin(Guid, "PluginPass Engine", Version)]
    public class PluginPassEnginePlugin : BaseUnityPlugin
    {
        internal const string Guid = "vexmech.pluginpass.engine";
        internal const string Version = "1.0.0";

        // ---- config ----
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _keepMySettings;
        private ConfigEntry<bool> _scenePlugins;
        private ConfigEntry<bool> _keepOriginalIfMissing;
        private ConfigEntry<string> _rulesPath;
        private ConfigEntry<bool> _verbose;

        // ---- state ----
        private Harmony _harmony;
        private int _seenSwapped = -1;
        private int _seenBlocked = -1;
        private int _seenRules = -1;

        void Awake()
        {
            Log.Init(Logger);
            BindConfig();
            Log.Verbose = _verbose.Value;

            if (!_enabled.Value)
            {
                // No beacon: the session plugin must see "engine absent" and fall back.
                Log.Info("PluginPass Engine " + Version
                         + " is disabled in config - the in-game session plugin will handle rules instead.");
                return;
            }

            Log.Rule("PluginPass Engine " + Version + " starting");
            Log.Info("  runtime : " + Environment.Version + " | Unity " + Application.unityVersion
                     + " | " + (IntPtr.Size * 8) + "-bit");

            EngineState.KeepMySettings = _keepMySettings.Value;
            EngineState.ScenePlugins = _scenePlugins.Value;
            EngineState.KeepOriginalIfMissing = _keepOriginalIfMissing.Value;
            EngineState.Init(_rulesPath.Value);

            _harmony = new Harmony(Guid);

            ApplyUrlPatch();
            ApplyRestorePatch();
            ApplySlotRenamePatch();

            if (!PluginUrlPatch.Enabled)
            {
                // Patch 1 is the engine. Without it there is nothing to advertise, and
                // planting the beacon anyway would disable the fallback for no reason.
                Log.Error("the url patch did not apply - NOT advertising the engine, "
                          + "so the in-game session plugin keeps working as before.");
                return;
            }

            Beacon.Create(Version);
            Log.Rule("PluginPass Engine ready");
        }

        // ------------------------------------------------------------------
        private void BindConfig()
        {
            _enabled = Config.Bind("0-General", "Enabled", true,
                "Master switch. False means nothing is patched and no handshake object is\n" +
                "created, so the in-game PluginPass session plugin falls back to its own\n" +
                "after-the-load sweep. Use this to A/B the two engines.");

            _keepMySettings = Config.Bind("0-General", "KeepMySettings", true,
                "Skip Atom.RestoreFromLast for a slot whose url this engine rewrote.\n" +
                "\n" +
                "ON  - your replacement comes up on its own configuration (for Embody-family\n" +
                "      builds, the profile its Init() loads). This is almost always what you\n" +
                "      want when the replacement is your own fork.\n" +
                "OFF - VaM restores normally. If your fork's class name differs from the\n" +
                "      original's, that means its no-match branch runs, which wipes every\n" +
                "      parameter to its hard default. Only turn this off if your fork uses\n" +
                "      the original's exact namespace and class name.");

            _scenePlugins = Config.Bind("0-General", "ScenePlugins", true,
                "Also apply rules to the scene-wide plugin list (the CoreControl atom).\n" +
                "Session plugins are never touched: this engine only acts on plugin managers\n" +
                "whose atom is in the loaded scene.");

            _keepOriginalIfMissing = Config.Bind("0-General", "KeepOriginalIfMissing", true,
                "When a replacement cannot be found on disk, leave the original plugin alone\n" +
                "instead of dropping it. A typo or an uninstalled fork then costs you nothing.");

            _rulesPath = Config.Bind("0-General", "RulesPath", "",
                "Absolute path to rules.json. Empty means the file the in-game plugin uses:\n" +
                "<VaM>/" + EngineState.RulesRelativePath);

            _verbose = Config.Bind("0-General", "Verbose", false,
                "Log extra detail, including every settings restore this engine suppressed.");
        }

        // ------------------------------------------------------------------
        private void ApplyUrlPatch()
        {
            try
            {
                PluginUrlPatch.Apply(_harmony);
                Log.Info("[1/3] url swap : MVRPluginManager.LateRestoreFromJSON patched "
                         + "(originals are never compiled)");
            }
            catch (Exception ex) { Log.Error("[1/3] url swap FAILED (not applied): " + ex); }
        }

        private void ApplyRestorePatch()
        {
            if (!_keepMySettings.Value)
            {
                Log.Info("[2/3] settings : KeepMySettings is off - VaM restores swapped slots normally");
                return;
            }
            try
            {
                RestoreSuppressPatch.Apply(_harmony);
                Log.Info("[2/3] settings : Atom.RestoreFromLast patched "
                         + "(swapped slots keep their own configuration)");
            }
            catch (Exception ex) { Log.Error("[2/3] settings FAILED (not applied): " + ex); }
        }

        private void ApplySlotRenamePatch()
        {
            if (!_keepMySettings.Value || !RestoreSuppressPatch.Enabled)
            {
                // Nothing reads the marks, so nothing needs to follow them.
                return;
            }
            try
            {
                SlotRenamePatch.Apply(_harmony);
                Log.Info("[3/3] slots    : GivePluginNewUID patched "
                         + "(swap marks follow a merge-restore renumbering)");
            }
            catch (Exception ex)
            {
                // Degraded, not broken: suppression still works everywhere except a
                // deferred approval during a merge restore.
                Log.Warn("[3/3] slots FAILED (not applied) - swap marks will not survive a "
                         + "merge restore that renumbers slots: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        void Update()
        {
            if (_harmony == null) return;

            // Three int comparisons. The beacon is only rewritten when a scene load
            // actually changed something, so there is no per-frame allocation.
            int swapped = PluginUrlPatch.Swapped;
            int blocked = PluginUrlPatch.Blocked;
            int rules = EngineState.RuleCount;
            if (swapped == _seenSwapped && blocked == _seenBlocked && rules == _seenRules) return;

            _seenSwapped = swapped;
            _seenBlocked = blocked;
            _seenRules = rules;
            Beacon.Publish(Version);

            Log.Debug(PluginUrlPatch.Describe() + " | " + RestoreSuppressPatch.Describe()
                      + " | " + SlotRenamePatch.Describe());
        }

        void OnDestroy()
        {
            try
            {
                PluginUrlPatch.Enabled = false;
                RestoreSuppressPatch.Enabled = false;
                SlotRenamePatch.Enabled = false;
                PluginUrlPatch.ForgetAll();
                Beacon.Destroy();
                if (_harmony != null) _harmony.UnpatchSelf();
                Log.Info("PluginPass Engine unpatched.");
            }
            catch { }
        }
    }
}
