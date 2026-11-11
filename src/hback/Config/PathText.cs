namespace HBack.Config;

/// <summary>
/// Small rules for the paths a person writes in the configuration and for the paths hback stores.
/// </summary>
public static class PathText
{
    /// <summary>Expands a leading "~" to the home folder, and %NAME% to an environment variable.</summary>
    public static string Expand(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return path;
    }

    /// <summary>"C:\code\" becomes "C:\code". A root such as "C:\" or "/" stays as it is.</summary>
    public static string TrimEndSeparators(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        return fullPath.Length > root.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }

    /// <summary>
    /// The path a source file gets inside the backup. It is the full source path with the drive colon
    /// and the leading slash dropped, so two sources can never collide and the original place of every
    /// file can be read from the backup itself.
    /// "C:\code\app\a.cs" becomes "C/code/app/a.cs", "/home/me/a.cs" becomes "home/me/a.cs", and
    /// "\\nas\share\a.cs" becomes "UNC/nas/share/a.cs".
    /// </summary>
    public static string ToBackupPath(string fullPath)
    {
        // A backslash is a legal character in a Unix file name, so it is a separator on Windows only.
        var p = Path.DirectorySeparatorChar == '\\' ? fullPath.Replace('\\', '/') : fullPath;
        if (p.StartsWith("//", StringComparison.Ordinal))
            p = "UNC/" + p.TrimStart('/');
        else if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0]) && Path.DirectorySeparatorChar == '\\')
            p = p[0] + p[2..];
        return p.Trim('/');
    }

    /// <summary>The same path with forward slashes, used to match exclude patterns.</summary>
    public static string ToMatchPath(string fullPath)
        => (Path.DirectorySeparatorChar == '\\' ? fullPath.Replace('\\', '/') : fullPath).Trim('/');

    public static string ParentOf(string backupPath)
    {
        var i = backupPath.LastIndexOf('/');
        return i < 0 ? "" : backupPath[..i];
    }

    public static string NameOf(string backupPath)
    {
        var i = backupPath.LastIndexOf('/');
        return i < 0 ? backupPath : backupPath[(i + 1)..];
    }

    public static string Join(string a, string b)
        => a.Length == 0 ? b : b.Length == 0 ? a : a + "/" + b;

    /// <summary>Splits a multi-line setting into its lines, without blank lines and "#" comments.</summary>
    public static List<string> Lines(string? text)
        => (text ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '#')
            .ToList();
}
