using System.Buffers;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NLog;
using UtfUnknown;

namespace TinadecTools.Tools.FileRW;

/// <summary>
/// 一行的索引
/// </summary>
/// <param name="LineStart">本行开始的偏移</param>
/// <param name="LineEnd">本行结束的偏移（不包含换行符）</param>
/// <param name="NextStart">下一行开始的偏移</param>
internal record struct LineSpan(
    long LineStart,
    long LineEnd,
    long NextStart
);

/// <summary>
/// 一行的内容
/// </summary>
/// <param name="Content">具体内容（不包括换行符）</param>
/// <param name="LineNumber">行号</param>
/// <param name="LineLength">本行长度</param>
public record struct LineContent(
    string Content,
    int LineNumber,
    long LineLength
);

internal class FileAccessor : IDisposable
{
    private readonly FileStream file;
    private List<LineSpan> index = new();
    private readonly string filepath;
    private readonly SafeFileHandle handle;
    private static readonly Logger logger = LogManager.GetCurrentClassLogger();
    private static readonly UTF8Encoding utf8_no_bom = new(false);
    private const int stream_buffer_size = 128 * 1024;
    private const int text_buffer_size = 16 * 1024;

    static FileAccessor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// 打开一个文件。
    /// </summary>
    /// <param name="filepath">文件路径</param>
    internal FileAccessor(string filepath)
    {
        this.filepath = filepath;
        file = new FileStream(filepath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 1024,
            FileOptions.Asynchronous);
        handle = file.SafeFileHandle;
        normalizeTextFile();
        buildIndex();
    }

    /// <summary>
    /// 打开文件时流式检测全文编码，并静默保存为 UTF-8（无 BOM）+ LF。
    /// </summary>
    private void normalizeTextFile()
    {
        if (file.Length == 0)
            return;

        file.Seek(0, SeekOrigin.Begin);
        var detection = CharsetDetector.DetectFromStream(file);
        var detected = detection.Detected;
        var encoding = detected?.Encoding ?? Encoding.UTF8;

        if (isUtf8Compatible(encoding) &&
            detected?.HasBOM != true &&
            !containsCarriageReturn())
        {
            file.Seek(0, SeekOrigin.Begin);
            return;
        }

        var tempPath = getTempPath();
        try
        {
            writeNormalizedTempFile(tempPath, encoding);

            file.SetLength(0);
            file.Seek(0, SeekOrigin.Begin);
            using var tempInput = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                stream_buffer_size, FileOptions.SequentialScan);
            tempInput.CopyTo(file, stream_buffer_size);
            file.Flush();
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException ex)
            {
                logger.Warn(ex, $"删除临时文件 {tempPath} 失败");
            }

