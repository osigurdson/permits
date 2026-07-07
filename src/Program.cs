using System.CommandLine;
using Permits;

var root = new RootCommand("Permits CLI");
var oltp = new Command("oltp", "OLTP commands");
var olap = new Command("olap", "OLAP commands");
var initOltp = new Command("init", "Create the OLTP database (Permits_OLTP)");
initOltp.SetAction(async (parseResult, cancellationToken) =>
{
    await DbInit.CreateOltpDbAsync(GetSqlPassword());
});
oltp.Subcommands.Add(initOltp);

root.Subcommands.Add(oltp);
root.Subcommands.Add(olap);

return await root.Parse(args).InvokeAsync();

static string GetSqlPassword()
{
    var sqlpwd = Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD");
    if (sqlpwd == null)
    {
        Console.WriteLine("MSSQL_SA_PASSWORD environment variable not set.");
        Environment.Exit(1);
    }
    return sqlpwd;
}

