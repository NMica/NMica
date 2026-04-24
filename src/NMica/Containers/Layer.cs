using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NMica.Containers;

/// <summary>
/// A single image layer: a gzipped POSIX-PAX tar of a directory tree, with both the compressed
/// digest (transport hash, goes in manifest) and uncompressed digest (diff-id, goes in image config)
/// computed in a single streaming pass.
/// </summary>
/// <remarks>
/// Mirrors <c>Microsoft.NET.Build.Containers.Layer</c>. The one-pass hashing via
/// <see cref="HashDigestGZipStream"/> is the same trick the SDK uses — SHA the uncompressed tar
/// while we gzip it, then a second pass over the finished blob file gives the compressed digest.
/// </remarks>
public sealed class Layer
{
    /// <summary>Path to the gzipped tarball on disk.</summary>
    public string BackingFile { get; }

    public Descriptor Descriptor { get; }

    private Layer(string backingFile, Descriptor descriptor)
    {
        BackingFile = backingFile;
        Descriptor = descriptor;
    }

    public Stream OpenBackingFile() => File.OpenRead(BackingFile);

    /// <summary>
    /// Build a layer from a host directory. The entire subtree is tarred (files and directory
    /// entries), rooted at <paramref name="containerPath"/> inside the tar. Gzipped output is
    /// staged in the system temp dir; the returned <see cref="Layer"/> holds the path.
    /// </summary>
    /// <param name="directory">Host directory to pack.</param>
    /// <param name="containerPath">Target path inside the container (e.g. <c>/app</c>).</param>
    /// <param name="manifestMediaType">Drives whether the layer is tagged as OCI or Docker.</param>
    public static Layer FromDirectory(string directory, string containerPath, string manifestMediaType)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var layerMediaType = MediaTypes.LayerMediaTypeFor(manifestMediaType);
        var backingFile = Path.Combine(Path.GetTempPath(), $"nmica-layer-{Guid.NewGuid():N}.tar.gz");

        string uncompressedDigest;
        long compressedSize;

        // Phase 1: write tar → gzip → disk, SHA-ing the uncompressed tar stream inline.
        using (var fs = File.Create(backingFile))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (var gzip = new GZipStream(fs, CompressionLevel.Optimal))
        using (var hashing = new HashDigestStream(gzip, hasher))
        using (var tar = new TarWriter(hashing, TarEntryFormat.Pax, leaveOpen: true))
        {
            WriteDirectoryToTar(tar, directory, containerPath);
            uncompressedDigest = "sha256:" + HexLower(hasher.GetHashAndReset());
            // gzip + hashing + fs all flush on dispose; order matters — TarWriter dispose flushes
            // the final entry, then gzip dispose flushes the gzip footer.
        }

        // Phase 2: SHA the compressed file on disk. One extra read pass — the SDK does the same.
        using (var fs = File.OpenRead(backingFile))
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(fs);
            compressedSize = fs.Length;
            var descriptor = new Descriptor(layerMediaType, "sha256:" + HexLower(hash), compressedSize)
            {
                UncompressedDigest = uncompressedDigest,
            };
            return new Layer(backingFile, descriptor);
        }
    }

    private static void WriteDirectoryToTar(TarWriter tar, string hostRoot, string containerRoot)
    {
        containerRoot = NormaliseTarPath(containerRoot);

        // Emit the root directory entry first so extractors know the working directory exists.
        if (!string.IsNullOrEmpty(containerRoot))
        {
            tar.WriteEntry(MakeDirEntry(containerRoot, hostRoot));
        }

        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.System };
        foreach (var path in Directory.EnumerateFileSystemEntries(hostRoot, "*", enumeration))
        {
            var relative = Path.GetRelativePath(hostRoot, path).Replace('\\', '/');
            var entryName = string.IsNullOrEmpty(containerRoot) ? relative : $"{containerRoot}/{relative}";
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Directory) != 0)
            {
                tar.WriteEntry(MakeDirEntry(entryName, path));
            }
            else
            {
                tar.WriteEntry(MakeFileEntry(entryName, path));
            }
        }
    }

    private static PaxTarEntry MakeDirEntry(string name, string hostPath) =>
        new(TarEntryType.Directory, EnsureTrailingSlash(name))
        {
            Mode = UnixMode(hostPath, isDirectory: true),
            ModificationTime = DateTimeOffset.UtcNow,
        };

    private static PaxTarEntry MakeFileEntry(string name, string hostPath)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            Mode = UnixMode(hostPath, isDirectory: false),
            ModificationTime = File.GetLastWriteTimeUtc(hostPath),
        };
        using var fs = File.OpenRead(hostPath);
        entry.DataStream = new MemoryStream(ReadAll(fs));
        return entry;
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Map host filesystem permissions to sane container-side Unix mode. On Unix hosts, preserve
    /// the execute bit from the file; on Windows hosts, pick a universal default (directories
    /// get 0755, files 0644) — the SDK does the same trick.
    /// </summary>
    private static UnixFileMode UnixMode(string path, bool isDirectory)
    {
        if (isDirectory) return (UnixFileMode)0b111_101_101; // 0755

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return (UnixFileMode)0b110_100_100; // 0644

        try
        {
            var fi = new FileInfo(path);
            var mode = File.GetUnixFileMode(fi.FullName);
            // If the host file is executable for anyone, preserve executability for all; otherwise
            // standard-shape 0644. Matches SDK's DetermineFileMode.
            var anyExec = (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            return anyExec ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100;
        }
        catch
        {
            return (UnixFileMode)0b110_100_100;
        }
    }

    private static string NormaliseTarPath(string p) =>
        p.Replace('\\', '/').Trim('/');

    private static string EnsureTrailingSlash(string p) =>
        p.EndsWith('/') ? p : p + "/";

    private static string HexLower(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i * 2] = Hex(b >> 4);
            chars[i * 2 + 1] = Hex(b & 0xF);
        }
        return new string(chars);
    }

    private static char Hex(int n) => (char)(n < 10 ? '0' + n : 'a' + (n - 10));
}

/// <summary>
/// A write-through stream that hashes every byte passed to it before forwarding to the inner
/// stream. Used to compute the uncompressed-tar SHA while we're gzipping it. Same pattern as
/// SDK's internal <c>HashDigestGZipStream</c>, minus the gzip responsibility (we compose from
/// the outside instead).
/// </summary>
internal sealed class HashDigestStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hasher;

    public HashDigestStream(Stream inner, IncrementalHash hasher)
    {
        _inner = inner;
        _hasher = hasher;
    }

    public override bool CanWrite => true;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _hasher.AppendData(buffer, offset, count);
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hasher.AppendData(buffer);
        _inner.Write(buffer);
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
