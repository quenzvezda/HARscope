namespace HarScope.Core.Querying;

/// <summary>Last line of defence for the output budget: never emits more than MaxOutputChars.</summary>
public sealed class OutputGuard(HarScopeOptions options)
{
    public string Serialize<T>(T result)
    {
        var json = HarJson.Serialize(result);
        if (json.Length <= options.MaxOutputChars) return json;

        // Results are structured, so they cannot be cut in the middle. Report the overflow instead.
        return HarJson.Serialize(new
        {
            truncated = true,
            error = "output_limit_exceeded",
            message = $"The result is {json.Length} chars, above the limit of {options.MaxOutputChars}. " +
                      "Request fewer sections, a smaller bodyMaxChars, or a smaller page.",
        });
    }
}
