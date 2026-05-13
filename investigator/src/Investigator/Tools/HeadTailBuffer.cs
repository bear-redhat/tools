namespace Investigator.Tools;

/// <summary>
/// Captures the first N and last M lines of streaming output without
/// holding the full content in memory. Uses a circular buffer for the tail.
/// </summary>
internal sealed class HeadTailBuffer
{
    private readonly int _headLimit;
    private readonly int _tailLimit;
    private readonly List<string> _head;
    private readonly string[] _tail;
    private int _tailWritePos;
    private int _totalLines;

    public HeadTailBuffer(int headLines, int tailLines)
    {
        _headLimit = headLines;
        _tailLimit = tailLines;
        _head = new List<string>(headLines);
        _tail = new string[tailLines];
    }

    public int LineCount => _totalLines;

    public void Add(string line)
    {
        _totalLines++;
        if (_head.Count < _headLimit)
        {
            _head.Add(line);
            return;
        }
        _tail[_tailWritePos % _tailLimit] = line;
        _tailWritePos++;
    }

    /// <summary>
    /// Builds the truncated view with a metadata header.
    /// When total lines fit within head+tail, all lines are returned.
    /// Otherwise head + omission marker + tail.
    /// </summary>
    public string Build(int exitCode, string relativePath)
    {
        var header = $"[exit_code: {exitCode} | {_totalLines} lines | full: {relativePath}]\n\n";

        var tailCount = Math.Min(_tailWritePos, _tailLimit);

        if (_totalLines <= _headLimit + _tailLimit)
        {
            var allLines = new List<string>(_head);
            AppendTailLines(allLines, tailCount);
            return header + string.Join('\n', allLines);
        }

        var headStr = string.Join('\n', _head);
        var tailStr = BuildTailString(tailCount);
        var omitted = _totalLines - _headLimit - tailCount;
        return header + headStr + $"\n... ({omitted} lines omitted) ...\n" + tailStr;
    }

    /// <summary>
    /// Builds only the raw content (no header) for short outputs or non-truncate tools.
    /// </summary>
    public string BuildRaw()
    {
        var tailCount = Math.Min(_tailWritePos, _tailLimit);
        var allLines = new List<string>(_head);
        AppendTailLines(allLines, tailCount);
        return string.Join('\n', allLines);
    }

    private void AppendTailLines(List<string> target, int tailCount)
    {
        if (tailCount == 0) return;
        var start = _tailWritePos > _tailLimit ? _tailWritePos % _tailLimit : 0;
        for (var i = 0; i < tailCount; i++)
            target.Add(_tail[(start + i) % _tailLimit]);
    }

    private string BuildTailString(int tailCount)
    {
        if (tailCount == 0) return "";
        var lines = new string[tailCount];
        var start = _tailWritePos > _tailLimit ? _tailWritePos % _tailLimit : 0;
        for (var i = 0; i < tailCount; i++)
            lines[i] = _tail[(start + i) % _tailLimit];
        return string.Join('\n', lines);
    }
}
