using FluentMigrator;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Jobs;

/// <summary>
/// Creates the background job queue table. Only discovered when the job system is registered
/// via AddBackgroundJobs (see <see cref="IPechkaMigrationSource"/>).
/// </summary>
[MigrationDate(2026, 8, 11, 0, 0)]
public class PechkaBackgroundJobsMigration : Migration
{
    public override void Up()
    {
        Create.Table("BackgroundJobs")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("Type").AsString(512).NotNullable()
            .WithColumn("Payload").AsString(int.MaxValue).Nullable()
            .WithColumn("State").AsInt32().NotNullable()
            .WithColumn("Attempts").AsInt32().NotNullable()
            .WithColumn("CreatedAt").AsDateTime().NotNullable()
            .WithColumn("TakenAt").AsDateTime().Nullable()
            .WithColumn("FinishedAt").AsDateTime().Nullable()
            .WithColumn("Error").AsString(int.MaxValue).Nullable();

        Create.Index("IX_BackgroundJobs_State_Id").OnTable("BackgroundJobs")
            .OnColumn("State").Ascending()
            .OnColumn("Id").Ascending();
    }

    public override void Down() => Delete.Table("BackgroundJobs");
}
