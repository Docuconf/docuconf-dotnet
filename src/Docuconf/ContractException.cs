namespace Docuconf;

/// <summary>Thrown when options classes cannot be turned into a valid contract. Lists every problem found.</summary>
public sealed class ContractException(IReadOnlyList<string> errors)
    : Exception("The configuration contract is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
{
    /// <summary>Every problem found.</summary>
    public IReadOnlyList<string> Errors { get; } = errors;
}
