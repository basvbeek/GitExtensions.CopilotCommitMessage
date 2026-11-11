using GitExtensions.CopilotCommitMessage;
using Xunit;

namespace GitExtensions.CopilotCommitMessage.Tests;

public sealed class PluginIconTests
{
    [Fact]
    public void Create_ReturnsTransparent32PixelCommitMark()
    {
        using System.Drawing.Bitmap icon = PluginIcon.Create();

        Assert.Equal(32, icon.Width);
        Assert.Equal(32, icon.Height);
        Assert.Equal(0, icon.GetPixel(0, 0).A);
        Assert.True(icon.GetPixel(8, 8).A > 0);
    }
}