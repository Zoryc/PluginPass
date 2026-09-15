//
// PluginPass - a Virt-A-Mate session plugin that decides which plugins a scene is
// allowed to load, and redirects plugin references to your own builds without
// editing a single scene file or .var package.
//
// Left column = a compact header, four tabs, and only that tab's controls.
// Right column = one panel showing whatever the current tab is about.
//
// -------------------------------------------------------------------------------
// Why there is no pre-load interception
// -------------------------------------------------------------------------------
// VaM compiles plugin scripts at runtime and then runs the result through
// DynamicCSharp.ScriptDomain.SecurityCheckAssembly, which rejects any assembly that
// references a restricted namespace. System.Reflection is on that list, matched
// exactly against every type reference in the module. No reflection means no
// Harmony, and no Harmony means a plugin script cannot patch
// MVRPluginManager.LateRestoreFromJSON to strip plugins before VaM reads them.
// True interception needs a BepInEx plugin, loaded outside the sandbox.
//
// So this works after the fact: when a scene finishes loading, matching plugins are
// removed or re-pointed. The blocked plugin is compiled once before it is dropped,
// but its ongoing cost - Update, coroutines, physics callbacks - is gone.
//
// Scope: atom plugins, plus scene plugins (CoreControl) when switched on. Session
// plugins are never touched - they are not on an atom, so the sweep cannot reach
// them.
//
// -------------------------------------------------------------------------------
// House rules for editing this file (see the VAM_PLUGIN_API reference repo)
// -------------------------------------------------------------------------------
//   * C# 6 only.
//   * No System.Reflection, no System.IO - VaM's sandbox rejects the assembly.
//   * No enum, anywhere. A field of a plugin-defined enum makes VaM's Mono runtime
//     read a null enum base type while laying out the class (mono_type_size in
//     TypeBuilder.CreateType) - a hard crash, nested or not. Use int constants.
//   * The MVRScript class must be the FIRST type in this file. A lone .cs is loaded
//     via ScriptAssembly.MainType, which is Assembly.GetTypes()[0]; a helper declared
//     above it makes VaM refuse the plugin ("must inherit from MVRScript").
//   * This file also happens to avoid 'out' parameters, coroutines and nested
//     helper classes. That is a stylistic choice kept from the crash investigation,
//     NOT a VaM rule: working plugins use all three freely. Don't "fix" other
//     plugins to match.
//   * Verify with build/verify.ps1 before loading in VaM - it enforces all of this.
//
// Licence: MIT
//

using System;
using System.Collections.Generic;
using System.Text;
using MVR.FileManagementSecure;
using SimpleJSON;
using UnityEngine;
using UnityEngine.UI;

namespace VexMech
{
    public class PluginPass : MVRScript
    {
        public const string Version = "4.4.1";

        private const string RulesDir = "Saves/PluginData/PluginPass";
        private const string RulesFile = RulesDir + "/rules.json";
        private const int MaxActivityLines = 40;

        // Self-guard. Compared without the extension so it holds however this plugin
        // is loaded - PluginPass.cs, PluginPass.cslist or PluginPass.dll.
        private const string SelfScriptBaseName = "PluginPass";

        private const string ScopeAllVersions = "Every version of this plugin";
        private const string ScopeThisVersion = "Only this version";
        private const string ScopeThisScript = "This script file, any package";
        private const string ScopeExactUrl = "This exact file only";

        private const string ActBlock = "Block it";
        private const string ActRedirect = "Replace it with my script";
        private const string ActAllow = "Allow it (exception to a wider rule)";

        // Delimiter used to sort label+url pairs as one string. Never appears in a path.
        private const char Sep = '\u0001';

        private const string TabBuild = "build";
        private const string TabRules = "rules";
        private const string TabActivity = "activity";
        private const string TabSettings = "settings";

        // Returned by Evaluate/Resolve to mean "leave this plugin exactly as it is".
        // A dedicated instance so it can be compared with ReferenceEquals and never
        // collides with a real url.
        private static readonly string Pass = new string('\0', 1);

        private PluginPassRule[] _rules = new PluginPassRule[0];

        // url -> Pass sentinel / null (block) / replacement url.
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _warnedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // replacement url -> exists on disk. Same lifetime as _cache.
        private readonly Dictionary<string, bool> _existsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private string _rulesError = "";
        private int _blockedTotal;
        private int _redirectedTotal;

        // One instance per session. A dormant copy applies no rule, writes no file, and
        // removes itself from the list it was added to.
        private bool _dormant;
        private string _dormantReason = "";
        private string _dormantShort = "";
        private string _selfUid = "";
        private bool _selfRemoveScheduled;

        // Set by the sweep: true when this copy's own plugin manager was found on an
        // atom in the scene, which is what proves it is NOT a session plugin.
        private bool _selfManagerOnAtom;
        private string _selfHostAtomUid = "";

        private readonly List<string> _activity = new List<string>();

        // Reused across the sweep so a scene load does not allocate per atom.
        private readonly List<string> _uidBuf = new List<string>();
        private readonly List<string> _urlBuf = new List<string>();

        // Picker data, filled by the same sweep that applies the rules.
        // _pickCount is SLOTS (one atom can carry the same plugin in several slots -
        // VaM loads each slot as its own instance); _pickAtoms is distinct atoms.
        private readonly Dictionary<string, int> _pickCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _pickAtoms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pickIsScene = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _atomsVisited;
        private int _managersRead;

        // settings
        private JSONStorableBool _useLatestJSON;
        private JSONStorableBool _enabledJSON;
        private JSONStorableBool _scenePluginsJSON;
        private JSONStorableBool _keepOriginalJSON;
        private JSONStorableBool _logJSON;

        // rule builder
        private JSONStorableStringChooser _pluginPickJSON;
        private JSONStorableStringChooser _scopeJSON;
        private JSONStorableStringChooser _actionJSON;
        private JSONStorableUrl _targetJSON;

        // rule list
        private JSONStorableStringChooser _ruleListJSON;

        // Left column header, right column panel. The right column shows exactly one
        // thing - whatever the current tab is about - rather than three unrelated
        // panels stacked on top of each other.
        private JSONStorableString _statusJSON;
        private JSONStorableString _detailJSON;
        private JSONStorableString _rulesTextJSON;

        // tabs
        private readonly List<UIDynamic> _tabBuildUI = new List<UIDynamic>();
        private readonly List<UIDynamic> _tabRulesUI = new List<UIDynamic>();
        private readonly List<UIDynamic> _tabActivityUI = new List<UIDynamic>();
        private readonly List<UIDynamic> _tabSettingsUI = new List<UIDynamic>();
        private UIDynamicButton _tabBuildBtn;
        private UIDynamicButton _tabRulesBtn;
        private UIDynamicButton _tabActivityBtn;
        private UIDynamicButton _tabSettingsBtn;
        private string _tab = TabBuild;

        // ============================================================================
        // lifecycle
        // ============================================================================

        public override void Init()
        {
            try
            {
                BuildUI();
                LoadRulesFromFile();
                SweepScene(false);      // gathers the evidence CheckEnvironment needs
                CheckEnvironment();
                ShowTab(_dormant ? TabSettings : TabBuild);
                RefreshStatus();

                SuperController sc = SuperController.singleton;
                if (sc != null)
                {
                    sc.onSceneLoadedHandlers += OnSceneLoadedHandler;
                    sc.onSubSceneLoadedHandlers += OnSubSceneLoadedHandler;
                }
            }
            catch (Exception e)
            {
                SuperController.LogError("PluginPass: Init failed: " + e);
            }
        }

        /// <summary>
        /// VaM builds a plugin's UI lazily. At Init() time UITransform does not exist,
        /// so MVRScript.CreateUIElement creates each element *inactive* and parks it in
        /// leftUIElements/rightUIElements. When the panel is finally opened,
        /// MVRScript.InitUI parents those elements and activates every one of them -
        /// which wiped the tab visibility set during Init, so all four tabs appeared at
        /// once. Re-apply the current tab once the base class has finished building.
        /// </summary>
        public override void InitUI()
        {
            base.InitUI();
            ShowTab(_tab);
        }

        /// <summary>Same again for the alternate (VR) panel.</summary>
        public override void InitUIAlt()
        {
            base.InitUIAlt();
            ShowTab(_tab);
        }

