using System;
using LinqToDB.Mapping;

namespace Pechka.AspNet.Jobs;

[Table("BackgroundJobs")]
internal class PechkaJobRow
{
    [PrimaryKey, Identity]
    public long Id { get; set; }

    [Column, NotNull]
    public string Type { get; set; } = null!;

    [Column]
    public string? Payload { get; set; }

    [Column]
    public int State { get; set; }

    [Column]
    public int Attempts { get; set; }

    [Column]
    public DateTime CreatedAt { get; set; }

    [Column]
    public DateTime? TakenAt { get; set; }

    [Column]
    public DateTime? FinishedAt { get; set; }

    [Column]
    public string? Error { get; set; }
}

internal static class JobState
{
    public const int Pending = 0;
    public const int Running = 1;
    public const int Completed = 2;
    public const int Failed = 3;
}

internal static class JobTime
{
    // Values are UTC, but stored in plain timestamp columns which Npgsql refuses to
    // accept with DateTimeKind.Utc
    public static DateTime UtcNow => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
}
