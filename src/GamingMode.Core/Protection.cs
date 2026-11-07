using System.Text.RegularExpressions;

namespace GamingMode.Core;

/// <summary>Decides whether a process is on the "never slow down" list.</summary>
public sealed class Protection
{
    private readonly Regex[] _names;
    private readonly string[] _pathParts;

    public Protection(IEnumerable<string> entries)
    {
        var names = new List<Regex>();
        var paths = new List<string>();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            if (entry.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                var part = entry[5..].Trim();
                if (part.Length > 0) paths.Add(part);
            }
            else
            {
                if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) entry = entry[..^4];
                names.Add(WildcardToRegex(entry));
            }
        }
        _names = names.ToArray();
        _pathParts = paths.ToArray();
    }

    public bool IsProtected(string processName, string? imagePath)
    {
        foreach (var n in _names) if (n.IsMatch(processName)) return true;
        if (!string.IsNullOrEmpty(imagePath))
            foreach (var p in _pathParts)
                if (imagePath.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static Regex WildcardToRegex(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
