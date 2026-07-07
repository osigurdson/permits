using Microsoft.Data.SqlClient;

namespace Permits;

static class PermitStatus
{
    public const string Pending = "PENDING";
    public const string Active = "ACTIVE";
    public const string Rejected = "REJECTED";
    public const string Suspended = "SUSPENDED";
    public const string Expired = "EXPIRED";
    public const string Completed = "COMPLETED";
}

/// <summary>
/// Applies simulator events to Permits_OLTP. The simulator assigns ids
/// sequentially from 1, matching IDENTITY(1,1) against a database created by
/// `oltp init`, so inserts rely on the database generating the same ids.
/// Continuation replays the already-applied prefix of the deterministic event
/// stream (recorded in sim_state) without touching the database, then applies
/// the next batch.
/// </summary>
static class SimRunner
{
    public static async Task InitAsync(string password, int seed, DateTime epoch, int eventCount)
    {
        using var conn = new SqlConnection(DbInit.GetConnStr(DbInit.OltpDbName, password));
        await conn.OpenAsync();

        var existing = await QuerySimStateAsync(conn);
        if (existing != null)
        {
            throw new InvalidOperationException(
                "Simulation already initialized; run 'oltp sim --event-count N' to continue " +
                "or 'oltp init' to recreate the database.");
        }

        await ExecAsync(conn, null,
            "INSERT INTO sim_state (id, seed, epoch, event_count) VALUES (1, @seed, @epoch, 0)",
            ("@seed", seed), ("@epoch", epoch));

        var sim = new PermitSimulator(seed, epoch);
        var last = await ApplyAsync(conn, sim, eventCount);
        await ExecAsync(conn, null, "UPDATE sim_state SET event_count = @n",
            ("@n", eventCount));
        Console.WriteLine(
            $"Applied {eventCount} events (seed {seed}, epoch {epoch:yyyy-MM-dd}), " +
            $"simulated through {last:yyyy-MM-dd HH:mm}.");
    }

    public static async Task RunAsync(string password, int eventCount)
    {
        using var conn = new SqlConnection(DbInit.GetConnStr(DbInit.OltpDbName, password));
        await conn.OpenAsync();

        var (seed, epoch, applied) = await QuerySimStateAsync(conn)
            ?? throw new InvalidOperationException(
                "Simulation not initialized; run 'oltp sim init' first.");

        var sim = new PermitSimulator(seed, epoch);
        for (int i = 0; i < applied; i++)
        {
            sim.Next(); // replay the prefix already in the database
        }

        var last = await ApplyAsync(conn, sim, eventCount);
        await ExecAsync(conn, null, "UPDATE sim_state SET event_count = @n",
            ("@n", applied + eventCount));
        Console.WriteLine(
            $"Applied {eventCount} events ({applied + eventCount} total), " +
            $"simulated through {last:yyyy-MM-dd HH:mm}.");
    }

    private static async Task<DateTime> ApplyAsync(
            SqlConnection conn,
            PermitSimulator sim,
            int eventCount)
    {
        var last = default(DateTime);
        for (int i = 0; i < eventCount; i++)
        {
            var e = sim.Next();
            using var tx = conn.BeginTransaction();
            await ApplyEventAsync(conn, tx, e);
            await tx.CommitAsync();
            last = e.Time;
        }
        return last;
    }

