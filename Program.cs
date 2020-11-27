// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

#nullable enable

namespace TypeDependencyHelper
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length != 1 && args.Length != 2)
            {
                Console.WriteLine(HelpMessage);
                return;
            }

            string? projectPath = args[0];

            if (!File.Exists(projectPath))
            {
                Console.WriteLine("The project file does not exist: {0}", projectPath);
                return;
            }

            LocateMSBuild();

            using var workspace = MSBuildWorkspace.Create();

            // Print message for WorkspaceFailed event to help diagnosing project load failures.
            workspace.WorkspaceFailed += (o, e) => Console.WriteLine(e.Diagnostic.Message);

            Console.WriteLine($"Loading project '{projectPath}'");

            // Attach progress reporter so we print projects as they are loaded.
            var project = await workspace.OpenProjectAsync(projectPath, new ConsoleProgressReporter());
            Console.WriteLine($"Finished loading project '{projectPath}'");

            Console.WriteLine($"Documents count = {project.DocumentIds.Count}");

            Console.WriteLine($"Loading namespaces");
            var namespaces = project.Documents
                    .SelectMany(document =>
                        document.GetSyntaxRootAsync()
                        .Result
                            .DescendantNodes()
                            .OfType<NamespaceDeclarationSyntax>()
                            .Select(ns => ns.Name.ToString())
                    ).Distinct().ToHashSet(StringComparer.Ordinal);

            Console.WriteLine($"Loading types");
            var declaredTypes = project.Documents
                    .Select(document =>
                        new
                        {
                            Model = document.GetSemanticModelAsync().Result,
                            Declarations = document.GetSyntaxRootAsync()
                                                    .Result
                                                    .DescendantNodes()
                                                    .OfType<BaseTypeDeclarationSyntax>()
                                                    .Where(t => !t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.EnumDeclaration))
                        }
                    )
                    .SelectMany(pair => pair.Declarations.Select(declaration =>
                        new
                        {
                            Pair = pair,
                            Declaration = declaration,
                            DeclaredType = pair.Model.GetDeclaredSymbol(declaration),
                            Dependencies = declaration.DescendantNodes().OfType<IdentifierNameSyntax>(),
                        }
                    ));

            Console.WriteLine($"Declared type count = {declaredTypes.Count()}");

            List<(string TypeKind, string Accessibility, string ContainingNamespace, string Name, HashSet<string> DepTypeNames, int Count, string FilePath)> result = new();

            Console.WriteLine($"Evaluating type dependencies");
            foreach (var typeTuple in declaredTypes)
            {
                HashSet<string> deps = new(StringComparer.Ordinal);

                if (typeTuple.Dependencies is not null)
                {
                    foreach (var identifierName in typeTuple.Dependencies)
                    {
                        var parType = typeTuple.Pair.Model.GetTypeInfo(identifierName);
                        if (parType.Type is not null
                            && parType.Type.Kind != SymbolKind.TypeParameter
                            && parType.Type.ContainingNamespace is not null
                            && namespaces.TryGetValue(parType.Type.ContainingNamespace.ToString()!, out string? _))
                        {
                            var depTypeName = parType.Type.ToString()!;
                            deps.Add(depTypeName);
                        }
                    }
                }

                var t = typeTuple.DeclaredType;
                result.Add((
                    t.TypeKind.ToString(),
                    typeTuple.DeclaredType.DeclaredAccessibility.ToString(),
                    t.ContainingNamespace.ToString()!,
                    t.Name,
                    deps,
                    deps.Count,
                    typeTuple.Declaration.SyntaxTree.FilePath));
            }

            string outfilePath;
            if (args.Length == 2)
            {
                if (Directory.Exists(args[1]))
                {
                    // Second argument is an output directory.
                    outfilePath = Path.Join(args[1], Path.GetFileNameWithoutExtension(projectPath) + "-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".csv");
                }
                else
                {
                    // Second argument is an output file name.
                    outfilePath = args[1];
                }
            }
            else
            {
                // No second argument so output to current directory.
                outfilePath = Path.GetFileNameWithoutExtension(projectPath) + "-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".csv";
            }

            using var outputFile = new StreamWriter(outfilePath);

            Console.WriteLine($"Writing to file: {outfilePath}");

            // Output header.
            // 'Status' is a empty column here.
            // After importing the output file in Excel we can use it for tracking nullable annotation process.
            // Possible values: Exclude, InProgress, Done.
            var d = '\t';
            var header = $"Status{d}DependencyCount{d}TypeKind{d}Accessibility{d}ContainingNamespace{d}Name{d}FilePath{d}Dependencies";
            outputFile.WriteLine(header);

            // Types without dependencies on top.
            var sortedResult = result.OrderBy(o => o.Count).ThenBy(o => o.Name);

            // Remove common prefix (project directory) and get relative paths to documents.
            var rootLength = (Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty).Length;
            foreach (var typeInfo in sortedResult)
            {
                var s = $"{d}{typeInfo.DepTypeNames.Count}{d}{typeInfo.TypeKind}{d}{typeInfo.Accessibility}{d}{typeInfo.ContainingNamespace}{d}{typeInfo.Name}{d}{typeInfo.FilePath.Substring(rootLength)}{d}{typeInfo.DepTypeNames.Aggregate("", (a, b) => a + "," + b)}";
                outputFile.WriteLine(s);
            }
        }

        private static void LocateMSBuild()
        {
            // Attempt to set the version of MSBuild.
            var visualStudioInstances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            var instance = visualStudioInstances.Length == 1
                // If there is only one instance of MSBuild on this machine, set that as the one to use.
                ? visualStudioInstances[0]
                // Handle selecting the version of MSBuild you want to use.
                : SelectVisualStudioInstance(visualStudioInstances);

            Console.WriteLine($"Using MSBuild at '{instance.MSBuildPath}' to load projects.");

            // NOTE: Be sure to register an instance with the MSBuildLocator
            //       before calling MSBuildWorkspace.Create()
            //       otherwise, MSBuildWorkspace won't MEF compose.
            MSBuildLocator.RegisterInstance(instance);
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (assemblyLoadContext, assemblyName) =>
            {
                var path = Path.Combine(instance.MSBuildPath, assemblyName.Name + ".dll");
                if (File.Exists(path))
                {
                    return assemblyLoadContext.LoadFromAssemblyPath(path);
                }

                return null;
            };
        }
        private static VisualStudioInstance SelectVisualStudioInstance(VisualStudioInstance[] visualStudioInstances)
        {
            Console.WriteLine("Multiple installs of MSBuild detected please select one:");
            for (int i = 0; i < visualStudioInstances.Length; i++)
            {
                Console.WriteLine($"Instance {i + 1}");
                Console.WriteLine($"    Name: {visualStudioInstances[i].Name}");
                Console.WriteLine($"    Version: {visualStudioInstances[i].Version}");
                Console.WriteLine($"    MSBuild Path: {visualStudioInstances[i].MSBuildPath}");
            }

            while (true)
            {
                var userResponse = Console.ReadLine();
                if (int.TryParse(userResponse, out int instanceNumber) &&
                    instanceNumber > 0 &&
                    instanceNumber <= visualStudioInstances.Length)
                {
                    return visualStudioInstances[instanceNumber - 1];
                }
                Console.WriteLine("Input not accepted, try again.");
            }
        }

        private class ConsoleProgressReporter : IProgress<ProjectLoadProgress>
        {
            public void Report(ProjectLoadProgress loadProgress)
            {
                var projectDisplay = Path.GetFileName(loadProgress.FilePath);
                if (loadProgress.TargetFramework != null)
                {
                    projectDisplay += $" ({loadProgress.TargetFramework})";
                }

                Console.WriteLine($"{loadProgress.Operation,-15} {loadProgress.ElapsedTime,-15:m\\:ss\\.fffffff} {projectDisplay}");
            }
        }

        private const string HelpMessage = @"
Collect an information about dependencies for custom types declared in C# project.
This information is for planning nullable annotations.

TypeDependencyHelper inputpath [outputpath]

    inputpath
            Path to a csproj project file.
    outputpath
            Optional. Path to a csv output file.
        ";
    }
}
