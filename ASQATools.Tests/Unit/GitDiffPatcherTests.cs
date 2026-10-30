using ASQATools.Services;
using Xunit;

namespace ASQATools.Tests.Unit;

public class GitDiffPatcherTests
{
    [Fact]
    public void ApplyGitHunks_ShouldApplyAdditionAndDeletionCorrectly()
    {
        string original = "public void Test()\n{\n    int a = 1;\n    return;\n}";
        string hunks = "@@ -2,3 +2,3 @@\n {\n-    int a = 1;\n+    int a = 42;\n     return;";

        string patched = CodeReflectorService.ApplyGitHunks(original, hunks, out int succeeded, out int failed);

        Assert.Equal(1, succeeded);
        Assert.Equal(0, failed);
        Assert.Contains("int a = 42;", patched);
        Assert.DoesNotContain("int a = 1;", patched);
    }
}
