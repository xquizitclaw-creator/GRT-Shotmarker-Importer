using ShotMarker.Core.Import;
using Xunit;

namespace ShotMarker.Core.Tests;

/// <summary>
/// Ruling F34: the CLI's only two channels to a script are its exit code and its log.
/// A crash or a silent success on bad input breaks both.
/// </summary>
public class ImportCliTests
{
    private static string ExportPath => Fixtures.Path("shotmarker/SM_export_Sep_21.tar");

    private static (int ExitCode, string Output) RunCapturingConsole(string[] args)
    {
        TextWriter original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            int exitCode = ImportCli.Run(args);
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void ACorruptGrtloadReportsAndExitsNonZeroInsteadOfCrashing()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = Path.Combine(dir, "corrupt.grtload");
            File.WriteAllText(load, "this is not xml at all !!!");

            var (exitCode, output) = RunCapturingConsole(new[] { ExportPath, load });

            Assert.NotEqual(0, exitCode);
            Assert.Contains("corrupt.grtload", output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AMalformedChargeReportsAndExitsNonZeroAndDoesNotImport()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = Path.Combine(dir, "session.grtload");

            var (exitCode, output) = RunCapturingConsole(new[] { ExportPath, load, "not-a-number" });

            Assert.NotEqual(0, exitCode);
            Assert.Contains("not-a-number", output);
            // Never even got as far as writing the sibling load or creating the fallback one.
            Assert.False(File.Exists(load));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AWellFormedChargeStillImports()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = Path.Combine(dir, "session.grtload");

            var (exitCode, output) = RunCapturingConsole(new[] { ExportPath, load, "41.5" });

            Assert.Equal(0, exitCode);
            Assert.Contains("wrote ", output);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
