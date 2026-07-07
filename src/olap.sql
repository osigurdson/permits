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

-- Populated by the ETL from the distinct types seen in the OLTP data.
CREATE TABLE dim_permit_type (
    permit_type_key INT IDENTITY(1,1) PRIMARY KEY,
    name NVARCHAR(100) NOT NULL UNIQUE
);
GO

-- Grain: one row per permit state transition
CREATE TABLE fact_permit_transition (
    permit_activity_id INT PRIMARY KEY,
    permit_id INT NOT NULL,
    permit_type_key INT NOT NULL
        CONSTRAINT fk_fact_transition_type REFERENCES dim_permit_type (permit_type_key),
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

-- Periodic snapshot. Grain: one row per permit per completed month, holding
-- the permit's state as of month end. Only non-terminal states are kept
-- (Rejected/Completed/ExpiredTerminal permits would otherwise accumulate a
-- row every month forever), so "active permits in month M" is a plain count.
CREATE TABLE fact_permit_state (
    month_key INT NOT NULL,
    permit_id INT NOT NULL,
    permit_type_key INT NOT NULL
        CONSTRAINT fk_fact_state_type REFERENCES dim_permit_type (permit_type_key),
    state_key INT NOT NULL
        CONSTRAINT fk_fact_state_state REFERENCES dim_permit_state (state_key),
    CONSTRAINT pk_fact_permit_state PRIMARY KEY (month_key, permit_id)
);
GO

-- Grain: one row per OLTP permit_payment row. Payments are mutable in OLTP
-- (status changes when a pending payment settles or fails), so the ETL
-- inserts new payments by id watermark and then refreshes changed statuses.
-- date_key is the payment date; OLTP does not record when a status changed.
CREATE TABLE fact_payment (
    payment_id INT PRIMARY KEY,
    permit_id INT NOT NULL,
    permit_type_key INT NOT NULL
        CONSTRAINT fk_fact_payment_type REFERENCES dim_permit_type (permit_type_key),
    date_key INT NOT NULL
        CONSTRAINT fk_fact_payment_date REFERENCES dim_date (date_key),
    status NVARCHAR(20) NOT NULL,
    amount DECIMAL(19, 4) NOT NULL
);
GO

-- States are fixed by the permit state machine (PermitStateMachine.cs).
INSERT INTO dim_permit_state (name) VALUES
    ('Initial'), ('Pending'), ('Rejected'), ('Active'),
    ('Completed'), ('Expired'), ('ExpiredTerminal'), ('Suspended');
GO
