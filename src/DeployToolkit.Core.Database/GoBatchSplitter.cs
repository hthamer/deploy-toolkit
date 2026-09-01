using System.Globalization;
using System.Text.RegularExpressions;

namespace DeployToolkit.Core.Database;

/// <summary>
/// Lexical state carried while walking T-SQL text line by line. Everything
/// except <see cref="Normal"/> can survive a newline (a <c>--</c> line comment
/// cannot — it always ends at the end of the physical line), so line comments
/// are tracked locally inside the scanner instead of being part of this enum.
/// </summary>
internal enum TSqlScanState
{
    /// <summary>Ordinary statement text.</summary>
    Normal,

    /// <summary>Inside a <c>/* ... */</c> block comment (may span lines).</summary>
    BlockComment,

    /// <summary>
    /// Inside a <c>'...'</c> single-quoted string literal. T-SQL has no backslash
    /// escapes: an embedded apostrophe is written as <c>''</c>, and string
    /// literals may legally span physical lines.
    /// </summary>
    SingleQuote,

    /// <summary>
    /// Inside a <c>"..."</c> double-quoted identifier (QUOTED_IDENTIFIER ON).
    /// Same doubling rule as single quotes: <c>""</c> is an embedded quote.
    /// </summary>
    DoubleQuote,

    /// <summary>
    /// Inside a <c>[...]</c> bracketed identifier. A closing bracket inside the
    /// identifier is written as <c>]]</c>.
    /// </summary>
    Bracket
}

