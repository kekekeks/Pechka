namespace Pechka.AspNet.Database;

public class DatabaseConfig
{
    public DatabaseType Type { get; set; }
    public string ConnectionString { get; set; }
}

public enum DatabaseType
{
    Pgsql,
    SqlServer
}