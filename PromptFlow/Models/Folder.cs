namespace PromptFlow.Models;

public sealed class Folder
{
    public long Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsLocked { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int ItemCount { get; set; }
}
