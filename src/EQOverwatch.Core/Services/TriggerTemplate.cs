using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Services;

/// <summary>Expands <c>${token}</c> placeholders in trigger text.</summary>
public static class TriggerTemplate
{
    /// <summary>
    /// <c>${Character}</c> and <c>${Trigger}</c> are built in and win over same-named regex groups. Any other token is looked
    /// up as a named or numbered group of <paramref name="regex"/>: a defined group that didn't participate renders as empty, a numeric token resolves by group index (so ${1} works for a named first group),
    /// while an undefined token is left exactly as written.
    /// </summary>
    public static string Render(string template, Regex regex, Match match, string characterName, string triggerName)
    {
        if (template.IndexOf("${", StringComparison.Ordinal) < 0)
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                int end = template.IndexOf('}', i + 2);
                if (end > 0 && TryResolve(template.AsSpan(i + 2, end - i - 2).ToString(), out string? value))
                {
                    sb.Append(value);
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(template[i]);
            i++;
        }

        return sb.ToString();

        bool TryResolve(string token, out string? value)
        {
            if (token.Equals("Character", StringComparison.OrdinalIgnoreCase))
            {
                value = characterName;
                return true;
            }

            if (token.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
            {
                value = triggerName;
                return true;
            }

            int group = regex.GroupNumberFromName(token);
            if (group >= 0)
            {
                value = match.Groups[group].Value;
                return true;
            }

            // ${1}: when group 1 is a *named* group, the name lookup above does not find "1", so fall back to the index.
            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                && index >= 0 && index < match.Groups.Count)
            {
                value = match.Groups[index].Value;
                return true;
            }

            value = null;
            return false;
        }
    }
}
