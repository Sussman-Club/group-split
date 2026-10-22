using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GroupSplit.API.Services.PersonalData;

/// <summary>Writes ZIP entries and hashes the exact uncompressed bytes without buffering them.</summary>
internal sealed class ExportArchive(ZipArchive zip)
{
    private readonly List<ExportFile> _manifest = [];

    internal async Task Complete(CancellationToken ct)
    {
        await using var entry = zip.CreateEntry("manifest.json").Open();
        await JsonSerializer.SerializeAsync(entry, new { SchemaVersion = 1, Algorithm = "SHA-256", Files = _manifest }, ExportJson.Options, ct);
    }

    private sealed record ExportFile(string Path, long Bytes, string Sha256);
    internal async Task Write(string path, Func<Stream, Task> write,
        CancellationToken ct)
    {
        await using var entry = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var hashed = new HashingWriteStream(entry, hash);
        await write(hashed);
        await hashed.FlushAsync(ct);
        _manifest.Add(new(path, hashed.Length, Convert.ToHexStringLower(hash.GetHashAndReset())));
    }

    private sealed class HashingWriteStream(Stream target, IncrementalHash hash) : Stream
    {
        private long _bytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes;
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override Task FlushAsync(CancellationToken ct) => target.FlushAsync(ct);
        public override void Write(byte[] buffer, int offset, int count)
        { hash.AppendData(buffer, offset, count); target.Write(buffer, offset, count); _bytes += count; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { hash.AppendData(buffer.Span); await target.WriteAsync(buffer, ct); _bytes += buffer.Length; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

}
