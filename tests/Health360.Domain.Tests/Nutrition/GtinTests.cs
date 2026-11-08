using Health360.Domain.Nutrition;

namespace Health360.Domain.Tests.Nutrition;

public class GtinTests
{
    [Theory]
    [InlineData("036000291452", "00036000291452")] // UPC-A
    [InlineData("0036000291452", "00036000291452")] // the same product as an EAN-13
    [InlineData("4006381333931", "04006381333931")] // EAN-13
    [InlineData("96385074", "00000096385074")] // EAN-8
    [InlineData("00036000291452", "00036000291452")] // GTIN-14
    [InlineData(" 036000291452 ", "00036000291452")]
    public void Valid_codes_become_the_14_digit_form(string code, string expected) => Assert.Equal(expected, Gtin.Normalize(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("036000291453")] // wrong check digit: a misread scan
    [InlineData("03600029145")] // 11 digits
    [InlineData("0360002914a2")]
    [InlineData("-36000291452")]
    public void Invalid_codes_are_null(string? code) => Assert.Null(Gtin.Normalize(code));
}
