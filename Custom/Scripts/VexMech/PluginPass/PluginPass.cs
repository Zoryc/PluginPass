//
// PluginPass - a Virt-A-Mate session plugin that decides which plugins a scene is
// allowed to load, and redirects plugin references to your own builds without
// editing a single scene file or .var package.
//
// Rules are made by pointing at a plugin that is already loaded and picking what to
// do with it. The JSON file underneath is still there for power users, but you never
// have to touch it.
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
// That is a hard ceiling of the plugin sandbox: true interception needs a BepInEx
// plugin, which is a compiled DLL loaded outside it.
//
// So this works after the fact: when a scene finishes loading, matching plugins are
// removed or re-pointed. The blocked plugin is compiled once before it is dropped,
// but its ongoing cost - Update, coroutines, physics callbacks - is gone.
//
// -------------------------------------------------------------------------------
// House rules for editing this file (see docs/VAM_PLUGIN_API.md)
// -------------------------------------------------------------------------------
//   * C# 6 only.
//   * No System.Reflection, no System.IO, no 'out' parameters (OutAttribute).
//   * No nested types. That means no nested class/enum, no iterators, and no
//     delegate that captures a LOCAL - capturing only 'this' is fine and compiles
//     to a plain method, capturing a local creates a hidden nested class.
//   * Verify with build/verify.ps1 before loading in VaM.
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
        public const string Version = "3.0.0";

        private const string RulesDir = "Saves/PluginData/PluginPass";
        private const string RulesFile = RulesDir + "/rules.json";
        private const int MaxActivityLines = 60;

        // Self-guard. Without reflection there is no way to ask "which MVRPlugin am
        // I?", so PluginPass excludes itself by script name instead.
        private const string SelfScriptName = "PluginPass.cs";

        // Scope choices, used as both value and label.
        private const string ScopeAllVersions = "Every version of this package";
        private const string ScopeThisVersion = "This version only";
        private const string ScopeThisScript = "This script file, any package";
        private const string ScopeExactUrl = "This exact url";

        private const string ActBlock = "Block it";
        private const string ActRedirect = "Redirect it to my script";
        private const string ActAllow = "Allow (exception to a broader rule)";

        // Returned by Evaluate/Resolve to mean "leave this plugin exactly as it is".
        // A dedicated instance so it can be compared with ReferenceEquals and never
        // collides with a real url.
        private static readonly string Pass = new string('\0', 1);

        private PluginPassRule[] _rules = new PluginPassRule[0];

        // url -> Pass sentinel / null (block) / replacement url.
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _warnedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string _rulesError = "";
        private int _blockedTotal;
        private int _redirectedTotal;

        private readonly List<string> _activity = new List<string>();
        private bool _activityDirty;

        // Reused across the sweep so a scene load does not allocate a pair of lists
        // per atom.
        private readonly List<string> _uidBuf = new List<string>();
        private readonly List<string> _urlBuf = new List<string>();

        // Picker data, filled by the same sweep that applies the rules.
        private readonly SortedDictionary<string, int> _pickCount =
            new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pickIsScene =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _atomsVisited;
        private int _managersRead;

        // settings
        private JSONStorableBool _enabledJSON;
        private JSONStorableBool _scenePluginsJSON;
        private JSONStorableBool _keepOriginalJSON;
        private JSONStorableBool _logJSON;

        // rule builder
        private JSONStorableStringChooser _pluginPickJSON;
        private JSONStorableStringChooser _scopeJSON;
        private JSONStorableStringChooser _actionJSON;
        private JSONStorableUrl _targetJSON;
        private JSONStorableString _previewJSON;

        // rule list
        private JSONStorableStringChooser _ruleListJSON;

        private JSONStorableString _statusJSON;
        private JSONStorableString _activityJSON;
        private JSONStorableString _rulesTextJSON;

        // ============================================================================
        // lifecycle
        // ============================================================================

        public override void Init()
        {
            try
            {
                BuildUI();
                LoadRulesFromFile();
                SweepScene(false);

                SuperController sc = SuperController.singleton;
                if (sc != null)
                {
                    sc.onSceneLoadedHandlers += OnSceneLoadedHandler;
                    sc.onSubSceneLoadedHandlers += OnSubSceneLoadedHandler;
                }

                RefreshStatus();
            }
            catch (Exception e)
            {
                SuperController.LogError("PluginPass: Init failed: " + e);
            }
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
            SweepScene(true);
            RefreshStatus();
        }

        private void OnSubSceneLoadedHandler(SubScene subScene)
        {
            SweepScene(true);
            RefreshStatus();
        }

        // ============================================================================
        // engine
        // ============================================================================

        /// <summary>
        /// One pass over the scene. Reads each atom's plugin list exactly once and uses
        /// it for both jobs - applying the rules and filling the rule-builder picker -
        /// because doing those in two passes meant reading every atom twice.
        ///
        /// Cost per scene load: one dictionary lookup per atom, and one GetJSON only
        /// for the atoms that actually have a plugin manager. Nothing runs per frame.
        /// </summary>
        private void SweepScene(bool applyRules)
        {
            SuperController sc = SuperController.singleton;
            if (sc == null) return;

            _pickCount.Clear();
            _pickIsScene.Clear();
            _atomsVisited = 0;
            _managersRead = 0;

            bool doApply = applyRules && _enabledJSON != null && _enabledJSON.val && _rules.Length > 0;

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
            if (isScenePlugins && (_scenePluginsJSON == null || !_scenePluginsJSON.val)) return;

            _atomsVisited++;

            MVRPluginManager mgr = FindPluginManager(atom);
            if (mgr == null) return;

            // Session plugins are NOT on an atom - they live on their own
            // MVRPluginManager, which is why loading a scene does not overwrite them.
            // This is belt and braces for the case where PluginPass is itself the
            // session plugin: never touch the manager hosting us.
            if (containingAtom == null && ReferenceEquals(mgr, manager)) return;

            _uidBuf.Clear();
            _urlBuf.Clear();
            if (!CollectPlugins(mgr, _uidBuf, _urlBuf)) return;
            _managersRead++;

            for (int i = 0; i < _urlBuf.Count; i++)
            {
                string u = _urlBuf[i];
                if (string.Equals(FileNameOf(PathOf(u)), SelfScriptName, StringComparison.OrdinalIgnoreCase))
                    continue;
                int n;
                _pickCount[u] = _pickCount.TryGetValue(u, out n) ? n + 1 : 1;
                if (isScenePlugins) _pickIsScene.Add(u);
            }

            if (doApply) ApplyRules(mgr, atom.uid, _uidBuf, _urlBuf);
        }

        private void ApplyRules(MVRPluginManager mgr, string where, List<string> uids, List<string> urls)
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
                    Record("FAILED    " + where + " / " + url);
                    continue;
                }

                created.pluginURLJSON.val = result;
                _redirectedTotal++;
                Record("redirect  " + where + " / " + Short(url) + "  ->  " + Short(result));

                // Only possible when the atom's plugin slots had gaps in them.
                if (created.uid != uid)
                    Record("  note: slot " + uid + " became " + created.uid +
                           " - triggers aimed at the old slot will not bind");
            }

            for (int i = 0; i < blockUids.Count; i++)
            {
                mgr.RemovePluginWithUID(blockUids[i]);
                _blockedTotal++;
                Record("blocked   " + where + " / " + Short(blockUrls[i]));
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
            if (string.Equals(file, SelfScriptName, StringComparison.OrdinalIgnoreCase)) return Pass;

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
                    WarnOnce(url, "rule '" + r.name + "' is a redirect with no 'to' target");
                    return Pass;
                }
                if (string.Equals(target, url, StringComparison.OrdinalIgnoreCase)) return Pass;

                if (!TargetExists(target))
                {
                    WarnOnce(target, "redirect target not found: " + target + " (rule '" + r.name + "')");
                    if (_keepOriginalJSON.val)
                    {
                        Record("missing   " + Short(target) + "  ->  kept the original");
                        return Pass;
                    }
                }
                return target;
            }

            return Pass;
        }

        private bool TargetExists(string target)
        {
            try { return FileManagerSecure.FileExists(target, false); }
            catch { return true; }   // never let a file-system hiccup change behaviour
        }

        private void WarnOnce(string key, string message)
        {
            if (!_warnedTargets.Add(key)) return;
            SuperController.LogError("PluginPass: " + message);
        }

        // Deliberately no 'out' parameters anywhere in this file: C# compiles an out
        // parameter with a System.Runtime.InteropServices.OutAttribute, and VaM's
        // sandbox rejects that whole namespace apart from GuidAttribute and
        // ComVisibleAttribute. Splitting a url is done with an index instead.
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

        /// <summary>A url trimmed to "Package / Script.cs" for the activity log.</summary>
        private static string Short(string url)
        {
            string pkg = PackageOf(url);
            string file = FileNameOf(PathOf(url));
            return pkg.Length > 0 ? GroupId(pkg) + " / " + file : file;
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

        /// <summary>
        /// Fills the plugin picker with everything loaded right now, so a rule is made
        /// by choosing from a list instead of typing a package id correctly.
        /// </summary>
        /// <summary>
        /// Fills the picker from what the sweep just found. It lists exactly what the
        /// engine acts on - atom plugins, and scene plugins when that is switched on -
        /// and deliberately NOT session plugins, because no rule can ever affect those.
        /// Offering them would let you build a rule that silently never fires.
        /// </summary>
        private void RebuildPicker()
        {
            if (_pluginPickJSON == null) return;

            List<string> values = new List<string>();
            List<string> labels = new List<string>();

            foreach (KeyValuePair<string, int> kv in _pickCount)
            {
                string url = kv.Key;
                values.Add(url);

                string pkg = PackageOf(url);
                StringBuilder sb = new StringBuilder();
                sb.Append(pkg.Length > 0 ? GroupId(pkg) : "(loose script)");
                sb.Append("   ").Append(FileNameOf(PathOf(url)));
                if (_pickIsScene.Contains(url)) sb.Append("   [scene]");
                if (kv.Value > 1) sb.Append("   x").Append(kv.Value).Append(" atoms");
                labels.Add(sb.ToString());
            }

            if (values.Count == 0)
            {
                values.Add("");
                labels.Add("(no atom plugins found - load a scene, then Rescan)");
            }

            _pluginPickJSON.choices = values;
            _pluginPickJSON.displayChoices = labels;
            if (!values.Contains(_pluginPickJSON.val)) _pluginPickJSON.valNoCallback = values[0];

            RefreshPreview();
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
                r.to = _targetJSON.val;
            }
            else if (act == ActAllow) r.action = PluginPassRule.ActionAllow;
            else r.action = PluginPassRule.ActionBlock;

            r.enabled = true;
            r.name = DescribeRule(r);
            return r;
        }

        private static string DescribeRule(PluginPassRule r)
        {
            StringBuilder sb = new StringBuilder();
            if (r.action == PluginPassRule.ActionBlock) sb.Append("block ");
            else if (r.action == PluginPassRule.ActionAllow) sb.Append("allow ");
            else sb.Append("redirect ");

            if (r.packagePattern != null) sb.Append(r.packagePattern);
            if (r.packagePattern != null && r.scriptPattern != null) sb.Append(" / ");
            if (r.scriptPattern != null) sb.Append(FileNameOf(r.scriptPattern));

            if (r.action == PluginPassRule.ActionRedirect && !string.IsNullOrEmpty(r.to))
                sb.Append("  ->  ").Append(Short(r.to));

            return sb.ToString();
        }

        private void RefreshPreview()
        {
            if (_previewJSON == null) return;

            StringBuilder sb = new StringBuilder();
            PluginPassRule r = BuildRuleFromPicker();

            if (r == null)
            {
                sb.Append("Pick a plugin above.\n\nPress <b>Rescan</b> after loading a scene.");
            }
            else
            {
                sb.Append("<b>This rule will say:</b>\n\n");
                sb.Append(DescribeRule(r)).Append("\n\n");

                if (r.packagePattern == null && r.scriptPattern == null)
                    sb.Append("<color=#b71c1c>That would match every plugin - not allowed.</color>\n");
                else if (r.action == PluginPassRule.ActionRedirect && string.IsNullOrEmpty(r.to))
                    sb.Append("<color=#b71c1c>Choose a replacement script first.</color>\n");
                else
                {
                    int hits = CountMatches(r);
                    sb.Append("Matches <b>").Append(hits).Append("</b> plugin(s) in the scene right now.");
                }
            }

            _previewJSON.val = sb.ToString();
        }

        /// <summary>How many currently-loaded plugins a candidate rule would hit.</summary>
        private int CountMatches(PluginPassRule r)
        {
            int hits = 0;
            List<string> urls = _pluginPickJSON.choices;
            if (urls == null) return 0;

            for (int i = 0; i < urls.Count; i++)
            {
                string url = urls[i];
                if (string.IsNullOrEmpty(url)) continue;
                string pkg = PackageOf(url);
                string path = PathOf(url);
                if (r.MatchesPackage(pkg, GroupId(pkg)) && r.MatchesScript(path, FileNameOf(path))) hits++;
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
            CommitRules(list, "added rule: " + r.name);
        }

        private void DeleteSelectedRule()
        {
            int idx = SelectedRuleIndex();
            if (idx < 0) { Record("no rule selected"); RefreshStatus(); return; }

            List<PluginPassRule> list = new List<PluginPassRule>(_rules);
            string gone = list[idx].name;
            list.RemoveAt(idx);
            CommitRules(list, "deleted rule: " + gone);
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
            if (idx <= 0) { Record("cannot move that rule up"); RefreshStatus(); return; }

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
            _rulesTextJSON.valNoCallback = RulesToText(list);
            SaveRulesToFile();
            Record(message);
            RefreshRuleList();
            RefreshPreview();
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
                labels.Add((i + 1) + ".  " + (_rules[i].enabled ? "[on]  " : "[off] ") + _rules[i].name);
            }

            if (values.Count == 0)
            {
                values.Add("");
                labels.Add("(no rules yet)");
            }

            _ruleListJSON.choices = values;
            _ruleListJSON.displayChoices = labels;
            if (!values.Contains(_ruleListJSON.val)) _ruleListJSON.valNoCallback = values[0];
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

        private void SaveRulesToFile()
        {
            try
            {
                FileManagerSecure.CreateDirectory(RulesDir);
                FileManagerSecure.WriteAllText(RulesFile, _rulesTextJSON.val);
            }
            catch (Exception e)
            {
                SuperController.LogError("PluginPass: could not write " + RulesFile + ": " + e.Message);
                Record("save FAILED: " + e.Message);
            }

            ApplyRulesText(_rulesTextJSON.val);
        }

        private static string RulesToText(List<PluginPassRule> rules)
        {
            JSONClass root = new JSONClass();

            JSONArray readme = new JSONArray();
            readme.Add(new JSONData("Made by the PluginPass UI - you can edit this by hand too."));
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
                        problems.Add("rule " + i + " ('" + r.name + "'): redirect without 'to'");
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
            // ---- left column: make a rule -----------------------------------------

            _statusJSON = new JSONStorableString("status", "");
            CreateTextField(_statusJSON, false).height = 150f;

            UIDynamicButton rescan = CreateButton("1.  Rescan scene for plugins", false);
            rescan.button.onClick.AddListener(delegate { SweepScene(false); RefreshStatus(); });

            _pluginPickJSON = new JSONStorableStringChooser(
                "plugin", new List<string>(), "", "2.  Plugin");
            _pluginPickJSON.setCallbackFunction = delegate (string v) { RefreshPreview(); };
            UIDynamicPopup pick = CreateFilterablePopup(_pluginPickJSON, false);
            pick.popupPanelHeight = 700f;

            List<string> scopes = new List<string>();
            scopes.Add(ScopeAllVersions);
            scopes.Add(ScopeThisVersion);
            scopes.Add(ScopeThisScript);
            scopes.Add(ScopeExactUrl);
            _scopeJSON = new JSONStorableStringChooser("scope", scopes, ScopeAllVersions, "3.  Applies to");
            _scopeJSON.setCallbackFunction = delegate (string v) { RefreshPreview(); };
            CreateScrollablePopup(_scopeJSON, false).popupPanelHeight = 300f;

            List<string> actions = new List<string>();
            actions.Add(ActBlock);
            actions.Add(ActRedirect);
            actions.Add(ActAllow);
            _actionJSON = new JSONStorableStringChooser("action", actions, ActBlock, "4.  Do what");
            _actionJSON.setCallbackFunction = delegate (string v) { RefreshPreview(); };
            CreateScrollablePopup(_actionJSON, false).popupPanelHeight = 260f;

            // VaM's own file browser, filtered to scripts under Custom/Scripts.
            _targetJSON = new JSONStorableUrl("target", "", "cs|cslist|dll", "Custom/Scripts");
            _targetJSON.setCallbackFunction = delegate (string v) { RefreshPreview(); };
            UIDynamicButton browse = CreateButton("5.  Choose my replacement script...", false);
            _targetJSON.RegisterFileBrowseButton(browse.button, false);

            _previewJSON = new JSONStorableString("preview", "");
            CreateTextField(_previewJSON, false).height = 230f;

            UIDynamicButton add = CreateButton("6.  Add this rule", false);
            add.button.onClick.AddListener(delegate { AddRuleFromBuilder(); });
            add.buttonColor = new Color(0.6f, 0.85f, 0.6f);

            // ---- right column: rules and settings ---------------------------------

            _ruleListJSON = new JSONStorableStringChooser(
                "ruleList", new List<string>(), "", "Rules (first match wins)");
            CreateFilterablePopup(_ruleListJSON, true).popupPanelHeight = 600f;

            UIDynamicButton toggle = CreateButton("Enable / disable selected", true);
            toggle.button.onClick.AddListener(delegate { ToggleSelectedRule(); });

            UIDynamicButton up = CreateButton("Move selected up", true);
            up.button.onClick.AddListener(delegate { MoveSelectedRuleUp(); });

            UIDynamicButton del = CreateButton("Delete selected", true);
            del.button.onClick.AddListener(delegate { DeleteSelectedRule(); });
            del.buttonColor = new Color(0.9f, 0.6f, 0.6f);

            CreateSpacer(true).height = 10f;

            UIDynamicButton apply = CreateButton("Apply rules to the scene now", true);
            apply.button.onClick.AddListener(delegate { SweepScene(true); RefreshStatus(); });

            _enabledJSON = new JSONStorableBool("enabled", true);
            _enabledJSON.setCallbackFunction = delegate { RefreshStatus(); };
            RegisterBool(_enabledJSON);
            CreateToggle(_enabledJSON, true).label = "PluginPass enabled";

            // Session plugins are never touched - they are not on an atom at all, so
            // the sweep cannot reach them. This toggle only covers the third category:
            // scene-level plugins, which live on the CoreControl atom.
            _scenePluginsJSON = new JSONStorableBool("applyToScenePlugins", true);
            _scenePluginsJSON.setCallbackFunction = delegate { SweepScene(false); RefreshStatus(); };
            RegisterBool(_scenePluginsJSON);
            CreateToggle(_scenePluginsJSON, true).label = "Also apply to scene plugins (CoreControl)";

            _keepOriginalJSON = new JSONStorableBool("keepOriginalIfTargetMissing", true);
            _keepOriginalJSON.setCallbackFunction = delegate { _cache.Clear(); _warnedTargets.Clear(); };
            RegisterBool(_keepOriginalJSON);
            CreateToggle(_keepOriginalJSON, true).label = "Keep original if redirect target is missing";

            _logJSON = new JSONStorableBool("logToConsole", false);
            RegisterBool(_logJSON);
            CreateToggle(_logJSON, true).label = "Log every action to the message log";

            _activityJSON = new JSONStorableString("activity", "");
            CreateTextField(_activityJSON, true).height = 300f;

            // ---- advanced: the file underneath ------------------------------------

            JSONStorableString title = new JSONStorableString("rulesTitle",
                "<b>Advanced</b> - " + RulesFile + "\nEdit by hand if you like, then Save.");
            CreateTextField(title, true).height = 80f;

            _rulesTextJSON = new JSONStorableString("rulesText", "");
            _rulesTextJSON.setCallbackFunction = delegate (string v) { ApplyRulesText(v); RefreshRuleList(); };
            UIDynamicTextField rulesField = CreateTextField(_rulesTextJSON, true);
            MakeEditable(_rulesTextJSON, rulesField, 500f);

            UIDynamicButton save = CreateButton("Save rules file", true);
            save.button.onClick.AddListener(delegate { SaveRulesToFile(); RefreshRuleList(); RefreshStatus(); });

            UIDynamicButton reload = CreateButton("Reload rules file", true);
            reload.button.onClick.AddListener(delegate { LoadRulesFromFile(); RefreshStatus(); });
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
            _activityDirty = true;

            if (_logJSON != null && _logJSON.val) SuperController.LogMessage("PluginPass: " + line);
        }

        private void RefreshStatus()
        {
            if (_statusJSON == null) return;

            StringBuilder sb = new StringBuilder();
            sb.Append("<b>PluginPass ").Append(Version).Append("</b>   ");
            if (!_enabledJSON.val) sb.Append("<color=#b71c1c>OFF</color>");
            else sb.Append("<color=#1b5e20>on</color>");
            sb.Append('\n');

            int active = 0;
            for (int i = 0; i < _rules.Length; i++) if (_rules[i].enabled) active++;
            sb.Append(active).Append(" active rule(s) of ").Append(_rules.Length).Append('\n');
            sb.Append("This session: ").Append(_blockedTotal).Append(" blocked, ")
              .Append(_redirectedTotal).Append(" redirected\n");
            sb.Append("<size=24>Scope: atom plugins")
              .Append(_scenePluginsJSON != null && _scenePluginsJSON.val ? " + scene plugins" : "")
              .Append(", never session plugins.\n");
            sb.Append("Last sweep: read ").Append(_managersRead).Append(" plugin list(s) across ")
              .Append(_atomsVisited).Append(" atom(s).</size>");

            if (_rulesError.Length > 0)
                sb.Append("\n<color=#b71c1c>").Append(_rulesError).Append("</color>");

            _statusJSON.val = sb.ToString();

            if (_activityDirty) RefreshActivity();
        }

        private void RefreshActivity()
        {
            _activityDirty = false;
            if (_activityJSON == null) return;

            StringBuilder sb = new StringBuilder();
            sb.Append("<b>Activity</b>  (newest first)\n\n");
            for (int i = _activity.Count - 1; i >= 0; i--)
                sb.Append(_activity[i]).Append('\n');

            _activityJSON.val = sb.ToString();
        }
    }

    /// <summary>
    /// One rule from rules.json: what to match, and what to do about it.
    ///
    /// Top-level rather than nested inside PluginPass, and the action is an int rather
    /// than an enum. Both deliberate: VaM's runtime compiler can fault inside
    /// TypeBuilder.CreateType() while closing a nested type container, so this file
    /// keeps zero nested types.
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
