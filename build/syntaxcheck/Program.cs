using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SyntaxCheck
{
    /// <summary>
    /// Checks the plugin sources against the C# dialect VaM can actually compile.
    ///
    /// VaM compiles plugin scripts at runtime with the Mono compiler shipped in
    /// VaM_Data/Managed/mcs.dll, whose Mono.CSharp.LanguageVersion enum stops at
    /// V_6 and defaults to 6. Anything from C# 7 on - tuples, pattern matching,
    /// out-var, local functions - compiles happily in a modern IDE and then fails
    /// inside the game, where the only feedback is a red line in the message log.
    ///
    /// Two checks run:
    ///
    ///   1. Parse errors, straight from the C# 6 syntax tree.
    ///
    ///   2. Language-version errors. Roslyn reports "feature X is not available in
    ///      C# 6" while binding, not while parsing, so a parse alone silently
    ///      accepts C# 7. Instead the sources are bound twice - once as C# 6, once
    ///      as latest - and anything that errors only in the C# 6 pass is, by
    ///      construction, a feature VaM's compiler does not have. Comparing the two
    ///      passes also cancels out the thousands of "type or namespace not found"
    ///      errors for VaM and Unity types, whose assemblies cannot be shipped to
    ///      CI: they are identical in both passes.
    ///
    /// This is therefore a dialect gate, not a full compile. The authoritative
    /// check is still loading the plugin in VaM.
    /// </summary>
    internal static class Program
    {
        private const LanguageVersion VamLanguageVersion = LanguageVersion.CSharp6;

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: syntaxcheck <directory> [<directory> ...]");
                return 2;
            }

            var paths = new List<string>();
            foreach (var dir in args)
            {
                if (!Directory.Exists(dir))
                {
                    Console.Error.WriteLine("syntaxcheck: not a directory: " + dir);
                    return 2;
                }
                paths.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
            }

            paths.Sort(StringComparer.Ordinal);
            if (paths.Count == 0)
            {
                Console.Error.WriteLine("syntaxcheck: no .cs files found under: " + string.Join(", ", args));
                return 2;
            }

            var sources = paths.ToDictionary(p => p, File.ReadAllText);
            var problems = new List<string>();

            // --- 1. parse errors under the VaM dialect --------------------------------
            var vamTrees = new List<SyntaxTree>();
            foreach (var p in paths)
            {
                var tree = CSharpSyntaxTree.ParseText(
                    sources[p], new CSharpParseOptions(VamLanguageVersion), p);
                vamTrees.Add(tree);

                foreach (var d in tree.GetDiagnostics())
                {
                    if (d.Severity == DiagnosticSeverity.Error) problems.Add(Format(d));
                }
            }

            // --- 2. features VaM's compiler does not have -----------------------------
            var latestTrees = paths.Select(p => CSharpSyntaxTree.ParseText(
                sources[p], new CSharpParseOptions(LanguageVersion.Latest), p));

            // Only corlib. Everything else is missing in both passes and cancels out.
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            };

            var asLatest = Bind("latest", latestTrees, references);
            var asVam = Bind("vam", vamTrees, references);

            var latestErrors = new HashSet<string>(asLatest);
            foreach (var e in asVam)
            {
                if (!latestErrors.Contains(e)) problems.Add(e);
            }

            // --- 3. the VaM sandbox rules, as far as syntax can see them -------------
            foreach (var tree in vamTrees) LintSandbox(tree, problems);

            // --- 4. single .cs: the MVRScript class must be the first type -----------
            // VaM loads a lone .cs through DynamicCSharp's MainType, which is
            // Assembly.GetTypes()[0] - the first type declared in the file - and refuses
            // the plugin ("must inherit from MVRScript") when that is a helper. A
            // .cslist plugin is loaded with FindAllSubtypesOf<MVRScript>() instead, so
            // the rule only applies when the tree has no .cslist at all.
            bool hasCslist = args.Any(dir => Directory.GetFiles(dir, "*.cslist", SearchOption.AllDirectories).Length > 0);
            if (!hasCslist)
            {
                foreach (var tree in vamTrees) LintFirstType(tree, problems);
            }

            var distinct = problems.Distinct().ToList();
            if (distinct.Count > 0)
            {
                foreach (var p in distinct) Console.Error.WriteLine(p);
                Console.Error.WriteLine(string.Format(
                    "syntaxcheck: {0} problem(s) - VaM would refuse or crash on this. " +
                    "'sandbox:' lines break its security check or its compiler; the rest is not C# {1}.",
                    distinct.Count, (int)VamLanguageVersion));
                return 1;
            }

            Console.WriteLine(string.Format(
                "syntaxcheck: {0} file(s) OK - valid C# {1}, no restricted namespace, no enum declared{2}",
                paths.Count, (int)VamLanguageVersion, hasCslist ? "" : ", MVRScript declared first"));
            return 0;
        }

        /// <summary>
        /// Namespaces DynamicCSharp refuses to let a plugin reference, matched exactly
        /// (VaM uses String.Compare on TypeReference.Namespace). Read out of
        /// VaM_Data/resources.assets - see the VAM_PLUGIN_API reference, section 1.2.
        /// </summary>
        private static readonly HashSet<string> Restricted = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.IO", "System.Reflection", "System.Runtime",
            "System.Runtime.InteropServices", "System.Security", "System.Configuration",
            "System.Data", "System.Deployment", "System.Dynamic", "System.Management",
            "System.Resources", "System.Net.Mail", "MVR.FileManagement", "DynamicCSharp",
            "Battlehub.RTSaveLoad", "ZenFulcrum.EmbeddedBrowser"
        };

        private static readonly HashSet<string> AllowedDespiteNamespace = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.IO.Stream", "System.IO.IOException",
            "System.Runtime.InteropServices.GuidAttribute",
            "System.Runtime.InteropServices.ComVisibleAttribute"
        };

        /// <summary>
        /// Approximates the two checks that normally need a compiled assembly and a
        /// real VaM install (build/verify.ps1), so CI - which has neither - still
        /// refuses to publish a package that VaM would reject or crash on:
        ///
        ///   * a reference into a restricted namespace, which fails the security check
        ///     at load time (see the VAM_PLUGIN_API reference, 1.2);
        ///   * any enum declaration: VaM's runtime crashes laying out a type that has
        ///     a field of a plugin-defined enum, and no working plugin declares one (1.4).
        ///
        /// 'out' parameters, 'yield' and nested helper types are deliberately NOT
        /// checked: earlier versions flagged them, and the installed corpus proved all
        /// three fine (1.3).
        ///
        /// Syntax-only, so it is a floor rather than a guarantee. verify.ps1 checking
        /// the emitted IL remains the authority.
        /// </summary>
        private static void LintSandbox(SyntaxTree tree, List<string> problems)
        {
            var root = tree.GetRoot();

            foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (u.Name == null) continue;
                var ns = u.Name.ToString();
                if (Restricted.Contains(ns))
                    problems.Add(At(u, "restricted namespace: using " + ns + " - VaM rejects this at load time"));
            }

            // A qualified reference reaches the same types without a using directive.
            // Compare the namespace part exactly: System.Runtime is restricted while
            // System.Runtime.CompilerServices is not, so a prefix test would be wrong.
            foreach (var q in root.DescendantNodes().OfType<QualifiedNameSyntax>())
            {
                // Only the outermost qualified name is a candidate type reference.
                // Its Left child (System.Security inside System.Security.Cryptography.MD5)
                // is a namespace path, and namespace paths are never checked by VaM -
                // treating one as "namespace + type" produced false positives on
                // working plugins that use System.Security.Cryptography.
                if (q.Parent is QualifiedNameSyntax) continue;
                if (q.Ancestors().Any(a => a is UsingDirectiveSyntax || a is NamespaceDeclarationSyntax)) continue;

                var full = q.ToString();
                int dot = full.LastIndexOf('.');
                if (dot <= 0) continue;
                if (!Restricted.Contains(full.Substring(0, dot))) continue;
                if (AllowedDespiteNamespace.Contains(full)) continue;
                problems.Add(At(q, "restricted namespace: " + full));
            }

            // Earlier versions of this tool also rejected 'out' parameters, 'yield',
            // and any nested type. All three turned out to be fine: measured across
            // 3,891 installed plugin files, 112 declare 'out' parameters, 679 use
            // 'yield', and nested helper types are everywhere - and they all load.
            //
            // Enums are the exception, and not only nested ones. VaM's Mono runtime
            // crashes (mono_type_size on a null enum base type, inside
            // TypeBuilder.CreateType) while laying out any type that has a field of an
            // enum defined in the plugin. An earlier version of this check only flagged
            // an enum nested in the MVRScript class; a namespace-level enum declared
            // before the class that used it crashed VaM just the same. Across 7,762
            // installed plugin files no enum is used as a field, so every enum
            // declaration is flagged. Use int constants.
            foreach (var e in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                problems.Add(At(e, "enum '" + e.Identifier + "' - VaM's runtime crashes laying out a type " +
                                  "that has a field of a plugin-defined enum, and no installed plugin uses one; " +
                                  "use int constants"));
            }
        }

        /// <summary>
        /// In a file that declares an MVRScript subclass, the first type declared at
        /// namespace level must be one of them. Files without an MVRScript (pure helper
        /// files) are left alone: with no .cslist they are not plugins anyway.
        /// </summary>
        private static void LintFirstType(SyntaxTree tree, List<string> problems)
        {
            var root = tree.GetRoot();
            var topTypes = root.DescendantNodes(n => !(n is BaseTypeDeclarationSyntax))
                .OfType<MemberDeclarationSyntax>()
                .Where(m => m is BaseTypeDeclarationSyntax || m is DelegateDeclarationSyntax)
                .ToList();
            if (topTypes.Count == 0) return;

            bool declaresScript = topTypes.OfType<ClassDeclarationSyntax>().Any(IsMVRScript);
            if (!declaresScript) return;

            var first = topTypes[0];
            var firstClass = first as ClassDeclarationSyntax;
            if (firstClass != null && IsMVRScript(firstClass)) return;

            string name = first is BaseTypeDeclarationSyntax
                ? ((BaseTypeDeclarationSyntax)first).Identifier.ToString()
                : ((DelegateDeclarationSyntax)first).Identifier.ToString();
            problems.Add(At(first, "'" + name + "' is the first type in the file but the plugin class is not - VaM loads a " +
                                   "single .cs by its first type and refuses it unless that inherits MVRScript; " +
                                   "declare the MVRScript class first, helpers after it or nested"));
        }

        private static bool IsMVRScript(ClassDeclarationSyntax c)
        {
            return c.BaseList != null && c.BaseList.Types.Any(b => b.Type.ToString() == "MVRScript");
        }

        private static string At(SyntaxNode node, string message)
        {
            var pos = node.GetLocation().GetLineSpan();
            return string.Format("{0}({1},{2}): sandbox: {3}",
                pos.Path, pos.StartLinePosition.Line + 1, pos.StartLinePosition.Character + 1, message);
        }

        private static IEnumerable<string> Bind(
            string name, IEnumerable<SyntaxTree> trees, IEnumerable<MetadataReference> references)
        {
            var compilation = CSharpCompilation.Create(name, trees, references);
            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(Format)
                .ToList();
        }

        private static string Format(Diagnostic d)
        {
            var pos = d.Location.GetLineSpan();
            return string.Format(
                "{0}({1},{2}): {3}: {4}",
                pos.Path, pos.StartLinePosition.Line + 1, pos.StartLinePosition.Character + 1,
                d.Id, d.GetMessage());
        }
    }
}
