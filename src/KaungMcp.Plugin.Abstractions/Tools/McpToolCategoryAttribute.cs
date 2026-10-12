namespace KaungMcp.Tools;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class McpToolCategoryAttribute(
    string id,
    string displayName,
    int order = 0,
    string? description = null) : Attribute
{
    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public int Order { get; } = order;

    public string? Description { get; } = description;
}
