namespace Permits;

enum PermitState
{
    Initial,
    Pending,
    Rejected,
    Active,
    Completed,
    Expired,
    ExpiredTerminal,
    Suspended
}

enum Trigger
{
    Apply,
    Reject,
    Approve,
    Inspected,
    Renew,
    Suspend,
    Reinstate
}

sealed class PermitStateMachine
{
    private readonly TimeSpan m_validityPeriod;
    private readonly TimeSpan GracePeriod = TimeSpan.FromDays(30);
    private DateTimeOffset m_expiration;

    public PermitState Current { get; private set; }

    public DateTimeOffset Expiration => m_expiration;

    private static readonly Dictionary<(PermitState, Trigger), PermitState> s_transitions = new()
    {
        [(PermitState.Initial, Trigger.Apply)] = PermitState.Pending,

        [(PermitState.Pending, Trigger.Reject)] = PermitState.Rejected,
        [(PermitState.Pending, Trigger.Approve)] = PermitState.Active,

        [(PermitState.Active, Trigger.Inspected)] = PermitState.Completed,
        [(PermitState.Active, Trigger.Suspend)] = PermitState.Suspended,
        [(PermitState.Active, Trigger.Renew)] = PermitState.Active,

        [(PermitState.Suspended, Trigger.Reinstate)] = PermitState.Active,
        [(PermitState.Expired, Trigger.Renew)] = PermitState.Active,
    };

    public void Update(DateTimeOffset now, Trigger trigger)
    {
        bool success = TryUpdate(now, trigger);
        if (!success)
        {
            throw new InvalidOperationException(
                $"Trigger '{trigger}' not valid for state '{Current}.");
        }
    }
    public bool TryUpdate(DateTimeOffset now, Trigger trigger)
    {
        Update(now);
        bool success = s_transitions.TryGetValue((Current, trigger), out var next);
        if (success)
        {
            Current = next;
            if (trigger == Trigger.Approve || trigger == Trigger.Renew)
            {
                m_expiration = now + m_validityPeriod;
            }
        }
        return success;
    }

    public void Update(DateTimeOffset now)
    {
        // At most one edge per call so every transition is observable by callers;
        // an Active permit past its grace period still passes through Expired.
        if (Current == PermitState.Active && now > m_expiration)
        {
            Current = PermitState.Expired;
        }
        else if (Current == PermitState.Expired && now > m_expiration + GracePeriod)
        {
            Current = PermitState.ExpiredTerminal;
        }
    }

    public PermitStateMachine(TimeSpan validityPeriod)
    {
        m_validityPeriod = validityPeriod;
    }
}
