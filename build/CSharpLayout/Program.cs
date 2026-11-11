using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length == 0)
{
    Console.Error.WriteLine("Pass at least one directory to check.");
    return 2;
}

var violations = 0;
foreach (var directory in args)
{
    foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
    {
        if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
        {
            continue;
        }

        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            var accessors = property.AccessorList;
            if (property.Parent is InterfaceDeclarationSyntax || property.Modifiers.Any(token => token.RawKind == (int)SyntaxKind.AbstractKeyword) ||
                accessors is null || accessors.Accessors.Count == 0 ||
                accessors.Accessors.Any(accessor => accessor.Body is not null || accessor.ExpressionBody is not null))
            {
                continue;
            }

            var identifier = property.Identifier.GetLocation().GetLineSpan().StartLinePosition;
            var end = accessors.CloseBraceToken.GetLocation().GetLineSpan().StartLinePosition;
            if (identifier.Line != end.Line)
            {
                Console.Error.WriteLine($"{file}({identifier.Line + 1},{identifier.Character + 1}): simple auto-properties must be on one line.");
                violations++;
            }
        }

        foreach (var declaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            var lines = new HashSet<int>();
            foreach (var member in declaration.Members)
            {
                var location = member.Identifier.GetLocation().GetLineSpan().StartLinePosition;
                if (!lines.Add(location.Line))
                {
                    Console.Error.WriteLine($"{file}({location.Line + 1},{location.Character + 1}): enum members must each be on a separate line.");
                    violations++;
                }
            }
        }
    }
}

return violations == 0 ? 0 : 1;
