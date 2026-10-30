using FluentValidation;

namespace Health360.Contracts.Notes;

/// <summary>
/// A new note on the Phase 0b throwaway <c>notes</c> table. Ports the prototype's <c>noteInputSchema</c>;
/// the check matches the table's constraint, so a valid note is never rejected into <c>failed_uploads</c>.
/// </summary>
/// <param name="Body">Trimmed before saving.</param>
public sealed record NoteInput(string Body);

public sealed class NoteInputValidator : AbstractValidator<NoteInput>
{
    /// <summary>The table's limit (<c>char_length(body) between 1 and 2000</c>).</summary>
    public const int MaxLength = 2000;

    public NoteInputValidator()
    {
        RuleFor(x => x.Body).Must(body => body?.Trim().Length is >= 1 and <= MaxLength)
            .WithMessage($"'Body' must be 1 to {MaxLength} characters.");
    }
}
