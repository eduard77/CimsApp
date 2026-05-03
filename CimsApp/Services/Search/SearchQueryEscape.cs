namespace CimsApp.Services.Search;

/// <summary>
/// Escapes user-supplied query text for safe inclusion in a SQL
/// Server <c>LIKE</c> pattern. T-S15-04 risk #1 mitigation.
/// SQL Server LIKE wildcards: <c>%</c> (any sequence), <c>_</c>
/// (single char), <c>[abc]</c> (character class). Each is
/// prefixed with the escape character <c>\</c>; the EF.Functions.Like
/// call must pass <c>"\\"</c> as the third argument so the engine
/// interprets the prefix.
/// </summary>
public static class SearchQueryEscape
{
    public const string EscapeCharacter = "\\";

    public static string EscapeLike(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new System.Text.StringBuilder(input.Length + 8);
        foreach (var ch in input)
        {
            if (ch == '\\' || ch == '%' || ch == '_' || ch == '[')
                sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Wraps the escaped term in <c>%...%</c> wildcards
    /// for substring matching.</summary>
    public static string ContainsPattern(string input)
        => $"%{EscapeLike(input)}%";
}
