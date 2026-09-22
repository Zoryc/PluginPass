using System;
using System.Collections.Generic;
using System.Text;
using MVR.FileManagementSecure;
using SimpleJSON;

namespace PluginPassEngine
{
    /// <summary>
    /// The rule model and matching engine, ported line-for-line from the in-game
    /// script (Custom/Scripts/VexMech/PluginPass/PluginPass.cs).
    ///
    /// The two implementations MUST agree: a rule has to mean the same thing whether
    /// the BepInEx engine applies it before the load or the session plugin applies it
    /// after. Every helper below is a direct port - same signature, same edge cases,
    /// same comments where they explain a trap. If you change one, change both.
    ///
    /// `.latest` resolution deliberately goes through FileManagerSecure rather than
    /// FileManager, even though nothing out here is sandboxed: it is the same call the
    /// script makes, so the two cannot drift on which version ".latest" means.
    /// </summary>
    internal static class RuleSet
    {
        internal const int ActionBlock = 0;
        internal const int ActionRedirect = 1;
        internal const int ActionAllow = 2;

        // Evaluate() outcomes.
        internal const int OutcomeKeep = 0;
        internal const int OutcomeBlock = 1;
        internal const int OutcomeSwap = 2;

        private const string SelfScriptBaseName = "PluginPass";

        internal class Rule
        {
            public bool enabled = true;
            public string name = "";
            public int action = ActionBlock;

            // null means "this facet is not part of the match"
            public string packagePattern;
            public string scriptPattern;
            public bool scriptIsPath;
            public string to;

            public bool MatchesPackage(string pkg, string group)
            {
                if (packagePattern == null) return true;
                if (packagePattern == "*") return true;
                if (pkg.Length == 0) return false;      // loose script, rule wants a package
                return Glob(pkg, packagePattern) || Glob(group, packagePattern);
            }

            public bool MatchesScript(string path, string file)
            {
                if (scriptPattern == null) return true;
                return Glob(scriptIsPath ? path : file, scriptPattern);
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
        }

        // ------------------------------------------------------------------
        // parsing
        // ------------------------------------------------------------------

        /// <summary>
        /// Parses the rules.json text. Problems are appended to <paramref name="problems"/>
        /// and the offending rule is skipped, exactly as the script does - a bad rule
        /// must never become a permissive one.
        /// </summary>
        internal static Rule[] Parse(string text, List<string> problems)
        {
            List<Rule> parsed = new List<Rule>();
            if (string.IsNullOrEmpty(text)) return parsed.ToArray();

            JSONNode root = JSON.Parse(text);
            if (root == null) { problems.Add("rules file is not valid JSON"); return parsed.ToArray(); }

            // Both shapes, because the in-game script accepts both. A hand-edited file
            // that one half reads and the other rejects would leave rules.json looking
            // fine while nothing at all was being applied.
            JSONArray arr = root.AsArray;                       // bare top-level array
            if (arr == null)
            {
                JSONNode rulesNode = root["rules"];
                arr = rulesNode == null ? null : rulesNode.AsArray;
            }
            if (arr == null) { problems.Add("rules file has no 'rules' array"); return parsed.ToArray(); }

            for (int i = 0; i < arr.Count; i++)
            {
                JSONClass o = arr[i].AsObject;
                if (o == null) { problems.Add("rule " + i + ": not an object"); continue; }

                Rule r = new Rule();
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
                if (act == "redirect" || act == "swap") r.action = ActionRedirect;
                else if (act == "allow") r.action = ActionAllow;
                else if (act == "block") r.action = ActionBlock;
                else { problems.Add("rule " + i + " ('" + r.name + "'): unknown action '" + act + "'"); continue; }

                r.to = o.HasKey("to") ? o["to"].Value.Trim()
                     : (o.HasKey("with") ? o["with"].Value.Trim() : null);

                // A rule that matches on nothing would swallow every plugin in the game.
                if (r.packagePattern == null && r.scriptPattern == null)
                {
                    problems.Add("rule " + i + " ('" + r.name + "'): needs 'package' and/or 'script'");
                    continue;
                }
                if (r.action == ActionRedirect && string.IsNullOrEmpty(r.to))
                {
                    problems.Add("rule " + i + " ('" + r.name + "'): replaces but has no 'to'");
                    continue;
                }

                parsed.Add(r);
            }

            return parsed.ToArray();
        }

        // ------------------------------------------------------------------
        // evaluation
        // ------------------------------------------------------------------

