using System;
using System.Linq;
using JetBrains.Annotations;
using Nuke.Common;
using Nuke.Common.IO;

namespace NMica.Tests.Utils
{
  /// <summary>
  /// A mounted path inside container that is linked to a path in the host
  /// </summary>
  public class MountedPath
  {
    public AbsolutePath HostPath { get; }
    private readonly string _path;

    internal MountedPath(string path, AbsolutePath hostPath)
    {
      this._path = PathConstruction.NormalizePath(path);
      HostPath = hostPath;
    }


    public static implicit operator string([CanBeNull] MountedPath path) => path?.ToString();

    public MountedPath Parent => IsWinRoot(this._path.TrimEnd('\\')) || IsUncRoot(this._path) || IsUnixRoot(this._path) ? (MountedPath) null : this / "..";

    public static MountedPath operator /(MountedPath left, [CanBeNull] string right) => new MountedPath(PathConstruction.Combine(left.NotNull("left != null"), right), left.HostPath / right);
    internal static bool IsWinRoot([CanBeNull] string root) => root != null && root.Length == 2 && char.IsLetter(root[0]) && root[1] == ':';

    internal static bool IsUnixRoot([CanBeNull] string root) => root != null && root.Length == 1 && root[0] == '/';
    internal static bool IsUncRoot([CanBeNull] string root) => root != null && root.Length >= 3 && (root[0] == '\\' && root[1] == '\\') && root.Skip(2).All(char.IsLetterOrDigit);
    internal static bool HasUnixRoot([CanBeNull] string path) => IsUnixRoot(GetHeadPart(path, 1));

    internal static bool HasUncRoot([CanBeNull] string path) => IsUncRoot(GetHeadPart(path, 3));

    internal static bool HasWinRoot([CanBeNull] string path) => IsWinRoot(GetHeadPart(path, 2));
    private static string GetHeadPart([CanBeNull] string str, int count) => new string((str ?? string.Empty).Take(count).ToArray());

    protected bool Equals(MountedPath other)
    {
      StringComparison comparisonType = HasWinRoot(this._path) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
      return string.Equals(this._path, other._path, comparisonType) && object.Equals(this.HostPath, other.HostPath);
    }

    public override bool Equals(object obj)
    {
      if (obj == null)
        return false;
      if (this == obj)
        return true;
      return !(obj.GetType() != this.GetType()) && this.Equals((MountedPath) obj);
    }

    public override int GetHashCode()
    {
      return HashCode.Combine(_path, HostPath);
    }

    public override string ToString() => this._path;

  }
}