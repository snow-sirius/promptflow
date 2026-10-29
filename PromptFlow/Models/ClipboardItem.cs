namespace PromptFlow.Models;

public sealed record ClipboardItem
{
    public long Id { get; init; }
    public string? TextContent { get; set; }
    public string? HtmlContent { get; set; }
    public string? RtfContent { get; set; }
    public byte[]? ImagePng { get; set; }
    public bool HasImage { get; set; }
    public string? ExtraFormatsJson { get; set; }
    public string DisplayText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime LastCopiedAt { get; set; }
    public DateTime LocalLastCopiedAt => LastCopiedAt.ToLocalTime();
    public bool IsFavorite { get; set; }
    public int? SortOrder { get; set; }

    public string Preview => string.IsNullOrWhiteSpace(DisplayText)
        ? (TextContent ?? (ImagePng is not null ? "图片" : "无文本内容"))
        : DisplayText;

    /// <summary>列表查询中统计的正文字符数（不含换行），避免为了字数把整段正文读进列表。</summary>
    public int TextLength { get; init; }

    /// <summary>只有纯文字词条才显示字数：图片词条和空文本都不占用角落。</summary>
    public bool HasTextCount => !HasImage && TextLength > 0;

    public string TextLengthText => TextLength > 0 ? $"{TextLength:N0} 字" : string.Empty;
}
