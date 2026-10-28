using System.Text.RegularExpressions;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.Core.Services;

public sealed record TriggerTestResult(
    bool IsPatternValid,
    string? Error,
    bool Matched,
    IReadOnlyDictionary<string, string> Groups);

/// <summary>Checks a rule against a sample line and reports its capture groups (used by the editor's test button).</summary>
public static class TriggerRuleTester
{
    private static readonly IReadOnlyDictionary<string, string> NoGroups = new Dictionary<string, string>();

    public static TriggerTestResult Evaluate(TriggerRule rule, string message)
    {
        ArgumentNullException.ThrowIfNull(rule);

        string pattern = rule.GetEffectivePattern();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new TriggerTestResult(false, "Pattern is empty.", false, NoGroups);
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TriggerEngineService.MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            return new TriggerTestResult(false, ex.Message, false, NoGroups);
        }

        try
        {
            Match match = regex.Match(message ?? string.Empty);
            if (!match.Success)
            {
                return new TriggerTestResult(true, null, false, NoGroups);
            }

            var groups = new Dictionary<string, string>();
            foreach (string name in regex.GetGroupNames())
            {
                if (name != "0" && match.Groups[name] is { Success: true } group)
                {
                    groups[name] = group.Value;
                }
            }

            return new TriggerTestResult(true, null, true, groups);
        }
        catch (RegexMatchTimeoutException)
        {
            return new TriggerTestResult(false, "The pattern took too long to evaluate (possible catastrophic backtracking).", false, NoGroups);
        }
    }
}