    private static Task ApplyEventAsync(SqlConnection conn, SqlTransaction tx, SimEvent e) => e switch
    {
        PersonRegistered p => ExecAsync(conn, tx,
            "INSERT INTO person (name) VALUES (@name)",
            ("@name", p.Name)),

        PermitApplied a => ExecAsync(conn, tx,
            """
            INSERT INTO permit (status, permit_type) VALUES (@status, @type);
            IF SCOPE_IDENTITY() <> @pid THROW 50000, 'Simulator/database permit id mismatch.', 1;
            INSERT INTO permit_person (permit_id, person_id, role) VALUES (@pid, @person, @role);
            INSERT INTO permit_activity (permit_id, activity_type_code, activity_time)
                VALUES (@pid, @act, @t);
            """,
            ("@status", PermitStatus.Pending), ("@type", a.PermitType),
            ("@pid", a.PermitId), ("@person", a.PersonId), ("@role", (int)PermitRole.Applicant),
            ("@act", (int)ActivityType.Submitted), ("@t", a.Time)),

        PermitApproved a => TransitionAsync(conn, tx, a.PermitId, PermitStatus.Active,
            ActivityType.Approved, a.Time, a.IssueDate, a.ExpiryDate),

        PermitRejected r => TransitionAsync(conn, tx, r.PermitId, PermitStatus.Rejected,
            ActivityType.Rejected, r.Time),

        // Also covers Expired -> Active: renewing restores the ACTIVE status.
        PermitRenewed r => TransitionAsync(conn, tx, r.PermitId, PermitStatus.Active,
            ActivityType.Renewed, r.Time, expiryDate: r.NewExpiryDate),

        PermitSuspended s => TransitionAsync(conn, tx, s.PermitId, PermitStatus.Suspended,
            ActivityType.Suspended, s.Time),

        PermitReinstated r => TransitionAsync(conn, tx, r.PermitId, PermitStatus.Active,
            ActivityType.Reinstated, r.Time),

        PermitExpired x => TransitionAsync(conn, tx, x.PermitId, PermitStatus.Expired,
            ActivityType.Expired, x.Time),

        // The status column stays EXPIRED; terminal expiry is distinguished by
        // the activity code (analytics treat ExpiredTerminal as an event).
        PermitExpiredTerminal x => TransitionAsync(conn, tx, x.PermitId, PermitStatus.Expired,
            ActivityType.ExpiredTerminal, x.Time),

        PermitCompleted c => TransitionAsync(conn, tx, c.PermitId, PermitStatus.Completed,
            ActivityType.Completed, c.Time),

        PaymentMade p => ExecAsync(conn, tx,
            """
            INSERT INTO permit_payment (permit_id, amount, payment_date, status)
                VALUES (@pid, @amount, @t, @status)
            """,
            ("@pid", p.PermitId), ("@amount", p.Amount), ("@t", p.Time), ("@status", p.Status)),

        PaymentSettled p => ExecAsync(conn, tx,
            "UPDATE permit_payment SET status = @status WHERE payment_id = @id",
            ("@status", PaymentStatus.Settled), ("@id", p.PaymentId)),

        PaymentFailed p => ExecAsync(conn, tx,
            "UPDATE permit_payment SET status = @status WHERE payment_id = @id",
            ("@status", PaymentStatus.Failed), ("@id", p.PaymentId)),

        _ => throw new InvalidOperationException($"Unhandled event type '{e.GetType().Name}'."),
    };

    private static Task TransitionAsync(
            SqlConnection conn,
            SqlTransaction tx,
            int permitId,
            string status,
            ActivityType activity,
            DateTime time,
            DateTime? issueDate = null,
            DateTime? expiryDate = null)
    {
        return ExecAsync(conn, tx,
            """
            UPDATE permit SET status = @status,
                issue_date = COALESCE(@issue, issue_date),
                expiry_date = COALESCE(@expiry, expiry_date)
                WHERE permit_id = @pid;
            INSERT INTO permit_activity (permit_id, activity_type_code, activity_time)
                VALUES (@pid, @act, @t);
            """,
            ("@status", status), ("@issue", (object?)issueDate ?? DBNull.Value),
            ("@expiry", (object?)expiryDate ?? DBNull.Value),
            ("@pid", permitId), ("@act", (int)activity), ("@t", time));
    }

    private static async Task<(int Seed, DateTime Epoch, int EventCount)?> QuerySimStateAsync(
            SqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seed, epoch, event_count FROM sim_state WHERE id = 1";
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }
        return (reader.GetInt32(0), reader.GetDateTime(1), reader.GetInt32(2));
    }

    private static async Task ExecAsync(
            SqlConnection conn,
            SqlTransaction? tx,
            string sql,
            params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync();
    }
}
