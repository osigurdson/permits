CREATE TABLE permit (
    permit_id INT IDENTITY(1,1) PRIMARY KEY,
    issue_date DATETIME2 NULL,          -- NULL until issued
    expiry_date DATETIME2 NULL,         -- NULL until issued
    status NVARCHAR(20) NOT NULL
        CONSTRAINT ck_permit_status CHECK (status IN
            ('PENDING', 'ACTIVE', 'REJECTED', 'SUSPENDED', 'REVOKED', 'EXPIRED', 'COMPLETED')),
    permit_type NVARCHAR(100) NOT NULL
);
GO

CREATE TABLE permit_payment (
    payment_id INT IDENTITY(1,1) PRIMARY KEY,
    permit_id INT NOT NULL,
    amount DECIMAL(19, 4) NOT NULL,
    payment_date DATETIME2 NOT NULL,
    status NVARCHAR(20) NOT NULL
        CONSTRAINT ck_permit_payment_status CHECK (status IN
            ('PENDING', 'SETTLED', 'FAILED', 'REFUNDED')),

    CONSTRAINT fk_permit_payment_permit FOREIGN KEY (permit_id)
        REFERENCES permit(permit_id)
);
GO

CREATE TABLE permit_activity (
    activity_id INT IDENTITY(1,1) PRIMARY KEY,
    permit_id INT NOT NULL,
    activity_type_code INT NOT NULL,    -- see ActivityType enum in Simulator.cs
    activity_time DATETIME2 NOT NULL,

    CONSTRAINT fk_permit_activity_permit FOREIGN KEY (permit_id)
        REFERENCES permit(permit_id)
);
GO

CREATE TABLE person (
    person_id INT IDENTITY(1,1) PRIMARY KEY,
    name NVARCHAR(100) NOT NULL
);
GO

CREATE TABLE permit_person (
    permit_id INT NOT NULL,
    person_id INT NOT NULL,
    role INT NOT NULL,                  -- see PermitRole enum in Simulator.cs

    CONSTRAINT pk_permit_person PRIMARY KEY (permit_id, person_id),
    CONSTRAINT fk_permit_person_permit FOREIGN KEY (permit_id)
        REFERENCES permit(permit_id),
    CONSTRAINT fk_permit_person_person FOREIGN KEY (person_id)
        REFERENCES person(person_id)
);
GO

-- Single-row simulator bookkeeping: the simulator is deterministic, so
-- (seed, epoch, event_count) is enough to replay and continue the stream.
CREATE TABLE sim_state (
    id INT NOT NULL CONSTRAINT pk_sim_state PRIMARY KEY
        CONSTRAINT ck_sim_state_singleton CHECK (id = 1),
    seed INT NOT NULL,
    epoch DATETIME2 NOT NULL,
    event_count INT NOT NULL
);
GO

CREATE INDEX ix_permit_payment_permit_id ON permit_payment(permit_id);
CREATE INDEX ix_permit_activity_permit_id_time ON permit_activity(permit_id, activity_time);
CREATE INDEX ix_permit_person_person_id ON permit_person(person_id);
GO
