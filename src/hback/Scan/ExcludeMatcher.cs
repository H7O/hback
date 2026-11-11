using System.Text;
using System.Text.RegularExpressions;

namespace HBack.Scan;

/// <summary>
/// Decides which files and folders a backup leaves out. A pattern reads like a .gitignore line:
/// <list type="bullet">
/// <item><c>node_modules</c> or <c>*.tmp</c>: no slash, so it matches that name in any folder.</item>
/// <item><c>bin/</c>: a trailing slash, so it matches folders only.</item>
/// <item><c>docs/drafts</c> or <c>/build</c>: a slash elsewhere, so it is a path. It is matched from the
/// top of each source folder, and against the full path, so <c>C:/code/old/</c> works too.</item>
/// <item><c>*</c> matches within a name, <c>**</c> across folders, <c>?</c> one character, <c>[ab]</c> one of a set.</item>
/// </list>
/// An excluded folder is not entered at all. There is no "!" to bring a file back.
/// </summary>
public sealed class ExcludeMatcher
{
    private sealed record PathRule(Regex Regex, bool FoldersOnly);

    private readonly HashSet<string> _names;
    private readonly HashSet<string> _folderNames;
    private readonly List<PathRule> _nameRules = [];
    private readonly List<PathRule> _pathRules = [];

    public ExcludeMatcher(IEnumerable<string> patterns, bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _names = new HashSet<string>(comparer);
        _folderNames = new HashSet<string>(comparer);
        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : 0);

        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().Replace('\\', '/');
            if (pattern.Length == 0 || pattern[0] == '#') continue;

            var foldersOnly = pattern.EndsWith('/');
            var anchored = pattern.StartsWith('/');
            pattern = pattern.Trim('/');
            if (pattern.Length == 0) continue;

            var isPath = anchored || pattern.Contains('/');
            if (!isPath && pattern.IndexOfAny(['*', '?', '[']) < 0)
            {
                (foldersOnly ? _folderNames : _names).Add(pattern);
                continue;
            }

            var rule = new PathRule(new Regex("^" + GlobToRegex(pattern) + "$", options), foldersOnly);
            (isPath ? _pathRules : _nameRules).Add(rule);
        }
    }

    /// <param name="name">The file or folder name.</param>
    /// <param name="relativePath">Its path from the top of the source folder, with forward slashes.</param>
    /// <param name="fullPath">Its full path with forward slashes and no leading slash.</param>
    public bool IsExcluded(string name, string relativePath, string fullPath, bool isFolder)
    {
        if (_names.Contains(name) || (isFolder && _folderNames.Contains(name)))
            return true;

        foreach (var rule in _nameRules)
            if ((isFolder || !rule.FoldersOnly) && rule.Regex.IsMatch(name))
                return true;

        foreach (var rule in _pathRules)
            if ((isFolder || !rule.FoldersOnly) && (rule.Regex.IsMatch(relativePath) || rule.Regex.IsMatch(fullPath)))
                return true;

        return false;
    }

    internal static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        // "**/" also matches nothing, so "**/x" finds x at the top as well.
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                    // A set holds at least one character, so the closing bracket is looked for after it.
                    var close = i + 2 < glob.Length ? glob.IndexOf(']', i + 2) : -1;
                    if (close < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }
                    var set = glob[(i + 1)..close];
                    sb.Append('[');
                    if (set[0] is '!' or '^')
                    {
                        sb.Append('^');
                        set = set[1..];
                    }
                    sb.Append(set.Replace(@"\", @"\\").Replace("[", @"\[").Replace("^", @"\^"));
                    sb.Append(']');
                    i = close;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return sb.ToString();
    }
}
