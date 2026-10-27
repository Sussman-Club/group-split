// Run by build/PdfJs.targets with the repository's .NET 10 SDK. No external tools or packages.
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

if (args.Length != 4)
    throw new ArgumentException("Expected version, SHA-512 integrity, asset directory, and cache directory.");

var (version, integrity, destination, cache) = (args[0], args[1], Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
var marker = Path.Combine(destination, ".restored");
if (File.Exists(marker))
{
    var manifest = File.ReadAllLines(marker);
    if (manifest.Length > 2 && manifest[0] == version && manifest[1] == integrity
        && manifest.Skip(2).All(name => File.Exists(Path.Combine(destination, name))))
        return;
}

Directory.CreateDirectory(cache);
var archive = Path.Combine(cache, $"pdfjs-dist-{version}.tgz");
if (!File.Exists(archive))
{
    Console.WriteLine($"Restoring PDF.js {version} from the npm registry...");
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var bytes = await client.GetByteArrayAsync($"https://registry.npmjs.org/pdfjs-dist/-/pdfjs-dist-{version}.tgz");
    Verify(bytes);
    await File.WriteAllBytesAsync(archive, bytes);
}
var package = await File.ReadAllBytesAsync(archive);
Verify(package);

// Extract only runtime assets and their license notices; never extract package scripts.
var staging = Path.Combine(cache, "assets-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(staging);
try
{
    using var compressed = new GZipStream(new MemoryStream(package), CompressionMode.Decompress);
    using var tar = new TarReader(compressed);
    while (tar.GetNextEntry() is { } entry)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            continue;
        var name = entry.Name;
        string? relative = name switch
        {
            "package/build/pdf.min.mjs" => "pdf.min.mjs",
            "package/build/pdf.worker.min.mjs" => "pdf.worker.min.mjs",
            "package/LICENSE" => "LICENSE",
            _ when name.StartsWith("package/cmaps/", StringComparison.Ordinal)
                || name.StartsWith("package/standard_fonts/", StringComparison.Ordinal)
                || name.StartsWith("package/wasm/", StringComparison.Ordinal) => name[8..],
            _ => null
        };
        if (relative is null) continue;
        var output = Path.GetFullPath(Path.Combine(staging, relative));
        if (!output.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Package asset escapes its extraction directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        entry.ExtractToFile(output, overwrite: false);
        File.SetLastWriteTimeUtc(output, DateTime.UtcNow);
    }
    if (!File.Exists(Path.Combine(staging, "pdf.min.mjs"))
        || !File.Exists(Path.Combine(staging, "pdf.worker.min.mjs")))
        throw new InvalidDataException("PDF.js package is missing the renderer or worker.");
    // Only this dedicated, generated asset directory is replaced.
    if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    Directory.Move(staging, destination);
    File.WriteAllLines(marker, new[] { version, integrity }.Concat(
        Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path)).Order(StringComparer.Ordinal)));
    Console.WriteLine($"PDF.js {version}: restored verified preview assets.");
}
finally
{
    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
}

void Verify(byte[] bytes)
{
    if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(bytes), Convert.FromBase64String(integrity)))
        throw new InvalidDataException("PDF.js package SHA-512 integrity check failed. Remove the cached archive and retry.");
}
