﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;
using CS = Microsoft.CodeAnalysis.CSharp;
using VB = Microsoft.CodeAnalysis.VisualBasic;

using static Microsoft.CodeAnalysis.UnitTests.SolutionGeneration;

namespace Microsoft.CodeAnalysis.UnitTests
{
    public class MSBuildWorkspaceTests : MSBuildWorkspaceTestBase
    {
        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestCreateMSBuildWorkspace()
        {
            using (var workspace = CreateMSBuildWorkspace())
            {
                Assert.NotNull(workspace);
                Assert.NotNull(workspace.Services);
                Assert.NotNull(workspace.Services.Workspace);
                Assert.Equal(workspace, workspace.Services.Workspace);
                Assert.NotNull(workspace.Services.HostServices);
                Assert.NotNull(workspace.Services.PersistentStorage);
                Assert.NotNull(workspace.Services.TemporaryStorage);
                Assert.NotNull(workspace.Services.TextFactory);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_SingleProjectSolution()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var document = project.Documents.First();
                var tree = await document.GetSyntaxTreeAsync();
                var type = tree.GetRoot().DescendantTokens().First(t => t.ToString() == "class").Parent;
                Assert.NotNull(type);
                Assert.Equal(true, type.ToString().StartsWith("public class CSharpClass", StringComparison.Ordinal));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_MultiProjectSolution()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var vbProject = solution.Projects.First(p => p.Language == LanguageNames.VisualBasic);

                // verify the dependent project has the correct metadata references (and does not include the output for the project references)
                var references = vbProject.MetadataReferences.ToList();
                Assert.Equal(4, references.Count);
                var fileNames = new HashSet<string>(references.Select(r => Path.GetFileName(((PortableExecutableReference)r).FilePath)));
                Assert.Equal(true, fileNames.Contains("System.Core.dll"));
                Assert.Equal(true, fileNames.Contains("System.dll"));
                Assert.Equal(true, fileNames.Contains("Microsoft.VisualBasic.dll"));
                Assert.Equal(true, fileNames.Contains("mscorlib.dll"));

                // the compilation references should have the metadata reference to the csharp project skeleton assembly
                var compilation = await vbProject.GetCompilationAsync();
                var compReferences = compilation.References.ToList();
                Assert.Equal(5, compReferences.Count);
            }
        }

