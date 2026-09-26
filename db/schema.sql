-- Relay — take-home seed schema
-- Portable SQL. Adapt types to your database's migration tooling as needed
-- (e.g., TIMESTAMP -> DATETIME2 on SQL Server, SERIAL/IDENTITY as appropriate).
-- All occurred_at values are stored in UTC.

CREATE TABLE accounts (
    id          INTEGER      NOT NULL PRIMARY KEY,
    name        VARCHAR(120) NOT NULL,
    industry    VARCHAR(60)  NOT NULL,
    timezone    VARCHAR(60)  NOT NULL,  -- IANA timezone of the account, e.g. 'America/Chicago'
    created_at  TIMESTAMP    NOT NULL   -- UTC
);

CREATE TABLE activity_events (
    id                INTEGER      NOT NULL PRIMARY KEY,
    account_id        INTEGER      NOT NULL REFERENCES accounts(id),
    location          VARCHAR(80)  NOT NULL,  -- the account's site/branch where the activity occurred
    event_type        VARCHAR(40)  NOT NULL,  -- 'call_received' | 'lead_created' | 'appointment_set'
    occurred_at       TIMESTAMP    NOT NULL,  -- UTC
    duration_seconds  INTEGER      NULL,      -- only meaningful for calls; may be NULL
    outcome           VARCHAR(40)  NULL       -- e.g. 'connected' | 'missed' | 'voicemail' | 'converted' | 'no_show'; may be NULL
);
