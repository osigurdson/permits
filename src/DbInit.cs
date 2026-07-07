using System.Text.RegularExpressions;
using System.Reflection;

using Microsoft.Data.SqlClient;

namespace Permits;

class DbInit
{
    public const string OltpDbName = "Permits_OLTP";

    public static string GetConnStr(string database, string password, int port = 1733)
    {
        return $"Server=localhost,{port};Database={database};" +
               $"User ID=sa;Password={password};Encrypt=False";
    }

    public static async Task CreateOltpDbAsync(string masterPassword)
    {
        await CreateDbAsync(OltpDbName, masterPassword);
        string sqlText = await ReadResourceAsync("oltp.sql");
        await RunScriptAsync(OltpDbName, sqlText, masterPassword);
    }

    private static async Task CreateDbAsync(
            string dbName,
            string masterPassword)
    {
        using var mconn = new SqlConnection(GetConnStr("master", masterPassword));
        await mconn.OpenAsync();
        using var cmd = mconn.CreateCommand();
        cmd.CommandText = $"""
        IF EXISTS (SELECT name FROM sys.databases WHERE name = '{dbName}')
        BEGIN
            ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            DROP DATABASE [{dbName}];
        END
        CREATE DATABASE [{dbName}];
        """;
        Console.WriteLine($"Created database: {dbName} ");
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task RunScriptAsync(
            string dbName,
            string script,
            string password)
    {
        using var conn = new SqlConnection(GetConnStr(dbName, password));
        await conn.OpenAsync();
        string[] batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (var b in batches)
        {
            if (string.IsNullOrWhiteSpace(b))
            {
                continue;
            }
            using var c = conn.CreateCommand();
            c.CommandText = b;
            await c.ExecuteNonQueryAsync();
        }
    }

    private static Task<string> ReadResourceAsync(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var fullName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(name));
        using var stream = asm.GetManifestResourceStream(fullName);
        if (stream == null)
        {
            throw new InvalidOperationException("Stream resource was null");
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEndAsync();
    }
}
