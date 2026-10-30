using Bunit;
using Health360.UI.Pages;

namespace Health360.UI.Tests;

public class HomeTests : BunitContext
{
    [Fact]
    public void Home_renders_the_app_name()
    {
        var home = Render<Home>();

        Assert.Equal("Health360", home.Find("h1").TextContent);
    }
}
