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
                "syntaxcheck: {0} file(s) OK - valid C# {1}, no restricted namespace, no nested type",
                paths.Count, (int)VamLanguageVersion));
            return 0;
        }

        /// <summary>
        /// Namespaces DynamicCSharp refuses to let a plugin reference, matched exactly
        /// (VaM uses String.Compare on TypeReference.Namespace). Read out of
        /// VaM_Data/resources.assets - see docs/VAM_PLUGIN_API.md section 1.2.
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
        ///     at load time. 'out' parameters count: they compile to an
        ///     OutAttribute in System.Runtime.InteropServices.
        ///   * a nested type, which VaM's runtime compiler can hard-crash closing.
        ///     'yield' counts: an iterator compiles to a nested state machine.
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
                var full = q.ToString();
                int dot = full.LastIndexOf('.');
                if (dot <= 0) continue;
                if (!Restricted.Contains(full.Substring(0, dot))) continue;
                if (AllowedDespiteNamespace.Contains(full)) continue;
                problems.Add(At(q, "restricted namespace: " + full));
            }

            foreach (var p in root.DescendantNodes().OfType<ParameterSyntax>())
            {
                if (p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)))
                    problems.Add(At(p, "'out' parameter '" + p.Identifier +
                                      "' compiles to System.Runtime.InteropServices.OutAttribute, which is restricted"));
            }

            foreach (var t in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (t.Parent is BaseTypeDeclarationSyntax)
                    problems.Add(At(t, "nested type '" + t.Identifier +
                                      "' - VaM's compiler can hard-crash closing a nested type container"));
            }

            foreach (var y in root.DescendantNodes().OfType<YieldStatementSyntax>())
            {
                problems.Add(At(y, "'yield' compiles to a nested state-machine type - use Invoke() instead"));
                break;      // one per file is enough to make the point
            }
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