        /// <summary>
        /// Decides what happens to one plugin url. Returns one of the Outcome
        /// constants; <paramref name="result"/> carries the pinned replacement url
        /// when the outcome is OutcomeSwap, and the rule that fired (or null) comes
        /// back in <paramref name="firedRule"/> for logging.
        /// </summary>
        internal static int Evaluate(Rule[] rules, string url, bool keepOriginalIfMissing,
                                     out string result, out Rule firedRule)
        {
            result = url;
            firedRule = null;
            if (rules == null || rules.Length == 0) return OutcomeKeep;

            string pkg = PackageOf(url);
            string path = PathOf(url);
            string file = FileNameOf(path);

            // Never let PluginPass remove itself - the engine can reach the session
            // plugin list through a preset restore, and a self-block there would be
            // unrecoverable without editing files by hand.
            if (IsSelfUrl(url)) return OutcomeKeep;

            string group = GroupId(pkg);

            for (int i = 0; i < rules.Length; i++)
            {
                Rule r = rules[i];
                if (!r.enabled) continue;
                if (!r.MatchesPackage(pkg, group)) continue;
                if (!r.MatchesScript(path, file)) continue;

                firedRule = r;
                if (r.action == ActionAllow) return OutcomeKeep;
                if (r.action == ActionBlock) return OutcomeBlock;

                string target = r.BuildTarget(pkg, path, file);
                if (string.IsNullOrEmpty(target))
                {
                    Log.Warn("rule '" + r.name + "' replaces but has no target - kept " + url);
                    return OutcomeKeep;
                }

                // VaM cannot load a ".latest" plugin url: FileManager.NormalizeLoadPath
                // only expands "SELF:/" and relative paths, so the literal string reaches
                // FileManager.FileExists - a dictionary lookup keyed on concrete versions -
                // and VaM logs "Plugin file ... does not exist". Resolve it before writing
                // it anywhere. The RULE keeps ".latest", so it still follows new versions;
                // only what gets applied is pinned.
                string resolved = PinPackageVersion(target);

                if (string.Equals(resolved, url, StringComparison.OrdinalIgnoreCase)) return OutcomeKeep;

                if (!TargetExists(resolved))
                {
                    string detail = string.Equals(resolved, target, StringComparison.Ordinal)
                        ? resolved
                        : target + " (resolved to " + resolved + ")";
                    Log.Warn("replacement not found: " + detail + " (rule '" + r.name + "')");
                    if (keepOriginalIfMissing) return OutcomeKeep;
                }

                result = resolved;
                return OutcomeSwap;
            }

            return OutcomeKeep;
        }

        // ------------------------------------------------------------------
        // url helpers - direct ports
        // ------------------------------------------------------------------

        private static readonly Dictionary<string, bool> _existsCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cleared whenever the rules are reloaded, so a newly built fork is seen.</summary>
        internal static void ClearCaches() { _existsCache.Clear(); }

        private static bool TargetExists(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;

            bool known;
            if (_existsCache.TryGetValue(target, out known)) return known;

            bool exists;
            try { exists = FileManagerSecure.FileExists(PinPackageVersion(target), false); }
            catch { exists = true; }   // never let a file-system hiccup change behaviour

            _existsCache[target] = exists;
            return exists;
        }

        internal static string PinPackageVersion(string url)
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
                if (!FileManagerSecure.PackageExists(pkg)) return url;
                int resolved = FileManagerSecure.GetPackageVersion(pkg);
                // Version 0 is a real, loadable version - it is what a first or
                // development build is numbered.
                if (resolved < 0) return url;
                return pkg.Substring(0, dot) + "." + resolved + ":" + PathOf(url);
            }
            catch { return url; }
        }

        private static int PackageSeparator(string url)
        {
            int i = url.IndexOf(':');
            return i > 1 ? i : -1;          // i == 1 would be a drive letter
        }

        internal static string PackageOf(string url)
        {
            int i = PackageSeparator(url);
            return i < 0 ? string.Empty : url.Substring(0, i);
        }

        internal static string PathOf(string url)
        {
            int i = PackageSeparator(url);
            return i < 0 ? url : url.Substring(i + 1);
        }

        internal static string FileNameOf(string path)
        {
            int slash = path.LastIndexOfAny(new char[] { '/', '\\' });
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        internal static string GroupId(string pkg)
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

        private static string StripExtension(string file)
        {
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }

        internal static bool IsSelfUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return string.Equals(StripExtension(FileNameOf(PathOf(url))),
                                 SelfScriptBaseName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Friendly label for logs: "AcidBubbles.Embody   Embody.cslist".</summary>
        internal static string FriendlyName(string url)
        {
            if (string.IsNullOrEmpty(url)) return "(none)";
            string pkg = PackageOf(url);
            string file = FileNameOf(PathOf(url));
            if (pkg.Length == 0) return file;
            return GroupId(pkg) + " " + file;
        }

        private static bool CharEq(char a, char b)
        {
            if (a == b) return true;
            if (a == '\\') a = '/';
            if (b == '\\') b = '/';
            if (a == b) return true;
            return char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
        }

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
    }
}