/// <summary>
/// Character-level line scanner shared by <see cref="GoBatchSplitter"/> (which
/// needs to know whether a physical line really is a GO separator) and
/// <see cref="SqlScriptAnalyzer"/> (which needs comments and string contents
/// blanked out before keyword matching). Pure text analysis — never touches a
/// database.
/// </summary>
internal static class TSqlLineScanner
{
    /// <summary>
    /// Scans one physical line of T-SQL starting in <paramref name="state"/>
    /// and returns the state to carry into the next line.
    /// </summary>
    /// <remarks>
    /// How the two consumers use it:
    /// <list type="bullet">
    /// <item><description><b>State tracking:</b> feed lines in order; the
    /// returned value is the state at the start of the next line. A GO
    /// separator is only honored on a line whose entry state is
    /// <see cref="TSqlScanState.Normal"/> — a bare <c>GO</c> inside a
    /// multi-line string or block comment must not split.</description></item>
    /// <item><description><b>Sanitization:</b> callers that want "statement
    /// text only" pass a char buffer the same length as the line. Every
    /// character consumed inside a comment, string literal, or bracketed
    /// identifier (including the delimiters) is overwritten with a space;
    /// live statement text is copied through untouched. That keeps word
    /// boundaries and line structure intact while removing any keyword that
    /// merely appears inside a literal or comment.</description></item>
    /// </list>
    /// T-SQL quoting rules implemented here (no backslash escapes anywhere):
    /// <c>''</c> inside a single-quoted string is an apostrophe, <c>""</c>
    /// inside a double-quoted identifier is a quote, <c>]]</c> inside a
    /// bracketed identifier is a bracket, and <c>*/</c> closes a block
    /// comment. Block comments are not nested (SQL Server behavior).
    /// </remarks>
    /// <param name="line">One physical line, without its newline.</param>
    /// <param name="state">State carried over from the previous line.</param>
    /// <param name="sanitized">
    /// Optional output buffer (same length as <paramref name="line"/>); pass
    /// <see cref="Span{T}.Empty"/> to only track state.
    /// </param>
    public static TSqlScanState Scan(string line, TSqlScanState state, Span<char> sanitized)
    {
        var s = state;
        var inLineComment = false; // intra-line only, never carried past the newline
        var i = 0;

        // Writes a space over a position that is NOT live statement text.
        // With an empty span (state-only scanning) this is a no-op. Static +
        // explicit span parameter: ref-like types cannot be captured by local
        // functions, only passed as arguments.
        static void Blank(Span<char> buffer, int index)
        {
            if (index < buffer.Length)
            {
                buffer[index] = ' ';
            }
        }

        while (i < line.Length && !inLineComment)
        {
            char c = line[i];
            char next = i + 1 < line.Length ? line[i + 1] : '\0';

            switch (s)
            {
                case TSqlScanState.Normal:
                    switch (c)
                    {
                        case '-' when next == '-':
                            // "--" line comment: the rest of the line is inert.
                            inLineComment = true;
                            Blank(sanitized, i);
                            Blank(sanitized, i + 1);
                            i += 2;
                            break;

                        case '/' when next == '*':
                            // "/*" opens a block comment, which may span lines.
                            s = TSqlScanState.BlockComment;
                            Blank(sanitized, i);
                            Blank(sanitized, i + 1);
                            i += 2;
                            break;

                        case '\'':
                            s = TSqlScanState.SingleQuote;
                            Blank(sanitized, i);
                            i++;
                            break;

                        case '"':
                            s = TSqlScanState.DoubleQuote;
                            Blank(sanitized, i);
                            i++;
                            break;

                        case '[':
                            s = TSqlScanState.Bracket;
                            Blank(sanitized, i);
                            i++;
                            break;

                        default:
                            i++; // live statement text — keep verbatim
                            break;
                    }
                    break;

                case TSqlScanState.BlockComment:
                    if (c == '*' && next == '/')
                    {
                        // "*/" closes the block comment (no nesting in T-SQL).
                        s = TSqlScanState.Normal;
                        Blank(sanitized, i);
                        Blank(sanitized, i + 1);
                        i += 2;
                    }
                    else
                    {
                        Blank(sanitized, i);
                        i++;
                    }
                    break;

                case TSqlScanState.SingleQuote:
                    if (c == '\'')
                    {
                        if (next == '\'')
                        {
                            // Doubled apostrophe: an escaped quote inside the
                            // string, NOT the end of it. Consume both chars.
                            Blank(sanitized, i);
                            Blank(sanitized, i + 1);
                            i += 2;
                        }
                        else
                        {
                            s = TSqlScanState.Normal;
                            Blank(sanitized, i);
                            i++;
                        }
                    }
                    else
                    {
                        Blank(sanitized, i);
                        i++;
                    }
                    break;

                case TSqlScanState.DoubleQuote:
                    // Same doubling rule as single quotes, with "" as escape.
                    if (c == '"')
                    {
                        if (next == '"')
                        {
                            Blank(sanitized, i);
                            Blank(sanitized, i + 1);
                            i += 2;
                        }
                        else
                        {
                            s = TSqlScanState.Normal;
                            Blank(sanitized, i);
                            i++;
                        }
                    }
                    else
                    {
                        Blank(sanitized, i);
                        i++;
                    }
                    break;

                case TSqlScanState.Bracket:
                    // ] inside a bracketed identifier is escaped as ]].
                    if (c == ']' && next == ']')
                    {
                        Blank(sanitized, i);
                        Blank(sanitized, i + 1);
                        i += 2;
                    }
                    else if (c == ']')
                    {
                        s = TSqlScanState.Normal;
                        Blank(sanitized, i);
                        i++;
                    }
                    else
                    {
                        Blank(sanitized, i);
                        i++;
                    }
                    break;
            }
        }

        // Anything after a "--" is comment up to the end of the line.
        if (inLineComment)
        {
            for (; i < line.Length; i++)
            {
                Blank(sanitized, i);
            }
        }

        return s;
    }

    /// <summary>
    /// Convenience overload used by the analyzer: sanitizes a whole script,
    /// replacing comments, string literal contents, and bracketed identifier
    /// contents with spaces while preserving line structure and statement text.
    /// </summary>
    public static string Sanitize(string script)
    {
        var outLines = new List<string>();
        var state = TSqlScanState.Normal;

        foreach (var line in SplitLines(script))
        {
            var buffer = new char[line.Length];
            line.CopyTo(0, buffer, 0, line.Length);
            state = Scan(line, state, buffer);
            outLines.Add(new string(buffer));
        }

        return string.Join('\n', outLines);
    }

