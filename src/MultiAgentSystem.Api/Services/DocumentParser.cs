// ============================================================
// DocumentParser - 文档解析与分片
//
// 设计意图：
//   - PDF 用 PdfPig 专业库提取（替代正则方案），支持 FlateDecode 压缩流 + 中文字体
//   - txt/md 加编码自动检测（BOM → UTF-8 严格验证 → GBK 回退），解决中文文档乱码
//   - docx 用 zipfile 解压 word/document.xml，正则去标签提纯文本
//   - 提供两种分片策略：滑动窗口 / 按标题语义分片（Markdown #）
//   - 解析失败时返回带"[解析失败]"标记的占位文本，不阻断后续流程
//
// 支持格式：
//   - .txt  纯文本，自动检测编码（UTF-8 BOM / UTF-8 无 BOM / GBK / Unicode）
//   - .md   Markdown，按 # 标题语义分片（保留语义边界）
//   - .pdf  PdfPig 按页提取，每页独立分片，PageNumber 准确
//   - .docx zipfile 解压 word/document.xml，正则去标签提纯文本
//
// 分片策略选择理由：
//   - Markdown 优先用标题分片：标题天然是语义边界，同一节内容主题集中
//   - PDF 按页分片：页是天然边界，且保留 PageNumber 便于溯源
//   - TXT/Word 用滑动窗口：保证分片大小均匀，overlap 保证上下文连续
//   - chunkSize=500 字符、overlap=100 字符：经验值，中英文混合场景下
//     500 字符约含 200-300 个 token，匹配 Embedding 模型上下文窗口
// ============================================================

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class DocumentParser
{
    /// <summary>默认分片大小（字符数）</summary>
    public const int DefaultChunkSize = 500;

    /// <summary>默认重叠大小（字符数），保证分片间上下文连续</summary>
    public const int DefaultOverlap = 100;

    /// <summary>
    /// 解析文档为纯文本（兼容旧调用方；PDF 在此返回全文拼接）
    /// </summary>
    /// <param name="fileName">原始文件名（用于推断类型）</param>
    /// <param name="bytes">文件二进制内容</param>
    /// <returns>解析出的纯文本；失败则返回带 [解析失败] 标记的占位</returns>
    public static string ExtractText(string fileName, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".txt" => DecodeText(bytes),
                ".md" => DecodeText(bytes),
                ".pdf" => ExtractPdfText(bytes),
                ".docx" => ExtractDocxText(bytes),
                _ => $"[未知文件类型 {ext}，无法解析] {DecodeText(bytes)}"
            };
        }
        catch (Exception ex)
        {
            // 解析失败不抛异常，返回占位文本，状态由调用方标记为 Failed
            return $"[解析失败：{ext} 文件 - {ex.Message}]";
        }
    }

    /// <summary>
    /// 文本编码自动检测（解决中文文档乱码）
    ///
    /// 检测顺序：
    ///   1. BOM 检测（UTF-8 BOM / UTF-16 LE / UTF-16 BE）
    ///   2. UTF-8 严格验证（无 BOM 但内容是合法 UTF-8 → 直接用）
    ///   3. GBK 回退（中文 Windows 常见编码，记事本 ANSI 默认即 GBK）
    ///
    /// 为什么需要 GBK 回退：
    ///   - Windows 中文环境导出的 txt 常是 GBK 编码
    ///   - 若强制 UTF-8 解码 GBK 字节流，会产生大量 U+FFFD 替换字符（乱码）
    ///   - GBK 回退保证中文内容可正确读取
    /// </summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        // 1. BOM 检测
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 2. UTF-8 严格验证（无 BOM 但可能是 UTF-8）：非法字节抛异常则回退
        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8，继续走 GBK 回退
        }

        // 3. GBK 回退（注册 CodePages 编码提供器后取 GBK）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GBK").GetString(bytes);
    }

    /// <summary>
    /// PDF 文本提取（PdfPig 专业库）
    ///
    /// 替代原正则方案：正则无法处理 FlateDecode 压缩流和 CID 中文字体，
    /// 提取出的是二进制碎片而非正文。PdfPig 能正确解码压缩流 + 字体映射。
    ///
    /// 本方法返回全文拼接（按页顺序），供 ExtractText 兼容调用。
    /// 按页分片请用 ParsePdfByPages（PageNumber 更准确）。
    /// </summary>
    private static string ExtractPdfText(byte[] bytes)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(bytes);
        foreach (var page in doc.GetPages())
        {
            var t = page.Text;
            if (!string.IsNullOrWhiteSpace(t))
            {
                sb.AppendLine(t);
            }
        }
        if (sb.Length == 0)
            return "[解析失败：PDF 未提取到文本，可能为纯图片型 PDF 或扫描件]";
        return sb.ToString();
    }

    /// <summary>
    /// PDF 按页提取 + 分片（PageNumber 准确）
    ///
    /// 每页独立分片，避免跨页切断语义，且 PageNumber 字段填实际页码便于溯源。
    /// 这是 PDF 解析的主路径（ParseAndChunk 对 .pdf 调用此方法）。
    /// </summary>
    private static List<DocumentChunk> ParsePdfByPages(int databaseId, byte[] bytes, int chunkSize, int overlap)
    {
        var chunks = new List<DocumentChunk>();
        using var doc = PdfDocument.Open(bytes);
        int chunkIdx = 0;
        foreach (var page in doc.GetPages())
        {
            var pageText = page.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(pageText)) continue;

            // 单页可能很长（如手册页），用滑动窗口二次切分
            var pageChunks = ChunkBySlidingWindow(pageText, databaseId, chunkSize, overlap);
            foreach (var c in pageChunks)
            {
                c.PageNumber = page.Number;
                c.ChunkIndex = chunkIdx++;
                chunks.Add(c);
            }
        }
        return chunks;
    }

    /// <summary>
    /// Word .docx 文本提取
    /// docx 本质是 zip，word/document.xml 含正文
    /// 用正则去标签，提取 w:t 文本节点内容
    /// 限制：不保留段落格式、表格结构
    /// </summary>
    private static string ExtractDocxText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        if (entry == null) return "[解析失败：docx 中未找到 document.xml]";

        using var es = entry.Open();
        using var sr = new StreamReader(es, Encoding.UTF8);
        var xml = sr.ReadToEnd();

        // 提取所有 <w:t>...</w:t> 文本节点，段落结尾补换行
        var sb = new StringBuilder();
        var paraMatches = Regex.Matches(xml, @"<w:p\b[^>]*>(.*?)</w:p>", RegexOptions.Singleline);
        foreach (Match pm in paraMatches)
        {
            var paraXml = pm.Groups[1].Value;
            var textMatches = Regex.Matches(paraXml, @"<w:t[^>]*>([^<]*)</w:t>");
            foreach (Match tm in textMatches)
            {
                sb.Append(tm.Groups[1].Value);
            }
            sb.Append('\n');
        }
        if (sb.Length == 0)
            return "[解析失败：docx 未提取到文本，可能为空文档]";
        return sb.ToString();
    }

    /// <summary>
    /// 主入口：解析 + 分片
    /// 自动按文件类型选择分片策略
    /// </summary>
    public static List<DocumentChunk> ParseAndChunk(string fileName, int databaseId, byte[] bytes,
        int chunkSize = DefaultChunkSize, int overlap = DefaultOverlap)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        // Markdown 走标题分片
        if (ext == ".md")
        {
            var text = DecodeText(bytes);
            var chunks = ChunkByHeadings(text, databaseId, chunkSize, overlap);
            if (chunks.Count > 0) return chunks;
            // 标题分片失败（无标题）则退化为滑动窗口
            return ChunkBySlidingWindow(text, databaseId, chunkSize, overlap);
        }

        // PDF 走按页提取 + 分片（PageNumber 准确，支持 FlateDecode 压缩流）
        if (ext == ".pdf")
        {
            try
            {
                var pdfChunks = ParsePdfByPages(databaseId, bytes, chunkSize, overlap);
                if (pdfChunks.Count > 0) return pdfChunks;
                // 按页提取为空，退化为全文占位（标记解析失败）
            }
            catch (Exception ex)
            {
                // PdfPig 异常：返回占位分片，状态由调用方标记为 Failed
                return new List<DocumentChunk>
                {
                    new() { DatabaseId = databaseId, Content = $"[解析失败：PDF - {ex.Message}]",
                            PageNumber = 1, ChunkIndex = 0, TokenCount = 0 }
                };
            }
        }

        // txt / docx 通用：提取文本 → 滑动窗口
        var text2 = ExtractText(fileName, bytes);
        return ChunkBySlidingWindow(text2, databaseId, chunkSize, overlap);
    }

    /// <summary>
    /// 滑动窗口分片
    ///
    /// 策略说明：
    ///   - 按 chunkSize 切片，相邻切片重叠 overlap 字符
    ///   - overlap 保证跨切片的语义不被硬切断（如长句被切两半时仍可召回）
    ///   - 步长 = chunkSize - overlap，避免内容遗漏
    ///   - 中文按字符计（一个汉字算 1 字符）；英文按字母计
    ///   - 实际生产建议按 token 计（用 Microsoft.ML.Tokenizers）
    /// </summary>
    public static List<DocumentChunk> ChunkBySlidingWindow(string text, int databaseId,
        int chunkSize, int overlap)
    {
        var chunks = new List<DocumentChunk>();
        if (string.IsNullOrEmpty(text)) return chunks;

        var step = Math.Max(1, chunkSize - overlap);
        var pos = 0;
        var idx = 0;

        while (pos < text.Length)
        {
            var len = Math.Min(chunkSize, text.Length - pos);
            var content = text.Substring(pos, len).Trim();
            if (!string.IsNullOrEmpty(content))
            {
                chunks.Add(new DocumentChunk
                {
                    DatabaseId = databaseId,
                    Content = content,
                    PageNumber = 1,
                    ChunkIndex = idx,
                    TokenCount = EstimateTokens(content)
                });
                idx++;
            }
            pos += step;
            // 末尾不足一个 overlap 的余量直接结束
            if (pos + overlap >= text.Length) break;
        }
        return chunks;
    }

    /// <summary>
    /// 按 Markdown 标题语义分片
    ///
    /// 策略说明：
    ///   - 以 # 开头的行作为分割边界
    ///   - 同一标题下内容聚为一个分片
    ///   - 若单节内容超过 chunkSize，再用滑动窗口二次切片
    ///   - 优势：保留语义边界，避免切断章节内逻辑
    ///   - 劣势：分片大小不均（大节会触发二次切分）
    /// </summary>
    public static List<DocumentChunk> ChunkByHeadings(string text, int databaseId,
        int chunkSize, int overlap)
    {
        var chunks = new List<DocumentChunk>();
        if (string.IsNullOrEmpty(text)) return chunks;

        var lines = text.Split('\n');
        var currentSection = new StringBuilder();
        var currentTitle = "";
        var idx = 0;

        void FlushSection()
        {
            var sectionText = currentSection.ToString().Trim();
            if (string.IsNullOrEmpty(sectionText)) return;

            // 单节过长 → 滑动窗口二次切分
            if (sectionText.Length > chunkSize)
            {
                var sub = ChunkBySlidingWindow(sectionText, databaseId, chunkSize, overlap);
                foreach (var c in sub)
                {
                    c.ChunkIndex = idx;
                    idx++;
                    chunks.Add(c);
                }
            }
            else
            {
                chunks.Add(new DocumentChunk
                {
                    DatabaseId = databaseId,
                    Content = sectionText,
                    PageNumber = 1,
                    ChunkIndex = idx,
                    TokenCount = EstimateTokens(sectionText)
                });
                idx++;
            }
            currentSection.Clear();
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            // Markdown 标题：# ~ ######
            if (trimmed.StartsWith('#') && trimmed.Length > 1 && trimmed[1] == ' ')
            {
                FlushSection();
                currentTitle = trimmed;
                currentSection.AppendLine(line);
            }
            else
            {
                currentSection.AppendLine(line);
            }
        }
        FlushSection();
        return chunks;
    }

    /// <summary>
    /// Token 数粗略估计
    /// 中文 1 字符约 1-2 token，英文 4 字符约 1 token
    /// 简化策略：中文字符数 + 英文单词数
    /// </summary>
    private static int EstimateTokens(string content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        var cjk = 0;
        var ascii = 0;
        foreach (var c in content)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            else if (char.IsLetterOrDigit(c)) ascii++;
        }
        return cjk + ascii / 4;
    }
}
