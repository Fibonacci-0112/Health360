using Health360.Contracts.Notes;

namespace Health360.UI.Tests.Contracts;

public class NoteInputTests
{
    private static readonly NoteInputValidator Validator = new();

    [Theory]
    [InlineData("a")]
    [InlineData("  padded  ")]
    public void AcceptsNonEmptyText(string body) => Assert.True(Validator.Validate(new NoteInput(body)).IsValid);

    [Fact]
    public void LengthIsCountedAfterTrimming()
    {
        Assert.True(Validator.Validate(new NoteInput(" " + new string('a', 2000) + " ")).IsValid);
        Assert.False(Validator.Validate(new NoteInput(new string('a', 2001))).IsValid);
    }

    [Fact]
    public void CountsCharactersAsPostgresDoes()
    {
        // 2000 emoji are 4000 UTF-16 units but 2000 characters to char_length.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 2000));
        Assert.Equal(2000, NoteInputValidator.Length(emoji));
        Assert.True(Validator.Validate(new NoteInput(emoji)).IsValid);
        Assert.False(Validator.Validate(new NoteInput(emoji + "a")).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n ")]
    public void RejectsBlankText(string body) => Assert.False(Validator.Validate(new NoteInput(body)).IsValid);
}
