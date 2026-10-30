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
        RuleFor(x => x.Body).Must(body => body is not null && Length(body) is >= 1 and <= MaxLength)
            .WithMessage($"'Body' must be 1 to {MaxLength} characters.");
    }

    /// <summary>
    /// Characters after trimming, counted as Postgres's <c>char_length</c> counts them (code points), so an
    /// emoji is one character, not the two UTF-16 units <see cref="string.Length"/> counts.
    /// </summary>
    public static int Length(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return body.Trim().EnumerateRunes().Count();
    }
}
