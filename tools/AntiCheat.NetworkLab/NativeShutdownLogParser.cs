using System.Globalization;
using System.Text.RegularExpressions;

internal sealed record NativeShutdownEvent(string Name, bool Joined);
internal sealed record NativeShutdownLogLine(IReadOnlyList<NativeShutdownEvent> Events, bool Damaged,
    bool UnresolvedLifecycle, int RecoveredRecords, int HeaderCount);

/// <summary>Parses only native timestamped log records for the owned server's shutdown barrier.</summary>
internal static class NativeShutdownLogParser
{
    private static readonly Regex Header = new(
        @"(?<utc>[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}) - (?<source>[^:\r\n\0]{1,80}): INFO: ",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    public static NativeShutdownLogLine Parse(string line)
    {
        var headers = Header.Matches(line).Cast<Match>().Where(match =>
            DateTime.TryParseExact(match.Groups["utc"].Value, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _)).ToArray();
        var events = new List<NativeShutdownEvent>(headers.Length);
        bool unresolved = false;
        int recovered = 0;
        string previousText = "";
        NativeShutdownEvent? previousEvent = null;
        for (int index = 0; index < headers.Length; index++)
        {
            var header = headers[index];
            int start = header.Index + header.Length;
            int end = index + 1 < headers.Length ? headers[index + 1].Index : line.Length;
            string text = line[start..end];
            var parsed = Event(header.Groups["source"].Value, text);
            if (parsed is not null)
            {
                // A full header embedded in an actor name is ambiguous. Recover a truncated
                // record only when its text is a prefix of the intact repeated native record.
                string prefix = index == 0 ? line[..header.Index] : previousText;
                bool prefixIsEmpty = prefix.Trim('\0').Length == 0;
                if (!prefixIsEmpty && (index == 0 || previousEvent is null))
                {
                    if (index > 0 && text.StartsWith(prefix.TrimEnd('\0'), StringComparison.Ordinal)) recovered++;
                    else { unresolved = true; previousText = text; previousEvent = null; continue; }
                }
                if (parsed.Name.Contains('\0')) unresolved = true;
                else events.Add(parsed);
            }
            previousText = text;
            previousEvent = parsed;
        }
        return new(events, line.Contains('\0') || headers.Length > 1, unresolved, recovered, headers.Length);
    }

    private static NativeShutdownEvent? Event(string source, string text)
    {
        const string joinPrefix = "Broadcast: ", joinSuffix = " has joined.", leaveSuffix = " disconnected.";
        if (source == "Utils" && text.StartsWith(joinPrefix, StringComparison.Ordinal) && text.EndsWith(joinSuffix, StringComparison.Ordinal))
        {
            string name = text[joinPrefix.Length..^joinSuffix.Length];
            return name.Length == 0 ? null : new(name, true);
        }
        if (source is "TShock" or "HandlerCollection`1" && text.EndsWith(leaveSuffix, StringComparison.Ordinal))
        {
            string name = text[..^leaveSuffix.Length];
            return name.Length == 0 ? null : new(name, false);
        }
        return null;
    }
}
