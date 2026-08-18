using System.Text.Json;
using Investigator.Contracts;
using Investigator.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Investigator.Tests;

/// <summary>
/// read_output is how a client pages a large log without pulling it into context, so its
/// header is load-bearing: a caller decides whether to keep reading based on it.
/// </summary>
public class ReadOutputToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inv-ro-{Guid.NewGuid():N}");

    public ReadOutputToolTests() => Directory.CreateDirectory(Path.Combine(_root, "tool_outputs"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string WriteLines(string name, int count, int width = 10)
    {
        var path = Path.Combine(_root, "tool_outputs", name);
        File.WriteAllLines(path, Enumerable.Range(1, count).Select(i => $"L{i}".PadRight(width, 'x')));
        return $"tool_outputs/{name}";
    }

    private ToolContext Context() => new(
        NullLogger.Instance, _root, OnOutputLine: null,
        NextOutputNumber: () => 1, CallerId: "test");

    private async Task<ToolResult> Read(string file, int? start = null, int? end = null)
    {
        var args = new Dictionary<string, object> { ["file"] = file };
        if (start is not null) args["start_line"] = start;
        if (end is not null) args["end_line"] = end;

        return await new ReadOutputTool().InvokeAsync(
            JsonSerializer.SerializeToElement(args), Context(), CancellationToken.None);
    }

    [Fact]
    public async Task ReadingAWholeSmallFile_ReportsEndOfFileWithTheRealTotal()
    {
        var file = WriteLines("small.txt", 12);

        var result = await Read(file);

        Assert.Contains("lines 1-12", result.Output);
        Assert.Contains("end of file (12 lines total)", result.Output);
    }

    [Fact]
    public async Task StoppingAtEndLine_AdvertisesWhereToContinue()
    {
        // The old header claimed "of N" using the line the loop stopped at, so a caller
        // reading it concluded it had the whole file after the first page.
        var file = WriteLines("big.txt", 500);

        var result = await Read(file, start: 1, end: 100);

        Assert.Contains("lines 1-100", result.Output);
        Assert.Contains("continue with start_line=101", result.Output);
        Assert.DoesNotContain("end of file", result.Output);
    }

    [Fact]
    public async Task PagingForward_EventuallyReachesTheEnd()
    {
        var file = WriteLines("paged.txt", 30);

        var first = await Read(file, start: 1, end: 10);
        Assert.Contains("continue with start_line=11", first.Output);

        var last = await Read(file, start: 11);
        Assert.Contains("end of file (30 lines total)", last.Output);
        Assert.Contains("L30", last.Output);
    }

    [Fact]
    public async Task ExceedingTheByteCap_StopsAndSaysWhereItStopped()
    {
        // 32KB cap; 2000 lines of ~40 bytes overruns it.
        var file = WriteLines("huge.txt", 2_000, width: 40);

        var result = await Read(file);

        Assert.Contains("continue with start_line=", result.Output);
        Assert.DoesNotContain("end of file", result.Output);
    }

    [Fact]
    public async Task ARangePastTheEnd_SaysHowLongTheFileIs()
    {
        var file = WriteLines("short.txt", 5);

        var result = await Read(file, start: 99);

        Assert.Contains("the file has 5 lines", result.Output);
    }

    [Fact]
    public async Task PathsOutsideTheWorkspace_AreRejected()
    {
        var result = await Read("../../../etc/passwd");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Invalid file path", result.Output);
    }
}