            file.Seek(0, SeekOrigin.Begin);
        }
    }

    private static bool isUtf8Compatible(Encoding encoding)
    {
        return encoding.CodePage == Encoding.UTF8.CodePage ||
               encoding.CodePage == Encoding.ASCII.CodePage;
    }

    private string getTempPath()
    {
        var directory = Path.GetDirectoryName(filepath);
        var filename = Path.GetFileName(filepath);
        return Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".{filename}.{Guid.NewGuid():N}.tmp");
    }

    private bool containsCarriageReturn()
    {
        file.Seek(0, SeekOrigin.Begin);

        var buffer = ArrayPool<byte>.Shared.Rent(stream_buffer_size);
        try
        {
            while (true)
            {
                var bytesRead = file.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    return false;

                for (var i = 0; i < bytesRead; i++)
                    if (buffer[i] == '\r')
                        return true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            file.Seek(0, SeekOrigin.Begin);
        }
    }

    private void writeNormalizedTempFile(string tempPath, Encoding encoding)
    {
        file.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(file, encoding, false, stream_buffer_size, true);
        using var tempOutput = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            stream_buffer_size, FileOptions.SequentialScan);
        using var writer = new StreamWriter(tempOutput, utf8_no_bom, stream_buffer_size);
        var buffer = ArrayPool<char>.Shared.Rent(text_buffer_size);
        var firstChar = true;
        var previousWasCr = false;

        try
        {
            while (true)
            {
                var charsRead = reader.Read(buffer, 0, buffer.Length);
                if (charsRead == 0)
                    break;

                for (var i = 0; i < charsRead; i++)
                {
                    var ch = buffer[i];

                    if (firstChar)
                    {
                        firstChar = false;
                        if (ch == '\uFEFF')
                            continue;
                    }

                    if (previousWasCr)
                    {
                        previousWasCr = false;
                        if (ch == '\n')
                            continue;
                    }

                    if (ch == '\r')
                    {
                        writer.Write('\n');
                        previousWasCr = true;
                        continue;
                    }

                    writer.Write(ch);
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 手动建立行号偏移索引
    /// </summary>
    private void buildIndex()
    {
        long currentOffset = 0;
        long lineStart = 0;

        while (true)
        {
            var b = file.ReadByte();
            if (b == -1) break;

            currentOffset++;

            if (b == '\n')
            {
                index.Add(new LineSpan(lineStart, currentOffset - 1, currentOffset));
                lineStart = currentOffset;
            }
        }

        if (lineStart < file.Length) index.Add(new LineSpan(lineStart, file.Length, file.Length));
    }

    /// <summary>
    /// 读取多个行范围（每个范围包括起始和结束行）
    /// </summary>
    /// <param name="lineRanges">行范围列表，Key=起始行，Value=结束行（都包括）</param>
    /// <returns></returns>
    public async Task<List<LineContent>> ReadLines(List<KeyValuePair<int, int>> lineRanges)
    {
        var contents = new List<LineContent>();

        foreach (var range in lineRanges)
        {
            var startLine = range.Key;
            var endLine = range.Value;

            // 验证范围
            if (startLine < 0 || startLine >= index.Count)
            {
                logger.Warn($"起始行号 {startLine} 超出范围 [0, {index.Count})");
                continue;
            }

            if (endLine < 0 || endLine >= index.Count)
            {
                logger.Warn($"结束行号 {endLine} 超出范围 [0, {index.Count})");
                continue;
            }

            if (startLine > endLine)
            {
                logger.Warn($"起始行号 {startLine} 大于结束行号 {endLine}");
                continue;
            }

            // 读取这个范围内的所有行
            for (var line = startLine; line <= endLine; line++)
            {
                var span = index[line];
                var length = span.LineEnd - span.LineStart;

                if (length <= 0)
                {
                    contents.Add(new LineContent(
                        string.Empty,
                        line,
                        0));
                    continue;
                }

                var buf = ArrayPool<byte>.Shared.Rent((int)length);
                try
                {
                    var memory = buf.AsMemory(0, (int)length);
                    var bytesRead =
                        await RandomAccess.ReadAsync(handle, memory, span.LineStart, CancellationToken.None);

                    if (bytesRead < length)
                        logger.Warn($"读取 {filepath} 偏移 {span.LineStart} 预期 {length} 字节，实际 {bytesRead}");

                    var content = Encoding.UTF8.GetString(buf, 0, bytesRead);
                    contents.Add(new LineContent(
                        content,
                        line,
                        bytesRead));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
        }

        return contents;
    }

    /// <summary>
    /// 替换单个连续范围的行（原地替换，要求字节数匹配）
    /// </summary>
    /// <param name="startLine">起始行号（包括）</param>
    /// <param name="endLine">结束行号（包括）</param>
    /// <param name="content">新内容，必须包含范围内所有行</param>
    public async Task<bool> ReplaceLines(int startLine, int endLine, List<LineContent> content)
    {
        // 验证范围
        if (startLine < 0 || startLine >= index.Count)
            throw new ArgumentOutOfRangeException(nameof(startLine), $"起始行号 {startLine} 超出范围 [0, {index.Count})");

        if (endLine < 0 || endLine >= index.Count)
            throw new ArgumentOutOfRangeException(nameof(endLine), $"结束行号 {endLine} 超出范围 [0, {index.Count})");

        if (startLine > endLine)
            throw new ArgumentException($"起始行号 {startLine} 不能大于结束行号 {endLine}");

        var expectedCount = endLine - startLine + 1;
        if (content.Count != expectedCount)
            throw new ArgumentException($"内容行数 {content.Count} 与范围行数 {expectedCount} 不匹配");

        // 验证内容中的行号是否连续且匹配范围
        if (content.Any(ct => ct.LineNumber < startLine || ct.LineNumber > endLine))
            throw new ArgumentOutOfRangeException(nameof(content), "内容中的行号超出指定范围");

        // 按行号排序（防止乱序）
        var sortedContent = content.OrderBy(ct => ct.LineNumber).ToList();

        // 验证行号连续性
        for (var i = 0; i < sortedContent.Count; i++)
            if (sortedContent[i].LineNumber != startLine + i)
                throw new ArgumentException($"内容行号不连续或不匹配，预期 {startLine + i}，实际 {sortedContent[i].LineNumber}");

        // 检查：换行符！如果有就REJECT
        var bad = content
            .Where(c => c.Content.Contains('\n') || c.Content.Contains('\r'))
            .Select(c => c.LineNumber)
            .ToList();

        if (bad.Count > 0)
            throw new ArgumentException(
                $"Line(s) [{string.Join(", ", bad)}] contain newlines",
                nameof(content));

        var rangeStart = index[startLine].LineStart;
        var rangeEndExclusive = index[endLine].NextStart;
        var currentLength = rangeEndExclusive - rangeStart;
        var hasTrailingNewline = index[endLine].NextStart > index[endLine].LineEnd;

        var replacementLineLengths = sortedContent
            .Select(ct => Encoding.UTF8.GetByteCount(ct.Content))
            .ToArray();
        var replacementLength = replacementLineLengths.Sum()
            + Math.Max(0, sortedContent.Count - 1)
            + (hasTrailingNewline ? 1 : 0);

        var rebuiltIndex = new List<LineSpan>(sortedContent.Count);
        long nextLineStart = rangeStart;
        for (var i = 0; i < sortedContent.Count; i++)
        {
            var lineStart = nextLineStart;
            var lineEnd = lineStart + replacementLineLengths[i];
            var lineNextStart = lineEnd;

            if (i < sortedContent.Count - 1 || hasTrailingNewline)
                lineNextStart++;

            rebuiltIndex.Add(new LineSpan(lineStart, lineEnd, lineNextStart));
            nextLineStart = lineNextStart;
        }

        var delta = replacementLength - currentLength;

        if (delta == 0)
        {
            await writeReplacementInPlaceAsync(rangeStart, sortedContent, hasTrailingNewline);
        }
        else
        {
            await rewriteFileWithReplacementAsync(rangeStart, rangeEndExclusive, sortedContent, hasTrailingNewline);
            applyIndexDelta(startLine, endLine, rebuiltIndex, delta);
        }

        if (delta == 0)
            applyIndexDelta(startLine, endLine, rebuiltIndex, 0);

        return true;
    }

    private async Task writeReplacementInPlaceAsync(long offset, IReadOnlyList<LineContent> content, bool hasTrailingNewline)
    {
        file.Seek(offset, SeekOrigin.Begin);
        await using var writer = new StreamWriter(file, utf8_no_bom, stream_buffer_size, leaveOpen: true);
        await writeReplacementSectionAsync(writer, content, hasTrailingNewline);
        await writer.FlushAsync();
        file.Seek(0, SeekOrigin.End);
    }

    private async Task rewriteFileWithReplacementAsync(long rangeStart, long rangeEndExclusive, IReadOnlyList<LineContent> content, bool hasTrailingNewline)
    {
        var originalLength = file.Length;
        var tempPath = getTempPath();

        try
        {
            await using (var tempOutput = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                stream_buffer_size, FileOptions.SequentialScan))
            {
                await copyRangeToStreamAsync(tempOutput, 0, rangeStart);
                await using (var writer = new StreamWriter(tempOutput, utf8_no_bom, stream_buffer_size, leaveOpen: true))
                {
                    await writeReplacementSectionAsync(writer, content, hasTrailingNewline);
                    await writer.FlushAsync();
                }
                await copyRangeToStreamAsync(tempOutput, rangeEndExclusive, originalLength - rangeEndExclusive);
                await tempOutput.FlushAsync();
            }

            file.SetLength(0);
            file.Seek(0, SeekOrigin.Begin);
            await using var tempInput = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                stream_buffer_size, FileOptions.SequentialScan);
            await tempInput.CopyToAsync(file, stream_buffer_size, CancellationToken.None);
            await file.FlushAsync(CancellationToken.None);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException ex)
            {
                logger.Warn(ex, $"删除临时文件 {tempPath} 失败");
            }

            file.Seek(0, SeekOrigin.Begin);
        }
    }

    private async Task writeReplacementSectionAsync(StreamWriter writer, IReadOnlyList<LineContent> content, bool hasTrailingNewline)
    {
        for (var i = 0; i < content.Count; i++)
        {
            await writer.WriteAsync(content[i].Content);

            if (i < content.Count - 1 || hasTrailingNewline)
                await writer.WriteAsync("\n");
        }
    }

    private async Task copyRangeToStreamAsync(Stream destination, long sourceOffset, long length)
    {
        if (length <= 0)
            return;

        var buffer = ArrayPool<byte>.Shared.Rent(stream_buffer_size);
        try
        {
            var currentOffset = sourceOffset;
            var remaining = length;

            while (remaining > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, remaining);
                var bytesRead = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, chunk), currentOffset, CancellationToken.None);
                if (bytesRead == 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), CancellationToken.None);
                currentOffset += bytesRead;
                remaining -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void applyIndexDelta(int startLine, int endLine, List<LineSpan> rebuiltIndex, long delta)
    {
        for (var i = startLine; i <= endLine; i++)
            index[i] = rebuiltIndex[i - startLine];

        if (delta == 0)
            return;

        for (var i = endLine + 1; i < index.Count; i++)
        {
            var span = index[i];
            index[i] = new LineSpan(
                span.LineStart + delta,
                span.LineEnd + delta,
                span.NextStart + delta);
        }
    }

    public void Dispose()
    {
        file.Dispose();
    }
}
