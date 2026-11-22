using System;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace KeeDesk.Generators;

[Generator]
public class BuildInfoGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
        {
            var buildDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var source = $$"""
                #nullable enable
                namespace KeeDesk;

                public static class BuildInfo
                {
                    public const string BuildDate = "{{buildDate}}";
                    public const string AppName = "KeeDesk";
                }
                """;
            ctx.AddSource("BuildInfo.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }
}
