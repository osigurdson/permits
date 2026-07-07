using System.CommandLine;
using Permits;

var root = new RootCommand("Permits CLI");
root.Subcommands.Add(CreateOltpCommand());
root.Subcommands.Add(CreateOlapCommand());
return await root.Parse(args).InvokeAsync();

static Command CreateOltpCommand()
{
    var oltp = new Command("oltp", "OLTP commands");

    // permits oltp init 
    var initOltp = new Command("init", "Create the OLTP database (Permits_OLTP)");
    initOltp.SetAction(async (parseResult, cancellationToken) =>
    {
        await DbInit.CreateOltpDbAsync(GetSqlPassword());
    });
    oltp.Subcommands.Add(initOltp);

    var eventCountOpt = new Option<int>("--event-count")
    {
        Required = true,
        Description = "Number of events to simulate",
    };
    var epochOpt = new Option<DateTime>("--epoch")
    {
        Required = true,
        Description = "Simulation start time (e.g. 2025-01-01)",
    };
    var seedOpt = new Option<int>("--seed")
    {
        Required = true,
        Description = "Random seed for the deterministic event stream",
    };

    var sim = new Command("sim", "Apply simulated events to the OLTP database");
    sim.Options.Add(eventCountOpt);
    sim.SetAction(async (parseResult, cancellationToken) =>
    {
        await SimRunner.RunAsync(GetSqlPassword(), parseResult.GetValue(eventCountOpt));
    });

    var simInit = new Command("init", "Initialize simulation state and apply the first events");
    simInit.Options.Add(epochOpt);
    simInit.Options.Add(seedOpt);
    simInit.Options.Add(eventCountOpt);
    simInit.SetAction(async (parseResult, cancellationToken) =>
    {
        await SimRunner.InitAsync(
            GetSqlPassword(),
            parseResult.GetValue(seedOpt),
            parseResult.GetValue(epochOpt),
            parseResult.GetValue(eventCountOpt));
    });
    sim.Subcommands.Add(simInit);
    oltp.Subcommands.Add(sim);

    return oltp;
}

static Command CreateOlapCommand()
{
    var olap = new Command("olap", "OLAP commands");

    // permits olap init
    var initOlap = new Command("init", "Create the OLAP database (Permits_DW)");
    initOlap.SetAction(async (parseResult, cancellationToken) =>
    {
        await DbInit.CreateOlapDbAsync(GetSqlPassword());
    });
    olap.Subcommands.Add(initOlap);

    // permits olap etl run
    var etl = new Command("etl", "ETL commands");
    var etlRun = new Command("run", "Load new OLTP activity into the warehouse (idempotent)");
    etlRun.SetAction(async (parseResult, cancellationToken) =>
    {
        await EtlRunner.RunAsync(GetSqlPassword());
    });
    etl.Subcommands.Add(etlRun);
    olap.Subcommands.Add(etl);

    // permits olap report --name permits-issued-report --from 2025-01 --to 2025-12
    var nameOpt = new Option<string>("--name")
    {
        Required = true,
        Description = "Report name (permits-issued-report)",
    };
    var fromOpt = new Option<string>("--from")
    {
        Required = true,
        Description = "First month, inclusive (yyyy-MM)",
    };
    var toOpt = new Option<string>("--to")
    {
        Required = true,
        Description = "Last month, inclusive (yyyy-MM)",
    };
    var csvOpt = new Option<bool>("--csv")
    {
        Description = "Output CSV instead of a table",
    };
    var report = new Command("report", "Output a report for the month range");
    report.Options.Add(nameOpt);
    report.Options.Add(fromOpt);
    report.Options.Add(toOpt);
    report.Options.Add(csvOpt);
    report.SetAction(async (parseResult, cancellationToken) =>
    {
        await ReportRunner.RunAsync(
            GetSqlPassword(),
            parseResult.GetValue(nameOpt)!,
            parseResult.GetValue(fromOpt)!,
            parseResult.GetValue(toOpt)!,
            parseResult.GetValue(csvOpt));
    });
    olap.Subcommands.Add(report);

    return olap;
}

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
