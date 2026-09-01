namespace PromptFlow.Models;

public sealed record ClipboardItem
{
    public long Id { get; init; }
    public string? TextContent { get; set; }
    public string? HtmlContent { get; set; }
    public string? RtfContent { get; set; }
    public byte[]? ImagePng { get; set; }
    public string? ExtraFormatsJson { get; set; }
    public string DisplayText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime LastCopiedAt { get; set; }
    public bool IsFavorite { get; set; }

    public string Preview => string.IsNullOrWhiteSpace(DisplayText)
        ? (TextContent ?? (ImagePng is not null ? "图片" : "无文本内容"))
        : DisplayText;
}
