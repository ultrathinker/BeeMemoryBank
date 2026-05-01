using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

public class MigrationRunnerTriggerTests
{
    [Fact]
    public void SplitStatements_CreateTrigger_YieldsCorrectCount()
    {
        var sql = """
            CREATE TABLE a(id INTEGER PRIMARY KEY);
            CREATE TABLE b(a_id INTEGER, val TEXT);
            CREATE TRIGGER trg_copy AFTER INSERT ON a BEGIN INSERT INTO b VALUES(NEW.id, 'copied'); END;
            """;

        var statements = MigrationRunner.SplitStatements(sql);

        statements.Should().HaveCount(3);
        statements[0].Trim().Should().StartWith("CREATE TABLE a");
        statements[1].Trim().Should().StartWith("CREATE TABLE b");
        statements[2].Trim().Should().StartWith("CREATE TRIGGER trg_copy");
        statements[2].Should().Contain("BEGIN");
        statements[2].Should().Contain("END");
    }

    [Fact]
    public async Task SplitStatements_TriggerFires_EndToEnd()
    {
        using var factory = DbConnectionFactory.CreateInMemory($"bmb_trigger_test_{Guid.NewGuid():N}");
        using var conn = factory.CreateConnection();

        await conn.ExecuteAsync("CREATE TABLE a(id INTEGER PRIMARY KEY, val TEXT)");
        await conn.ExecuteAsync("CREATE TABLE b(a_id INTEGER, val TEXT)");

        var triggerSql = """
            CREATE TRIGGER trg_copy AFTER INSERT ON a
            BEGIN
                INSERT INTO b(a_id, val) VALUES(NEW.id, NEW.val);
                UPDATE b SET val = 'triggered' WHERE a_id = NEW.id;
            END
            """;

        var stmts = MigrationRunner.SplitStatements(triggerSql);
        stmts.Should().HaveCount(1);

        await conn.ExecuteAsync(stmts[0].Trim());

        await conn.ExecuteAsync("INSERT INTO a(val) VALUES ('hello')");

        var rows = (await conn.QueryAsync<(int a_id, string val)>("SELECT a_id, val FROM b")).ToList();
        rows.Should().HaveCount(1);
        rows[0].val.Should().Be("triggered");
    }

    [Fact]
    public void SplitStatements_Regression_NoTriggerSql()
    {
        var sql = "CREATE TABLE a(id INT);\nCREATE INDEX ix_a ON a(id);\n-- comment\nINSERT INTO a VALUES (1);";

        var statements = MigrationRunner.SplitStatements(sql);

        statements.Should().HaveCount(3);
        statements[0].Trim().Should().Be("CREATE TABLE a(id INT)");
        statements[1].Trim().Should().Be("CREATE INDEX ix_a ON a(id)");
        statements[2].Trim().Should().Contain("INSERT INTO a VALUES (1)");
    }

    [Fact]
    public void SplitStatements_CaseExpression_DoesNotAffectDepth()
    {
        var sql = """
            CREATE TABLE t AS SELECT CASE WHEN x > 0 THEN 1 ELSE 0 END AS y FROM src;
            CREATE TABLE z(id INT);
            """;

        var statements = MigrationRunner.SplitStatements(sql);

        statements.Should().HaveCount(2);
    }

    [Fact]
    public void SplitStatements_SemicolonInStringLiteral_PreservesStatement()
    {
        var sql = "INSERT INTO t(val) VALUES('a;b');\nINSERT INTO t(val) VALUES('c');";

        var statements = MigrationRunner.SplitStatements(sql);

        statements.Should().HaveCount(2);
        statements[0].Trim().Should().Be("INSERT INTO t(val) VALUES('a;b')");
        statements[1].Trim().Should().Be("INSERT INTO t(val) VALUES('c')");
    }
}
