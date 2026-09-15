using Microsoft.AspNetCore.Components.Forms;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// A browser file whose bytes have been copied while the InputFile element was still alive.
/// </summary>
/// <remarks>
/// <see cref="IBrowserFile"/> is normally backed by the browser's input element. A dialog
/// returns its result after that element is disposed, so the original handle cannot safely be
/// opened by the page that saves the result. This small adapter keeps the same command
/// contract while making the returned file independent of the component lifetime.
/// </remarks>
public sealed class BufferedBrowserFile : IBrowserFile
{
    private readonly byte[] _content;

    public BufferedBrowserFile(string name, string contentType, DateTimeOffset lastModified,
        byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        Name = name;
        ContentType = contentType;
        LastModified = lastModified;
        _content = content;
    }

    public string Name { get; }
    public DateTimeOffset LastModified { get; }
    public long Size => _content.LongLength;
    public string ContentType { get; }

    public Stream OpenReadStream(long maxAllowedSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Size > maxAllowedSize)
            throw new IOException($"The file size ({Size} bytes) exceeds the maximum allowed size of {maxAllowedSize} bytes.");

        return new MemoryStream(_content, writable: false);
    }
}
