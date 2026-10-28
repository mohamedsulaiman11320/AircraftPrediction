using AFP.Domain.Enums;

namespace AFP.Domain.Entities;

/// <summary>Seeded lifecycle reference row (table <c>status</c>). Rows come only from the reference-data seed.</summary>
public class Status
{
    public LifecycleStatus StatusId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>Implemented by every table that carries a lifecycle <c>status_id</c> foreign key.</summary>
public interface ILifecycleEntity
{
    LifecycleStatus StatusId { get; set; }
}
