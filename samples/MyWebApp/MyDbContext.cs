using LinqToDB.Data;
using LinqToDB.DataProvider;
using LinqToDB.Mapping;
using Pechka.AspNet.Database;

namespace MyWebApp;

[Table("ToDoItems")]
public class TodoItem
{
    [PrimaryKey, Identity]
    public int Id { get; set; }
    [Column]
    public string Name { get; set; }
}

public class MyDbContext : DataConnection
{
    public MyDbContext(IDataProvider dataProvider, string connectionString) : base(dataProvider, connectionString)
    {
    }
}

public class MyDbContextManager : DbContextManagerBase<MyDbContext>
{
    public MyDbContextManager(IDataProvider dataProvider, string connectionString) : base(() =>
        new MyDbContext(dataProvider, connectionString))
    {
    }
}