        [Fact(Skip = "Needs target file update. Activate when we move to new base drop."), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(2824, "https://github.com/dotnet/roslyn/issues/2824")]
        public async Task Test_OpenProjectReferencingPortableProject()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"CSharpProject\ReferencesPortableProject.csproj", GetResourceText("CSharpProject_ReferencesPortableProject.csproj") },
                { @"CSharpProject\Program.cs", GetResourceText("CSharpProject_CSharpClass.cs") },
                { @"CSharpProject\PortableProject.csproj", GetResourceText("CSharpProject_PortableProject.csproj") },
                { @"CSharpProject\CSharpClass.cs", GetResourceText("CSharpProject_CSharpClass.cs") }
            });

            CreateFiles(files);

            var projectFilePath = GetSolutionFileName(@"CSharpProject\ReferencesPortableProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var hasFacades = project.MetadataReferences.OfType<PortableExecutableReference>().Any(r => r.FilePath.Contains("Facade"));
                Assert.True(hasFacades);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task Test_SharedMetadataReferences()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var p0 = solution.Projects.ElementAt(0);
                var p1 = solution.Projects.ElementAt(1);

                Assert.NotSame(p0, p1);

                var p0mscorlib = GetMetadataReference(p0, "mscorlib");
                var p1mscorlib = GetMetadataReference(p1, "mscorlib");

                Assert.NotNull(p0mscorlib);
                Assert.NotNull(p1mscorlib);

                // metadata references to mscorlib in both projects are the same
                Assert.Same(p0mscorlib, p1mscorlib);
            }
        }

        private static MetadataReference GetMetadataReference(Project project, string name)
        {
            return project.MetadataReferences.OfType<PortableExecutableReference>().SingleOrDefault(mr => mr.FilePath.Contains(name));
        }

        private static MetadataReference GetMetadataReferenceByAlias(Project project, string aliasName)
        {
            return project.MetadataReferences.OfType<PortableExecutableReference>().SingleOrDefault(mr =>
            !mr.Properties.Aliases.IsDefault && mr.Properties.Aliases.Contains(aliasName));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(546171, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/546171")]
        public async Task Test_SharedMetadataReferencesWithAliases()
        {
            var projPath1 = @"CSharpProject\CSharpProject_ExternAlias.csproj";
            var projPath2 = @"CSharpProject\CSharpProject_ExternAlias2.csproj";
            var files = new FileSet(new Dictionary<string, object>
            {
                { projPath1, GetResourceText("CSharpProject_CSharpProject_ExternAlias.csproj") },
                { projPath2, GetResourceText("CSharpProject_CSharpProject_ExternAlias2.csproj") },
                { @"CSharpProject\CSharpExternAlias.cs", GetResourceText("CSharpProject_CSharpExternAlias.cs") },
            });

            CreateFiles(files);

            var fullPath1 = GetSolutionFileName(projPath1);
            var fullPath2 = GetSolutionFileName(projPath2);
            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj1 = await workspace.OpenProjectAsync(fullPath1);
                var proj2 = await workspace.OpenProjectAsync(fullPath2);

                var p1Sys1 = GetMetadataReferenceByAlias(proj1, "Sys1");
                var p1Sys2 = GetMetadataReferenceByAlias(proj1, "Sys2");
                var p2Sys1 = GetMetadataReferenceByAlias(proj2, "Sys1");
                var p2Sys3 = GetMetadataReferenceByAlias(proj2, "Sys3");

                Assert.NotNull(p1Sys1);
                Assert.NotNull(p1Sys2);
                Assert.NotNull(p2Sys1);
                Assert.NotNull(p2Sys3);

                // same filepath but different alias so they are not the same instance
                Assert.NotSame(p1Sys1, p1Sys2);
                Assert.NotSame(p2Sys1, p2Sys3);

                // same filepath and alias so they are the same instance
                Assert.Same(p1Sys1, p2Sys1);

                var mdp1Sys1 = GetMetadata(p1Sys1);
                var mdp1Sys2 = GetMetadata(p1Sys2);
                var mdp2Sys1 = GetMetadata(p2Sys1);
                var mdp2Sys3 = GetMetadata(p2Sys1);

                Assert.NotNull(mdp1Sys1);
                Assert.NotNull(mdp1Sys2);
                Assert.NotNull(mdp2Sys1);
                Assert.NotNull(mdp2Sys3);

                // all references to System.dll share the same metadata bytes
                Assert.Same(mdp1Sys1.Id, mdp1Sys2.Id);
                Assert.Same(mdp1Sys1.Id, mdp2Sys1.Id);
                Assert.Same(mdp1Sys1.Id, mdp2Sys3.Id);
            }
        }

        private Metadata GetMetadata(MetadataReference mref)
        {
            var fnGetMetadata = mref.GetType().GetMethod("GetMetadata", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            return fnGetMetadata?.Invoke(mref, null) as Metadata;
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(552981, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/552981")]
        public async Task TestOpenSolution_DuplicateProjectGuids()
        {
            CreateFiles(GetSolutionWithDuplicatedGuidFiles());
            var solutionFilePath = GetSolutionFileName("DuplicatedGuids.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(831379, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/831379")]
        public async Task GetCompilationWithCircularProjectReferences()
        {
            CreateFiles(GetSolutionWithCircularProjectReferences());
            var solutionFilePath = GetSolutionFileName("CircularSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                // Verify we can get compilations for both projects
                var projects = solution.Projects.ToArray();

                // Exactly one of them should have a reference to the other. Which one it is, is unspecced
                Assert.True(projects[0].ProjectReferences.Any(r => r.ProjectId == projects[1].Id) ||
                            projects[1].ProjectReferences.Any(r => r.ProjectId == projects[0].Id));

                var compilation1 = await projects[0].GetCompilationAsync();
                var compilation2 = await projects[1].GetCompilationAsync();

                // Exactly one of them should have a compilation to the other. Which one it is, is unspecced
                Assert.True(compilation1.References.OfType<CompilationReference>().Any(c => c.Compilation == compilation2) ||
                            compilation2.References.OfType<CompilationReference>().Any(c => c.Compilation == compilation1));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOutputFilePaths()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var p1 = sol.Projects.First(p => p.Language == LanguageNames.CSharp);
                var p2 = sol.Projects.First(p => p.Language == LanguageNames.VisualBasic);

                Assert.Equal("CSharpProject.dll", Path.GetFileName(p1.OutputFilePath));
                Assert.Equal("VisualBasicProject.dll", Path.GetFileName(p2.OutputFilePath));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCrossLanguageReferencesUsesInMemoryGeneratedMetadata()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var p1 = sol.Projects.First(p => p.Language == LanguageNames.CSharp);
                var p2 = sol.Projects.First(p => p.Language == LanguageNames.VisualBasic);

                // prove there is no existing metadata on disk for this project
                Assert.Equal("CSharpProject.dll", Path.GetFileName(p1.OutputFilePath));
                Assert.Equal(false, File.Exists(p1.OutputFilePath));

                // prove that vb project refers to csharp project via generated metadata (skeleton) assembly. 
                // it should be a MetadataImageReference
                var c2 = await p2.GetCompilationAsync();
                var pref = c2.References.OfType<PortableExecutableReference>().FirstOrDefault(r => r.Display == "CSharpProject");
                Assert.NotNull(pref);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCrossLanguageReferencesWithOutOfDateMetadataOnDiskUsesInMemoryGeneratedMetadata()
        {
            await PrepareCrossLanguageProjectWithEmittedMetadataAsync();
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            // recreate the solution so it will reload from disk
            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var p1 = sol.Projects.First(p => p.Language == LanguageNames.CSharp);

                // update project with top level change that should now invalidate use of metadata from disk
                var d1 = p1.Documents.First();
                var root = await d1.GetSyntaxRootAsync();
                var decl = root.DescendantNodes().OfType<CS.Syntax.ClassDeclarationSyntax>().First();
                var newDecl = decl.WithIdentifier(CS.SyntaxFactory.Identifier("Pogrom").WithLeadingTrivia(decl.Identifier.LeadingTrivia).WithTrailingTrivia(decl.Identifier.TrailingTrivia));
                var newRoot = root.ReplaceNode(decl, newDecl);
                var newDoc = d1.WithSyntaxRoot(newRoot);
                p1 = newDoc.Project;
                var p2 = p1.Solution.Projects.First(p => p.Language == LanguageNames.VisualBasic);

                // we should now find a MetadataImageReference that was generated instead of a MetadataFileReference
                var c2 = await p2.GetCompilationAsync();
                var pref = c2.References.OfType<PortableExecutableReference>().FirstOrDefault(r => r.Display == "EmittedCSharpProject");
                Assert.NotNull(pref);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestInternalsVisibleToSigned()
        {
            var solution = await SolutionAsync(
                Project(
                    ProjectName("Project1"),
                    Sign,
                    Document(string.Format(
@"using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo(""Project2, PublicKey={0}"")]
class C1
{{
}}", PublicKey))),
                Project(
                    ProjectName("Project2"),
                    Sign,
                    ProjectReference("Project1"),
                    Document(@"class C2 : C1 { }")));

            var project2 = solution.GetProjectsByName("Project2").First();
            var compilation = await project2.GetCompilationAsync();
            var diagnostics = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning)
                .ToArray();

            Assert.Equal(Enumerable.Empty<Diagnostic>(), diagnostics);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestVersions()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var sversion = solution.Version;
                var latestPV = solution.GetLatestProjectVersion();
                var project = solution.Projects.First();
                var pversion = project.Version;
                var document = project.Documents.First();
                var dversion = await document.GetTextVersionAsync();
                var latestDV = await project.GetLatestDocumentVersionAsync();

                // update document
                var solution1 = solution.WithDocumentText(document.Id, SourceText.From("using test;"));
                var document1 = solution1.GetDocument(document.Id);
                var dversion1 = await document1.GetTextVersionAsync();
                Assert.NotEqual(dversion, dversion1); // new document version
                Assert.Equal(true, dversion1.TestOnly_IsNewerThan(dversion));
                Assert.Equal(solution.Version, solution1.Version); // updating document should not have changed solution version
                Assert.Equal(project.Version, document1.Project.Version); // updating doc should not have changed project version
                var latestDV1 = await document1.Project.GetLatestDocumentVersionAsync();
                Assert.NotEqual(latestDV, latestDV1);
                Assert.Equal(true, latestDV1.TestOnly_IsNewerThan(latestDV));
                Assert.Equal(latestDV1, await document1.GetTextVersionAsync()); // projects latest doc version should be this doc's version

                // update project
                var solution2 = solution1.WithProjectCompilationOptions(project.Id, project.CompilationOptions.WithOutputKind(OutputKind.NetModule));
                var document2 = solution2.GetDocument(document.Id);
                var dversion2 = await document2.GetTextVersionAsync();
                Assert.Equal(dversion1, dversion2); // document didn't change, so version should be the same.
                Assert.NotEqual(document1.Project.Version, document2.Project.Version); // project did change, so project versions should be different
                Assert.Equal(true, document2.Project.Version.TestOnly_IsNewerThan(document1.Project.Version));
                Assert.Equal(solution1.Version, solution2.Version); // solution didn't change, just individual project.

                // update solution
                var pid2 = ProjectId.CreateNewId();
                var solution3 = solution2.AddProject(pid2, "foo", "foo", LanguageNames.CSharp);
                Assert.NotEqual(solution2.Version, solution3.Version); // solution changed, added project.
                Assert.Equal(true, solution3.Version.TestOnly_IsNewerThan(solution2.Version));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_LoadMetadataForReferencedProjects()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;

                var project = await workspace.OpenProjectAsync(projectFilePath);
                var document = project.Documents.First();
                var tree = await document.GetSyntaxTreeAsync();
                var expectedFileName = GetSolutionFileName(@"CSharpProject\CSharpClass.cs");
                Assert.Equal(expectedFileName, tree.FilePath);
                var type = tree.GetRoot().DescendantTokens().First(t => t.ToString() == "class").Parent;
                Assert.NotNull(type);
                Assert.Equal(true, type.ToString().StartsWith("public class CSharpClass", StringComparison.Ordinal));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithoutPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithoutPrefer32Bit.csproj")));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu32BitPreferred, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithoutPrefer32BitAndLibrary()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithoutPrefer32Bit.csproj"))
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputType", "Library"));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithPrefer32Bit.csproj")));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu32BitPreferred, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithPrefer32BitAndLibrary()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithPrefer32Bit.csproj"))
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputType", "Library"));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_CSharp_WithPrefer32BitAndWinMDObj()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithPrefer32Bit.csproj"))
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputType", "winmdobj"));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_WithoutOutputPath()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputPath", ""));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                Assert.NotEmpty(project.OutputFilePath);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_WithoutAssemblyName()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "AssemblyName", ""));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                Assert.NotEmpty(project.OutputFilePath);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_WithoutCSharpTargetsImported_Succeeds()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithoutCSharpTargetsImported.csproj")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var documents = project.Documents.ToList();
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_WithoutCSharpTargetsImported_DocumentsArePickedUp()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithoutCSharpTargetsImported.csproj")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                Assert.True(project.Documents.ToList().Any());
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_VisualBasic_WithoutVBTargetsImported_Succeeds()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithoutVBTargetsImported.vbproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var documents = project.Documents.ToList();
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_VisualBasic_WithoutVBTargetsImported_DocumentsArePickedUp()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithoutVBTargetsImported.vbproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var documents = project.Documents.ToList();
                Assert.True(project.Documents.ToList().Any());
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_VisualBasic_WithoutPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithoutPrefer32Bit.vbproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu32BitPreferred, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_VisualBasic_WithoutPrefer32BitAndLibrary()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithoutPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "OutputType", "Library"));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_VisualBasic_WithPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu32BitPreferred, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_VisualBasic_WithPrefer32BitAndLibrary()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "OutputType", "Library"));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(739043, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/739043")]
        public async Task TestOpenProject_VisualBasic_WithPrefer32BitAndWinMDObj()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "OutputType", "winmdobj"));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_VisualBasic_WithoutOutputPath()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "OutputPath", ""));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                Assert.NotEmpty(project.OutputFilePath);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_VisualBasic_WithLanguageVersion15_3()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "LangVersion", "15.3"));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                Assert.Equal(VB.LanguageVersion.VisualBasic15_3, ((VB.VisualBasicParseOptions)project.ParseOptions).LanguageVersion);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_VisualBasic_WithLatestLanguageVersion()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "LangVersion", "Latest"));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                Assert.Equal(VB.LanguageVersionFacts.MapSpecifiedToEffectiveVersion(VB.LanguageVersion.Latest), ((VB.VisualBasicParseOptions)project.ParseOptions).LanguageVersion);
                Assert.Equal(VB.LanguageVersion.Latest, ((VB.VisualBasicParseOptions)project.ParseOptions).SpecifiedLanguageVersion);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_VisualBasic_WithoutAssemblyName()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "AssemblyName", ""));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                Assert.NotEmpty(project.OutputFilePath);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task Test_Respect_ReferenceOutputassembly_Flag()
        {
            const string top = @"VisualBasicProject_Circular_Top.vbproj";
            const string target = @"VisualBasicProject_Circular_Target.vbproj";
            var projFile = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(top, GetResourceText(top))
                .WithFile(target, GetResourceText(target)));
            var projectFilePath = GetSolutionFileName(top);

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                Assert.Empty(project.ProjectReferences);
            }
        }

        [Fact(Skip = "https://github.com/dotnet/roslyn/issues/15102"), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithXaml()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithXaml.csproj"))
                .WithFile(@"CSharpProject\App.xaml", GetResourceText("CSharpProject_App.xaml"))
                .WithFile(@"CSharpProject\App.xaml.cs", GetResourceText("CSharpProject_App.xaml.cs"))
                .WithFile(@"CSharpProject\MainWindow.xaml", GetResourceText("CSharpProject_MainWindow.xaml"))
                .WithFile(@"CSharpProject\MainWindow.xaml.cs", GetResourceText("CSharpProject_MainWindow.xaml.cs")));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var documents = project.Documents.ToList();

                // AssemblyInfo.cs, App.xaml.cs, MainWindow.xaml.cs, App.g.cs, MainWindow.g.cs, + unusual AssemblyAttributes.cs
                Assert.Equal(6, documents.Count);

                // both xaml code behind files are documents
                Assert.Equal(true, documents.Contains(d => d.Name == "App.xaml.cs"));
                Assert.Equal(true, documents.Contains(d => d.Name == "MainWindow.xaml.cs"));

                // prove no xaml files are documents
                Assert.Equal(false, documents.Contains(d => d.Name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)));

                // prove that generated source files for xaml files are included in documents list
                Assert.Equal(true, documents.Contains(d => d.Name == "App.g.cs"));
                Assert.Equal(true, documents.Contains(d => d.Name == "MainWindow.g.cs"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestMetadataReferenceHasBadHintPath()
        {
            // prove that even with bad hint path for metadata reference the workspace can succeed at finding the correct metadata reference.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_BadHintPath.csproj")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var refs = project.MetadataReferences.ToList();
                var csharpLib = refs.OfType<PortableExecutableReference>().FirstOrDefault(r => r.FilePath.Contains("Microsoft.CSharp"));
                Assert.NotNull(csharpLib);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(531631, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/531631")]
        public async Task TestOpenProject_AssemblyNameIsPath()
        {
            // prove that even if assembly name is specified as a path instead of just a name, workspace still succeeds at opening project.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_AssemblyNameIsPath.csproj")));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var comp = await project.GetCompilationAsync();
                Assert.Equal("ReproApp", comp.AssemblyName);
                var expectedOutputPath = GetParentDirOfParentDirOfContainingDir(project.FilePath);
                Assert.Equal(expectedOutputPath, Path.GetDirectoryName(project.OutputFilePath));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(531631, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/531631")]
        public async Task TestOpenProject_AssemblyNameIsPath2()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_AssemblyNameIsPath2.csproj")));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var comp = await project.GetCompilationAsync();
                Assert.Equal("ReproApp", comp.AssemblyName);
                var expectedOutputPath = Path.Combine(Path.GetDirectoryName(project.FilePath), @"bin");
                Assert.Equal(expectedOutputPath, Path.GetDirectoryName(Path.GetFullPath(project.OutputFilePath)));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithDuplicateFile()
        {
            // Verify that we don't throw in this case
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_DuplicateFile.csproj")));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var documents = project.Documents.ToList();
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithInvalidFileExtension()
        {
            // make sure the file does in fact exist, but with an unrecognized extension
            const string projFileName = @"CSharpProject\CSharpProject.csproj.nyi";
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(projFileName, GetResourceText("CSharpProject_CSharpProject.csproj")));

            AssertThrows<InvalidOperationException>(delegate
            {
                MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(projFileName)).Wait();
            },
            (e) =>
            {
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, GetSolutionFileName(projFileName), ".nyi");
                Assert.Equal(expected, e.Message);
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_ProjectFileExtensionAssociatedWithUnknownLanguage()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var projFileName = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var language = "lingo";
            AssertThrows<InvalidOperationException>(delegate
            {
                var ws = MSBuildWorkspace.Create();
                ws.AssociateFileExtensionWithLanguage("csproj", language); // non-existent language
                ws.OpenProjectAsync(projFileName).Wait();
            },
            (e) =>
            {
                // the exception should tell us something about the language being unrecognized.
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_language_1_is_not_supported, projFileName, language);
                Assert.Equal(expected, e.Message);
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithAssociatedLanguageExtension1()
        {
            // make a CSharp solution with a project file having the incorrect extension 'vbproj', and then load it using the overload the lets us
            // specify the language directly, instead of inferring from the extension
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.vbproj", GetResourceText("CSharpProject_CSharpProject.csproj")));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.AssociateFileExtensionWithLanguage("vbproj", LanguageNames.CSharp);
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var document = project.Documents.First();
                var tree = await document.GetSyntaxTreeAsync();
                var diagnostics = tree.GetDiagnostics().ToList();
                Assert.Equal(0, diagnostics.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithAssociatedLanguageExtension2_IgnoreCase()
        {
            // make a CSharp solution with a project file having the incorrect extension 'anyproj', and then load it using the overload the lets us
            // specify the language directly, instead of inferring from the extension
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.anyproj", GetResourceText("CSharpProject_CSharpProject.csproj")));
            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.anyproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                // prove that the association works even if the case is different
                workspace.AssociateFileExtensionWithLanguage("ANYPROJ", LanguageNames.CSharp);
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var document = project.Documents.First();
                var tree = await document.GetSyntaxTreeAsync();
                var diagnostics = tree.GetDiagnostics().ToList();
                Assert.Equal(0, diagnostics.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithNonExistentSolutionFile_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("NonExistentSolution.sln");

            AssertThrows<FileNotFoundException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                }
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithInvalidSolutionFile_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName(@"http://localhost/Invalid/InvalidSolution.sln");

            AssertThrows<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                }
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithTemporaryLockedFile_SucceedsWithoutFailureEvent()
        {
            // when skipped we should see a diagnostic for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles());

            using (var ws = CreateMSBuildWorkspace())
            {
                var failed = false;
                ws.WorkspaceFailed += (s, args) =>
                {
                    failed |= args.Diagnostic is DocumentDiagnostic;
                };

                // open source file so it cannot be read by workspace;
                var sourceFile = GetSolutionFileName(@"CSharpProject\CSharpClass.cs");
                var file = File.Open(sourceFile, FileMode.Open, FileAccess.Write, FileShare.None);
                try
                {
                    var solution = await ws.OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln"));
                    var doc = solution.Projects.First().Documents.First(d => d.FilePath == sourceFile);

                    // start reading text
                    var getTextTask = doc.GetTextAsync();

                    // wait 1 unit of retry delay then close file
                    var delay = TextDocumentState.RetryDelay;
                    await Task.Delay(delay).ContinueWith(t => file.Close(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                    // finish reading text
                    var text = await getTextTask;
                    Assert.NotEqual(0, text.ToString().Length);
                }
                finally
                {
                    file.Close();
                }

                Assert.Equal(false, failed);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithLockedFile_FailsWithFailureEvent()
        {
            // when skipped we should see a diagnostic for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var failed = false;
                workspace.WorkspaceFailed += (s, args) =>
                {
                    failed |= args.Diagnostic is DocumentDiagnostic;
                };

                // open source file so it cannot be read by workspace;
                var sourceFile = GetSolutionFileName(@"CSharpProject\CSharpClass.cs");
                var file = File.Open(sourceFile, FileMode.Open, FileAccess.Write, FileShare.None);
                try
                {
                    var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                    var doc = solution.Projects.First().Documents.First(d => d.FilePath == sourceFile);
                    var text = await doc.GetTextAsync();
                    Assert.Equal(0, text.ToString().Length);
                }
                finally
                {
                    file.Close();
                }

                Assert.Equal(true, failed);
            }
        }


        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithInvalidProjectPath_SkipTrue_SucceedsWithFailureEvent()
        {
            // when skipped we should see a diagnostic for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_InvalidProjectPath.sln")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                Assert.Equal(1, diagnostics.Count);
            }
        }

        [WorkItem(985906, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/985906")]
        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task HandleSolutionProjectTypeSolutionFolder()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_SolutionFolder.sln")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.WorkspaceFailed += (s, args) =>
                {
                    Assert.True(false, "There should be no failure");
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithInvalidProjectPath_SkipFalse_Fails()
        {
            // when not skipped we should get an exception for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_InvalidProjectPath.sln")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.SkipUnrecognizedProjects = false;

                AssertThrows<InvalidOperationException>(() =>
                {
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                });
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithNonExistentProject_SkipTrue_SucceedsWithFailureEvent()
        {
            // when skipped we should see a diagnostic for the non-existent project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_NonExistentProject.sln")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                Assert.Equal(1, diagnostics.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithNonExistentProject_SkipFalse_Fails()
        {
            // when skipped we should see an exception for the non-existent project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_NonExistentProject.sln")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.SkipUnrecognizedProjects = false;

                AssertThrows<FileNotFoundException>(() =>
                {
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                });
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithUnrecognizedProjectFileExtension_Fails()
        {
            // proves that for solution open, project type guid and extension are both necessary
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_CSharp_UnknownProjectExtension.sln"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText("CSharpProject_CSharpProject.csproj")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                Assert.Equal(0, solution.ProjectIds.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithUnrecognizedProjectTypeGuidButRecognizedExtension_Succeeds()
        {
            // proves that if project type guid is not recognized, a known project file extension is all we need.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_CSharp_UnknownProjectTypeGuid.sln")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                Assert.Equal(1, solution.ProjectIds.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_WithUnrecognizedProjectTypeGuidAndUnrecognizedExtension_WithSkipTrue_SucceedsWithFailureEvent()
        {
            // proves that if both project type guid and file extension are unrecognized, then project is skipped.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_CSharp_UnknownProjectTypeGuidAndUnknownExtension.sln"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText("CSharpProject_CSharpProject.csproj")));
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                Assert.Equal(1, diagnostics.Count);
                Assert.Equal(0, solution.ProjectIds.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithUnrecognizedProjectTypeGuidAndUnrecognizedExtension_WithSkipFalse_Fails()
        {
            // proves that if both project type guid and file extension are unrecognized, then open project fails.
            const string noProjFileName = @"CSharpProject\CSharpProject.noproj";
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_CSharp_UnknownProjectTypeGuidAndUnknownExtension.sln"))
                .WithFile(noProjFileName, GetResourceText("CSharpProject_CSharpProject.csproj")));

            AssertThrows<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.SkipUnrecognizedProjects = false;
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                }
            },
            e =>
            {
                var noProjFullFileName = GetSolutionFileName(noProjFileName);
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, noProjFullFileName, ".noproj");
                Assert.Equal(expected, e.Message);
            });
        }

        private HostServices _hostServicesWithoutCSharp = MefHostServices.Create(MefHostServices.DefaultAssemblies.Where(a => !a.FullName.Contains("CSharp")));

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(3931, "https://github.com/dotnet/roslyn/issues/3931")]
        public void TestOpenSolution_WithMissingLanguageLibraries_WithSkipFalse_Throws()
        {
            // proves that if the language libraries are missing then the appropriate error occurs
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            AssertThrows<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace(_hostServicesWithoutCSharp))
                {
                    workspace.SkipUnrecognizedProjects = false;
                    workspace.OpenSolutionAsync(solutionFilePath).Wait();
                }
            },
            e =>
            {
                var projFileName = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, projFileName, ".csproj");
                Assert.Equal(expected, e.Message);
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(3931, "https://github.com/dotnet/roslyn/issues/3931")]
        public async Task TestOpenSolution_WithMissingLanguageLibraries_WithSkipTrue_SucceedsWithDiagnostic()
        {
            // proves that if the language libraries are missing then the appropriate error occurs
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace(_hostServicesWithoutCSharp))
            {
                workspace.SkipUnrecognizedProjects = true;

                var dx = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += delegate (object sender, WorkspaceDiagnosticEventArgs e)
                {
                    dx.Add(e.Diagnostic);
                };

                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

            Assert.Equal(1, dx.Count);

            var projFileName = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, projFileName, ".csproj");
            Assert.Equal(expected, dx[0].Message);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(3931, "https://github.com/dotnet/roslyn/issues/3931")]
        public void TestOpenProject_WithMissingLanguageLibraries_Throws()
        {
            // proves that if the language libraries are missing then the appropriate error occurs
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var ws = MSBuildWorkspace.Create(_hostServicesWithoutCSharp);
            var projectName = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            AssertThrows<InvalidOperationException>(() =>
            {
                var project = ws.OpenProjectAsync(projectName).Result;
            },
            e =>
            {
                var expected = string.Format(WorkspacesResources.Cannot_open_project_0_because_the_file_extension_1_is_not_associated_with_a_language, projectName, ".csproj");
                Assert.Equal(expected, e.Message);
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithInvalidFilePath_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var projectFilePath = GetSolutionFileName(@"http://localhost/Invalid/InvalidProject.csproj");

            AssertThrows<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.OpenProjectAsync(projectFilePath).Wait();
                }
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithNonExistentProjectFile_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var projectFilePath = GetSolutionFileName(@"CSharpProject\NonExistentProject.csproj");

            AssertThrows<FileNotFoundException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.OpenProjectAsync(projectFilePath).Wait();
                }
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithInvalidProjectReference_SkipTrue_SucceedsWithEvent()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_InvalidProjectReference.vbproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var project = await workspace.OpenProjectAsync(projectFilePath);

                Assert.Equal(1, project.Solution.ProjectIds.Count); // didn't really open referenced project due to invalid file path.
                Assert.Equal(0, project.ProjectReferences.Count()); // no resolved project references
                Assert.Equal(1, project.AllProjectReferences.Count); // dangling project reference
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithInvalidProjectReference_SkipFalse_Fails()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_InvalidProjectReference.vbproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            AssertThrows<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.SkipUnrecognizedProjects = false;
                    workspace.OpenProjectAsync(projectFilePath).Wait();
                }
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithNonExistentProjectReference_SkipTrue_SucceedsWithEvent()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_NonExistentProjectReference.vbproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var project = await workspace.OpenProjectAsync(projectFilePath);

                Assert.Equal(1, project.Solution.ProjectIds.Count); // didn't really open referenced project due to invalid file path.
                Assert.Equal(0, project.ProjectReferences.Count()); // no resolved project references
                Assert.Equal(1, project.AllProjectReferences.Count); // dangling project reference
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithNonExistentProjectReference_SkipFalse_Fails()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_NonExistentProjectReference.vbproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            AssertThrows<FileNotFoundException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.SkipUnrecognizedProjects = false;
                    workspace.OpenProjectAsync(projectFilePath).Wait();
                }
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithUnrecognizedProjectReferenceFileExtension_SkipTrue_SucceedsWithEvent()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_UnknownProjectExtension.vbproj"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText(@"CSharpProject_CSharpProject.csproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var diagnostics = new List<WorkspaceDiagnostic>();
                workspace.WorkspaceFailed += (s, args) =>
                {
                    diagnostics.Add(args.Diagnostic);
                };

                var project = await workspace.OpenProjectAsync(projectFilePath);

                Assert.Equal(1, project.Solution.ProjectIds.Count); // didn't really open referenced project due to unrecognized extension.
                Assert.Equal(0, project.ProjectReferences.Count()); // no resolved project references
                Assert.Equal(1, project.AllProjectReferences.Count); // dangling project reference
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithUnrecognizedProjectReferenceFileExtension_SkipFalse_Fails()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_UnknownProjectExtension.vbproj"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText(@"CSharpProject_CSharpProject.csproj")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            AssertThrows<InvalidOperationException>(() =>
            {
                using (var workspace = CreateMSBuildWorkspace())
                {
                    workspace.SkipUnrecognizedProjects = false;
                    workspace.OpenProjectAsync(projectFilePath).Wait();
                }
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithUnrecognizedProjectReferenceFileExtension_WithMetadata_SkipTrue_SucceedsByLoadingMetadata()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_UnknownProjectExtension.vbproj"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText(@"CSharpProject_CSharpProject.csproj"))
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", GetResourceBytes("CSharpProject.dll")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);

                Assert.Equal(1, project.Solution.ProjectIds.Count);
                Assert.Equal(0, project.ProjectReferences.Count());
                Assert.Equal(0, project.AllProjectReferences.Count());

                var metaRefs = project.MetadataReferences.ToList();
                Assert.Equal(true, metaRefs.Any(r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll")));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithUnrecognizedProjectReferenceFileExtension_WithMetadata_SkipFalse_SucceedsByLoadingMetadata()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_UnknownProjectExtension.vbproj"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText(@"CSharpProject_CSharpProject.csproj"))
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", GetResourceBytes("CSharpProject.dll")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.SkipUnrecognizedProjects = false;
                var project = await workspace.OpenProjectAsync(projectFilePath);

                Assert.Equal(1, project.Solution.ProjectIds.Count);
                Assert.Equal(0, project.ProjectReferences.Count());
                Assert.Equal(0, project.AllProjectReferences.Count());

                var metaRefs = project.MetadataReferences.ToList();
                Assert.Equal(true, metaRefs.Any(r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll")));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithUnrecognizedProjectReferenceFileExtension_BadMsbuildProject_SkipTrue_SucceedsWithDanglingProjectReference()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_UnknownProjectExtension.vbproj"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceBytes("CSharpProject.dll"))); // use metadata file as stand-in for bad project file
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.SkipUnrecognizedProjects = true;

                var project = await workspace.OpenProjectAsync(projectFilePath);

                Assert.Equal(1, project.Solution.ProjectIds.Count);
                Assert.Equal(0, project.ProjectReferences.Count());
                Assert.Equal(1, project.AllProjectReferences.Count());
                Assert.Equal(1, workspace.Diagnostics.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithReferencedProject_LoadMetadata_ExistingMetadata_Succeeds()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", GetResourceBytes("CSharpProject.dll")));
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;
                var project = await workspace.OpenProjectAsync(projectFilePath);

                // referenced project got converted to a metadata reference
                var projRefs = project.ProjectReferences.ToList();
                var metaRefs = project.MetadataReferences.ToList();
                Assert.Equal(0, projRefs.Count);
                Assert.Equal(true, metaRefs.Any(r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll")));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WithReferencedProject_LoadMetadata_NonExistentMetadata_LoadsProjectInstead()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var projectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;
                var project = await CreateMSBuildWorkspace().OpenProjectAsync(projectFilePath);

                // referenced project is still a project ref, did not get converted to metadata ref
                var projRefs = project.ProjectReferences.ToList();
                var metaRefs = project.MetadataReferences.ToList();
                Assert.Equal(1, projRefs.Count);
                Assert.False(metaRefs.Any(r => r.Properties.Aliases.Contains("CSharpProject")));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_UpdateExistingReferences()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", GetResourceBytes("CSharpProject.dll")));
            var vbProjectFilePath = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");
            var csProjectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            // first open vb project that references c# project, but only reference the c# project's built metadata
            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;
                var vbProject = await workspace.OpenProjectAsync(vbProjectFilePath);

                // prove vb project references c# project as a metadata reference
                Assert.Equal(0, vbProject.ProjectReferences.Count());
                Assert.Equal(true, vbProject.MetadataReferences.Any(r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll")));

                // now explicitly open the c# project that got referenced as metadata
                var csProject = await workspace.OpenProjectAsync(csProjectFilePath);

                // show that the vb project now references the c# project directly (not as metadata)
                vbProject = workspace.CurrentSolution.GetProject(vbProject.Id);
                Assert.Equal(1, vbProject.ProjectReferences.Count());
                Assert.False(vbProject.MetadataReferences.Any(r => r.Properties.Aliases.Contains("CSharpProject")));
            }
        }

        [ConditionalFact(typeof(Framework35Installed))]
        [Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(528984, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/528984")]
        public async Task TestOpenProject_AddVBDefaultReferences()
        {
            var projectFile = "VisualBasicProject_VisualBasicProject_3_5.vbproj";
            CreateFiles(projectFile, "VisualBasicProject_VisualBasicClass.vb");
            var projectFilePath = GetSolutionFileName(projectFile);

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projectFilePath);
                var compilation = await project.GetCompilationAsync();
                var diagnostics = compilation.GetDiagnostics();
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TTestCompilationOptions_CSharp_DebugType_Full()
        {
            CreateCSharpFilesWith("DebugType", "full");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_DebugType_None()
        {
            CreateCSharpFilesWith("DebugType", "none");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_DebugType_PDBOnly()
        {
            CreateCSharpFilesWith("DebugType", "pdbonly");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_DebugType_Portable()
        {
            CreateCSharpFilesWith("DebugType", "portable");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_DebugType_Embedded()
        {
            CreateCSharpFilesWith("DebugType", "embedded");
            await AssertCSParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OutputKind_DynamicallyLinkedLibrary()
        {
            CreateCSharpFilesWith("OutputType", "Library");
            await AssertCSCompilationOptionsAsync(OutputKind.DynamicallyLinkedLibrary, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OutputKind_ConsoleApplication()
        {
            CreateCSharpFilesWith("OutputType", "Exe");
            await AssertCSCompilationOptionsAsync(OutputKind.ConsoleApplication, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OutputKind_WindowsApplication()
        {
            CreateCSharpFilesWith("OutputType", "WinExe");
            await AssertCSCompilationOptionsAsync(OutputKind.WindowsApplication, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OutputKind_NetModule()
        {
            CreateCSharpFilesWith("OutputType", "Module");
            await AssertCSCompilationOptionsAsync(OutputKind.NetModule, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OptimizationLevel_Release()
        {
            CreateCSharpFilesWith("Optimize", "True");
            await AssertCSCompilationOptionsAsync(OptimizationLevel.Release, options => options.OptimizationLevel);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_OptimizationLevel_Debug()
        {
            CreateCSharpFilesWith("Optimize", "False");
            await AssertCSCompilationOptionsAsync(OptimizationLevel.Debug, options => options.OptimizationLevel);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_MainFileName()
        {
            CreateCSharpFilesWith("StartupObject", "Foo");
            await AssertCSCompilationOptionsAsync("Foo", options => options.MainTypeName);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_SignAssembly_Missing()
        {
            CreateCSharpFiles();
            await AssertCSCompilationOptionsAsync(null, options => options.CryptoKeyFile);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_SignAssembly_False()
        {
            CreateCSharpFilesWith("SignAssembly", "false");
            await AssertCSCompilationOptionsAsync(null, options => options.CryptoKeyFile);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_SignAssembly_True()
        {
            CreateCSharpFilesWith("SignAssembly", "true");
            await AssertCSCompilationOptionsAsync("snKey.snk", options => Path.GetFileName(options.CryptoKeyFile));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_DelaySign_False()
        {
            CreateCSharpFilesWith("DelaySign", "false");
            await AssertCSCompilationOptionsAsync(null, options => options.DelaySign);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_DelaySign_True()
        {
            CreateCSharpFilesWith("DelaySign", "true");
            await AssertCSCompilationOptionsAsync(true, options => options.DelaySign);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_CheckOverflow_True()
        {
            CreateCSharpFilesWith("CheckForOverflowUnderflow", "true");
            await AssertCSCompilationOptionsAsync(true, options => options.CheckOverflow);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_CSharp_CheckOverflow_False()
        {
            CreateCSharpFilesWith("CheckForOverflowUnderflow", "false");
            await AssertCSCompilationOptionsAsync(false, options => options.CheckOverflow);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_Compatibility_ECMA1()
        {
            CreateCSharpFilesWith("LangVersion", "ISO-1");
            await AssertCSParseOptionsAsync(CS.LanguageVersion.CSharp1, options => options.LanguageVersion);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_Compatibility_ECMA2()
        {
            CreateCSharpFilesWith("LangVersion", "ISO-2");
            await AssertCSParseOptionsAsync(CS.LanguageVersion.CSharp2, options => options.LanguageVersion);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_Compatibility_None()
        {
            CreateCSharpFilesWith("LangVersion", "3");
            await AssertCSParseOptionsAsync(CS.LanguageVersion.CSharp3, options => options.LanguageVersion);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_LanguageVersion_Latest()
        {
            CreateCSharpFiles();
            await AssertCSParseOptionsAsync(CS.LanguageVersion.CSharp7, options => options.LanguageVersion);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_CSharp_PreprocessorSymbols()
        {
            CreateCSharpFilesWith("DefineConstants", "DEBUG;TRACE;X;Y");
            await AssertCSParseOptionsAsync("DEBUG,TRACE,X,Y", options => string.Join(",", options.PreprocessorSymbolNames));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestConfigurationDebug()
        {
            CreateCSharpFiles();
            await AssertCSParseOptionsAsync("DEBUG,TRACE", options => string.Join(",", options.PreprocessorSymbolNames));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestConfigurationRelease()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace(("Configuration", "Release")))
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = sol.Projects.First();
                var options = project.ParseOptions;

                Assert.False(options.PreprocessorSymbolNames.Any(name => name == "DEBUG"), "DEBUG symbol not expected");
                Assert.True(options.PreprocessorSymbolNames.Any(name => name == "TRACE"), "TRACE symbol expected");
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_DebugType_Full()
        {
            CreateVBFilesWith("DebugType", "full");
            await AssertVBParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_DebugType_None()
        {
            CreateVBFilesWith("DebugType", "none");
            await AssertVBParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_DebugType_PDBOnly()
        {
            CreateVBFilesWith("DebugType", "pdbonly");
            await AssertVBParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_DebugType_Portable()
        {
            CreateVBFilesWith("DebugType", "portable");
            await AssertVBParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_DebugType_Embedded()
        {
            CreateVBFilesWith("DebugType", "embedded");
            await AssertVBParseOptionsAsync(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_VBRuntime_Embed()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_Embed.vbproj")));
            await AssertVBCompilationOptionsAsync(true, options => options.EmbedVbCoreRuntime);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OutputKind_DynamicallyLinkedLibrary()
        {
            CreateVBFilesWith("OutputType", "Library");
            await AssertVBCompilationOptionsAsync(OutputKind.DynamicallyLinkedLibrary, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OutputKind_ConsoleApplication()
        {
            CreateVBFilesWith("OutputType", "Exe");
            await AssertVBCompilationOptionsAsync(OutputKind.ConsoleApplication, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OutputKind_WindowsApplication()
        {
            CreateVBFilesWith("OutputType", "WinExe");
            await AssertVBCompilationOptionsAsync(OutputKind.WindowsApplication, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OutputKind_NetModule()
        {
            CreateVBFilesWith("OutputType", "Module");
            await AssertVBCompilationOptionsAsync(OutputKind.NetModule, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_RootNamespace()
        {
            CreateVBFilesWith("RootNamespace", "Foo.Bar");
            await AssertVBCompilationOptionsAsync("Foo.Bar", options => options.RootNamespace);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionStrict_On()
        {
            CreateVBFilesWith("OptionStrict", "On");
            await AssertVBCompilationOptionsAsync(VB.OptionStrict.On, options => options.OptionStrict);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionStrict_Off()
        {
            CreateVBFilesWith("OptionStrict", "Off");

            // The VBC MSBuild task specifies '/optionstrict:custom' rather than '/optionstrict-'
            // See https://github.com/dotnet/roslyn/blob/58f44c39048032c6b823ddeedddd20fa589912f5/src/Compilers/Core/MSBuildTask/Vbc.cs#L390-L418 for details.

            await AssertVBCompilationOptionsAsync(VB.OptionStrict.Custom, options => options.OptionStrict);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionStrict_Custom()
        {
            CreateVBFilesWith("OptionStrictType", "Custom");
            await AssertVBCompilationOptionsAsync(VB.OptionStrict.Custom, options => options.OptionStrict);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionInfer_True()
        {
            CreateVBFilesWith("OptionInfer", "On");
            await AssertVBCompilationOptionsAsync(true, options => options.OptionInfer);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionInfer_False()
        {
            CreateVBFilesWith("OptionInfer", "Off");
            await AssertVBCompilationOptionsAsync(false, options => options.OptionInfer);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionExplicit_True()
        {
            CreateVBFilesWith("OptionExplicit", "On");
            await AssertVBCompilationOptionsAsync(true, options => options.OptionExplicit);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionExplicit_False()
        {
            CreateVBFilesWith("OptionExplicit", "Off");
            await AssertVBCompilationOptionsAsync(false, options => options.OptionExplicit);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionCompareText_True()
        {
            CreateVBFilesWith("OptionCompare", "Text");
            await AssertVBCompilationOptionsAsync(true, options => options.OptionCompareText);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionCompareText_False()
        {
            CreateVBFilesWith("OptionCompare", "Binary");
            await AssertVBCompilationOptionsAsync(false, options => options.OptionCompareText);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionRemoveIntegerOverflowChecks_True()
        {
            CreateVBFilesWith("RemoveIntegerChecks", "true");
            await AssertVBCompilationOptionsAsync(false, options => options.CheckOverflow);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionRemoveIntegerOverflowChecks_False()
        {
            CreateVBFilesWith("RemoveIntegerChecks", "false");
            await AssertVBCompilationOptionsAsync(true, options => options.CheckOverflow);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_OptionAssemblyOriginatorKeyFile_SignAssemblyFalse()
        {
            CreateVBFilesWith("SignAssembly", "false");
            await AssertVBCompilationOptionsAsync(null, options => options.CryptoKeyFile);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestCompilationOptions_VisualBasic_GlobalImports()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("VisualBasicProject").FirstOrDefault();
                var options = (VB.VisualBasicCompilationOptions)project.CompilationOptions;
                var imports = options.GlobalImports;

                AssertEx.Equal(
                    expected: new[]
                    {
                        "Microsoft.VisualBasic",
                        "System",
                        "System.Collections",
                        "System.Collections.Generic",
                        "System.Diagnostics",
                        "System.Linq",
                    },
                    actual: imports.Select(i => i.Name));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestParseOptions_VisualBasic_PreprocessorSymbols()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "DefineConstants", "X=1,Y=2,Z,T=-1,VBC_VER=123,F=false"));
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("VisualBasicProject").FirstOrDefault();
                var options = (VB.VisualBasicParseOptions)project.ParseOptions;
                var defines = new List<KeyValuePair<string, object>>(options.PreprocessorSymbols);
                defines.Sort((x, y) => x.Key.CompareTo(y.Key));

                AssertEx.Equal(
                    expected: new[]
                    {
                        new KeyValuePair<string, object>("_MyType", "Windows"),
                        new KeyValuePair<string, object>("CONFIG", "Debug"),
                        new KeyValuePair<string, object>("DEBUG", -1),
                        new KeyValuePair<string, object>("F", false),
                        new KeyValuePair<string, object>("PLATFORM", "AnyCPU"),
                        new KeyValuePair<string, object>("T", -1),
                        new KeyValuePair<string, object>("TARGET", "library"),
                        new KeyValuePair<string, object>("TRACE", -1),
                        new KeyValuePair<string, object>("VBC_VER", 123),
                        new KeyValuePair<string, object>("X", 1),
                        new KeyValuePair<string, object>("Y", 2),
                        new KeyValuePair<string, object>("Z", true),
                    },
                    actual: defines);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task Test_VisualBasic_ConditionalAttributeEmitted()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicClass.vb", GetResourceText(@"VisualBasicProject_VisualBasicClass_WithConditionalAttributes.vb"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "DefineConstants", "EnableMyAttribute"));
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = sol.GetProjectsByName("VisualBasicProject").FirstOrDefault();
                var options = (VB.VisualBasicParseOptions)project.ParseOptions;
                Assert.Equal(true, options.PreprocessorSymbolNames.Contains("EnableMyAttribute"));

                var compilation = await project.GetCompilationAsync();
                var metadataBytes = compilation.EmitToArray();
                var mtref = MetadataReference.CreateFromImage(metadataBytes);
                var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
                var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
                var attrs = sym.GetAttributes();

                Assert.Equal(true, attrs.Any(ad => ad.AttributeClass.Name == "MyAttribute"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task Test_VisualBasic_ConditionalAttributeNotEmitted()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicClass.vb", GetResourceText(@"VisualBasicProject_VisualBasicClass_WithConditionalAttributes.vb")));
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = sol.GetProjectsByName("VisualBasicProject").FirstOrDefault();
                var options = (VB.VisualBasicParseOptions)project.ParseOptions;
                Assert.Equal(false, options.PreprocessorSymbolNames.Contains("EnableMyAttribute"));

                var compilation = await project.GetCompilationAsync();
                var metadataBytes = compilation.EmitToArray();
                var mtref = MetadataReference.CreateFromImage(metadataBytes);
                var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
                var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
                var attrs = sym.GetAttributes();

                Assert.Equal(false, attrs.Any(ad => ad.AttributeClass.Name == "MyAttribute"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task Test_CSharp_ConditionalAttributeEmitted()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpClass.cs", GetResourceText(@"CSharpProject_CSharpClass_WithConditionalAttributes.cs"))
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "DefineConstants", "EnableMyAttribute"));
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = sol.GetProjectsByName("CSharpProject").FirstOrDefault();
                var options = project.ParseOptions;
                Assert.Equal(true, options.PreprocessorSymbolNames.Contains("EnableMyAttribute"));

                var compilation = await project.GetCompilationAsync();
                var metadataBytes = compilation.EmitToArray();
                var mtref = MetadataReference.CreateFromImage(metadataBytes);
                var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
                var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
                var attrs = sym.GetAttributes();

                Assert.Equal(true, attrs.Any(ad => ad.AttributeClass.Name == "MyAttr"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task Test_CSharp_ConditionalAttributeNotEmitted()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpClass.cs", GetResourceText(@"CSharpProject_CSharpClass_WithConditionalAttributes.cs")));
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = sol.GetProjectsByName("CSharpProject").FirstOrDefault();
                var options = project.ParseOptions;
                Assert.Equal(false, options.PreprocessorSymbolNames.Contains("EnableMyAttribute"));

                var compilation = await project.GetCompilationAsync();
                var metadataBytes = compilation.EmitToArray();
                var mtref = MetadataReference.CreateFromImage(metadataBytes);
                var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
                var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
                var attrs = sym.GetAttributes();

                Assert.Equal(false, attrs.Any(ad => ad.AttributeClass.Name == "MyAttr"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_WithLinkedDocument()
        {
            var fooText = GetResourceText(@"OtherStuff_Foo.cs");

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText(@"CSharpProject_WithLink.csproj"))
                .WithFile(@"OtherStuff\Foo.cs", fooText));
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
                var documents = project.Documents.ToList();
                var fooDoc = documents.Single(d => d.Name == "Foo.cs");
                Assert.Equal(1, fooDoc.Folders.Count);
                Assert.Equal("Blah", fooDoc.Folders[0]);

                // prove that the file path is the correct full path to the actual file
                Assert.Equal(true, fooDoc.FilePath.Contains("OtherStuff"));
                Assert.Equal(true, File.Exists(fooDoc.FilePath));
                var text = File.ReadAllText(fooDoc.FilePath);
                Assert.Equal(fooText, text);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAddDocumentAsync()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();

                var newText = SourceText.From("public class Bar { }");
                workspace.AddDocument(project.Id, new string[] { "NewFolder" }, "Bar.cs", newText);

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var project2 = solution2.GetProjectsByName("CSharpProject").FirstOrDefault();
                var documents = project2.Documents.ToList();
                Assert.Equal(4, documents.Count);
                var document2 = documents.Single(d => d.Name == "Bar.cs");
                var text2 = await document2.GetTextAsync();
                Assert.Equal(newText.ToString(), text2.ToString());
                Assert.Equal(1, document2.Folders.Count);

                // check actual file on disk...
                var textOnDisk = File.ReadAllText(document2.FilePath);
                Assert.Equal(newText.ToString(), textOnDisk);

                // check project file on disk
                var projectFileText = File.ReadAllText(project2.FilePath);
                Assert.Equal(true, projectFileText.Contains(@"NewFolder\Bar.cs"));

                // reload project & solution to prove project file change was good
                using (var workspaceB = CreateMSBuildWorkspace())
                {
                    var solutionB = await workspaceB.OpenSolutionAsync(solutionFilePath);
                    var projectB = workspaceB.CurrentSolution.GetProjectsByName("CSharpProject").FirstOrDefault();
                    var documentsB = projectB.Documents.ToList();
                    Assert.Equal(4, documentsB.Count);
                    var documentB = documentsB.Single(d => d.Name == "Bar.cs");
                    Assert.Equal(1, documentB.Folders.Count);
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestUpdateDocumentAsync()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
                var document = project.Documents.Single(d => d.Name == "CSharpClass.cs");
                var originalText = await document.GetTextAsync();

                var newText = SourceText.From("public class Bar { }");
                workspace.TryApplyChanges(solution.WithDocumentText(document.Id, newText, PreservationMode.PreserveIdentity));

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var project2 = solution2.GetProjectsByName("CSharpProject").FirstOrDefault();
                var documents = project2.Documents.ToList();
                Assert.Equal(3, documents.Count);
                var document2 = documents.Single(d => d.Name == "CSharpClass.cs");
                var text2 = await document2.GetTextAsync();
                Assert.Equal(newText.ToString(), text2.ToString());

                // check actual file on disk...
                var textOnDisk = File.ReadAllText(document2.FilePath);
                Assert.Equal(newText.ToString(), textOnDisk);

                // check original text in original solution did not change
                var text = await document.GetTextAsync();

                Assert.Equal(originalText.ToString(), text.ToString());
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestRemoveDocumentAsync()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
                var document = project.Documents.Single(d => d.Name == "CSharpClass.cs");
                var originalText = await document.GetTextAsync();

                workspace.RemoveDocument(document.Id);

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var project2 = solution2.GetProjectsByName("CSharpProject").FirstOrDefault();
                Assert.Equal(0, project2.Documents.Count(d => d.Name == "CSharpClass.cs"));

                // check actual file on disk...
                Assert.False(File.Exists(document.FilePath));

                // check original text in original solution did not change
                var text = await document.GetTextAsync();
                Assert.Equal(originalText.ToString(), text.ToString());
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestApplyChanges_UpdateDocumentText()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var documents = solution.GetProjectsByName("CSharpProject").FirstOrDefault().Documents.ToList();
                var document = documents.Single(d => d.Name.Contains("CSharpClass"));
                var text = await document.GetTextAsync();
                var newText = SourceText.From("using System.Diagnostics;\r\n" + text.ToString());
                var newSolution = solution.WithDocumentText(document.Id, newText);

                workspace.TryApplyChanges(newSolution);

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var document2 = solution2.GetDocument(document.Id);
                var text2 = await document2.GetTextAsync();
                Assert.Equal(newText.ToString(), text2.ToString());

                // check actual file on disk...
                var textOnDisk = File.ReadAllText(document.FilePath);
                Assert.Equal(newText.ToString(), textOnDisk);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestApplyChanges_AddDocument()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
                var newDocId = DocumentId.CreateNewId(project.Id);
                var newText = SourceText.From("public class Bar { }");
                var newSolution = solution.AddDocument(newDocId, "Bar.cs", newText);

                workspace.TryApplyChanges(newSolution);

                // check workspace current solution
                var solution2 = workspace.CurrentSolution;
                var document2 = solution2.GetDocument(newDocId);
                var text2 = await document2.GetTextAsync();
                Assert.Equal(newText.ToString(), text2.ToString());

                // check actual file on disk...
                var textOnDisk = File.ReadAllText(document2.FilePath);
                Assert.Equal(newText.ToString(), textOnDisk);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestApplyChanges_NotSupportedChangesFail()
        {
            var csharpProjPath = @"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj";
            var vbProjPath = @"AnalyzerSolution\VisualBasicProject_AnalyzerReference.vbproj";
            CreateFiles(GetAnalyzerReferenceSolutionFiles());

            using (var workspace = CreateMSBuildWorkspace())
            {
                var csProjectFilePath = GetSolutionFileName(csharpProjPath);
                var csProject = await workspace.OpenProjectAsync(csProjectFilePath);
                var csProjectId = csProject.Id;

                var vbProjectFilePath = GetSolutionFileName(vbProjPath);
                var vbProject = await workspace.OpenProjectAsync(vbProjectFilePath);
                var vbProjectId = vbProject.Id;

                // adding additional documents not supported.
                Assert.Equal(false, workspace.CanApplyChange(ApplyChangesKind.AddAdditionalDocument));
                Assert.Throws<NotSupportedException>(delegate
                {
                    workspace.TryApplyChanges(workspace.CurrentSolution.AddAdditionalDocument(DocumentId.CreateNewId(csProjectId), "foo.xaml", SourceText.From("<foo></foo>")));
                });

                var xaml = workspace.CurrentSolution.GetProject(csProjectId).AdditionalDocuments.FirstOrDefault(d => d.Name == "XamlFile.xaml");
                Assert.NotNull(xaml);

                // removing additional documents not supported
                Assert.Equal(false, workspace.CanApplyChange(ApplyChangesKind.RemoveAdditionalDocument));
                Assert.Throws<NotSupportedException>(delegate
                {
                    workspace.TryApplyChanges(workspace.CurrentSolution.RemoveAdditionalDocument(xaml.Id));
                });
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestWorkspaceChangedEvent()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                await workspace.OpenSolutionAsync(solutionFilePath);
                var expectedEventKind = WorkspaceChangeKind.DocumentChanged;
                var originalSolution = workspace.CurrentSolution;

                using (var eventWaiter = workspace.VerifyWorkspaceChangedEvent(args =>
                {
                    Assert.Equal(expectedEventKind, args.Kind);
                    Assert.NotNull(args.NewSolution);
                    Assert.NotSame(originalSolution, args.NewSolution);
                }))
                {
                    // change document text (should fire SolutionChanged event)
                    var doc = workspace.CurrentSolution.Projects.First().Documents.First();
                    var text = await doc.GetTextAsync();
                    var newText = "/* new text */\r\n" + text.ToString();

                    workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(doc.Id, SourceText.From(newText), PreservationMode.PreserveIdentity));

                    Assert.True(eventWaiter.WaitForEventToFire(AsyncEventTimeout),
                        string.Format("event {0} was not fired within {1}",
                        Enum.GetName(typeof(WorkspaceChangeKind), expectedEventKind),
                        AsyncEventTimeout));
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestWorkspaceChangedWeakEvent()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                await workspace.OpenSolutionAsync(solutionFilePath);
                var expectedEventKind = WorkspaceChangeKind.DocumentChanged;
                var originalSolution = workspace.CurrentSolution;

                using (var eventWanter = workspace.VerifyWorkspaceChangedEvent(args =>
                {
                    Assert.Equal(expectedEventKind, args.Kind);
                    Assert.NotNull(args.NewSolution);
                    Assert.NotSame(originalSolution, args.NewSolution);
                }))
                {
                    // change document text (should fire SolutionChanged event)
                    var doc = workspace.CurrentSolution.Projects.First().Documents.First();
                    var text = await doc.GetTextAsync();
                    var newText = "/* new text */\r\n" + text.ToString();

                    workspace.TryApplyChanges(
                        workspace
                        .CurrentSolution
                        .WithDocumentText(
                            doc.Id,
                            SourceText.From(newText),
                            PreservationMode.PreserveIdentity));

                    Assert.True(eventWanter.WaitForEventToFire(AsyncEventTimeout),
                        string.Format("event {0} was not fired within {1}",
                        Enum.GetName(typeof(WorkspaceChangeKind), expectedEventKind),
                        AsyncEventTimeout));
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestSemanticVersionCS()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                var csprojectId = solution.Projects.First(p => p.Language == LanguageNames.CSharp).Id;
                var csdoc1 = solution.GetProject(csprojectId).Documents.Single(d => d.Name == "CSharpClass.cs");

                // add method
                var csdoc1Root = await csdoc1.GetSyntaxRootAsync();
                var startOfClassInterior = csdoc1Root.DescendantNodes().OfType<CS.Syntax.ClassDeclarationSyntax>().First().OpenBraceToken.FullSpan.End;
                var csdoc1Text = await csdoc1.GetTextAsync();
                var csdoc2 = AssertSemanticVersionChanged(csdoc1, csdoc1Text.Replace(new TextSpan(startOfClassInterior, 0), "public async Task M() {\r\n}\r\n"));

                // change interior of method
                var csdoc2Root = await csdoc2.GetSyntaxRootAsync();
                var startOfMethodInterior = csdoc2Root.DescendantNodes().OfType<CS.Syntax.MethodDeclarationSyntax>().First().Body.OpenBraceToken.FullSpan.End;
                var csdoc2Text = await csdoc2.GetTextAsync();
                var csdoc3 = AssertSemanticVersionUnchanged(csdoc2, csdoc2Text.Replace(new TextSpan(startOfMethodInterior, 0), "int x = 10;\r\n"));

                // add whitespace outside of method
                var csdoc3Text = await csdoc3.GetTextAsync();
                var csdoc4 = AssertSemanticVersionUnchanged(csdoc3, csdoc3Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\n\r\n   \r\n"));

                // add field with initializer
                csdoc1Text = await csdoc1.GetTextAsync();
                var csdoc5 = AssertSemanticVersionChanged(csdoc1, csdoc1Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\npublic int X = 20;\r\n"));

                // change initializer value 
                var csdoc5Root = await csdoc5.GetSyntaxRootAsync();
                var literal = csdoc5Root.DescendantNodes().OfType<CS.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
                var csdoc5Text = await csdoc5.GetTextAsync();
                var csdoc6 = AssertSemanticVersionUnchanged(csdoc5, csdoc5Text.Replace(literal.Span, "100"));

                // add const field with initializer
                csdoc1Text = await csdoc1.GetTextAsync();
                var csdoc7 = AssertSemanticVersionChanged(csdoc1, csdoc1Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\npublic const int X = 20;\r\n"));

                // change constant initializer value
                var csdoc7Root = await csdoc7.GetSyntaxRootAsync();
                var literal7 = csdoc7Root.DescendantNodes().OfType<CS.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
                var csdoc7Text = await csdoc7.GetTextAsync();
                var csdoc8 = AssertSemanticVersionChanged(csdoc7, csdoc7Text.Replace(literal7.Span, "100"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestSemanticVersionVB()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                var vbprojectId = solution.Projects.First(p => p.Language == LanguageNames.VisualBasic).Id;
                var vbdoc1 = solution.GetProject(vbprojectId).Documents.Single(d => d.Name == "VisualBasicClass.vb");

                // add method
                var vbdoc1Root = await vbdoc1.GetSyntaxRootAsync();
                var startOfClassInterior = GetMethodInsertionPoint(vbdoc1Root.DescendantNodes().OfType<VB.Syntax.ClassBlockSyntax>().First());
                var vbdoc1Text = await vbdoc1.GetTextAsync();
                var vbdoc2 = AssertSemanticVersionChanged(vbdoc1, vbdoc1Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\nPublic Sub M()\r\n\r\nEnd Sub\r\n"));

                // change interior of method
                var vbdoc2Root = await vbdoc2.GetSyntaxRootAsync();
                var startOfMethodInterior = vbdoc2Root.DescendantNodes().OfType<VB.Syntax.MethodBlockBaseSyntax>().First().BlockStatement.FullSpan.End;
                var vbdoc2Text = await vbdoc2.GetTextAsync();
                var vbdoc3 = AssertSemanticVersionUnchanged(vbdoc2, vbdoc2Text.Replace(new TextSpan(startOfMethodInterior, 0), "\r\nDim x As Integer = 10\r\n"));

                // add whitespace outside of method
                var vbdoc3Text = await vbdoc3.GetTextAsync();
                var vbdoc4 = AssertSemanticVersionUnchanged(vbdoc3, vbdoc3Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\n\r\n   \r\n"));

                // add field with initializer
                vbdoc1Text = await vbdoc1.GetTextAsync();
                var vbdoc5 = AssertSemanticVersionChanged(vbdoc1, vbdoc1Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\nPublic X As Integer = 20;\r\n"));

                // change initializer value
                var vbdoc5Root = await vbdoc5.GetSyntaxRootAsync();
                var literal = vbdoc5Root.DescendantNodes().OfType<VB.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
                var vbdoc5Text = await vbdoc5.GetTextAsync();
                var vbdoc6 = AssertSemanticVersionUnchanged(vbdoc5, vbdoc5Text.Replace(literal.Span, "100"));

                // add const field with initializer
                vbdoc1Text = await vbdoc1.GetTextAsync();
                var vbdoc7 = AssertSemanticVersionChanged(vbdoc1, vbdoc1Text.Replace(new TextSpan(startOfClassInterior, 0), "\r\nPublic Const X As Integer = 20;\r\n"));

                // change constant initializer value
                var vbdoc7Root = await vbdoc7.GetSyntaxRootAsync();
                var literal7 = vbdoc7Root.DescendantNodes().OfType<VB.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
                var vbdoc7Text = await vbdoc7.GetTextAsync();
                var vbdoc8 = AssertSemanticVersionChanged(vbdoc7, vbdoc7Text.Replace(literal7.Span, "100"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(529276, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/529276"), WorkItem(12086, "DevDiv_Projects/Roslyn")]
        public async Task TestOpenProject_LoadMetadataForReferenceProjects_NoMetadata()
        {
            var projPath = @"CSharpProject\CSharpProject_ProjectReference.csproj";
            var files = GetProjectReferenceSolutionFiles();

            CreateFiles(files);

            var projectFullPath = GetSolutionFileName(projPath);

            using (var workspace = CreateMSBuildWorkspace())
            {
                workspace.LoadMetadataForReferencedProjects = true;

                var proj = await workspace.OpenProjectAsync(projectFullPath);

                // prove that project gets opened instead.
                Assert.Equal(2, workspace.CurrentSolution.Projects.Count());

                // and all is well
                var comp = await proj.GetCompilationAsync();
                var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
                Assert.Empty(errs);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(918072, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/918072")]
        public async Task TestAnalyzerReferenceLoadStandalone()
        {
            var projPaths = new[] { @"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj", @"AnalyzerSolution\VisualBasicProject_AnalyzerReference.vbproj" };
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var workspace = CreateMSBuildWorkspace())
            {
                foreach (var projectPath in projPaths)
                {
                    var projectFullPath = GetSolutionFileName(projectPath);

                    var proj = await workspace.OpenProjectAsync(projectFullPath);
                    Assert.Equal(1, proj.AnalyzerReferences.Count);
                    var analyzerReference = proj.AnalyzerReferences.First() as AnalyzerFileReference;
                    Assert.NotNull(analyzerReference);
                    Assert.True(analyzerReference.FullPath.EndsWith("CSharpProject.dll", StringComparison.OrdinalIgnoreCase));
                }

                // prove that project gets opened instead.
                Assert.Equal(2, workspace.CurrentSolution.Projects.Count());
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAdditionalFilesStandalone()
        {
            var projPaths = new[] { @"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj", @"AnalyzerSolution\VisualBasicProject_AnalyzerReference.vbproj" };
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var workspace = CreateMSBuildWorkspace())
            {
                foreach (var projectPath in projPaths)
                {
                    var projectFullPath = GetSolutionFileName(projectPath);

                    var proj = await workspace.OpenProjectAsync(projectFullPath);
                    Assert.Equal(1, proj.AdditionalDocuments.Count());
                    var doc = proj.AdditionalDocuments.First();
                    Assert.Equal("XamlFile.xaml", doc.Name);
                    var text = await doc.GetTextAsync();
                    Assert.Contains("Window", text.ToString(), StringComparison.Ordinal);
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestLoadTextSync()
        {
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var workspace = new AdhocWorkspace(MSBuildMefHostServices.DefaultServices))
            {
                var projectFullPath = GetSolutionFileName(@"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj");

                var loader = new MSBuildProjectLoader(workspace);
                var infos = await loader.LoadProjectInfoAsync(projectFullPath);

                var doc = infos[0].Documents[0];
                var tav = doc.TextLoader.LoadTextAndVersionSynchronously(workspace, doc.Id, CancellationToken.None);

                var adoc = infos[0].AdditionalDocuments.First(a => a.Name == "XamlFile.xaml");
                var atav = adoc.TextLoader.LoadTextAndVersionSynchronously(workspace, adoc.Id, CancellationToken.None);
                Assert.Contains("Window", atav.Text.ToString(), StringComparison.Ordinal);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestGetTextSynchronously()
        {
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var workspace = CreateMSBuildWorkspace())
            {
                var projectFullPath = GetSolutionFileName(@"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj");
                var proj = await workspace.OpenProjectAsync(projectFullPath);

                var doc = proj.Documents.First();
                var text = doc.State.GetTextSynchronously(CancellationToken.None);

                var adoc = proj.AdditionalDocuments.First(a => a.Name == "XamlFile.xaml");
                var atext = adoc.State.GetTextSynchronously(CancellationToken.None);
                Assert.Contains("Window", atext.ToString(), StringComparison.Ordinal);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(546171, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/546171")]
        public async Task TestCSharpExternAlias()
        {
            var projPath = @"CSharpProject\CSharpProject_ExternAlias.csproj";
            var files = new FileSet(new Dictionary<string, object>
            {
                { projPath, GetResourceText("CSharpProject_CSharpProject_ExternAlias.csproj") },
                { @"CSharpProject\CSharpExternAlias.cs", GetResourceText("CSharpProject_CSharpExternAlias.cs") },
            });

            CreateFiles(files);

            var fullPath = GetSolutionFileName(projPath);
            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(fullPath);
                var comp = await proj.GetCompilationAsync();
                comp.GetDiagnostics().Where(d => d.Severity > DiagnosticSeverity.Info).Verify();
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(530337, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/530337")]
        public async Task TestProjectReferenceWithExternAlias()
        {
            var files = GetProjectReferenceSolutionFiles();
            CreateFiles(files);

            var fullPath = GetSolutionFileName(@"CSharpProjectReference.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(fullPath);
                var proj = sol.Projects.First();
                var comp = await proj.GetCompilationAsync();
                comp.GetDiagnostics().Where(d => d.Severity > DiagnosticSeverity.Info).Verify();
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestProjectReferenceWithReferenceOutputAssemblyFalse()
        {
            var files = GetProjectReferenceSolutionFiles();
            files = VisitProjectReferences(files, r =>
            {
                r.Add(new XElement(XName.Get("ReferenceOutputAssembly", MSBuildNamespace), "false"));
            });

            CreateFiles(files);

            var fullPath = GetSolutionFileName(@"CSharpProjectReference.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(fullPath);
                foreach (var project in sol.Projects)
                {
                    Assert.Equal(0, project.ProjectReferences.Count());
                }
            }
        }

        private FileSet VisitProjectReferences(FileSet files, Action<XElement> visitProjectReference)
        {
            var result = new List<KeyValuePair<string, object>>();
            foreach (var file in files)
            {
                var text = file.Value.ToString();
                if (file.Key.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                {
                    text = VisitProjectReferences(text, visitProjectReference);
                }

                result.Add(new KeyValuePair<string, object>(file.Key, text));
            }

            return new FileSet(result);
        }

        private string VisitProjectReferences(string projectFileText, Action<XElement> visitProjectReference)
        {
            var document = XDocument.Parse(projectFileText);
            var projectReferenceItems = document.Descendants(XName.Get("ProjectReference", MSBuildNamespace));
            foreach (var projectReferenceItem in projectReferenceItems)
            {
                visitProjectReference(projectReferenceItem);
            }

            return document.ToString();
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestProjectReferenceWithNoGuid()
        {
            var files = GetProjectReferenceSolutionFiles();
            files = VisitProjectReferences(files, r =>
            {
                r.Elements(XName.Get("Project", MSBuildNamespace)).Remove();
            });

            CreateFiles(files);

            var fullPath = GetSolutionFileName(@"CSharpProjectReference.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var sol = await workspace.OpenSolutionAsync(fullPath);
                foreach (var project in sol.Projects)
                {
                    Assert.InRange(project.ProjectReferences.Count(), 0, 1);
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(5668, "https://github.com/dotnet/roslyn/issues/5668")]
        public async Task TestOpenProject_MetadataReferenceHasDocComments()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var comp = await project.GetCompilationAsync();
                var symbol = comp.GetTypeByMetadataName("System.Console");
                var docComment = symbol.GetDocumentationCommentXml();
                Assert.NotNull(docComment);
                Assert.NotEqual(0, docComment.Length);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CSharp_HasSourceDocComments()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
                var parseOptions = (CS.CSharpParseOptions)project.ParseOptions;
                Assert.Equal(DocumentationMode.Parse, parseOptions.DocumentationMode);
                var comp = await project.GetCompilationAsync();
                var symbol = comp.GetTypeByMetadataName("CSharpProject.CSharpClass");
                var docComment = symbol.GetDocumentationCommentXml();
                Assert.NotNull(docComment);
                Assert.NotEqual(0, docComment.Length);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_VisualBasic_HasSourceDocComments()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First(p => p.Language == LanguageNames.VisualBasic);
                var parseOptions = (VB.VisualBasicParseOptions)project.ParseOptions;
                Assert.Equal(DocumentationMode.Diagnose, parseOptions.DocumentationMode);
                var comp = await project.GetCompilationAsync();
                var symbol = comp.GetTypeByMetadataName("VisualBasicProject.VisualBasicClass");
                var docComment = symbol.GetDocumentationCommentXml();
                Assert.NotNull(docComment);
                Assert.NotEqual(0, docComment.Length);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CrossLanguageSkeletonReferenceHasDocComments()
        {
            CreateFiles(GetMultiProjectSolutionFiles());
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var csproject = workspace.CurrentSolution.Projects.First(p => p.Language == LanguageNames.CSharp);
                var csoptions = (CS.CSharpParseOptions)csproject.ParseOptions;
                Assert.Equal(DocumentationMode.Parse, csoptions.DocumentationMode);
                var cscomp = await csproject.GetCompilationAsync();
                var cssymbol = cscomp.GetTypeByMetadataName("CSharpProject.CSharpClass");
                var cscomment = cssymbol.GetDocumentationCommentXml();
                Assert.NotNull(cscomment);

                var vbproject = workspace.CurrentSolution.Projects.First(p => p.Language == LanguageNames.VisualBasic);
                var vboptions = (VB.VisualBasicParseOptions)vbproject.ParseOptions;
                Assert.Equal(DocumentationMode.Diagnose, vboptions.DocumentationMode);
                var vbcomp = await vbproject.GetCompilationAsync();
                var vbsymbol = vbcomp.GetTypeByMetadataName("VisualBasicProject.VisualBasicClass");
                var parent = vbsymbol.BaseType; // this is the vb imported version of the csharp symbol
                var vbcomment = parent.GetDocumentationCommentXml();

                Assert.Equal(cscomment, vbcomment);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithProjectFileLocked()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            // open for read-write so no-one else can read
            var projectFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            using (File.Open(projectFile, FileMode.Open, FileAccess.ReadWrite))
            {
                AssertThrows<IOException>(() =>
                    {
                        using (var workspace = CreateMSBuildWorkspace())
                        {
                            workspace.OpenProjectAsync(projectFile).Wait();
                        }
                    });
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenProject_WithNonExistentProjectFile()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            // open for read-write so no-one else can read
            var projectFile = GetSolutionFileName(@"CSharpProject\NoProject.csproj");
            AssertThrows<FileNotFoundException>(() =>
                {
                    using (var workspace = CreateMSBuildWorkspace())
                    {
                        workspace.OpenProjectAsync(projectFile).Wait();
                    }
                });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestOpenSolution_WithNonExistentSolutionFile()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            // open for read-write so no-one else can read
            var solutionFile = GetSolutionFileName(@"NoSolution.sln");
            AssertThrows<System.IO.FileNotFoundException>(() =>
                {
                    using (var workspace = CreateMSBuildWorkspace())
                    {
                        workspace.OpenSolutionAsync(solutionFile).Wait();
                    }
                });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_SolutionFileHasEmptyLinesAndWhitespaceOnlyLines()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"TestSolution.sln", GetResourceText("TestSolution_CSharp_EmptyLines.sln") },
                { @"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject.csproj") },
                { @"CSharpProject\CSharpClass.cs", GetResourceText("CSharpProject_CSharpClass.cs") },
                { @"CSharpProject\Properties\AssemblyInfo.cs", GetResourceText("CSharpProject_AssemblyInfo.cs") }
            });

            CreateFiles(files);
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(531543, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/531543")]
        public async Task TestOpenSolution_SolutionFileHasEmptyLineBetweenProjectBlock()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"TestSolution.sln", GetResourceText("TestLoad_SolutionFileWithEmptyLineBetweenProjectBlock.sln") }
            });

            CreateFiles(files);
            var solutionFilePath = GetSolutionFileName("TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }
        }

        [Fact(Skip = "531283"), Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(531283, "DevDiv")]
        public async Task TestOpenSolution_SolutionFileHasMissingEndProject()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"TestSolution1.sln", GetResourceText("TestSolution_MissingEndProject1.sln") },
                { @"TestSolution2.sln", GetResourceText("TestSolution_MissingEndProject2.sln") },
                { @"TestSolution3.sln", GetResourceText("TestSolution_MissingEndProject3.sln") }
            });

            CreateFiles(files);

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution1.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution2.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution3.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(792912, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/792912")]
        public async Task TestOpenSolution_WithDuplicatedGuidsBecomeSelfReferential()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"DuplicatedGuids.sln", GetResourceText("TestSolution_DuplicatedGuidsBecomeSelfReferential.sln") },
                { @"ReferenceTest\ReferenceTest.csproj", GetResourceText("CSharpProject_DuplicatedGuidsBecomeSelfReferential.csproj") },
                { @"Library1\Library1.csproj", GetResourceText("CSharpProject_DuplicatedGuidLibrary1.csproj") },
            });

            CreateFiles(files);
            var solutionFilePath = GetSolutionFileName("DuplicatedGuids.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                Assert.Equal(2, solution.ProjectIds.Count);

                var testProject = solution.Projects.FirstOrDefault(p => p.Name == "ReferenceTest");
                Assert.NotNull(testProject);
                Assert.Equal(1, testProject.AllProjectReferences.Count);

                var libraryProject = solution.Projects.FirstOrDefault(p => p.Name == "Library1");
                Assert.NotNull(libraryProject);
                Assert.Equal(0, libraryProject.AllProjectReferences.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(792912, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/792912")]
        public async Task TestOpenSolution_WithDuplicatedGuidsBecomeCircularReferential()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"DuplicatedGuids.sln", GetResourceText("TestSolution_DuplicatedGuidsBecomeCircularReferential.sln") },
                { @"ReferenceTest\ReferenceTest.csproj", GetResourceText("CSharpProject_DuplicatedGuidsBecomeCircularReferential.csproj") },
                { @"Library1\Library1.csproj", GetResourceText("CSharpProject_DuplicatedGuidLibrary3.csproj") },
                { @"Library2\Library2.csproj", GetResourceText("CSharpProject_DuplicatedGuidLibrary4.csproj") },
            });

            CreateFiles(files);
            var solutionFilePath = GetSolutionFileName("DuplicatedGuids.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                Assert.Equal(3, solution.ProjectIds.Count);

                var testProject = solution.Projects.FirstOrDefault(p => p.Name == "ReferenceTest");
                Assert.NotNull(testProject);
                Assert.Equal(1, testProject.AllProjectReferences.Count);

                var library1Project = solution.Projects.FirstOrDefault(p => p.Name == "Library1");
                Assert.NotNull(library1Project);
                Assert.Equal(1, library1Project.AllProjectReferences.Count);

                var library2Project = solution.Projects.FirstOrDefault(p => p.Name == "Library2");
                Assert.NotNull(library2Project);
                Assert.Equal(0, library2Project.AllProjectReferences.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(991528, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/991528")]
        public async Task MSBuildProjectShouldHandleCodePageProperty()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { "Encoding.csproj", GetResourceText("Encoding.csproj").Replace("<CodePage>ReplaceMe</CodePage>", "<CodePage>1254</CodePage>") },
                { "class1.cs", "//\u201C" }
            });

            CreateFiles(files);

            var projPath = GetSolutionFileName("Encoding.csproj");
            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);
                var document = project.Documents.First(d => d.Name == "class1.cs");
                var text = await document.GetTextAsync();
                Assert.Equal(Encoding.GetEncoding(1254), text.Encoding);

                // The smart quote (“) in class1.cs shows up as "â€œ" in codepage 1254. Do a sanity
                // check here to make sure this file hasn't been corrupted in a way that would
                // impact subsequent asserts.
                Assert.Equal("//\u00E2\u20AC\u0153".Length, 5);
                Assert.Equal("//\u00E2\u20AC\u0153".Length, text.Length);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(991528, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/991528")]
        public async Task MSBuildProjectShouldHandleInvalidCodePageProperty()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { "Encoding.csproj", GetResourceText("Encoding.csproj").Replace("<CodePage>ReplaceMe</CodePage>", "<CodePage>-1</CodePage>") },
                { "class1.cs", "//\u201C" }
            });

            CreateFiles(files);

            var projPath = GetSolutionFileName("Encoding.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);
                var document = project.Documents.First(d => d.Name == "class1.cs");
                var text = await document.GetTextAsync();
                Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), text.Encoding);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(991528, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/991528")]
        public async Task MSBuildProjectShouldHandleInvalidCodePageProperty2()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { "Encoding.csproj", GetResourceText("Encoding.csproj").Replace("<CodePage>ReplaceMe</CodePage>", "<CodePage>Broken</CodePage>") },
                { "class1.cs", "//\u201C" }
            });

            CreateFiles(files);

            var projPath = GetSolutionFileName("Encoding.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);
                var document = project.Documents.First(d => d.Name == "class1.cs");
                var text = await document.GetTextAsync();
                Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), text.Encoding);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(991528, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/991528")]
        public async Task MSBuildProjectShouldHandleDefaultCodePageProperty()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { "Encoding.csproj", GetResourceText("Encoding.csproj").Replace("<CodePage>ReplaceMe</CodePage>", string.Empty) },
                { "class1.cs", "//\u201C" }
            });

            CreateFiles(files);

            var projPath = GetSolutionFileName("Encoding.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);
                var document = project.Documents.First(d => d.Name == "class1.cs");
                var text = await document.GetTextAsync();
                Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), text.Encoding);
                Assert.Equal("//\u201C", text.ToString());
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(981208, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/981208")]
        public void DisposeMSBuildWorkspaceAndServicesCollected()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var workspace = sol.Workspace;
            var project = sol.Projects.First();
            var document = project.Documents.First();
            var tree = document.GetSyntaxTreeAsync().Result;
            var type = tree.GetRoot().DescendantTokens().First(t => t.ToString() == "class").Parent;
            var compilation = document.GetSemanticModelAsync().WaitAndGetResult_CanCallOnBackground(CancellationToken.None);
            Assert.NotNull(type);
            Assert.Equal(true, type.ToString().StartsWith("public class CSharpClass", StringComparison.Ordinal));
            Assert.NotNull(compilation);

            // MSBuildWorkspace doesn't have a cache service
            Assert.Null(workspace.CurrentSolution.Services.CacheService);

            var weakSolution = ObjectReference.Create(sol);
            var weakCompilation = ObjectReference.Create(compilation);

            sol.Workspace.Dispose();
            project = null;
            document = null;
            tree = null;
            type = null;
            sol = null;
            compilation = null;

            weakSolution.AssertReleased();
            weakCompilation.AssertReleased();
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(1088127, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/1088127")]
        public async Task MSBuildWorkspacePreservesEncoding()
        {
            var encoding = Encoding.BigEndianUnicode;
            var fileContent = @"//“
class C { }";
            var files = new FileSet(new Dictionary<string, object>
            {
                { "Encoding.csproj", GetResourceText("Encoding.csproj").Replace("<CodePage>ReplaceMe</CodePage>", string.Empty) },
                { "class1.cs", encoding.GetBytesWithPreamble(fileContent) }
            });

            CreateFiles(files);
            var projPath = GetSolutionFileName("Encoding.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var project = await workspace.OpenProjectAsync(projPath);

                var document = project.Documents.First(d => d.Name == "class1.cs");

                // update root without first looking at text (no encoding is known)
                var gen = Editing.SyntaxGenerator.GetGenerator(document);
                var doc2 = document.WithSyntaxRoot(gen.CompilationUnit()); // empty CU
                var doc2text = await doc2.GetTextAsync();
                Assert.Null(doc2text.Encoding);
                var doc2tree = await doc2.GetSyntaxTreeAsync();
                Assert.Null(doc2tree.Encoding);
                Assert.Null(doc2tree.GetText().Encoding);

                // observe original text to discover encoding
                var text = await document.GetTextAsync();
                Assert.Equal(encoding.EncodingName, text.Encoding.EncodingName);
                Assert.Equal(fileContent, text.ToString());

                // update root blindly again, after observing encoding, see that now encoding is known
                var doc3 = document.WithSyntaxRoot(gen.CompilationUnit()); // empty CU
                var doc3text = await doc3.GetTextAsync();
                Assert.NotNull(doc3text.Encoding);
                Assert.Equal(encoding.EncodingName, doc3text.Encoding.EncodingName);
                var doc3tree = await doc3.GetSyntaxTreeAsync();
                Assert.Equal(doc3text.Encoding, doc3tree.GetText().Encoding);
                Assert.Equal(doc3text.Encoding, doc3tree.Encoding);

                // change doc to have no encoding, still succeeds at writing to disk with old encoding
                var root = await document.GetSyntaxRootAsync();
                var noEncodingDoc = document.WithText(SourceText.From(text.ToString(), encoding: null));
                var noEncodingDocText = await noEncodingDoc.GetTextAsync();
                Assert.Null(noEncodingDocText.Encoding);

                // apply changes (this writes the changed document)
                var noEncodingSolution = noEncodingDoc.Project.Solution;
                Assert.True(noEncodingSolution.Workspace.TryApplyChanges(noEncodingSolution));

                // prove the written document still has the same encoding
                var filePath = GetSolutionFileName("Class1.cs");
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var reloadedText = EncodedStringText.Create(stream);
                    Assert.Equal(encoding.EncodingName, reloadedText.Encoding.EncodingName);
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAddRemoveMetadataReference_GAC()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var projFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var projFileText = File.ReadAllText(projFile);
            Assert.Equal(false, projFileText.Contains(@"System.Xaml"));

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();

                var mref = MetadataReference.CreateFromFile(typeof(System.Xaml.XamlObjectReader).Assembly.Location);

                // add reference to System.Xaml
                workspace.TryApplyChanges(project.AddMetadataReference(mref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.Equal(true, projFileText.Contains(@"<Reference Include=""System.Xaml,"));

                // remove reference to System.Xaml
                workspace.TryApplyChanges(workspace.CurrentSolution.GetProject(project.Id).RemoveMetadataReference(mref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.Equal(false, projFileText.Contains(@"<Reference Include=""System.Xaml,"));
            }
        }

        [Fact(Skip = "https://github.com/dotnet/roslyn/issues/16301")]
        [Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public void TestAddRemoveMetadataReference_ReferenceAssembly()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText(@"CSharpProject_CSharpProject_WithSystemNumerics.csproj")));

            var csProjFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var csProjFileText = File.ReadAllText(csProjFile);
            Assert.Equal(true, csProjFileText.Contains(@"<Reference Include=""System.Numerics"""));

            var vbProjFile = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");
            var vbProjFileText = File.ReadAllText(vbProjFile);
            Assert.Equal(false, vbProjFileText.Contains(@"System.Numerics"));

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln"));
                var csProject = solution.Projects.First(p => p.Language == LanguageNames.CSharp);
                var vbProject = solution.Projects.First(p => p.Language == LanguageNames.VisualBasic);

                var numericsMetadata = csProject.MetadataReferences.Single(m => m.Display.Contains("System.Numerics"));

                // add reference to System.Xaml
                workspace.TryApplyChanges(vbProject.AddMetadataReference(numericsMetadata).Solution);
                var newVbProjFileText = File.ReadAllText(vbProjFile);
                Assert.Equal(true, newVbProjFileText.Contains(@"<Reference Include=""System.Numerics"""));

                // remove reference MyAssembly.dll
                workspace.TryApplyChanges(workspace.CurrentSolution.GetProject(vbProject.Id).RemoveMetadataReference(numericsMetadata).Solution);
                var newVbProjFileText2 = File.ReadAllText(vbProjFile);
                Assert.Equal(false, newVbProjFileText2.Contains(@"<Reference Include=""System.Numerics"""));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAddRemoveMetadataReference_NonGACorRefAssembly()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"References\MyAssembly.dll", GetResourceBytes("EmptyLibrary.dll")));

            var projFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var projFileText = File.ReadAllText(projFile);
            Assert.Equal(false, projFileText.Contains(@"MyAssembly"));

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();

                var myAssemblyPath = GetSolutionFileName(@"References\MyAssembly.dll");
                var mref = MetadataReference.CreateFromFile(myAssemblyPath);

                // add reference to MyAssembly.dll
                workspace.TryApplyChanges(project.AddMetadataReference(mref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.Equal(true, projFileText.Contains(@"<Reference Include=""MyAssembly"""));
                Assert.Equal(true, projFileText.Contains(@"<HintPath>..\References\MyAssembly.dll"));

                // remove reference MyAssembly.dll
                workspace.TryApplyChanges(workspace.CurrentSolution.GetProject(project.Id).RemoveMetadataReference(mref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.Equal(false, projFileText.Contains(@"<Reference Include=""MyAssembly"""));
                Assert.Equal(false, projFileText.Contains(@"<HintPath>..\References\MyAssembly.dll"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAddRemoveAnalyzerReference()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"Analyzers\MyAnalyzer.dll", GetResourceBytes("EmptyLibrary.dll")));

            var projFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            var projFileText = File.ReadAllText(projFile);
            Assert.Equal(false, projFileText.Contains(@"<Analyzer Include=""..\Analyzers\MyAnalyzer.dll"));

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First();

                var myAnalyzerPath = GetSolutionFileName(@"Analyzers\MyAnalyzer.dll");
                var aref = new AnalyzerFileReference(myAnalyzerPath, new InMemoryAssemblyLoader());

                // add reference to MyAnalyzer.dll
                workspace.TryApplyChanges(project.AddAnalyzerReference(aref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.Equal(true, projFileText.Contains(@"<Analyzer Include=""..\Analyzers\MyAnalyzer.dll"));

                // remove reference MyAnalyzer.dll
                workspace.TryApplyChanges(workspace.CurrentSolution.GetProject(project.Id).RemoveAnalyzerReference(aref).Solution);
                projFileText = File.ReadAllText(projFile);
                Assert.Equal(false, projFileText.Contains(@"<Analyzer Include=""..\Analyzers\MyAnalyzer.dll"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestAddRemoveProjectReference()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            var projFile = GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj");
            var projFileText = File.ReadAllText(projFile);
            Assert.Equal(true, projFileText.Contains(@"<ProjectReference Include=""..\CSharpProject\CSharpProject.csproj"">"));

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solutionFilePath = GetSolutionFileName("TestSolution.sln");
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);
                var project = solution.Projects.First(p => p.Language == LanguageNames.VisualBasic);
                var pref = project.ProjectReferences.First();

                // remove project reference
                workspace.TryApplyChanges(workspace.CurrentSolution.GetProject(project.Id).RemoveProjectReference(pref).Solution);
                Assert.Equal(0, workspace.CurrentSolution.GetProject(project.Id).ProjectReferences.Count());

                projFileText = File.ReadAllText(projFile);
                Assert.Equal(false, projFileText.Contains(@"<ProjectReference Include=""..\CSharpProject\CSharpProject.csproj"">"));

                // add it back
                workspace.TryApplyChanges(workspace.CurrentSolution.GetProject(project.Id).AddProjectReference(pref).Solution);
                Assert.Equal(1, workspace.CurrentSolution.GetProject(project.Id).ProjectReferences.Count());

                projFileText = File.ReadAllText(projFile);
                Assert.Equal(true, projFileText.Contains(@"<ProjectReference Include=""..\CSharpProject\CSharpProject.csproj"">"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        [WorkItem(1101040, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/1101040")]
        public async Task TestOpenProject_BadLink()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText(@"CSharpProject_CSharpProject_BadLink.csproj")));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);
                var docs = proj.Documents.ToList();
                Assert.Equal(3, docs.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_BadElement()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText(@"CSharpProject_CSharpProject_BadElement.csproj")));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);

                Assert.Equal(1, workspace.Diagnostics.Count);
                Assert.True(workspace.Diagnostics[0].Message.StartsWith("Msbuild failed"));

                Assert.Equal(0, proj.DocumentIds.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_BadTaskImport()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText(@"CSharpProject_CSharpProject_BadTasks.csproj")));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);

                Assert.Equal(1, workspace.Diagnostics.Count);
                Assert.True(workspace.Diagnostics[0].Message.StartsWith("Msbuild failed"));

                Assert.Equal(0, proj.DocumentIds.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenSolution_BadTaskImport()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText(@"CSharpProject_CSharpProject_BadTasks.csproj")));

            var solutionFilePath = GetSolutionFileName(@"TestSolution.sln");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var solution = await workspace.OpenSolutionAsync(solutionFilePath);

                Assert.Equal(1, workspace.Diagnostics.Count);
                Assert.True(workspace.Diagnostics[0].Message.StartsWith("Msbuild failed"));

                Assert.Equal(1, solution.ProjectIds.Count);
                Assert.Equal(0, solution.Projects.First().DocumentIds.Count);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_MsbuildError()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText(@"CSharpProject_CSharpProject_MsbuildError.csproj")));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);

                Assert.Equal(1, workspace.Diagnostics.Count);
                Assert.True(workspace.Diagnostics[0].Message.StartsWith("Msbuild failed"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_WildcardsWithLink()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText(@"CSharpProject_CSharpProject_Wildcards.csproj")));

            var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

            using (var workspace = CreateMSBuildWorkspace())
            {
                var proj = await workspace.OpenProjectAsync(projectFilePath);

                // prove that the file identified with a wildcard and remapped to a computed link is named correctly.
                Assert.True(proj.Documents.Any(d => d.Name == "AssemblyInfo.cs"));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.MSBuildWorkspace)]
        public async Task TestOpenProject_CommandLineArgsHaveNoErrors()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            using (var workspace = CreateMSBuildWorkspace())
            {
                var loader = workspace.Services
                    .GetLanguageServices(LanguageNames.CSharp)
                    .GetRequiredService<IProjectFileLoader>();

                var projectFilePath = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");

                var properties = ImmutableDictionary<string, string>.Empty;
                var projectFile = await loader.LoadProjectFileAsync(projectFilePath, properties, CancellationToken.None);
                var projectFileInfo = await projectFile.GetProjectFileInfoAsync(CancellationToken.None);

                var commandLineParser = workspace.Services
                    .GetLanguageServices(loader.Language)
                    .GetRequiredService<ICommandLineParserService>();

                var projectDirectory = Path.GetDirectoryName(projectFilePath);
                var commandLineArgs = commandLineParser.Parse(
                    arguments: projectFileInfo.CommandLineArgs,
                    baseDirectory: projectDirectory,
                    isInteractive: false,
                    sdkDirectory: System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());

                Assert.Equal(0, commandLineArgs.Errors.Length);
            }
        }

        private class InMemoryAssemblyLoader : IAnalyzerAssemblyLoader
        {
            public void AddDependencyLocation(string fullPath)
            {
            }

            public Assembly LoadFromPath(string fullPath)
            {
                var bytes = File.ReadAllBytes(fullPath);
                return Assembly.Load(bytes);
            }
        }
    }
}
