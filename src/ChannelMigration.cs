using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for channels: a channel moves one message at a time, so its verbs are `transmit` and
/// `receive`. `send` is `transmit`, `send_if_room` is `transmit_if_room`, `add_sender` is `add_transmitter` and
/// `close_sending` is `close_transmitting`. The last three name only a channel's routines and are renamed after any
/// `.`. A `.send(` is renamed when its receiver is a name the file gives a channel type (`jobs: @Channel[U64]`,
/// `core: ChannelCore[T]`) or the channel type itself (`routine @Channel[T].send`), and listed otherwise. Comments are
/// renamed the same way, the text of a string never.
public static partial class ChannelMigration
{
    [GeneratedRegex(@"\.(send_if_room|add_sender|close_sending)(?=\()")]
    private static partial Regex ChannelOnly();

    [GeneratedRegex(@"(?<![\w.])(?<name>[A-Za-z_]\w*)\s*:\s*@?(?:Shared)?Channel(?:Core)?\[")]
    private static partial Regex ChannelBinding();

    [GeneratedRegex(@"(?<receiver>[A-Za-z_]\w*|(?:Shared)?Channel(?:Core)?\[[^\]]*\])\.send(?=\()")]
    private static partial Regex SendCall();

    private static readonly Dictionary<string, string> Renamed = new()
    {
        ["send_if_room"] = "transmit_if_room", ["add_sender"] = "add_transmitter", ["close_sending"] = "close_transmitting",
    };

    /// The source with the channel verbs renamed. A `.send(` whose receiver is not known to be a channel is added to
    /// <paramref name="report"/>.
    public static string Rewrite(string text, List<string> report, string file)
    {
        var channels = new HashSet<string>(ChannelBinding().Matches(text).Select(m => m.Groups["name"].Value));
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var result = new StringBuilder();
            foreach (var (literal, part) in Migrate.CodeAndLiterals(lines[i]))
            {
                result.Append(literal ? part : RewriteCode(part, channels, report, $"{file}:{i + 1}"));
            }
            lines[i] = result.ToString();
        }
        return string.Join('\n', lines);
    }

    private static string RewriteCode(string code, HashSet<string> channels, List<string> report, string where)
    {
        code = ChannelOnly().Replace(code, m => "." + Renamed[m.Groups[1].Value]);
        return SendCall().Replace(code, m =>
        {
            string receiver = m.Groups["receiver"].Value;
            if (receiver.EndsWith(']') || channels.Contains(receiver)) return receiver + ".transmit";
            report.Add($"{where}: '{receiver}.send(' is 'transmit' when '{receiver}' is a channel: rewrite it by hand");
            return m.Value;
        });
    }
}
