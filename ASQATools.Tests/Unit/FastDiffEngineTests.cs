using ASQATools.Shared.Services;
using Xunit;

namespace ASQATools.Tests.Unit;

public class FastDiffEngineTests
{
    [Fact]
    public void HasOnlyWhitespaceOrNewlineChanges_ShouldReturnTrue_WhenOnlyWhitespaceDiffers()
    {
        string oldText = "var a = 1;\r\nvar b = 2;\n";
        string newText = "var a = 1; \n\n var b = 2;  \r\n";

        bool result = FastDiffEngine.HasOnlyWhitespaceOrNewlineChanges(oldText, newText);
        Assert.True(result);
    }

    [Fact]
    public void HasOnlyWhitespaceOrNewlineChanges_ShouldReturnFalse_WhenRealCodeDiffers()
    {
        string oldText = "var a = 1;";
        string newText = "var a = 2;";

        bool result = FastDiffEngine.HasOnlyWhitespaceOrNewlineChanges(oldText, newText);
        Assert.False(result);
    }

    [Fact]
    public void HasOnlyWhitespaceOrNewlineChanges_ShouldReturnFalse_WhenWhitespaceBetweenTokensIsDeleted()
    {
        string oldText = "int a = 1;";
        string newText = "inta = 1;";
        Assert.False(FastDiffEngine.HasOnlyWhitespaceOrNewlineChanges(oldText, newText));
    }

    [Fact]
    public void GenerateUnifiedDiffWithLineNumbers_ShouldCreateHunksWithCorrectPrefixes()
    {
        string oldText = "line 1\nline 2\nline 3";
        string newText = "line 1\nline 2 modified\nline 3";

        string diff = FastDiffEngine.GenerateUnifiedDiffWithLineNumbers(oldText, newText, 1);

        Assert.Contains("@@", diff);
        Assert.Contains("- L", diff);
        Assert.Contains("+ L", diff);
    }
}
