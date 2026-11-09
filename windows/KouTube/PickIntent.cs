using System.Text.RegularExpressions;

namespace KouTube;

public static class PickIntent
{
    // Deterministic local parsing: show editable topics rather than claiming an AI understood an ambiguous request.
    public static List<string> Topics(string text)
    {
        text = text.Trim();
        var about = Regex.Match(text, @"\b(?:про|на тему|по темам)\s+", RegexOptions.IgnoreCase);
        if (about.Success) text = text[(about.Index + about.Length)..];
        else if (text.Contains(':')) text = text[(text.IndexOf(':') + 1)..];
        text = Regex.Replace(text, @"\b(?:и\s+т\.?\s*д\.?|и тому подобное)\b.*$", "", RegexOptions.IgnoreCase);
        return Regex.Split(text, @"[,;\n]+|\s+и\s+", RegexOptions.IgnoreCase)
            .Select(x => Regex.Replace(x.Trim(' ', '.', '!', '?'), @"^(?:там\s+|про\s+)", "", RegexOptions.IgnoreCase))
            .Where(x => x.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
    }

    public record Episode(string Group, string Name, int Number);
    static readonly Regex Number = new(@"(?i)(?:\b(?:серия|серии|часть|эпизод|episode|part|ep)\s*[#№.:\-]?\s*(?<n>\d{1,3})\b|\b(?<n>\d{1,3})\s*(?:серия|часть|эпизод)\b|[#№]\s*(?<n>\d{1,3})\b|\bS(?<s>\d{1,2})E(?<n>\d{1,3})\b)");

    public static Episode? Series(string title, string? channel)
    {
        var match = Number.Match(title);
        if (!match.Success || !int.TryParse(match.Groups["n"].Value, out var number) || number <= 0) return null;
        var name = title[..match.Index].Trim(' ', '-', ':', '|', '.', '(');
        if (name.Length < 3) name = title[(match.Index + match.Length)..].Trim(' ', '-', ':', '|', '.', ')');
        if (name.Length < 3 || string.IsNullOrWhiteSpace(channel)) return null;
        var normalized = Regex.Replace(Format.UnFullwidth(name).ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd}]+", " ").Trim();
        return new((channel.ToLowerInvariant() + "|" + normalized + "|" + match.Groups["s"].Value), name, number);
    }
}
