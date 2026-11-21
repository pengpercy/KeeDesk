using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace KeePassModern.Generators;

[Generator]
public class ViewLocatorGenerator : IIncrementalGenerator
{
    private sealed class NamedTypeSymbolComparer : IEqualityComparer<INamedTypeSymbol>
    {
        public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y)
            => SymbolEqualityComparer.Default.Equals(x, y);

        public int GetHashCode(INamedTypeSymbol obj)
            => SymbolEqualityComparer.Default.GetHashCode(obj);
    }

    private static readonly IEqualityComparer<INamedTypeSymbol> TypeComparer = new NamedTypeSymbolComparer();

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var viewModels = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (s, _) => IsClassEndingWith(s, "ViewModel"),
                transform: (ctx, _) => GetClassSymbol(ctx))
            .Where(m => m != null);

        var views = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (s, _) => IsClassEndingWith(s, "View") || IsClassEndingWith(s, "Window"),
                transform: (ctx, _) => GetClassSymbol(ctx))
            .Where(m => m != null);

        var compilationAndClasses = context.CompilationProvider
            .Combine(viewModels.Collect())
            .Combine(views.Collect());

        context.RegisterSourceOutput(compilationAndClasses, (spc, source) =>
        {
            Execute(spc, source.Left.Right, source.Right);
        });
    }

    private static bool IsClassEndingWith(SyntaxNode node, string suffix)
        => node is ClassDeclarationSyntax c && c.Identifier.Text.EndsWith(suffix);

    private static INamedTypeSymbol? GetClassSymbol(GeneratorSyntaxContext context)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration);
        if (symbol is INamedTypeSymbol namedType &&
            !namedType.IsAbstract &&
            namedType.DeclaredAccessibility == Accessibility.Public)
        {
            return namedType;
        }
        return null;
    }

    private static void Execute(
        SourceProductionContext context,
        System.Collections.Immutable.ImmutableArray<INamedTypeSymbol?> viewModels,
        System.Collections.Immutable.ImmutableArray<INamedTypeSymbol?> views)
    {
        var uniqueViewModels = viewModels
            .Where(vm => vm is not null)
            .Select(vm => vm!)
            .Distinct(TypeComparer)
            .ToList();

        var uniqueViews = views
            .Where(v => v is not null)
            .Select(v => v!)
            .Distinct(TypeComparer)
            .ToList();

        var mappings = new List<(INamedTypeSymbol ViewModel, INamedTypeSymbol View)>();

        foreach (var vm in uniqueViewModels)
        {
            var coreName = vm.Name.Substring(0, vm.Name.Length - "ViewModel".Length);
            var view = uniqueViews.FirstOrDefault(v => v.Name == coreName)
                       ?? uniqueViews.FirstOrDefault(v => v.Name == coreName + "View");

            if (view != null)
            {
                mappings.Add((vm, view));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using Avalonia.Controls;");
        sb.AppendLine();
        sb.AppendLine("namespace KeePassModern;");
        sb.AppendLine();
        sb.AppendLine("public partial class ViewLocator");
        sb.AppendLine("{");
        sb.AppendLine("    private Control? AutoBuild(object param)");
        sb.AppendLine("    {");
        sb.AppendLine("        return param switch");
        sb.AppendLine("        {");
        foreach (var mapping in mappings)
        {
            sb.AppendLine($"            {mapping.ViewModel.ToDisplayString()} => new {mapping.View.ToDisplayString()}(),");
        }
        sb.AppendLine("            _ => null");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("ViewLocator.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }
}
