using System.Text;

namespace J0kersMediaServer.Services;

/// <summary>
/// Fills the placeholders in an embedded HTML page.
///
/// The player and the watch page used to be C# raw string literals, which
/// meant every literal brace in their CSS and JavaScript had to be written
/// twice and no editor could see them as HTML at all. They are ordinary files
/// under wwwroot now, embedded in the assembly exactly the way dashboard.html
/// is, with the few values the server has to supply spelled __LIKE_THIS__.
///
/// The substitution deliberately walks the template once from left to right
/// rather than calling string.Replace for each placeholder in turn. Chained
/// Replace calls rescan text they have already inserted, so a stream whose
/// name happened to contain one of the token spellings would have part of a
/// later value substituted inside it. One pass gives what C# interpolation
/// gave: a value is written out and never looked at again.
/// </summary>
internal static class PageTemplate
{
    /// <summary>
    /// Returns <paramref name="template"/> with every occurrence of each token
    /// replaced by its value. Values are inserted verbatim, so whatever
    /// encoding a value needs for where it lands - HTML encoding for page
    /// text, JSON for anything inside a script element - is the caller's to
    /// apply before handing it over.
    /// </summary>
    internal static string Fill(string template, params (string Token, string Value)[] values)
    {
        var sb = new StringBuilder(template.Length + 256);
        var i = 0;
        while (i < template.Length)
        {
            // The longest match wins so that a page using both __NAME__ and
            // __NAME_JS__ cannot have the shorter token claim the longer one
            // and leave "_JS__" stranded in the output.
            var match = -1;
            for (var k = 0; k < values.Length; k++)
            {
                var token = values[k].Token;
                if (match >= 0 && token.Length <= values[match].Token.Length) continue;
                if (template.AsSpan(i).StartsWith(token, StringComparison.Ordinal)) match = k;
            }
            if (match < 0) { sb.Append(template[i]); i++; }
            else { sb.Append(values[match].Value); i += values[match].Token.Length; }
        }
        return sb.ToString();
    }
}
