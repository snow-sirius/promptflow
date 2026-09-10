namespace PromptFlow.Models;

public sealed record ClipboardItemPage(
    IReadOnlyList<ClipboardItem> Items,
    bool HasMore,
    DateTime? BeforeTime = null,
    long? BeforeId = null,
    int? BeforeSortOrder = null);
