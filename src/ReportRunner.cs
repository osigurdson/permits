using System.Globalization;

using Microsoft.Data.SqlClient;

namespace Permits;

/// <summary>
/// Runs named warehouse reports and writes them to stdout as CSV.
/// Month arguments are inclusive and use the yyyy-MM format from the README.
/// </summary>
static class ReportRunner
{
    private static readonly (string Name, string Description)[] Reports =
    [
        ("permits-issued-report", "Permits issued (Pending -> Active) by type and month"),
        ("active-vs-expired", "Active permits at month end vs permits expired during the month"),
        ("approvals-renewals-suspensions", "Monthly counts of approval, renewal and suspension transitions"),
    ];

    public static void List()
    {
        WriteTable(["name", "description"], rightAlign: [false, false],
            Reports.Select(r => new[] { r.Name, r.Description }).ToList());
    }

    public static async Task RunAsync(string password, string name, string from, string to, bool csv)
    {
        int fromMonth = ParseMonthKey(from);
        int toMonth = ParseMonthKey(to);

        switch (name)
        {
            case "permits-issued-report":
                await PermitsIssuedAsync(password, fromMonth, toMonth, csv);
                break;
            case "active-vs-expired":
                await ActiveVsExpiredAsync(password, fromMonth, toMonth, csv);
                break;
            case "approvals-renewals-suspensions":
                await ApprovalsRenewalsSuspensionsAsync(password, fromMonth, toMonth, csv);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown report '{name}'. Available: " +
                    string.Join(", ", Reports.Select(r => r.Name)) + ".");
        }
    }

    // Permits issued = Pending -> Active transitions, by type and month.
    private static async Task PermitsIssuedAsync(
            string password, int fromMonth, int toMonth, bool csv)
    {
        using var conn = new SqlConnection(DbInit.GetConnStr(DbInit.OlapDbName, password));
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.month_key, t.name, COUNT(*) AS permits_issued
            FROM fact_permit_transition f
            JOIN dim_date d ON d.date_key = f.date_key
            JOIN dim_permit_type t ON t.permit_type_key = f.permit_type_key
            JOIN dim_permit_state fs ON fs.state_key = f.from_state_key
            JOIN dim_permit_state ts ON ts.state_key = f.to_state_key
            WHERE fs.name = 'Pending' AND ts.name = 'Active'
              AND d.month_key BETWEEN @from AND @to
            GROUP BY d.month_key, t.name
            ORDER BY d.month_key, t.name
            """;
        cmd.Parameters.AddWithValue("@from", fromMonth);
        cmd.Parameters.AddWithValue("@to", toMonth);

        var rows = new List<string[]>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            int monthKey = reader.GetInt32(0);
            rows.Add([
                $"{monthKey / 100:D4}-{monthKey % 100:D2}",
                reader.GetString(1),
                reader.GetInt32(2).ToString(),
            ]);
        }
        Write(["month", "permit_type", "permits_issued"], rightAlign: [false, false, true], rows, csv);
    }

    // Active is a month-end census from the snapshot table; expired counts
    // expiry events (transitions into Expired) during the month, per README.
    private static async Task ActiveVsExpiredAsync(
            string password, int fromMonth, int toMonth, bool csv)
    {
        using var conn = new SqlConnection(DbInit.GetConnStr(DbInit.OlapDbName, password));
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH active AS (
                SELECT f.month_key, COUNT(*) AS n
                FROM fact_permit_state f
                JOIN dim_permit_state s ON s.state_key = f.state_key
                WHERE s.name = 'Active' AND f.month_key BETWEEN @from AND @to
                GROUP BY f.month_key
            ),
            expired AS (
                SELECT d.month_key, COUNT(*) AS n
                FROM fact_permit_transition f
                JOIN dim_permit_state s ON s.state_key = f.to_state_key
                JOIN dim_date d ON d.date_key = f.date_key
                WHERE s.name = 'Expired' AND d.month_key BETWEEN @from AND @to
                GROUP BY d.month_key
            )
            SELECT COALESCE(a.month_key, e.month_key) AS month_key,
                   COALESCE(a.n, 0) AS active_permits,
                   COALESCE(e.n, 0) AS expired_permits
            FROM active a
            FULL OUTER JOIN expired e ON e.month_key = a.month_key
            ORDER BY month_key
            """;
        cmd.Parameters.AddWithValue("@from", fromMonth);
        cmd.Parameters.AddWithValue("@to", toMonth);

        var rows = new List<string[]>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            int monthKey = reader.GetInt32(0);
            rows.Add([
                $"{monthKey / 100:D4}-{monthKey % 100:D2}",
                reader.GetInt32(1).ToString(),
                reader.GetInt32(2).ToString(),
            ]);
        }
        Write(["month", "active_permits", "expired_permits"],
            rightAlign: [false, true, true], rows, csv);
    }

    // Pure edge counts: approvals are Pending -> Active, renewals are any
    // renew back to Active (Active -> Active and the Expired -> Active grace
    // renewal), suspensions are Active -> Suspended.
    private static async Task ApprovalsRenewalsSuspensionsAsync(
            string password, int fromMonth, int toMonth, bool csv)
    {
        using var conn = new SqlConnection(DbInit.GetConnStr(DbInit.OlapDbName, password));
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.month_key,
                   SUM(CASE WHEN fs.name = 'Pending' AND ts.name = 'Active'
                       THEN 1 ELSE 0 END) AS approvals,
                   SUM(CASE WHEN fs.name IN ('Active', 'Expired') AND ts.name = 'Active'
                       THEN 1 ELSE 0 END) AS renewals,
                   SUM(CASE WHEN fs.name = 'Active' AND ts.name = 'Suspended'
                       THEN 1 ELSE 0 END) AS suspensions
            FROM fact_permit_transition f
            JOIN dim_permit_state fs ON fs.state_key = f.from_state_key
            JOIN dim_permit_state ts ON ts.state_key = f.to_state_key
            JOIN dim_date d ON d.date_key = f.date_key
            WHERE d.month_key BETWEEN @from AND @to
            GROUP BY d.month_key
            ORDER BY d.month_key
            """;
        cmd.Parameters.AddWithValue("@from", fromMonth);
        cmd.Parameters.AddWithValue("@to", toMonth);

        var rows = new List<string[]>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            int monthKey = reader.GetInt32(0);
            rows.Add([
                $"{monthKey / 100:D4}-{monthKey % 100:D2}",
                reader.GetInt32(1).ToString(),
                reader.GetInt32(2).ToString(),
                reader.GetInt32(3).ToString(),
            ]);
        }
        Write(["month", "approvals", "renewals", "suspensions"],
            rightAlign: [false, true, true, true], rows, csv);
    }

    private static void Write(string[] headers, bool[] rightAlign, List<string[]> rows, bool csv)
    {
        if (csv)
        {
            Console.WriteLine(string.Join(',', headers));
            foreach (var row in rows)
            {
                Console.WriteLine(string.Join(',', row));
            }
            return;
        }
        WriteTable(headers, rightAlign, rows);
    }

    private static void WriteTable(string[] headers, bool[] rightAlign, List<string[]> rows)
    {
        var widths = headers.Select(h => h.Length).ToArray();
        foreach (var row in rows)
        {
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        string Line(string left, string mid, string right) =>
            left + string.Join(mid, widths.Select(w => new string('─', w + 2))) + right;
        string Row(string[] cells) => "│ " + string.Join(" │ ", cells.Select((c, i) =>
            rightAlign[i] ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))) + " │";

        Console.WriteLine(Line("┌", "┬", "┐"));
        Console.WriteLine(Row(headers));
        Console.WriteLine(Line("├", "┼", "┤"));
        foreach (var row in rows)
        {
            Console.WriteLine(Row(row));
        }
        Console.WriteLine(Line("└", "┴", "┘"));
        Console.WriteLine($"{rows.Count} row{(rows.Count == 1 ? "" : "s")}");
    }

    private static int ParseMonthKey(string month)
    {
        var parsed = DateTime.ParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture);
        return parsed.Year * 100 + parsed.Month;
    }
}
