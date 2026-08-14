using Npgsql;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests;

public class RetryClassifierTests
{
    private static PostgresException Pg(string sqlState) =>
        new("test error", "ERROR", "ERROR", sqlState);

    [Fact]
    public void Null_Is_Not_Transient() => Assert.False(TransactionRetry.IsDefaultTransient(null));

    [Fact]
    public void Plain_Exception_Is_Not_Transient() =>
        Assert.False(TransactionRetry.IsDefaultTransient(new Exception("nope")));

    [Fact]
    public void Serialization_Failure_Is_Transient() =>
        Assert.True(TransactionRetry.IsDefaultTransient(Pg(PostgresErrorCodes.SerializationFailure)));

    [Fact]
    public void Deadlock_Is_Transient() =>
        Assert.True(TransactionRetry.IsDefaultTransient(Pg(PostgresErrorCodes.DeadlockDetected)));

    [Fact]
    public void Unique_Violation_Is_Not_Transient() =>
        Assert.False(TransactionRetry.IsDefaultTransient(Pg(PostgresErrorCodes.UniqueViolation)));

    [Fact]
    public void Inner_Exception_Chain_Is_Walked() =>
        Assert.True(TransactionRetry.IsDefaultTransient(
            new InvalidOperationException("outer",
                new InvalidOperationException("middle", Pg(PostgresErrorCodes.SerializationFailure)))));

    [Fact]
    public void AggregateException_Inners_Are_Inspected() =>
        Assert.True(TransactionRetry.IsDefaultTransient(new AggregateException(
            new Exception("unrelated"), Pg(PostgresErrorCodes.DeadlockDetected))));

    [Fact]
    public void AggregateException_Without_A_Transient_Inner_Is_Not_Transient() =>
        Assert.False(TransactionRetry.IsDefaultTransient(new AggregateException(
            new Exception("a"), Pg(PostgresErrorCodes.UniqueViolation))));

    [Fact]
    public void AggregateException_Inners_Are_Inspected_Recursively() =>
        Assert.True(TransactionRetry.IsDefaultTransient(new AggregateException(
            new Exception("wrapper", Pg(PostgresErrorCodes.SerializationFailure)))));

    [Fact]
    public void Npgsql_Transient_Flag_Is_Honored() =>
        // Connection-failure SQL states are reported transient by Npgsql itself
        Assert.True(TransactionRetry.IsDefaultTransient(Pg(PostgresErrorCodes.ConnectionException)));
}
