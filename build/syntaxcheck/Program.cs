using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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

            if (problems.Count > 0)
            {
                foreach (var p in problems.Distinct()) Console.Error.WriteLine(p);
                Console.Error.WriteLine(string.Format(
                    "syntaxcheck: {0} problem(s) - not valid C# {1}, which is all VaM can compile",
                    problems.Distinct().Count(), (int)VamLanguageVersion));
                return 1;
            }

            Console.WriteLine(string.Format(
                "syntaxcheck: {0} file(s) are valid C# {1} - OK",
                paths.Count, (int)VamLanguageVersion));
            return 0;
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
