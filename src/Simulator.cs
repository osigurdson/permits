namespace Permits;

// Codes stored in the OLTP tables. The DB stores the int; these are the source of truth.
enum ActivityType
{
    Submitted = 1,
    Approved = 2,
    Rejected = 3,
    Renewed = 4,
    Suspended = 5,
    Reinstated = 6,
    Expired = 7,
    ExpiredTerminal = 8,
    Completed = 9,
}

enum PermitRole
{
    Applicant = 1,
    Owner = 2,
    Agent = 3,
}

static class PaymentStatus
{
    public const string Pending = "PENDING";
    public const string Settled = "SETTLED";
    public const string Failed = "FAILED";
    public const string Refunded = "REFUNDED";
}

// One domain event per Next(); a single event may expand to several SQL statements.
abstract record SimEvent(DateTime Time);

record PersonRegistered(DateTime Time, int PersonId, string Name) : SimEvent(Time);
record PermitApplied(DateTime Time, int PermitId, int PersonId, string PermitType) : SimEvent(Time);
record PermitApproved(DateTime Time, int PermitId, DateTime IssueDate, DateTime ExpiryDate) : SimEvent(Time);
record PermitRejected(DateTime Time, int PermitId) : SimEvent(Time);
record PermitRenewed(DateTime Time, int PermitId, DateTime NewExpiryDate) : SimEvent(Time);
record PermitSuspended(DateTime Time, int PermitId) : SimEvent(Time);
record PermitReinstated(DateTime Time, int PermitId) : SimEvent(Time);
record PermitExpired(DateTime Time, int PermitId) : SimEvent(Time);
record PermitExpiredTerminal(DateTime Time, int PermitId) : SimEvent(Time);
record PermitCompleted(DateTime Time, int PermitId) : SimEvent(Time);
record PaymentMade(DateTime Time, int PaymentId, int PermitId, decimal Amount, string Status) : SimEvent(Time);
record PaymentSettled(DateTime Time, int PaymentId) : SimEvent(Time);
record PaymentFailed(DateTime Time, int PaymentId) : SimEvent(Time);

/// <summary>
/// Deterministic, in-memory simulation of the permit domain. No I/O: same seed and
/// epoch always yield the same event stream. Ids are assigned sequentially from 1,
/// matching IDENTITY(1,1) against a freshly created database.
///
/// Every permit is backed by a PermitStateMachine; the simulator never sets state
/// directly. Random rolls only pick a trigger, and the machine decides whether it
/// is legal, so the event stream cannot contain an invalid transition.
/// </summary>
class PermitSimulator
{
    private static readonly string[] PermitTypes =
        ["Building-001", "Electrical-004", "Plumbing-002", "Demolition-007", "Signage-003"];

    private static readonly TimeSpan ValidityPeriod = TimeSpan.FromDays(365);

    private readonly Random m_rng;
    private DateTime m_clock;

    private int m_nextPersonId = 1;
    private int m_nextPermitId = 1;
    private int m_nextPaymentId = 1;

    private sealed class SimPermit
    {
        public required int PermitId;
        public required PermitStateMachine Machine;
        public DateTime? InspectedAt; // final inspection date, drawn at approval
    }

    private sealed class PaymentState
    {
        public required int PaymentId;
        public required string Status;
    }

    private readonly List<int> m_persons = [];
    private readonly List<SimPermit> m_permits = [];
    private readonly List<PaymentState> m_pendingPayments = [];

    public PermitSimulator(int seed, DateTime epoch)
    {
        m_rng = new Random(seed);
        m_clock = epoch;
    }

    public IEnumerable<SimEvent> Events()
    {
        while (true)
        {
            yield return Next();
        }
    }

    public SimEvent Next()
    {
        m_clock = m_clock.AddMinutes(m_rng.Next(30, 60 * 36));

        // Clock-driven lifecycle first, in priority order. Events are stamped at
        // detection time, not backdated, so activity times stay monotonic per
        // permit and replaying the activity log always reproduces current state.

        // 1. Clock transitions owned by the state machine: Active -> Expired once
        //    validity lapses, Expired -> ExpiredTerminal once the grace period ends.
        //    One event per Next(); remaining permits are picked up on later ticks.
        foreach (var permit in m_permits)
        {
            var previous = permit.Machine.Current;
            permit.Machine.Update(m_clock);
            if (permit.Machine.Current == previous)
            {
                continue;
            }
            // Steps are <= 36h and expiry is checked every step, while the grace
            // period is 30 days, so a permit is always seen Expired before terminal.
            return permit.Machine.Current switch
            {
                PermitState.Expired => new PermitExpired(m_clock, permit.PermitId),
                PermitState.ExpiredTerminal => new PermitExpiredTerminal(m_clock, permit.PermitId),
                _ => throw new InvalidOperationException(
                    $"Unexpected clock transition {previous} -> {permit.Machine.Current}."),
            };
        }

        // 2. Final inspection due: the passed inspection completes the permit.
        var inspect = m_permits.FirstOrDefault(p =>
            p.Machine.Current == PermitState.Active && p.InspectedAt < m_clock);
        if (inspect != null)
        {
            inspect.Machine.Update(m_clock, Trigger.Inspected);
            return new PermitCompleted(m_clock, inspect.PermitId);
        }

        // Nothing exists yet: the only valid move is registering a person.
        if (m_persons.Count == 0)
        {
            return RegisterPerson();
        }

        var roll = m_rng.Next(100);
        return roll switch
        {
            < 10 => RegisterPerson(),
            < 40 => Apply(),
            < 55 => SettleOrFailPayment() ?? Apply(),
            _ => TransitionPermit() ?? Apply(),
        };
    }