    /// <summary>Normalizes <c>\r\n</c> and stray <c>\r</c> to <c>\n</c>, then splits.</summary>
    public static IReadOnlyList<string> SplitLines(string script) =>
        script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}

/// <summary>
/// Splits a T-SQL script into batches on SSMS-style <c>GO</c> separators,
/// without executing anything and without any SQL provider dependency.
/// Pure text analysis only — see <see cref="Split"/>.
/// </summary>
public static class GoBatchSplitter
{
    /// <summary>
    /// SSMS-style GO token, matched per line, case-insensitive: optional
    /// leading whitespace, the literal GO, an optional repeat count
    /// (<c>GO 3</c>), optional trailing whitespace, and an optional trailing
    /// <c>--</c> line comment. Anything else on the line (e.g. <c>SELECT 1 GO</c>,
    /// <c>GOO</c>, <c>GOCODE</c>) disqualifies it — GO is only recognized as a
    /// whole-line token, exactly like SSMS/sqlcmd treat it.
    /// </summary>
    private static readonly Regex GoLineRegex = new(
        @"^\s*GO(?:\s+(?<count>\d+))?\s*(?:--.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Splits <paramref name="script"/> into batches at <c>GO</c> separators.
    /// </summary>
    /// <remarks>
    /// Semantics (mirrors SSMS, but purely client-side — the server never sees
    /// the word GO; it is removed from the batches it produces):
    /// <list type="bullet">
    /// <item><description>A line is a separator only when the whole line is
    /// <c>GO</c> (optionally with a count and/or a trailing <c>--</c> comment)
    /// <b>and</b> the scanner is in Normal state at the start of that line — so
    /// GO inside single/double-quoted strings, <c>--</c> comments, multi-line
    /// <c>/* */</c> comments, or after an unterminated <c>[</c> never splits.</description></item>
    /// <item><description><c>GO</c> alone ends one batch;
    /// <c>GO n</c> repeats the preceding batch n times (SSMS "GO 3 = run it 3
    /// times"), so the returned list contains that batch text n times.</description></item>
    /// <item><description>Batches are trimmed of surrounding whitespace/blank
    /// lines; empty batches (e.g. consecutive GOs, or a GO 0) are skipped.</description></item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<string> Split(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return Array.Empty<string>();
        }

        var batches = new List<string>();
        var currentLines = new List<string>();
        var state = TSqlScanState.Normal;

        void Flush(int repeatCount)
        {
            // Trim collapses leading/trailing blank lines and indentation
            // around the batch; interior formatting is preserved as-is.
            var text = string.Join('\n', currentLines).Trim();
            if (text.Length == 0)
            {
                return; // consecutive GOs (or GO 0) produce no empty batch
            }

            for (var r = 0; r < repeatCount; r++)
            {
                batches.Add(text);
            }
        }

        foreach (var line in TSqlLineScanner.SplitLines(script))
        {
            var match = GoLineRegex.Match(line);
            if (state == TSqlScanState.Normal && match.Success)
            {
                // A real separator. The count defaults to 1 (plain GO);
                // "GO 3" repeats the preceding batch 3 times.
                var repeat = 1;
                if (match.Groups["count"].Success)
                {
                    repeat = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
                }

                Flush(repeat);
                currentLines.Clear();
            }
            else
            {
                // Ordinary batch content — including GO lines that are really
                // string content, comment text, or bracketed identifiers.
                currentLines.Add(line);
            }

            // Track lexical state across lines; we don't need the sanitized
            // output here, hence the empty span.
            state = TSqlLineScanner.Scan(line, state, Span<char>.Empty);
        }

        Flush(1); // trailing batch after the last GO (or a GO-less script)
        return batches;
    }
}
