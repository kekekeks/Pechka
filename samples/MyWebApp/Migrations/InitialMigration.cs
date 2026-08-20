using FluentMigrator;
using Pechka.AspNet.Database;

namespace MyWebApp.Migrations;

[MigrationDate(2026, 8, 21, 0, 0)]
public class InitialMigration : Migration
{
    public override void Up()
    {
        Create.Table("ToDoItems")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString().NotNullable();
        // Deterministic transient-failure injector (see RetryProbe): survives rollbacks
        Execute.Sql("CREATE SEQUENCE retry_probe");
    }

    public override void Down()
    {
        Delete.Table("ToDoItems");
        Execute.Sql("DROP SEQUENCE retry_probe");
    }
}
