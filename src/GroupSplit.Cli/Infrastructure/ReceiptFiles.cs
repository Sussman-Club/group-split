using GroupSplit.Cli.Api;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>Local file handling shared by the receipt upload and download commands.</summary>
public static class ReceiptFiles
{
    public static string ContentTypeFor(string path)
    {
        return Path.GetExtension(Path.GetFileName(path)).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    public static FileStream OpenForUpload(string path)
    {
        try
        {
            return new FileStream(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or FileNotFoundException
            or DirectoryNotFoundException
            or UnauthorizedAccessException
            or IOException)
        {
            throw CliException.Input(
                $"Could not read receipt file '{path}': {exception.Message}",
                "Check the path and that the file is readable.");
        }
    }

    public static async Task<string> DownloadAsync(
        HttpClient client,
        string relativeUrl,
        string destination,
        CancellationToken cancellationToken)
    {
        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(destination);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw CliException.Input(
                $"The download destination '{destination}' is not a valid path: {exception.Message}",
                "Choose a writable file path.");
        }

        if (File.Exists(fullPath))
        {
            throw CliException.Input(
                $"The download destination already exists: '{fullPath}'.",
                "Choose a new path so an existing file is not overwritten.");
        }

        using var response = await client.GetAsync(
            relativeUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = response.Content is null
                ? null
                : await response.Content.ReadAsStringAsync(cancellationToken);

            var headers = response.Headers
                .Concat(response.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IEnumerable<string>)group.SelectMany(pair => pair.Value).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            throw new ApiException(
                $"The HTTP status code of the response was not expected ({(int)response.StatusCode}).",
                (int)response.StatusCode,
                body,
                headers,
                null);
        }

        var created = false;
        try
        {
            await using var output = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true);
            created = true;

            await response.Content.CopyToAsync(output, cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException
            or DirectoryNotFoundException
            or UnauthorizedAccessException
            or IOException)
        {
            if (created)
            {
                TryDelete(fullPath);
            }
            throw CliException.Input(
                $"Could not write receipt file '{fullPath}': {exception.Message}",
                "Choose a writable destination path.");
        }

        return fullPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Preserve the original write error; a partial local file is still not a
            // successful download, and the next invocation will report its existence.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }
}
