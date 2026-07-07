CREATE TABLE dim_date (
    date_key INT PRIMARY KEY,       -- yyyymmdd
    [date] DATE NOT NULL UNIQUE,
    month_key INT NOT NULL          -- yyyymm; the reports' bucketing grain
);
GO

CREATE TABLE dim_permit_state (
    state_key INT IDENTITY(1,1) PRIMARY KEY,
    name NVARCHAR(20) NOT NULL UNIQUE
);
GO

-- Grain: one row per permit state transition
CREATE TABLE fact_permit_transition (
    permit_activity_id INT PRIMARY KEY,
    permit_id INT NOT NULL,
    permit_type NVARCHAR(100) NOT NULL,
    from_state_key INT NOT NULL
        CONSTRAINT fk_fact_transition_from REFERENCES dim_permit_state (state_key),
    to_state_key INT NOT NULL
        CONSTRAINT fk_fact_transition_to REFERENCES dim_permit_state (state_key),
    date_key INT NOT NULL
        CONSTRAINT fk_fact_transition_date REFERENCES dim_date (date_key),
    transition_time DATETIME2 NOT NULL,
    from_state_entered_time DATETIME2 NULL  -- NULL on the apply row
);
GO

-- States are fixed by the permit state machine (PermitStateMachine.cs).
INSERT INTO dim_permit_state (name) VALUES
    ('Initial'), ('Pending'), ('Rejected'), ('Active'),
    ('Completed'), ('Expired'), ('ExpiredTerminal'), ('Suspended');
GO
