using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TinadecTools.Generators;

[Generator]
public sealed class ToolFunctionGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var toolFunctionAttributeSymbol = "TinadecTools.Abstractions.ToolFunctionAttribute";

        var methods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                toolFunctionAttributeSymbol,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, _) => ctx)
            .Collect();

        context.RegisterSourceOutput(methods, static (spc, items) =>
        {
            var source = new StringBuilder();
            source.AppendLine("using System.Text.Json.Serialization.Metadata;");
            source.AppendLine("using TinadecTools.Abstractions;");
            source.AppendLine();
            source.AppendLine("namespace TinadecTools.Abstractions;");
            source.AppendLine();
            source.AppendLine("internal static partial class GeneratedToolRegistry");
            source.AppendLine("{");
            source.AppendLine("    public static void RegisterAll()");
            source.AppendLine("    {");

            foreach (var item in items)
            {
                if (item.TargetSymbol is not IMethodSymbol method)
                {
                    continue;
                }

                if (!method.IsStatic || method.Parameters.Length != 2)
                {
                    continue;
                }

                if (method.Parameters[1].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::System.Threading.CancellationToken")
                {
                    continue;
                }

                var attr = method.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "TinadecTools.Abstractions.ToolFunctionAttribute");

                if (attr?.ConstructorArguments.Length != 1)
                {
                    continue;
                }

                var toolId = attr.ConstructorArguments[0].Value as string;
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    continue;
                }

                var requiresApproval = attr.NamedArguments.FirstOrDefault(a => a.Key == "RequiresApproval").Value.Value as bool? ?? false;

                var argsType = method.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var resultType = GetResultType(method.ReturnType);
                if (resultType is null)
                {
                    continue;
                }

                var containingType = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var contextType = $"{method.ContainingType.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{method.ContainingType.Name}JsonContext";
                var contextVar = $"{method.ContainingType.Name.ToLowerInvariant()}_{method.Name.ToLowerInvariant()}Json";

                source.AppendLine($"""        var {contextVar} = new {contextType}();""");
                source.AppendLine($"""        ToolRegistry.Register<{argsType}, {resultType}>("{escape(toolId!)}", {containingType}.{method.Name}, (JsonTypeInfo<{argsType}>) {contextVar}.GetTypeInfo(typeof({argsType}))!, (JsonTypeInfo<{resultType}>) {contextVar}.GetTypeInfo(typeof({resultType}))!, requiresApproval: {requiresApproval.ToString().ToLowerInvariant()});""");
            }

            source.AppendLine("    }");
            source.AppendLine("}");
            spc.AddSource("ToolFunctionRegistry.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
        });
    }

    private static string? GetResultType(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol namedType || namedType.Name != "ValueTask" || namedType.TypeArguments.Length != 1)
        {
            return null;
        }

        return namedType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static string escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
