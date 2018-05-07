using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace ConsoleClient
{
    class Program
    {
        static void Main(string[] args)
        {
            //Problems:
            // - Need to be able to exclude types and namespaces (like the EF migrations)
            // - not finding any attributes

            // great starting tutorial series https://joshvarty.com/2014/10/30/learn-roslyn-now-part-7-introducing-the-semantic-model/

            MSBuildWorkspace workspace = MSBuildWorkspace.Create();
            Solution solution = workspace.OpenSolutionAsync(@"C:\workspaces\ScoutSheet\ScoutSheet\ScoutSheet.sln").Result;

            var projectList = solution.GetProjectDependencyGraph().GetTopologicallySortedProjects();
            List<IMethodSymbol> untestedMethodList = new List<IMethodSymbol>();
            List<IMethodSymbol> testedMethodList = new List<IMethodSymbol>();

            foreach (ProjectId projectId in projectList)
            {
                Project project = solution.GetProject(projectId);

                IEnumerable<IMethodSymbol> methodList = GetMethods(project);

                IEnumerable<IGrouping<bool, IMethodSymbol>> testedGrouping = methodList.GroupBy(method => IsMethodTested(method, solution));

                untestedMethodList.AddRange(testedGrouping.Where(group => group.Key == false).SelectMany(method => method));
                testedMethodList.AddRange(testedGrouping.Where(group => group.Key == true).SelectMany(method => method));
            }
            Console.WriteLine("Untested Methods: \n");
            foreach (IMethodSymbol untestedMethod in untestedMethodList)
            {
                Console.WriteLine($"{GetFullMetadataName(untestedMethod.ContainingType)} {untestedMethod.Name}");
            }
            Console.WriteLine("Tested Methods: \n");
            foreach (IMethodSymbol testedMethod in testedMethodList)
            {
                Console.WriteLine($"{GetFullMetadataName(testedMethod.ContainingType)} {testedMethod.Name}");
            }

            Console.ReadKey();
        }

        private static IEnumerable<IMethodSymbol> GetMethods(Project project) // for tree? have overrides taking different types that all feed into syntax tree?
        {
            //https://stackoverflow.com/questions/39235100/get-dependencies-between-classes-in-roslyn
            Compilation compilation = project.GetCompilationAsync().Result;
            // option 1
            IEnumerable<IMethodSymbol> methodSymbols = compilation.GetSymbolsWithName(name => true, SymbolFilter.Member).OfType<IMethodSymbol>();

            ////option 2
            //IEnumerable<MethodDeclarationSyntax> methodSymbols2 = compilation.SyntaxTrees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>());

            return methodSymbols;
        }

        private static IEnumerable<IMethodSymbol> GetReferencingMethods(IMethodSymbol methodSymbol, Solution solution)
        {
            //methodSymbol.GetAttributes().Where(ad => ad.AttributeClass.AllInterfaces == typeof());
            IEnumerable<SymbolCallerInfo> symbolCallers = SymbolFinder.FindCallersAsync(methodSymbol, solution).Result;
            IEnumerable<IMethodSymbol> callingMethods = symbolCallers.Select(caller => caller.CallingSymbol).OfType<IMethodSymbol>();
            return callingMethods;

        }

        private static bool IsMethodTested(IMethodSymbol methodSymbol, Solution solution)
        {
            bool isMethodTested = false;
            if (IsMethodUnitTest(methodSymbol))
            {
                isMethodTested = true;
            }
            else
            {
                IEnumerable<IMethodSymbol> referencingMethodList = GetReferencingMethods(methodSymbol, solution);
                isMethodTested = referencingMethodList.Any(refMethod => IsMethodUnitTest(refMethod));
            }

            return isMethodTested;
        }

        private static bool IsMethodUnitTest(IMethodSymbol methodSymbol)
        {
            // ultimately, It would be cool to inject a configFacade. in the console app, i could construct a configuration facade based on arguments and register it with ninject
            List<string> testTypes = new List<string>() {
                //"Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod"
                "TestMethod" // Currently can't get a fully qualified name for external types
            }; // get this from configuration or pass it to this method

            // with reflection, we could just use isAssignableFrom, but that doesn't exist for symbols and we can't convert symbols to type objects
            //https://stackoverflow.com/questions/33965410/how-to-compare-a-microsoft-codeanalysis-itypesymbol-to-a-system-type
            //https://stackoverflow.com/questions/39708316/roslyn-is-a-inamedtypesymbol-of-a-class-or-subclass-of-a-given-type

            // I take fully-qualified names of test attributes and compare on those. Easier to configure, and roslyn has public fully qualified name support quasi-planned
            // additionally, it doesn't require me to create or pass a compilation. However, there is the rare case that it could confuse types that have the same 
            // fullname. It also makes comparison a little derpier because I can't use built in comparison like ClassifyConversion
            IEnumerable<string> attributeTypeStrings = methodSymbol.GetAttributes().SelectMany(ad => GetAssignableTypes(ad.AttributeClass).Select(s => GetFullMetadataName(s)));
            bool doesHaveTestAttribute = testTypes.Intersect(attributeTypeStrings).Any();

            return doesHaveTestAttribute;
        }

        private static IEnumerable<INamedTypeSymbol> GetAssignableTypes(INamedTypeSymbol typeSymbol)
        {
            List<INamedTypeSymbol> typesAssignableTo = new List<INamedTypeSymbol>();

            // assignable as self
            typesAssignableTo.Add(typeSymbol);

            // assignable as inherited classes
            IEnumerable<INamedTypeSymbol> inheritedClasses = GetClassInheritanceChain(typeSymbol);
            typesAssignableTo.AddRange(inheritedClasses);

            // assignable as interfaces
            typesAssignableTo.AddRange(typeSymbol.AllInterfaces);

            return typesAssignableTo;
        }

        private static IEnumerable<INamedTypeSymbol> GetClassInheritanceChain(INamedTypeSymbol typeSymbol)
        {
            List<INamedTypeSymbol> inheritedClassChain = new List<INamedTypeSymbol>();
            INamedTypeSymbol currentSymbol = typeSymbol.BaseType;
            while (currentSymbol != null)
            {
                inheritedClassChain.Add(typeSymbol);
            }

            return inheritedClassChain;
        }

        private static ITypeSymbol TypeToSymbol(Type type, Compilation compilation)
        {
            // can't convert type symbols 
            //https://stackoverflow.com/questions/33965410/how-to-compare-a-microsoft-codeanalysis-itypesymbol-to-a-system-type
            //https://stackoverflow.com/questions/39708316/roslyn-is-a-inamedtypesymbol-of-a-class-or-subclass-of-a-given-type
            INamedTypeSymbol typeSymbol = compilation.GetTypeByMetadataName(type.FullName);

            return typeSymbol;
        }


        public static string GetFullMetadataName(ISymbol symbol)
        {
            //Source: https://stackoverflow.com/questions/27105909/get-fully-qualified-metadata-name-in-roslyn
            //Source: https://stackoverflow.com/questions/36407563/how-can-i-get-the-fully-qualified-namespace-from-a-using-directive-in-roslyn
            SymbolDisplayFormat displayFormat = new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions:
                    SymbolDisplayMemberOptions.IncludeContainingType |
                    SymbolDisplayMemberOptions.IncludeExplicitInterface,
                parameterOptions:
                    SymbolDisplayParameterOptions.IncludeParamsRefOut |
                    SymbolDisplayParameterOptions.IncludeType,
                // Not showing the name is important because we visit parameters to display their
                // types.  If we visited their types directly, we wouldn't get ref/out/params.
                miscellaneousOptions:
                    SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                    SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                    SymbolDisplayMiscellaneousOptions.UseAsterisksInMultiDimensionalArrays |
                    SymbolDisplayMiscellaneousOptions.UseErrorTypeSymbolName
            );
            string formattedTypeName = symbol.OriginalDefinition.ToDisplayString(displayFormat);
            return formattedTypeName;
            
        }

        private static bool IsRootNamespace(ISymbol symbol)
        {
            INamespaceSymbol s = null;
            return ((s = symbol as INamespaceSymbol) != null) && s.IsGlobalNamespace;
        }
    }
}
