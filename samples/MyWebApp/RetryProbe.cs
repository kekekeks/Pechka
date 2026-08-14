using LinqToDB.Data;

namespace MyWebApp;

// Deterministic transient-failure injector: the sequence survives rollbacks, so every odd
// attempt raises a genuine serialization_failure (40001) and every even attempt passes.
// Requires: CREATE SEQUENCE retry_probe;
public static class RetryProbe
{
    public static Task FailEveryOtherAttempt(MyDbContextManager db)
        => db.ExecAsync(ctx => ctx.ExecuteAsync(
            "DO $$ BEGIN IF nextval('retry_probe') % 2 = 1 THEN RAISE SQLSTATE '40001'; END IF; END $$"));
}
