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

/// <summary>Adds job expiration and the indices matching the claim/cleanup queries.</summary>
[MigrationDate(2026, 8, 15, 0, 0)]
public class PechkaBackgroundJobsMigration2 : Migration
{
    public override void Up()
    {
        Alter.Table("BackgroundJobs").AddColumn("ExpiresAt").AsDateTime().Nullable();

        Delete.Index("IX_BackgroundJobs_State_Id").OnTable("BackgroundJobs");
        Create.Index("IX_BackgroundJobs_State_Type_Id").OnTable("BackgroundJobs")
            .OnColumn("State").Ascending()
            .OnColumn("Type").Ascending()
            .OnColumn("Id").Ascending();
        Create.Index("IX_BackgroundJobs_State_FinishedAt").OnTable("BackgroundJobs")
            .OnColumn("State").Ascending()
            .OnColumn("FinishedAt").Ascending();
        Create.Index("IX_BackgroundJobs_State_ExpiresAt").OnTable("BackgroundJobs")
            .OnColumn("State").Ascending()
            .OnColumn("ExpiresAt").Ascending();
    }

    public override void Down()
    {
        Delete.Index("IX_BackgroundJobs_State_ExpiresAt").OnTable("BackgroundJobs");
        Delete.Index("IX_BackgroundJobs_State_FinishedAt").OnTable("BackgroundJobs");
        Delete.Index("IX_BackgroundJobs_State_Type_Id").OnTable("BackgroundJobs");
        Create.Index("IX_BackgroundJobs_State_Id").OnTable("BackgroundJobs")
            .OnColumn("State").Ascending()
            .OnColumn("Id").Ascending();
        Delete.Column("ExpiresAt").FromTable("BackgroundJobs");
    }
}
