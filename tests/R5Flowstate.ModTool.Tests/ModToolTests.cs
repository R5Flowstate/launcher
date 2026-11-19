using System.Text.Json;
using R5Flowstate.Content;
using R5Flowstate.ModTool;
using Xunit;

namespace R5Flowstate.ModToolTests;

[Collection("curated")]
public class ModToolTests
{
    [Fact]
    public void BadOp_Exits2()
    {
        var root = NewRoot();
        try
        {
            var sw = new StringWriter();
            var code = ModTool.ModTool.Run(new[] { "nope", "--install-root", root }, sw);
            Assert.Equal(2, code);
            Assert.Contains("\"code\":\"bad_op\"", Last(sw.ToString()), StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("set-mods")]
    [InlineData("apply-pending")]
    public void MutatingOpsOutsideStaging_AreGone(string op)
    {
        var root = NewRoot();
        try
        {
            var sw = new StringWriter();
            var code = ModTool.ModTool.Run(new[] { op, "--install-root", root }, sw);
            Assert.Equal(2, code);
            Assert.Contains("\"code\":\"bad_op\"", Last(sw.ToString()), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, "mods")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void UnknownFlag_Refused()
    {
        var root = NewRoot();
        try
        {
            var sw = new StringWriter();
            var code = ModTool.ModTool.Run(new[] { "hello", "--install-root", root, "--file", "x" }, sw);
            Assert.Equal(2, code);
            Assert.Contains("\"code\":\"bad_args\"", Last(sw.ToString()), StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void BadInstallRoot_Exits3()
    {
        var dir = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sw = new StringWriter();
            var code = ModTool.ModTool.Run(new[] { "hello", "--install-root", dir }, sw);
            Assert.Equal(3, code);
            Assert.Contains("\"code\":\"bad_root\"", Last(sw.ToString()), StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void SweepStagedTemp_KeepsFinishedFolders()
    {
        var root = NewRoot();
        try
        {
            var staged = Path.Combine(root, ".r5f", "staged");
            Directory.CreateDirectory(Path.Combine(staged, ".tmp_1"));
            File.WriteAllText(Path.Combine(staged, ".tmp_2.zip"), "x");
            Directory.CreateDirectory(Path.Combine(staged, "Author-Name"));
            ModInstaller.SweepStagedTemp(root);
            Assert.False(Directory.Exists(Path.Combine(staged, ".tmp_1")));
            Assert.False(File.Exists(Path.Combine(staged, ".tmp_2.zip")));
            Assert.True(Directory.Exists(Path.Combine(staged, "Author-Name")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    static string WorkRoot => @"G:\claude-scratch\mods-apply\work";

    static string NewRoot()
    {
        var dir = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "r5apex.exe"), Array.Empty<byte>());
        return dir;
    }

    static string Last(string text)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? string.Empty : lines[^1];
    }

    static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of the scratch install.
        }
    }
}