        public void OnDestroy()
        {
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc != null)
                {
                    sc.onSceneLoadedHandlers -= OnSceneLoadedHandler;
                    sc.onSubSceneLoadedHandlers -= OnSubSceneLoadedHandler;
                }
            }
            catch { }
        }

        private void OnSceneLoadedHandler()
        {
            bool wasDormant = _dormant;

            // One sweep: it applies the rules (only if this copy currently believes it
            // is the active one) and gathers the evidence for the check below. At Init
            // the scene's atoms may not have been registered yet, so this is also where
            // a copy sitting on an atom finally notices and removes itself.
            SweepScene(!wasDormant);
            CheckEnvironment();

            if (!_dormant && wasDormant)
            {
                // The copy that owned the session is gone - take over.
                Record("took over as the active instance");
                SweepScene(true);
            }

            RefreshStatus();
        }

        private void OnSubSceneLoadedHandler(SubScene subScene)
        {
            if (_dormant) return;
            SweepScene(true);
            RefreshStatus();
        }

        // ============================================================================
        // one instance, and only as a session plugin
        // ============================================================================

        /// <summary>
        /// Decides whether this copy is the one that does the work.
        ///
        /// Two things can make a copy dormant:
        ///
        ///   * it is not a session plugin. PluginPass rewrites atom plugin lists during
        ///     scene load; a copy living on an atom would be saved into scenes and
        ///     would fight the session copy, so it refuses to run.
        ///   * another PluginPass already holds the session. VaM compiles every plugin
        ///     slot into its OWN assembly (ScriptDomain.CompileAndLoadScriptSource has
        ///     no cache), so a static field is NOT shared between two copies and cannot
        ///     be used to detect this. The session plugin manager is shared, though, so
        ///     the copies agree on an owner by reading that one list: lowest slot wins.
        /// </summary>
        private void CheckEnvironment()
        {
            _dormant = false;
            _dormantReason = "";
            _dormantShort = "";

            // storeId is "<pluginUid>_<Namespace.ClassName>" - VaM names the script's
            // GameObject that way before Init runs, and storeId falls back to the name.
            _selfUid = OwnPluginUid();

            // Both tests below act only on POSITIVE evidence. If the sweep found
            // nothing, or this copy cannot work out its own slot, it keeps running -
            // a wrong guess must never delete a working plugin.
            if (_selfManagerOnAtom)
            {
                _dormant = true;
                _dormantShort = "is loaded on the atom '" + _selfHostAtomUid +
                                "' instead of as a session plugin";
                _dormantReason =
                    "Loaded on the atom '" + _selfHostAtomUid + "', not as a session plugin.\n\n" +
                    "PluginPass edits atom plugin lists while a scene loads, so a copy " +
                    "living inside a scene would be saved into that scene and would fight " +
                    "the session copy.\n\n" +
                    "It is removing itself. Add it under " +
                    "User Preferences > Plugins > Session Plugins instead.";
                ScheduleSelfRemove();
                return;
            }

            string owner = FirstPluginPassUid();
            if (owner.Length == 0 || _selfUid.Length == 0) return;      // cannot tell - run
            if (string.Equals(owner, _selfUid, StringComparison.Ordinal)) return;

            _dormant = true;
            _dormantShort = "is a duplicate of the copy already running in slot " + owner;
            _dormantReason =
                "Another PluginPass is already running in session slot " + owner + ".\n\n" +
                "Only one instance runs per session, so this copy (" + _selfUid + ") " +
                "applies no rules, writes no rules file, and is removing itself.";
            ScheduleSelfRemove();
        }

        /// <summary>
        /// Takes this copy out of the plugin list it was added to, one frame later.
        ///
        /// Never inline: Init runs inside MVRPluginManager.SyncPluginUrlInternal, which
        /// is still iterating the plugin it is building. Removing it from there would
        /// destroy the object mid-loop. Invoke runs on a plain scheduler callback, so
        /// FileManager.AssertNotCalledFromUnityMessaging (which only trips on
        /// SendMessage / SendMessageUpwards / BroadcastMessage frames) is satisfied.
        /// </summary>
        private void ScheduleSelfRemove()
        {
            if (_selfRemoveScheduled) return;
            if (manager == null || string.IsNullOrEmpty(_selfUid)) return;   // cannot address ourselves

            _selfRemoveScheduled = true;

            // Unconditional, and phrased as intent rather than as a result: the removal
            // has not happened yet and can still fail. The UI is about to disappear, so
            // the message log is the only place this can still be read.
            SuperController.LogMessage(
                "PluginPass: slot " + _selfUid + " " + _dormantShort +
                ", so it is removing itself. Any other copy is unaffected.");

            Invoke("SelfRemove", 0f);
        }

        /// <summary>Called by Invoke a frame after the decision. Public so Unity finds it.</summary>
        public void SelfRemove()
        {
            // Re-check: between scheduling and firing, the copy that owned the session
            // could have gone away and this one taken over. Never remove an active copy.
            if (!_dormant) return;

            try
            {
                MVRPluginManager mgr = manager;
                if (mgr == null || string.IsNullOrEmpty(_selfUid)) return;
                mgr.RemovePluginWithUID(_selfUid);
            }
            catch (Exception e)
            {
                SuperController.LogError(
                    "PluginPass: could not remove this copy (" + _selfUid + "): " + e.Message +
                    " - it stays idle instead.");
            }
        }

        private string OwnPluginUid()
        {
            string id = storeId;
            if (string.IsNullOrEmpty(id)) return "";
            int us = id.IndexOf('_');
            return us > 0 ? id.Substring(0, us) : "";
        }

        /// <summary>
        /// The lowest-numbered slot in this manager holding a PluginPass. Every copy
        /// reads the same list and so picks the same owner.
        /// </summary>
        private string FirstPluginPassUid()
        {
            List<string> uids = new List<string>();
            List<string> urls = new List<string>();
            if (!CollectPlugins(manager, uids, urls)) return "";

            string best = "";
            int bestOrder = int.MaxValue;
            for (int i = 0; i < urls.Count; i++)
            {
                if (!IsSelfUrl(urls[i])) continue;
                int order = UidOrder(uids[i]);
                if (order < bestOrder) { bestOrder = order; best = uids[i]; }
            }
            return best;
        }

        /// <summary>plugin#2 sorts before plugin#10, which an ordinal compare would not.</summary>
        private static int UidOrder(string uid)
        {
            int hash = uid.LastIndexOf('#');
            if (hash < 0) return int.MaxValue;
            int n;
            if (!int.TryParse(uid.Substring(hash + 1), out n)) return int.MaxValue;
            return n;
        }

        /// <summary>True when a plugin url points at PluginPass itself, any extension.</summary>
        private static bool IsSelfUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return string.Equals(StripExtension(FileNameOf(PathOf(url))),
                                 SelfScriptBaseName, StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================================
        // engine
        // ============================================================================

        /// <summary>
        /// One pass over the scene. Reads each atom's plugin list exactly once and uses
        /// it for both jobs - applying the rules and filling the rule-builder picker -
        /// because doing those separately meant reading every atom twice.
        ///
        /// Cost: one dictionary lookup per atom, and a plugin-list read only for atoms
        /// that actually have a plugin manager. Nothing runs per frame.
        /// </summary>
        private void SweepScene(bool applyRules)
        {
            SuperController sc = SuperController.singleton;
            if (sc == null) return;

            _pickCount.Clear();
            _pickAtoms.Clear();
            _pickIsScene.Clear();
            // Reset to "no evidence". Only a positive sighting makes this copy remove
            // itself, so a sweep that finds nothing leaves it running.
            _selfManagerOnAtom = false;
            _selfHostAtomUid = "";
            _atomsVisited = 0;
            _managersRead = 0;

            bool doApply = applyRules && !_dormant &&
                           _enabledJSON != null && _enabledJSON.val && _rules.Length > 0;

            try
            {
                List<Atom> atoms = sc.GetAtoms();
                bool sawCore = false;

                if (atoms != null)
                {
                    for (int i = 0; i < atoms.Count; i++)
                    {
                        Atom a = atoms[i];
                        if (a == null) continue;
                        bool isCore = a.uid == "CoreControl";
                        if (isCore) sawCore = true;
                        VisitAtom(a, isCore, doApply);
                    }
                }

                // Scene-level plugins live on CoreControl, which is not always listed.
                if (!sawCore) VisitAtom(sc.GetAtomByUid("CoreControl"), true, doApply);
            }
            catch (Exception e)
            {
                SuperController.LogError("PluginPass: pass over the scene failed: " + e);
            }

            RebuildPicker();
        }

        private void VisitAtom(Atom atom, bool isScenePlugins, bool doApply)
        {
            if (atom == null) return;

            _atomsVisited++;

            MVRPluginManager mgr = FindPluginManager(atom);
            if (mgr == null) return;

            // Object identity, deliberately not containingAtom. A session plugin's
            // containingAtom is whatever its manager carries and is not reliably null,
            // so the earlier check based on it was wrong. This one is exact: if the
            // very manager hosting this copy belongs to an atom in the scene, then this
            // copy is an atom or scene plugin, not a session plugin.
            //
            // It also runs before the scene-plugin gate below, so the answer does not
            // depend on a user toggle. And it never touches the list it lives in.
            if (ReferenceEquals(mgr, manager))
            {
                _selfManagerOnAtom = true;
                _selfHostAtomUid = atom.uid;
                return;
            }

            if (isScenePlugins && (_scenePluginsJSON == null || !_scenePluginsJSON.val)) return;

            _uidBuf.Clear();
            _urlBuf.Clear();
            if (!CollectPlugins(mgr, _uidBuf, _urlBuf)) return;
            _managersRead++;

            for (int i = 0; i < _urlBuf.Count; i++) NotePlugin(_urlBuf[i], isScenePlugins, 1);

            // Distinct atoms per url: count each url once for this atom, however many
            // of its slots carry it.
            for (int i = 0; i < _urlBuf.Count; i++)
            {
                string u = _urlBuf[i];
                bool seenEarlierOnThisAtom = false;
                for (int k = 0; k < i; k++)
                {
                    if (string.Equals(_urlBuf[k], u, StringComparison.OrdinalIgnoreCase)) { seenEarlierOnThisAtom = true; break; }
                }
                if (seenEarlierOnThisAtom) continue;
                if (IsSelfUrl(u)) continue;

                int a;
                _pickAtoms.TryGetValue(u, out a);
                _pickAtoms[u] = a + 1;
            }

            if (doApply) ApplyRules(mgr, atom.uid, _uidBuf, _urlBuf, isScenePlugins);
        }

        /// <summary>Records a url for the picker. delta of -1 removes one sighting.</summary>
        private void NotePlugin(string url, bool isScene, int delta)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (IsSelfUrl(url)) return;

            int n;
            _pickCount.TryGetValue(url, out n);
            n += delta;
            if (n <= 0)
            {
                _pickCount.Remove(url);
                _pickAtoms.Remove(url);
                _pickIsScene.Remove(url);   // gone from the picker, so drop its [scene] tag too
            }
            else
            {
                _pickCount[url] = n;
                if (isScene && delta > 0) _pickIsScene.Add(url);
            }
        }

        private void ApplyRules(MVRPluginManager mgr, string where, List<string> uids, List<string> urls, bool isScene)
        {
            List<string> blockUids = new List<string>();
            List<string> blockUrls = new List<string>();

            // Redirects first, blocks afterwards. Removing a plugin frees its uid and
            // CreatePlugin takes the lowest free one, so a block done earlier would
            // hand the next redirect the wrong slot - and the slot is half of the
            // storable id that the scene's triggers are keyed on.
            for (int i = 0; i < uids.Count; i++)
            {
                string url = urls[i];
                string result = Resolve(url);
                if (ReferenceEquals(result, Pass)) continue;

                if (result == null)
                {
                    blockUids.Add(uids[i]);
                    blockUrls.Add(url);
                    continue;
                }

                // uid and the JSON key are kept in sync by VaM (GivePluginNewUID sets
                // both), so the key read back from GetJSON is a valid uid to remove.
                string uid = uids[i];
                mgr.RemovePluginWithUID(uid);

                MVRPlugin created = mgr.CreatePlugin();
                if (created == null || created.pluginURLJSON == null)
                {
                    SuperController.LogError("PluginPass: could not create the replacement for " + url);
                    Record("FAILED    " + where + " / " + FriendlyName(url));
                    continue;
                }

                created.pluginURLJSON.val = result;
                _redirectedTotal++;
                Record("replaced  " + FriendlyName(url) + "  ->  " + FriendlyName(result) + "   (" + where + ")");

                // keep the picker honest without a second pass over the scene
                NotePlugin(url, isScene, -1);
                NotePlugin(result, isScene, 1);

                // Only possible when the atom's plugin slots had gaps in them.
                if (created.uid != uid)
                    Record("  note: slot " + uid + " became " + created.uid +
                           " - triggers aimed at the old slot will not bind");
            }

            for (int i = 0; i < blockUids.Count; i++)
            {
                mgr.RemovePluginWithUID(blockUids[i]);
                _blockedTotal++;
                Record("blocked   " + FriendlyName(blockUrls[i]) + "   (" + where + ")");
                NotePlugin(blockUrls[i], isScene, -1);
            }
        }

        /// <summary>
        /// Reads an atom's plugin list through the public JSON, which avoids touching
        /// MVRPluginManager's protected plugin list - reflection is forbidden here.
        /// Returns false when there is nothing to do.
        /// </summary>
        private static bool CollectPlugins(MVRPluginManager mgr, List<string> uids, List<string> urls)
        {
            if (mgr == null) return false;

            JSONClass jc = mgr.GetJSON(true, true, true);
            if (jc == null) return false;

            JSONNode node = jc["plugins"];
            if (node == null) return false;            // SimpleJSON lazy-creator aware

            JSONClass plugins = node.AsObject;
            if (plugins == null || plugins.Count == 0) return false;

            foreach (string key in plugins.Keys)
            {
                JSONNode value = plugins[key];
                if (value == null) continue;
                string url = value.Value;
                if (string.IsNullOrEmpty(url)) continue;

                uids.Add(key);
                urls.Add(url);
            }

            return uids.Count > 0;
        }

        /// <summary>
        /// A single dictionary lookup, no allocation.
        ///
        /// "PluginManager" is the storable id VaM uses: it is what appears in scene
        /// files (atoms[].storables[].id) and in plugin preset .vap files. An earlier
        /// version fell back to scanning atom.GetStorableIDs() when that missed, but
        /// that call builds a fresh List and walks the atom's whole storable
        /// dictionary - so the fallback ran, and allocated, for every atom without
        /// plugins. It bought nothing and cost the most.
        /// </summary>
        private static MVRPluginManager FindPluginManager(Atom atom)
        {
            if (atom == null) return null;
            return atom.GetStorableByID("PluginManager") as MVRPluginManager;
        }

        // ============================================================================
        // rule evaluation
        // ============================================================================

        private string Resolve(string url)
        {
            string cached;
            if (_cache.TryGetValue(url, out cached)) return cached;
            string result = Evaluate(url);
            _cache[url] = result;
            return result;
        }

        private string Evaluate(string url)
        {
            string pkg = PackageOf(url);
            string path = PathOf(url);
            string file = FileNameOf(path);

            // Never let PluginPass remove itself.
            if (IsSelfUrl(url)) return Pass;

            string group = GroupId(pkg);

            for (int i = 0; i < _rules.Length; i++)
            {
                PluginPassRule r = _rules[i];
                if (!r.enabled) continue;
                if (!r.MatchesPackage(pkg, group)) continue;
                if (!r.MatchesScript(path, file)) continue;

                if (r.action == PluginPassRule.ActionAllow) return Pass;
                if (r.action == PluginPassRule.ActionBlock) return null;

                string target = r.BuildTarget(pkg, path, file);
                if (string.IsNullOrEmpty(target))
                {
                    WarnOnce(url, "rule '" + r.name + "' replaces but has no target");
                    return Pass;
                }
                if (string.Equals(target, url, StringComparison.OrdinalIgnoreCase)) return Pass;

                if (!TargetExists(target))
                {
                    WarnOnce(target, "replacement not found: " + target + " (rule '" + r.name + "')");
                    // Null-safe on purpose: if the toggle does not exist yet, keeping the
                    // original is the safe answer. Dropping a plugin because a UI field
                    // was missing would be the worst possible failure here.
                    if (_keepOriginalJSON == null || _keepOriginalJSON.val)
                    {
                        Record("missing   " + FriendlyName(target) + "  ->  kept the original");
                        return Pass;
                    }
                }
                return target;
            }

            return Pass;
        }

        /// <summary>
        /// Cached per url. The Rules tab asks this on every panel refresh - each
        /// dropdown change, each button - and without the cache that was a disk
        /// probe per refresh. Cleared whenever the rules are re-applied.
        /// </summary>
        private bool TargetExists(string target)
        {
            bool known;
            if (_existsCache.TryGetValue(target, out known)) return known;

            bool exists;
            try { exists = FileManagerSecure.FileExists(PinPackageVersion(target), false); }
            catch { exists = true; }   // never let a file-system hiccup change behaviour

            _existsCache[target] = exists;
            return exists;
        }

        /// <summary>
        /// Replaces a symbolic package version with the concrete one it points at:
        /// Me.Fork.latest:/x.cs becomes Me.Fork.7:/x.cs.
        ///
        /// FileManager.FileExists is a dictionary lookup on the literal path, and the
        /// keys hold real version numbers, so ".latest" (and ".minN") never match and
        /// the file looks absent. FileManager.GetPackage does resolve those forms -
        /// which is what FileManagerSecure.PackageExists / GetPackageVersion call - so
        /// ask it which version is meant and probe that.
        ///
        /// This is not cosmetic. TargetExists also decides whether a redirect happens
        /// at all: with "keep original if replacement is missing" on, every ".latest"
        /// replacement was silently skipped and the original plugin stayed.
        /// </summary>
        private static string PinPackageVersion(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            string pkg = PackageOf(url);
            if (pkg.Length == 0) return url;            // loose script

            int dot = pkg.LastIndexOf('.');
            if (dot <= 0) return url;                   // SELF, or no version segment

            string version = pkg.Substring(dot + 1);
            if (version.Length == 0) return url;

            bool numeric = true;
            for (int i = 0; i < version.Length; i++)
                if (version[i] < '0' || version[i] > '9') { numeric = false; break; }
            if (numeric) return url;                    // already concrete

            try
            {
                if (!FileManagerSecure.PackageExists(pkg)) return url;   // null-safe in VaM
                int resolved = FileManagerSecure.GetPackageVersion(pkg);
                if (resolved <= 0) return url;
                return pkg.Substring(0, dot) + "." + resolved + ":" + PathOf(url);
            }
            catch { return url; }
        }

        private void WarnOnce(string key, string message)
        {
            if (!_warnedTargets.Add(key)) return;
            SuperController.LogError("PluginPass: " + message);
        }

        // Two accessors instead of one 'out' method. (An earlier note here claimed VaM
        // rejects 'out' parameters; it does not - that was a csc artefact. Kept as is
        // because the split reads fine either way.)
        private static int PackageSeparator(string url)
        {
            int i = url.IndexOf(':');
            return i > 1 ? i : -1;          // i == 1 would be a drive letter
        }

        private static string PackageOf(string url)
        {
            int i = PackageSeparator(url);
            return i < 0 ? string.Empty : url.Substring(0, i);
        }

        private static string PathOf(string url)
        {
            int i = PackageSeparator(url);
            return i < 0 ? url : url.Substring(i + 1);
        }

        /// <summary>Author.Name.7 and Author.Name.latest both reduce to Author.Name.</summary>
        private static string GroupId(string pkg)
        {
            int i = pkg.LastIndexOf('.');
            if (i <= 0) return pkg;

            string tail = pkg.Substring(i + 1);
            if (tail.Length == 0) return pkg;
            if (tail == "latest") return pkg.Substring(0, i);
            for (int k = 0; k < tail.Length; k++)
                if (tail[k] < '0' || tail[k] > '9') return pkg;
            return pkg.Substring(0, i);
        }

        private static string FileNameOf(string path)
        {
            int slash = path.LastIndexOfAny(new char[] { '/', '\\' });
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        private static string StripExtension(string file)
        {
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }

        /// <summary>Creator half of Creator.Package, or "" for a loose script.</summary>
        private static string CreatorOf(string url)
        {
            string group = GroupId(PackageOf(url));
            if (group.Length == 0) return "";
            int dot = group.IndexOf('.');
            return dot > 0 ? group.Substring(0, dot) : group;
        }

        /// <summary>
        /// The short name a human uses. AcidBubbles.Embody.1:/Custom/Scripts/
        /// AcidBubbles/Embody/Embody.cslist becomes simply "Embody"; when the script
        /// inside a package is named differently it becomes "Package / Script".
        /// </summary>
        private static string FriendlyName(string url)
        {
            if (string.IsNullOrEmpty(url)) return "(none)";

            string baseName = StripExtension(FileNameOf(PathOf(url)));
            string group = GroupId(PackageOf(url));
            if (group.Length == 0) return baseName;

            int dot = group.IndexOf('.');
            string pkgName = dot > 0 ? group.Substring(dot + 1) : group;
            if (pkgName.Length == 0) return baseName;

            if (string.Equals(pkgName, baseName, StringComparison.OrdinalIgnoreCase)) return pkgName;
            return pkgName + " / " + baseName;
        }

        /// <summary>
        /// Repoints a package url at whatever version is newest: Me.Fork.3:/x.cs
        /// becomes Me.Fork.latest:/x.cs. VaM resolves ".latest" through
        /// VarPackageGroup.NewestEnabledPackage, and it is what scenes in the wild
        /// already use (1,383 such references across the installed scenes here).
        ///
        /// Left alone: loose scripts (no package), packages with no version segment,
        /// and SELF. Already-".latest" urls pass through unchanged, so it is safe to
        /// apply twice.
        /// </summary>
        private static string ToLatestVersion(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            string pkg = PackageOf(url);
            if (pkg.Length == 0) return url;            // loose script

            string group = GroupId(pkg);
            if (group.Length >= pkg.Length) return url; // unversioned, or SELF

            return group + ".latest:" + PathOf(url);
        }

        /// <summary>Version segment of the package, or "" when unversioned/loose.</summary>
        private static string VersionOf(string url)
        {
            string pkg = PackageOf(url);
            if (pkg.Length == 0) return "";
            string group = GroupId(pkg);
            if (group.Length >= pkg.Length) return "";
            return pkg.Substring(group.Length + 1);
        }

        /// <summary>
        /// Case-insensitive glob with * and ?, treating / and \ as the same character.
        /// Iterative with backtracking - no allocation, no Regex.
        /// </summary>
        internal static bool Glob(string text, string pattern)
        {
            int t = 0, p = 0, starP = -1, starT = 0;
            int tn = text.Length, pn = pattern.Length;

            while (t < tn)
            {
                if (p < pn && (pattern[p] == '?' || CharEq(pattern[p], text[t])))
                {
                    t++; p++;
                }
                else if (p < pn && pattern[p] == '*')
                {
                    starP = p++;
                    starT = t;
                }
                else if (starP >= 0)
                {
                    p = starP + 1;
                    t = ++starT;
                }
                else return false;
            }

            while (p < pn && pattern[p] == '*') p++;
            return p == pn;
        }

        private static bool CharEq(char a, char b)
        {
            if (a == b) return true;
            if (a == '\\') a = '/';
            if (b == '\\') b = '/';
            if (a == b) return true;
            return char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
        }

        // ============================================================================
        // rule builder
        // ============================================================================

        private void RebuildPicker()
        {
            if (_pluginPickJSON == null) return;

            // Sort by the friendly name, not the raw url, so the list reads like the
            // names people know. Key is "labelurl" to keep duplicates apart.
            List<string> keyed = new List<string>(_pickCount.Count);
            foreach (KeyValuePair<string, int> kv in _pickCount)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(FriendlyName(kv.Key));
                if (_pickIsScene.Contains(kv.Key)) sb.Append("   [scene]");
                else if (kv.Value > 1)
                {
                    // Slots and atoms are different numbers: a scene can put the same
                    // plugin in three slots of one atom, and VaM runs all three. Say
                    // both, and flag the same-atom duplicate - that is worth knowing.
                    int atoms;
                    _pickAtoms.TryGetValue(kv.Key, out atoms);
                    sb.Append("   x").Append(kv.Value);
                    if (atoms > 0 && atoms < kv.Value)
                        sb.Append(" (").Append(atoms).Append(atoms == 1 ? " atom" : " atoms")
                          .Append(", duplicated)");
                    else if (atoms > 0)
                        sb.Append(" atoms");
                    // atoms == 0 means this url was substituted in by a redirect during
                    // the sweep, so the per-atom figure is unknown - say slots only.
                    else
                        sb.Append(" slots");
                }
                sb.Append(Sep).Append(kv.Key);
                keyed.Add(sb.ToString());
            }
            keyed.Sort(StringComparer.OrdinalIgnoreCase);

            List<string> values = new List<string>(keyed.Count);
            List<string> labels = new List<string>(keyed.Count);
            for (int i = 0; i < keyed.Count; i++)
            {
                int cut = keyed[i].IndexOf(Sep);
                labels.Add(keyed[i].Substring(0, cut));
                values.Add(keyed[i].Substring(cut + 1));
            }

            if (values.Count == 0)
            {
                values.Add("");
                labels.Add("(no atom plugins found - load a scene, then Rescan)");
            }

            // choices BEFORE displayChoices: the choices setter resets displayChoices
            // to the raw values, so the reverse order would show urls, not names.
            _pluginPickJSON.choices = values;
            _pluginPickJSON.displayChoices = labels;
            if (!values.Contains(_pluginPickJSON.val)) _pluginPickJSON.valNoCallback = values[0];

            RefreshDetail();
        }

        /// <summary>Turns the builder's current selections into a rule, or null.</summary>
        private PluginPassRule BuildRuleFromPicker()
        {
            string url = _pluginPickJSON.val;
            if (string.IsNullOrEmpty(url)) return null;

            string pkg = PackageOf(url);
            string path = PathOf(url);
            string file = FileNameOf(path);

            PluginPassRule r = new PluginPassRule();
            string scope = _scopeJSON.val;

            if (scope == ScopeAllVersions && pkg.Length > 0) r.packagePattern = GroupId(pkg);
            else if (scope == ScopeThisVersion && pkg.Length > 0) r.packagePattern = pkg;
            else if (scope == ScopeExactUrl)
            {
                if (pkg.Length > 0) r.packagePattern = pkg;
                r.scriptPattern = path;
            }
            else r.scriptPattern = file;    // ScopeThisScript, and the loose-script fallback

            r.scriptIsPath = r.scriptPattern != null &&
                             (r.scriptPattern.IndexOf('/') >= 0 || r.scriptPattern.IndexOf('\\') >= 0);

            string act = _actionJSON.val;
            if (act == ActRedirect)
            {
                r.action = PluginPassRule.ActionRedirect;
                r.to = _useLatestJSON != null && _useLatestJSON.val
                     ? ToLatestVersion(_targetJSON.val)
                     : _targetJSON.val;
            }
            else if (act == ActAllow) r.action = PluginPassRule.ActionAllow;
            else r.action = PluginPassRule.ActionBlock;

            r.enabled = true;
            r.name = DescribeRule(r);
            return r;
        }

        /// <summary>A one-line human summary, used as the rule's stored name.</summary>
        private static string DescribeRule(PluginPassRule r)
        {
            StringBuilder sb = new StringBuilder();

            if (r.action == PluginPassRule.ActionBlock) sb.Append("Block ");
            else if (r.action == PluginPassRule.ActionAllow) sb.Append("Allow ");
            else sb.Append("Replace ");

            sb.Append(SubjectOf(r));

            if (r.action == PluginPassRule.ActionRedirect)
                sb.Append("  ->  ").Append(FriendlyName(r.to));

            return sb.ToString();
        }

        /// <summary>What a rule is about, in human terms.</summary>
        private static string SubjectOf(PluginPassRule r)
        {
            string pkg = r.packagePattern;
            string scr = r.scriptPattern;

            if (pkg != null)
            {
                // packagePattern is Creator.Package or Creator.Package.7
                string asUrl = pkg + ":/x/" + (scr != null ? FileNameOf(scr) : "x.cs");
                string name = FriendlyName(asUrl);
                if (scr == null) name = PackageDisplay(pkg);

                string version = VersionOf(pkg + ":/x");
                if (version.Length > 0) return name + " v" + version;
                return name + " (all versions)";
            }

            if (scr != null) return StripExtension(FileNameOf(scr)) + " (any package)";
            return "(nothing)";
        }

        private static string PackageDisplay(string pkg)
        {
            string group = GroupId(pkg);
            int dot = group.IndexOf('.');
            return dot > 0 ? group.Substring(dot + 1) : group;
        }

        /// <summary>How many currently-loaded plugins a candidate rule would hit.</summary>
        private int CountMatches(PluginPassRule r)
        {
            int hits = 0;
            foreach (KeyValuePair<string, int> kv in _pickCount)
            {
                string url = kv.Key;
                string pkg = PackageOf(url);
                string path = PathOf(url);
                if (r.MatchesPackage(pkg, GroupId(pkg)) && r.MatchesScript(path, FileNameOf(path)))
                    hits += kv.Value;
            }
            return hits;
        }

        private void AddRuleFromBuilder()
        {
            PluginPassRule r = BuildRuleFromPicker();
            if (r == null) { Record("pick a plugin first"); RefreshStatus(); return; }

            if (r.packagePattern == null && r.scriptPattern == null)
            {
                Record("that rule would match everything - not added");
                RefreshStatus();
                return;
            }
            if (r.action == PluginPassRule.ActionRedirect && string.IsNullOrEmpty(r.to))
            {
                Record("choose a replacement script first");
                RefreshStatus();
                return;
            }

            List<PluginPassRule> list = new List<PluginPassRule>(_rules);
            list.Add(r);
            CommitRules(list, "added: " + r.name);
        }

        private void DeleteSelectedRule()
        {
            int idx = SelectedRuleIndex();
            if (idx < 0) { Record("no rule selected"); RefreshStatus(); return; }

            List<PluginPassRule> list = new List<PluginPassRule>(_rules);
            string gone = list[idx].name;
            list.RemoveAt(idx);
            CommitRules(list, "deleted: " + gone);
        }

        private void ToggleSelectedRule()
        {
            int idx = SelectedRuleIndex();
            if (idx < 0) { Record("no rule selected"); RefreshStatus(); return; }

            List<PluginPassRule> list = new List<PluginPassRule>(_rules);
            list[idx].enabled = !list[idx].enabled;
            CommitRules(list, (list[idx].enabled ? "enabled: " : "disabled: ") + list[idx].name);
        }

        private void MoveSelectedRuleUp()
        {
            int idx = SelectedRuleIndex();
            if (idx <= 0) { Record("already first, or nothing selected"); RefreshStatus(); return; }

            List<PluginPassRule> list = new List<PluginPassRule>(_rules);
            PluginPassRule moved = list[idx];
            list.RemoveAt(idx);
            list.Insert(idx - 1, moved);
            _ruleListJSON.valNoCallback = (idx - 1).ToString();
            CommitRules(list, "moved up: " + moved.name);
        }

        private int SelectedRuleIndex()
        {
            if (_ruleListJSON == null) return -1;
            int idx;
            if (!int.TryParse(_ruleListJSON.val, out idx)) return -1;
            if (idx < 0 || idx >= _rules.Length) return -1;
            return idx;
        }

        /// <summary>
        /// One way in and out for every rule change: rebuild the JSON text from the
        /// list, then save, which re-parses it. The round trip through the file format
        /// is validated on every edit rather than only on load.
        /// </summary>
        private void CommitRules(List<PluginPassRule> list, string message)
        {
            // Refuse before touching anything, so an idle copy's UI cannot drift out of
            // step with the file the active copy owns.
            if (_dormant)
            {
                Record("idle copy - rule change ignored");
                RefreshStatus();
                return;
            }

            _rulesTextJSON.valNoCallback = RulesToText(list);
            bool saved = SaveRulesToFile();
            Record(saved ? message : message + "   (in memory only - NOT saved to disk)");
            RefreshRuleList();
            RefreshStatus();
        }

        private void RefreshRuleList()
        {
            if (_ruleListJSON == null) return;

            List<string> values = new List<string>();
            List<string> labels = new List<string>();

            for (int i = 0; i < _rules.Length; i++)
            {
                values.Add(i.ToString());
                labels.Add((i + 1) + ".  " + (_rules[i].enabled ? "" : "[off]  ") + _rules[i].name);
            }

            if (values.Count == 0)
            {
                values.Add("");
                labels.Add("(no rules yet - make one on the Build tab)");
            }

            // choices before displayChoices - see RebuildPicker.
            _ruleListJSON.choices = values;
            _ruleListJSON.displayChoices = labels;
            if (!values.Contains(_ruleListJSON.val)) _ruleListJSON.valNoCallback = values[0];

            RefreshDetail();
        }

        // ============================================================================
        // file browser
        // ============================================================================

        /// <summary>
        /// Makes the "choose my replacement" button open VaM's browser in plugin mode.
        ///
        /// Without this you get a bare folder tree. MVRPluginManager.BeginBrowse fills
        /// JSONStorableUrl.shortCuts with the package shortcuts for Custom/Scripts plus
        /// a Root entry, and VaM's own plugin picker only looks right because of that.
        /// Reproduced here with the sandbox-legal API: FileManagerSecure rather than
        /// MVR.FileManagement, and FileManagerSecure.GetFullPath rather than
        /// System.IO.Path.GetFullPath, since System.IO is a restricted namespace.
        /// </summary>
        private void BeginTargetBrowse(JSONStorableUrl jsurl)
        {
            if (jsurl == null) return;
            try
            {
                List<ShortCut> result = new List<ShortCut>();

                ShortCut root = new ShortCut();
                root.displayName = "Root";
                root.path = FileManagerSecure.GetFullPath(".");
                result.Add(root);

                List<ShortCut> packages =
                    FileManagerSecure.GetShortCutsForDirectory("Custom/Scripts", true, true, true, true);
                if (packages != null)
                    for (int i = 0; i < packages.Count; i++) result.Add(packages[i]);

                jsurl.shortCuts = result;
            }
            catch (Exception e)
            {
                SuperController.LogError("PluginPass: could not build the browser shortcuts: " + e.Message);
            }
        }

        private void OnTargetChosen(string v)
        {
            // Nothing to mirror on the left: the right-hand panel is always visible
            // and already shows the chosen replacement in full.
            RefreshDetail();
        }

        private void ClearTarget()
        {
            if (_targetJSON != null) _targetJSON.val = "";
            // JSONStorableUrl.set_val is a no-op (no callback) when the value is
            // unchanged, so refresh explicitly rather than rely on OnTargetChosen.
            RefreshDetail();
        }

        // ============================================================================
        // rules file
        // ============================================================================

        private void LoadRulesFromFile()
        {
            string text = null;
            try
            {
                if (FileManagerSecure.FileExists(RulesFile, false))
                    text = FileManagerSecure.ReadAllText(RulesFile);
            }
            catch (Exception e)
            {
                SuperController.LogError("PluginPass: could not read " + RulesFile + ": " + e.Message);
            }

            if (text == null) text = RulesToText(new List<PluginPassRule>());

            _rulesTextJSON.valNoCallback = text;
            ApplyRulesText(text);
            RefreshRuleList();
        }

        /// <summary>
        /// Writes the rules file and re-applies the text. Returns false when the write
        /// failed: the rules are still live in memory for this session, but the caller
        /// should say so rather than report a clean save.
        /// </summary>
        private bool SaveRulesToFile()
        {
            // An idle copy must not write: the active one owns the file.
            if (_dormant)
            {
                Record("idle copy - rules file not written");
                return false;
            }

            bool saved = true;
            try
            {
                FileManagerSecure.CreateDirectory(RulesDir);
                FileManagerSecure.WriteAllText(RulesFile, _rulesTextJSON.val);
            }
            catch (Exception e)
            {
                saved = false;
                SuperController.LogError("PluginPass: could not write " + RulesFile + ": " + e.Message);
                Record("save FAILED: " + e.Message);
            }

            ApplyRulesText(_rulesTextJSON.val);
            return saved;
        }

        private static string RulesToText(List<PluginPassRule> rules)
        {
            JSONClass root = new JSONClass();

            JSONArray readme = new JSONArray();
            readme.Add(new JSONData("Written by the PluginPass UI - you can edit this by hand too."));
            readme.Add(new JSONData("The first rule that matches a plugin wins."));
            readme.Add(new JSONData("package: Author.Name = every version, Author.Name.7 = that one, * wildcards ok."));
            readme.Add(new JSONData("script:  Foo.cs matches the file name; anything with a / matches the whole path."));
            readme.Add(new JSONData("action:  block | redirect | allow      to: replacement url for a redirect."));
            readme.Add(new JSONData("{path} {file} {package} in 'to' are substituted from the original url."));
            root.Add("_readme", readme);

            JSONArray arr = new JSONArray();
            for (int i = 0; i < rules.Count; i++) arr.Add(rules[i].ToJSON());
            root.Add("rules", arr);

            return root.ToString("   ");
        }

        private void ApplyRulesText(string text)
        {
            _rulesError = "";
            _cache.Clear();
            _warnedTargets.Clear();
            _existsCache.Clear();

            List<PluginPassRule> parsed = new List<PluginPassRule>();
            List<string> problems = new List<string>();

            try
            {
                JSONNode root = JSONNode.Parse(text);
                if (root == null) throw new Exception("not valid JSON");

                JSONArray arr;
                JSONClass obj = root.AsObject;
                if (obj != null && obj.HasKey("rules")) arr = obj["rules"].AsArray;
                else arr = root.AsArray;

                if (arr == null) throw new Exception("expected a \"rules\" array");

                for (int i = 0; i < arr.Count; i++)
                {
                    JSONClass o = arr[i].AsObject;
                    if (o == null) { problems.Add("rule " + i + ": not an object"); continue; }

                    PluginPassRule r = new PluginPassRule();
                    r.enabled = !o.HasKey("enabled") || o["enabled"].AsBool;
                    r.name = o.HasKey("name") ? o["name"].Value
                           : (o.HasKey("comment") ? o["comment"].Value : "rule " + i);

                    string pkg = o.HasKey("package") ? o["package"].Value.Trim() : "";
                    string scr = o.HasKey("script") ? o["script"].Value.Trim() : "";
                    r.packagePattern = pkg.Length > 0 ? pkg : null;
                    r.scriptPattern = scr.Length > 0 ? scr : null;
                    r.scriptIsPath = r.scriptPattern != null &&
                                     (r.scriptPattern.IndexOf('/') >= 0 || r.scriptPattern.IndexOf('\\') >= 0);

                    string act = o.HasKey("action") ? o["action"].Value.Trim().ToLowerInvariant() : "block";
                    if (act == "redirect") r.action = PluginPassRule.ActionRedirect;
                    else if (act == "allow") r.action = PluginPassRule.ActionAllow;
                    else if (act == "block") r.action = PluginPassRule.ActionBlock;
                    else { problems.Add("rule " + i + " ('" + r.name + "'): unknown action '" + act + "'"); continue; }

                    r.to = o.HasKey("to") ? o["to"].Value.Trim() : null;

                    // A rule that matches on nothing would swallow every plugin in the game.
                    if (r.packagePattern == null && r.scriptPattern == null)
                    {
                        problems.Add("rule " + i + " ('" + r.name + "'): needs 'package' and/or 'script'");
                        continue;
                    }
                    if (r.action == PluginPassRule.ActionRedirect && string.IsNullOrEmpty(r.to))
                    {
                        problems.Add("rule " + i + " ('" + r.name + "'): replaces but has no 'to'");
                        continue;
                    }

                    parsed.Add(r);
                }
            }
            catch (Exception e)
            {
                _rulesError = e.Message;
                SuperController.LogError("PluginPass: rules not loaded (" + e.Message + ") - no rules are active");
                _rules = new PluginPassRule[0];
                RefreshStatus();
                return;
            }

            if (problems.Count > 0)
            {
                _rulesError = string.Join("\n", problems.ToArray());
                for (int i = 0; i < problems.Count; i++)
                    SuperController.LogError("PluginPass: " + problems[i] + " - rule skipped");
            }

            _rules = parsed.ToArray();
            RefreshStatus();
        }

        // ============================================================================
        // ui
        // ============================================================================

        private void BuildUI()
        {
            // ---- left column: a compact header, then tabs, then controls -----------

            _statusJSON = new JSONStorableString("status", "");
            CreateTextField(_statusJSON, false).height = 112f;

            _tabBuildBtn = CreateButton("1  BUILD A RULE", false);
            _tabBuildBtn.button.onClick.AddListener(delegate { ShowTab(TabBuild); });

            _tabRulesBtn = CreateButton("2  MY RULES", false);
            _tabRulesBtn.button.onClick.AddListener(delegate { ShowTab(TabRules); });

            _tabActivityBtn = CreateButton("3  ACTIVITY", false);
            _tabActivityBtn.button.onClick.AddListener(delegate { ShowTab(TabActivity); });

            _tabSettingsBtn = CreateButton("4  SETTINGS", false);
            _tabSettingsBtn.button.onClick.AddListener(delegate { ShowTab(TabSettings); });

            // ---- BUILD tab ---------------------------------------------------------

            AddSpacer(_tabBuildUI, 14f);

            UIDynamicButton rescan = CreateButton("Rescan the scene", false);
            rescan.button.onClick.AddListener(delegate { SweepScene(false); RefreshStatus(); });
            _tabBuildUI.Add(rescan);

            _pluginPickJSON = new JSONStorableStringChooser("plugin", new List<string>(), "", "1  Plugin");
            _pluginPickJSON.setCallbackFunction = delegate (string v) { RefreshDetail(); };
            UIDynamicPopup pick = CreateFilterablePopup(_pluginPickJSON, false);
            pick.popupPanelHeight = 700f;
            _tabBuildUI.Add(pick);

            AddSpacer(_tabBuildUI, 14f);

            List<string> scopes = new List<string>();
            scopes.Add(ScopeAllVersions);
            scopes.Add(ScopeThisVersion);
            scopes.Add(ScopeThisScript);
            scopes.Add(ScopeExactUrl);
            _scopeJSON = new JSONStorableStringChooser("scope", scopes, ScopeAllVersions, "2  Applies to");
            _scopeJSON.setCallbackFunction = delegate (string v) { RefreshDetail(); };
            UIDynamicPopup scopePop = CreateScrollablePopup(_scopeJSON, false);
            scopePop.popupPanelHeight = 300f;
            _tabBuildUI.Add(scopePop);

            AddSpacer(_tabBuildUI, 14f);

            List<string> actions = new List<string>();
            actions.Add(ActBlock);
            actions.Add(ActRedirect);
            actions.Add(ActAllow);
            _actionJSON = new JSONStorableStringChooser("action", actions, ActBlock, "3  Do what");
            _actionJSON.setCallbackFunction = delegate (string v) { RefreshDetail(); };
            UIDynamicPopup actionPop = CreateScrollablePopup(_actionJSON, false);
            actionPop.popupPanelHeight = 260f;
            _tabBuildUI.Add(actionPop);

            AddSpacer(_tabBuildUI, 14f);

            // VaM's own browser, in plugin mode (see BeginTargetBrowse).
            _targetJSON = new JSONStorableUrl("target", "", "cs|cslist|dll", "Custom/Scripts");
            _targetJSON.beginBrowseWithObjectCallback = BeginTargetBrowse;
            _targetJSON.setCallbackFunction = OnTargetChosen;

            UIDynamicButton browse = CreateButton("4  Choose my replacement script...", false);
            _targetJSON.RegisterFileBrowseButton(browse.button, false);
            _tabBuildUI.Add(browse);

            // The browser hands back a pinned version (Me.Fork.3:/...). Rewriting it to
            // Me.Fork.latest: is the whole point of the plugin for most people: update
            // your own fork and every rule follows it, with no rule to edit.
            _useLatestJSON = new JSONStorableBool("useLatestVersionOfReplacement", true);
            _useLatestJSON.setCallbackFunction = delegate { RefreshDetail(); };
            RegisterBool(_useLatestJSON);
            UIDynamicToggle latestToggle = CreateToggle(_useLatestJSON, false);
            latestToggle.label = "Always use the latest version of my script";
            _tabBuildUI.Add(latestToggle);

            UIDynamicButton clearTarget = CreateButton("Clear replacement", false);
            clearTarget.button.onClick.AddListener(delegate { ClearTarget(); });
            _tabBuildUI.Add(clearTarget);

            AddSpacer(_tabBuildUI, 24f);

            UIDynamicButton add = CreateButton("ADD THIS RULE", false);
            add.button.onClick.AddListener(delegate { AddRuleFromBuilder(); });
            add.buttonColor = new Color(0.55f, 0.85f, 0.55f);
            _tabBuildUI.Add(add);

            // ---- RULES tab ---------------------------------------------------------

            AddSpacer(_tabRulesUI, 14f);

            _ruleListJSON = new JSONStorableStringChooser("ruleList", new List<string>(), "", "Rule");
            _ruleListJSON.setCallbackFunction = delegate (string v) { RefreshDetail(); };
            UIDynamicPopup rulePop = CreateFilterablePopup(_ruleListJSON, false);
            rulePop.popupPanelHeight = 620f;
            _tabRulesUI.Add(rulePop);

            UIDynamicButton toggle = CreateButton("Enable / disable this rule", false);
            toggle.button.onClick.AddListener(delegate { ToggleSelectedRule(); });
            _tabRulesUI.Add(toggle);

            UIDynamicButton up = CreateButton("Move up   (earlier rules win)", false);
            up.button.onClick.AddListener(delegate { MoveSelectedRuleUp(); });
            _tabRulesUI.Add(up);

            UIDynamicButton del = CreateButton("Delete this rule", false);
            del.button.onClick.AddListener(delegate { DeleteSelectedRule(); });
            del.buttonColor = new Color(0.9f, 0.6f, 0.6f);
            _tabRulesUI.Add(del);

            AddSpacer(_tabRulesUI, 14f);

            UIDynamicButton apply = CreateButton("Apply rules to this scene", false);
            apply.button.onClick.AddListener(delegate { SweepScene(true); RefreshStatus(); });
            _tabRulesUI.Add(apply);

            // ---- ACTIVITY tab ------------------------------------------------------

            AddSpacer(_tabActivityUI, 14f);

            UIDynamicButton clearLog = CreateButton("Clear the log", false);
            clearLog.button.onClick.AddListener(delegate { ClearActivity(); });
            _tabActivityUI.Add(clearLog);

            // ---- SETTINGS tab ------------------------------------------------------

            AddSpacer(_tabSettingsUI, 14f);

            _enabledJSON = new JSONStorableBool("enabled", true);
            _enabledJSON.setCallbackFunction = delegate { RefreshStatus(); };
            RegisterBool(_enabledJSON);
            UIDynamicToggle t1 = CreateToggle(_enabledJSON, false);
            t1.label = "PluginPass enabled";
            _tabSettingsUI.Add(t1);

            // Session plugins are never touched - they are not on an atom at all, so
            // the sweep cannot reach them. This only covers the third category:
            // scene-level plugins, which live on the CoreControl atom.
            _scenePluginsJSON = new JSONStorableBool("applyToScenePlugins", true);
            // Note: RestoreFromJSON invokes this callback too, so restoring the session
            // preset runs one extra picker-only sweep. It applies no rules and costs a
            // dictionary walk, so it is left as is rather than special-cased.
            _scenePluginsJSON.setCallbackFunction = delegate { SweepScene(false); RefreshStatus(); };
            RegisterBool(_scenePluginsJSON);
            UIDynamicToggle t2 = CreateToggle(_scenePluginsJSON, false);
            t2.label = "Also apply to scene plugins";
            _tabSettingsUI.Add(t2);

            _keepOriginalJSON = new JSONStorableBool("keepOriginalIfTargetMissing", true);
            _keepOriginalJSON.setCallbackFunction = delegate { _cache.Clear(); _warnedTargets.Clear(); _existsCache.Clear(); };
            RegisterBool(_keepOriginalJSON);
            UIDynamicToggle t3 = CreateToggle(_keepOriginalJSON, false);
            t3.label = "Keep original if replacement is missing";
            _tabSettingsUI.Add(t3);

            _logJSON = new JSONStorableBool("logToConsole", false);
            RegisterBool(_logJSON);
            UIDynamicToggle t4 = CreateToggle(_logJSON, false);
            t4.label = "Also log to VaM's message log";
            _tabSettingsUI.Add(t4);

            AddSpacer(_tabSettingsUI, 14f);

            _rulesTextJSON = new JSONStorableString("rulesText", "");
            _rulesTextJSON.setCallbackFunction = delegate (string v) { ApplyRulesText(v); RefreshRuleList(); };
            UIDynamicTextField rulesField = CreateTextField(_rulesTextJSON, false);
            MakeEditable(_rulesTextJSON, rulesField, 420f);
            _tabSettingsUI.Add(rulesField);

            UIDynamicButton save = CreateButton("Save rules file", false);
            save.button.onClick.AddListener(delegate { SaveRulesToFile(); RefreshRuleList(); RefreshStatus(); });
            _tabSettingsUI.Add(save);

            UIDynamicButton reload = CreateButton("Reload rules file", false);
            reload.button.onClick.AddListener(delegate { LoadRulesFromFile(); RefreshStatus(); });
            _tabSettingsUI.Add(reload);

            // ---- right column: one panel, showing whatever the tab is about --------

            _detailJSON = new JSONStorableString("detail", "");
            CreateTextField(_detailJSON, true).height = 1020f;
        }

        private void AddSpacer(List<UIDynamic> tab, float height)
        {
            UIDynamic s = CreateSpacer(false);
            s.height = height;
            tab.Add(s);
        }

        private void ShowTab(string tab)
        {
            // A popup's dropdown panel is drawn outside the element, so hiding the
            // element does not close an open list - it just leaves it floating over
            // the next tab. Close them all first.
            ClosePopups();

            // Switching tabs abandons whatever was half-entered, so start clean rather
            // than leaving a stale draft rule or selection behind.
            ResetDrafts();

            _tab = tab;
            SetTabActive(_tabBuildUI, tab == TabBuild);
            SetTabActive(_tabRulesUI, tab == TabRules);
            SetTabActive(_tabActivityUI, tab == TabActivity);
            SetTabActive(_tabSettingsUI, tab == TabSettings);

            Color on = new Color(0.45f, 0.72f, 1f);
            Color off = new Color(0.82f, 0.82f, 0.82f);
            if (_tabBuildBtn != null) _tabBuildBtn.buttonColor = tab == TabBuild ? on : off;
            if (_tabRulesBtn != null) _tabRulesBtn.buttonColor = tab == TabRules ? on : off;
            if (_tabActivityBtn != null) _tabActivityBtn.buttonColor = tab == TabActivity ? on : off;
            if (_tabSettingsBtn != null) _tabSettingsBtn.buttonColor = tab == TabSettings ? on : off;

            RefreshDetail();
        }

        private void ClosePopups()
        {
            ClosePopup(_pluginPickJSON);
            ClosePopup(_scopeJSON);
            ClosePopup(_actionJSON);
            ClosePopup(_ruleListJSON);
        }

        /// <summary>
        /// Shuts a dropdown and clears its filter text, so it reopens clean.
        ///
        /// Order matters: UIPopup.set_filter re-syncs the panel and touches
        /// visibility itself, so clearing the filter AFTER hiding could bring the
        /// list straight back. Clear the filter first, then hide.
        /// </summary>
        private static void ClosePopup(JSONStorableStringChooser chooser)
        {
            if (chooser == null) return;
            try
            {
                if (chooser.popup != null)
                {
                    chooser.popup.filter = "";
                    chooser.popup.visible = false;
                }
                if (chooser.popupAlt != null)
                {
                    chooser.popupAlt.filter = "";
                    chooser.popupAlt.visible = false;
                }
            }
            catch { }
        }

        /// <summary>
        /// Clears everything the user had part-entered: the draft rule on the Build tab
        /// and the selection on the Rules tab.
        /// </summary>
        private void ResetDrafts()
        {
            if (_pluginPickJSON != null && _pluginPickJSON.choices != null && _pluginPickJSON.choices.Count > 0)
                _pluginPickJSON.valNoCallback = _pluginPickJSON.choices[0];

            if (_scopeJSON != null) _scopeJSON.valNoCallback = ScopeAllVersions;
            if (_actionJSON != null) _actionJSON.valNoCallback = ActBlock;

            if (_targetJSON != null) _targetJSON.valNoCallback = "";

            if (_ruleListJSON != null && _ruleListJSON.choices != null && _ruleListJSON.choices.Count > 0)
                _ruleListJSON.valNoCallback = _ruleListJSON.choices[0];
        }

        private static void SetTabActive(List<UIDynamic> elements, bool active)
        {
            for (int i = 0; i < elements.Count; i++)
            {
                UIDynamic e = elements[i];
                if (e != null && e.gameObject != null) e.gameObject.SetActive(active);
            }
        }

        private static void MakeEditable(JSONStorableString storable, UIDynamicTextField field, float height)
        {
            field.height = height;
            try
            {
                InputField input = field.gameObject.AddComponent<InputField>();
                input.textComponent = field.UItext;
                input.lineType = InputField.LineType.MultiLineNewline;
                input.targetGraphic = field.backgroundImage;
                storable.inputField = input;
            }
            catch (Exception e)
            {
                // Worst case the field stays read-only and the file can still be edited
                // outside the game.
                SuperController.LogError("PluginPass: rules editor is read-only: " + e.Message);
            }
        }

        private void Record(string line)
        {
            _activity.Add(line);
            if (_activity.Count > MaxActivityLines)
                _activity.RemoveRange(0, _activity.Count - MaxActivityLines);

            // No RefreshDetail here: a sweep can log dozens of lines, and the panel is
            // rebuilt once at the end of the sweep anyway.
            if (_logJSON != null && _logJSON.val) SuperController.LogMessage("PluginPass: " + line);
        }

        private void ClearActivity()
        {
            _activity.Clear();
            RefreshDetail();
        }

        /// <summary>The compact header at the top of the left column. Four lines, no more.</summary>
        private void RefreshStatus()
        {
            if (_statusJSON == null) return;

            StringBuilder sb = new StringBuilder();
            sb.Append("<b>PluginPass ").Append(Version).Append("</b>   ");
            if (_dormant) sb.Append(_selfRemoveScheduled
                ? "<color=#b71c1c>REMOVING ITSELF</color>" : "<color=#b71c1c>IDLE</color>");
            else sb.Append(_enabledJSON != null && _enabledJSON.val
                ? "<color=#1b5e20>on</color>" : "<color=#b71c1c>OFF</color>");
            sb.Append('\n');

            if (_dormant)
            {
                sb.Append("<size=24><color=#b71c1c>This copy does nothing - see the panel on the right.</color></size>");
                _statusJSON.val = sb.ToString();
                return;
            }

            int active = 0;
            for (int i = 0; i < _rules.Length; i++) if (_rules[i].enabled) active++;
            sb.Append("<size=26>").Append(active).Append(" of ").Append(_rules.Length)
              .Append(" rule(s) active\n");
            sb.Append(_blockedTotal).Append(" blocked, ").Append(_redirectedTotal)
              .Append(" replaced this session</size>");

            if (_rulesError.Length > 0)
                sb.Append("\n<color=#b71c1c><size=24>").Append(_rulesError).Append("</size></color>");

            _statusJSON.val = sb.ToString();
        }

        /// <summary>
        /// The right-hand panel. It shows one subject at a time - whatever the current
        /// tab is about - so full urls, match patterns and the log each have room
        /// instead of competing for the same column.
        /// </summary>
        private void RefreshDetail()
        {
            if (_detailJSON == null) return;

            string body;
            if (_tab == TabRules) body = BuildRulesDetail();
            else if (_tab == TabActivity) body = BuildActivityDetail();
            else if (_tab == TabSettings) body = BuildSettingsDetail();
            else body = BuildBuilderDetail();

            // The banner goes above whatever the tab shows: an idle copy stays browsable
            // but can never be mistaken for the one doing the work.
            if (_dormant)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(_selfRemoveScheduled
                    ? "<color=#b71c1c><b>THIS COPY IS REMOVING ITSELF</b></color>\n\n<size=26>"
                    : "<color=#b71c1c><b>THIS COPY IS IDLE</b></color>\n\n<size=26>");
                sb.Append(_dormantReason).Append("</size>\n\n");
                sb.Append("<size=22>--------------------------------</size>\n\n");
                sb.Append(body);
                body = sb.ToString();
            }

            _detailJSON.val = body;
        }

        private string BuildBuilderDetail()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<b>BUILD A RULE</b>\n\n");

            PluginPassRule r = BuildRuleFromPicker();
            if (r == null)
            {
                sb.Append("<size=26>Pick a plugin on the left, choose what to do with it, ");
                sb.Append("then press Add.\n\n");
                sb.Append("Nothing in the list? Load a scene and press Rescan.\n\n");
                sb.Append("Only atom and scene plugins are listed. Session plugins are never ");
                sb.Append("affected, so they are not offered.</size>");
                return sb.ToString();
            }

            string url = _pluginPickJSON.val;
            sb.Append("<b>Selected plugin</b>\n");
            sb.Append(FriendlyName(url)).Append('\n');

            string creator = CreatorOf(url);
            string version = VersionOf(url);
            sb.Append("<size=24>");
            if (creator.Length > 0) sb.Append("by ").Append(creator);
            if (version.Length > 0) sb.Append("      version ").Append(version);
            if (creator.Length == 0 && version.Length == 0) sb.Append("loose script, no package");
            sb.Append('\n').Append(url).Append("</size>\n\n");

            sb.Append("<b>The rule you are about to add</b>\n");
            sb.Append(r.name).Append("\n\n");

            sb.Append("<size=24><b>It will match</b>\n");
            sb.Append("package:   ").Append(r.packagePattern == null ? "(any)" : r.packagePattern).Append('\n');
            sb.Append("script:    ").Append(r.scriptPattern == null ? "(any)" : r.scriptPattern).Append('\n');
            if (r.action == PluginPassRule.ActionRedirect)
            {
                sb.Append("\n<b>Replacement</b>\n");
                sb.Append(string.IsNullOrEmpty(r.to) ? "(none chosen yet)" : r.to).Append('\n');
            }
            sb.Append("</size>\n");

            if (r.action == PluginPassRule.ActionRedirect && string.IsNullOrEmpty(r.to))
                sb.Append("<color=#b71c1c>Choose a replacement script first.</color>");
            else if (r.packagePattern == null && r.scriptPattern == null)
                sb.Append("<color=#b71c1c>That would match every plugin - not allowed.</color>");
            else
                sb.Append("Affects <b>").Append(CountMatches(r))
                  .Append("</b> plugin slot(s) loaded right now.");

            return sb.ToString();
        }

        private string BuildRulesDetail()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<b>MY RULES</b>\n\n");

            if (_rules.Length == 0)
            {
                sb.Append("<size=26>No rules yet. Build one on tab 1.</size>");
                return sb.ToString();
            }

            int idx = SelectedRuleIndex();
            if (idx < 0)
            {
                sb.Append("<size=26>Select a rule on the left.</size>");
                return sb.ToString();
            }

            PluginPassRule r = _rules[idx];
            sb.Append("<b>Rule ").Append(idx + 1).Append(" of ").Append(_rules.Length).Append("</b>");
            sb.Append(r.enabled ? "" : "   <color=#b71c1c>[disabled]</color>").Append("\n\n");
            sb.Append(r.name).Append("\n\n");

            sb.Append("<size=24><b>Matches</b>\n");
            sb.Append("package:   ").Append(r.packagePattern == null ? "(any)" : r.packagePattern).Append('\n');
            sb.Append("script:    ").Append(r.scriptPattern == null ? "(any)" : r.scriptPattern).Append('\n');

            if (r.action == PluginPassRule.ActionRedirect)
            {
                sb.Append("\n<b>Replacement</b>\n").Append(r.to).Append('\n');
                if (!string.IsNullOrEmpty(r.to) && TargetExists(r.to))
                {
                    sb.Append("<color=#1b5e20>found on disk</color>");
                    // For a ".latest" rule, say which version that is right now.
                    string pinned = PinPackageVersion(r.to);
                    if (!string.Equals(pinned, r.to, StringComparison.Ordinal))
                        sb.Append("  -  currently ").Append(PackageOf(pinned));
                    sb.Append('\n');
                }
                else sb.Append("<color=#b71c1c>NOT found on disk</color>\n");
            }
            sb.Append("</size>\n");

            sb.Append("Matches <b>").Append(CountMatches(r)).Append("</b> plugin slot(s) loaded right now.\n\n");
            sb.Append("<size=24>Earlier rules win. Move a rule up to give it priority - ");
            sb.Append("that is how an Allow exception beats a wider Block.</size>");

            return sb.ToString();
        }

        private string BuildActivityDetail()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<b>ACTIVITY</b>   <size=22>newest first</size>\n\n");

            if (_activity.Count == 0)
            {
                sb.Append("<size=26>Nothing yet this session.\n\n");
                sb.Append("Load a scene with a matching plugin and it will show up here.</size>");
                return sb.ToString();
            }

            sb.Append("<size=24>");
            for (int i = _activity.Count - 1; i >= 0; i--) sb.Append(_activity[i]).Append('\n');
            sb.Append("</size>");
            return sb.ToString();
        }

        private string BuildSettingsDetail()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<b>SETTINGS</b>\n\n");
            sb.Append("<size=26><b>What PluginPass touches</b>\n");
            sb.Append("Atom plugins            yes\n");
            sb.Append("Scene plugins           ");
            sb.Append(_scenePluginsJSON != null && _scenePluginsJSON.val ? "yes\n" : "no\n");
            sb.Append("Session plugins         never\n\n");
            sb.Append("Session plugins are not attached to an atom, so the sweep cannot reach ");
            sb.Append("them. That is also why loading a scene never disturbs them.\n\n");
            sb.Append("<b>Last sweep</b>\n");
            sb.Append("read ").Append(_managersRead).Append(" plugin list(s) over ")
              .Append(_atomsVisited).Append(" atom(s)\n\n");
            sb.Append("<b>Rules file</b>\n").Append(RulesFile).Append('\n');
            sb.Append("The Advanced box edits it directly. Press Save to apply.</size>");
            return sb.ToString();
        }
    }

    /// <summary>
    /// One rule from rules.json: what to match, and what to do about it.
    ///
    /// Declared AFTER PluginPass on purpose: for a lone .cs, VaM takes the first type
    /// in the file as the plugin and requires it to be an MVRScript. The action is an
    /// int rather than an enum because any plugin-defined enum used as a field crashes
    /// VaM's runtime during type layout (see the VAM_PLUGIN_API reference, 1.4).
    /// Nested helper classes would be fine - top-level is just simpler.
    /// </summary>
    internal class PluginPassRule
    {
        public const int ActionBlock = 0;
        public const int ActionRedirect = 1;
        public const int ActionAllow = 2;

        public bool enabled = true;
        public string name = "";
        public int action = ActionBlock;

        // null means "this facet is not part of the match"
        public string packagePattern;
        public string scriptPattern;
        public bool scriptIsPath;   // pattern contains a separator -> match whole path
        public string to;

        public bool MatchesPackage(string pkg, string group)
        {
            if (packagePattern == null) return true;
            if (packagePattern == "*") return true;
            if (pkg.Length == 0) return false;      // loose script, rule wants a package
            return PluginPass.Glob(pkg, packagePattern) || PluginPass.Glob(group, packagePattern);
        }

        public bool MatchesScript(string path, string file)
        {
            if (scriptPattern == null) return true;
            return PluginPass.Glob(scriptIsPath ? path : file, scriptPattern);
        }

        public string BuildTarget(string pkg, string path, string file)
        {
            if (string.IsNullOrEmpty(to)) return null;
            if (to.IndexOf('{') < 0) return to;
            StringBuilder sb = new StringBuilder(to);
            sb.Replace("{path}", path);
            sb.Replace("{file}", file);
            sb.Replace("{package}", pkg);
            return sb.ToString();
        }

        public JSONClass ToJSON()
        {
            JSONClass o = new JSONClass();
            o.Add("enabled", new JSONData(enabled ? "true" : "false"));
            o.Add("name", new JSONData(name));
            if (packagePattern != null) o.Add("package", new JSONData(packagePattern));
            if (scriptPattern != null) o.Add("script", new JSONData(scriptPattern));

            string act = "block";
            if (action == ActionRedirect) act = "redirect";
            else if (action == ActionAllow) act = "allow";
            o.Add("action", new JSONData(act));

            if (action == ActionRedirect && !string.IsNullOrEmpty(to)) o.Add("to", new JSONData(to));
            return o;
        }
    }
}
