using System.Text.RegularExpressions;

namespace GroupSplit.Cli.Test;

/// <summary>
/// Every <c>groupsplit</c> command in a <c>bash</c> block of <c>docs/cli.md</c> or
/// <c>skills/</c> parses against the real command tree. Agents copy these literally, and an
/// example with an option the command does not have exits 3 for every one of them.
/// </summary>
/// <remarks>
/// Parsed, not run: nothing is sent. Placeholders -- <c>&lt;group-id&gt;</c>, <c>$G</c>,
/// <c>7c1e...</c> -- become a guid, which every id argument accepts, and a trailing comment,
/// pipe or redirect is cut off first.
/// </remarks>
public sealed partial class DocumentedExampleTests
{
    public static TheoryData<string, string> Examples()
    {
        var root = Path.GetDirectoryName(AppContext.BaseDirectory)!;
        while (!File.Exists(Path.Combine(root, "group-split.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No repository root above the tests.");
        }

        var files = Directory
            .EnumerateFiles(Path.Combine(root, "skills"), "*.md", SearchOption.AllDirectories)
            .Prepend(Path.Combine(root, "docs", "cli.md"));

        var examples = new TheoryData<string, string>();

        foreach (var file in files)
        {
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            var fenced = false;
            string? command = null;
            var start = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    fenced = !fenced && line == "```bash";
                    continue;
                }

                if (!fenced)
                {
                    continue;
                }

                if (command is null)
                {
                    if (!line.StartsWith("groupsplit ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    command = line;
                    start = i + 1;
                }
                else
                {
                    command += " " + line;
                }

                if (command.EndsWith('\\'))
                {
                    command = command[..^1];
                    continue;
                }

                examples.Add($"{name}:{start}", command);
                command = null;
            }
        }

        return examples;
    }

    [Fact]
    public void The_examples_are_found()
    {
        Assert.True(Examples().Count > 100, "The extraction found too few examples to trust.");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Documented_example_parses(string where, string example)
    {
        var words = ConfirmCommandTests.PosixShell.Split(Trailer().Replace(example, ""))
            .Skip(1)
            .Select(word => Placeholder().Replace(word, Guid.NewGuid().ToString()))
            .ToArray();

        var parsed = CliApplication.CreateRootCommand().Parse(words);

        Assert.True(parsed.Errors.Count == 0,
            $"{where}: {example}\n{string.Join("\n", parsed.Errors.Select(error => error.Message))}");
    }

    /// <summary>A comment, a pipe or a redirect, and everything after it.</summary>
    [GeneratedRegex(@"\s+(#|\||>>?)\s.*$")]
    private static partial Regex Trailer();

    /// <summary><c>&lt;group-id&gt;</c>, <c>$G</c>, <c>7c1e...</c>, <c>3f25c1a8-...-444455556666</c>.</summary>
    [GeneratedRegex(@"<[a-z-]+>|\$[A-Z_]+|[0-9a-f]{4,8}-?\.\.\.(-[0-9a-f]{12})?")]
    private static partial Regex Placeholder();
}