    private PersonRegistered RegisterPerson()
    {
        var id = m_nextPersonId++;
        m_persons.Add(id);
        return new PersonRegistered(m_clock, id, $"Person {id:D4}");
    }

    private SimEvent Apply()
    {
        var personId = m_persons[m_rng.Next(m_persons.Count)];
        var type = PermitTypes[m_rng.Next(PermitTypes.Length)];
        var permitId = m_nextPermitId++;
        var machine = new PermitStateMachine(ValidityPeriod);
        machine.Update(m_clock, Trigger.Apply);
        m_permits.Add(new SimPermit { PermitId = permitId, Machine = machine });
        return new PermitApplied(m_clock, permitId, personId, type);
    }

    private SimEvent? SettleOrFailPayment()
    {
        if (m_pendingPayments.Count == 0)
        {
            return null;
        }
        var payment = m_pendingPayments[m_rng.Next(m_pendingPayments.Count)];
        m_pendingPayments.Remove(payment);
        if (m_rng.Next(100) < 85)
        {
            payment.Status = PaymentStatus.Settled;
            return new PaymentSettled(m_clock, payment.PaymentId);
        }
        payment.Status = PaymentStatus.Failed;
        return new PaymentFailed(m_clock, payment.PaymentId);
    }

    private SimEvent? TransitionPermit()
    {
        var candidates = m_permits.Where(p =>
            p.Machine.Current is PermitState.Pending or PermitState.Active
                or PermitState.Suspended or PermitState.Expired).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        var permit = candidates[m_rng.Next(candidates.Count)];
        var machine = permit.Machine;
        var roll = m_rng.Next(100);

        switch (machine.Current)
        {
            case PermitState.Pending:
                if (roll < 15)
                {
                    machine.Update(m_clock, Trigger.Reject);
                    return new PermitRejected(m_clock, permit.PermitId);
                }
                if (roll < 65)
                {
                    machine.Update(m_clock, Trigger.Approve);
                    // Works + final inspection take 2-18 months. Most complete inside
                    // the 1-year validity; the rest expire unless renewed in time.
                    permit.InspectedAt = m_clock.AddDays(m_rng.Next(60, 540));
                    return new PermitApproved(m_clock, permit.PermitId, m_clock,
                        machine.Expiration.DateTime);
                }
                // Application fee: most settle immediately, some stay pending.
                return MakePayment(permit.PermitId);

            case PermitState.Active:
                // Completion and expiry are clock-driven (see Next()); here the
                // permit holder acts: renew, or the agency suspends.
                if (roll < 30)
                {
                    machine.Update(m_clock, Trigger.Renew);
                    return new PermitRenewed(m_clock, permit.PermitId, machine.Expiration.DateTime);
                }
                if (roll < 42)
                {
                    machine.Update(m_clock, Trigger.Suspend);
                    return new PermitSuspended(m_clock, permit.PermitId);
                }
                return MakePayment(permit.PermitId); // renewal / inspection fee

            case PermitState.Suspended:
                if (roll < 60)
                {
                    machine.Update(m_clock, Trigger.Reinstate);
                    return new PermitReinstated(m_clock, permit.PermitId);
                }
                return MakePayment(permit.PermitId); // fine paid while suspended

            case PermitState.Expired:
                // Within the 30-day grace window the holder may renew back to
                // Active; otherwise the clock eventually makes expiry terminal.
                if (roll < 40)
                {
                    machine.Update(m_clock, Trigger.Renew);
                    return new PermitRenewed(m_clock, permit.PermitId, machine.Expiration.DateTime);
                }
                return null;

            default:
                return null;
        }
    }

    private PaymentMade MakePayment(int permitId)
    {
        var amount = m_rng.Next(1, 40) * 25m; // 25.00 .. 975.00
        var paymentId = m_nextPaymentId++;
        var settled = m_rng.Next(100) < 70;
        var status = settled ? PaymentStatus.Settled : PaymentStatus.Pending;
        if (!settled)
        {
            m_pendingPayments.Add(new PaymentState { PaymentId = paymentId, Status = status });
        }
        return new PaymentMade(m_clock, paymentId, permitId, amount, status);
    }
}
