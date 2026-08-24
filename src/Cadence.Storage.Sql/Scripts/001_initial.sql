/*
    Cadence schema, revision 001.

    Every statement is guarded, so applying this to a database that already has some of it is safe.
    The migrator journals by script name and will not re-run this, but a DBA who applied it by hand
    and then turned AutoMigrate on should not get an error either.

    All instants are DATETIME2(3) holding UTC. DATETIMEOFFSET would preserve an offset Cadence never
    uses -- every time it writes comes from ISystemClock.UtcNow -- and would make the exact-instant
    comparison that UX_CadenceJobRun_Occurrence depends on answer to the stored offset rather than
    to the instant. Occurrences land on whole seconds, so millisecond precision is ample.

    The {schema} token is replaced by the migrator with SqlStorageOptions.SchemaName, which is
    validated as a plain identifier before it is ever substituted.
*/

IF SCHEMA_ID(N'{schema}') IS NULL
    EXEC(N'CREATE SCHEMA [{schema}]');
GO

-- The migrator's own journal. Created first, because the migrator writes to it.
IF OBJECT_ID(N'[{schema}].[CadenceSchemaVersion]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadenceSchemaVersion]
    (
        ScriptName   NVARCHAR(200) NOT NULL,
        AppliedAtUtc DATETIME2(3)  NOT NULL,
        CONSTRAINT PK_CadenceSchemaVersion PRIMARY KEY (ScriptName)
    );
END
GO

/*
    Run history, and -- for a scheduled occurrence -- the claim itself. One row per occurrence is
    enforced by the filtered unique index below, which is the whole of Cadence's clustering
    guarantee. There is no lock primitive anywhere.

    Clustered on Seq rather than on RunId: a random GUID as the clustered key fragments the index on
    every insert, and inserts are the hot path here. RunId stays the logical primary key.
*/
IF OBJECT_ID(N'[{schema}].[CadenceJobRun]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadenceJobRun]
    (
        Seq             BIGINT           NOT NULL IDENTITY(1, 1),
        RunId           UNIQUEIDENTIFIER NOT NULL,
        JobName         NVARCHAR(200)    NOT NULL,
        ScheduledForUtc DATETIME2(3)     NULL,
        [Trigger]       TINYINT          NOT NULL,   -- bracketed: TRIGGER is a reserved keyword
        Status          TINYINT          NOT NULL,
        InstanceId      NVARCHAR(200)    NOT NULL,
        StartedAtUtc    DATETIME2(3)     NOT NULL,
        CompletedAtUtc  DATETIME2(3)     NULL,
        DurationMs      BIGINT           NULL,
        Error           NVARCHAR(MAX)    NULL,
        CONSTRAINT PK_CadenceJobRun PRIMARY KEY NONCLUSTERED (RunId)
    );

    CREATE CLUSTERED INDEX IX_CadenceJobRun_Seq ON [{schema}].[CadenceJobRun] (Seq);
END
GO

/*
    The claim. An INSERT that violates this index means another instance already started this slot;
    error 2601/2627 is the only signal Cadence treats as "someone else won", and every other error
    propagates -- swallowing a dead connection here would silently skip a run.

    Filtered so API and manual runs, which belong to no occurrence, are exempt.
*/
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_CadenceJobRun_Occurrence'
                 AND object_id = OBJECT_ID(N'[{schema}].[CadenceJobRun]'))
BEGIN
    CREATE UNIQUE INDEX UX_CadenceJobRun_Occurrence
        ON [{schema}].[CadenceJobRun] (JobName, ScheduledForUtc)
        WHERE ScheduledForUtc IS NOT NULL;
END
GO

-- Serves GetLastRun, GetLastSuccess, CountConsecutiveFailures, the dashboard's per-job query and
-- the janitor's per-job trim. Status is included so the common reads never touch the base table.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_CadenceJobRun_Job_Started'
                 AND object_id = OBJECT_ID(N'[{schema}].[CadenceJobRun]'))
BEGIN
    CREATE INDEX IX_CadenceJobRun_Job_Started
        ON [{schema}].[CadenceJobRun] (JobName, StartedAtUtc DESC)
        INCLUDE (Status);
END
GO

-- Serves the janitor's age purge, which sweeps across all jobs.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_CadenceJobRun_Started'
                 AND object_id = OBJECT_ID(N'[{schema}].[CadenceJobRun]'))
BEGIN
    CREATE INDEX IX_CadenceJobRun_Started
        ON [{schema}].[CadenceJobRun] (StartedAtUtc)
        INCLUDE (Status);
END
GO

-- Progress reported by a running job. Cascades, so purging a run takes its log with it and the
-- janitor never has to delete from two tables in the right order.
IF OBJECT_ID(N'[{schema}].[CadenceJobRunLog]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadenceJobRunLog]
    (
        Seq          BIGINT           NOT NULL IDENTITY(1, 1),
        RunId        UNIQUEIDENTIFIER NOT NULL,
        TimestampUtc DATETIME2(3)     NOT NULL,
        Message      NVARCHAR(2000)   NOT NULL,
        DataJson     NVARCHAR(MAX)    NULL,
        CONSTRAINT PK_CadenceJobRunLog PRIMARY KEY CLUSTERED (Seq),
        CONSTRAINT FK_CadenceJobRunLog_Run FOREIGN KEY (RunId)
            REFERENCES [{schema}].[CadenceJobRun] (RunId) ON DELETE CASCADE
    );

    CREATE INDEX IX_CadenceJobRunLog_Run ON [{schema}].[CadenceJobRunLog] (RunId, Seq);
END
GO

-- The editable schedule. This table is the product: rows here override what the code declared.
IF OBJECT_ID(N'[{schema}].[CadenceJobSchedule]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadenceJobSchedule]
    (
        JobName        NVARCHAR(200) NOT NULL,
        CronExpression NVARCHAR(200) NOT NULL,
        TimeZoneId     NVARCHAR(100) NOT NULL,
        Enabled        BIT           NOT NULL,
        Overlap        TINYINT       NULL,
        MaxDurationMs  BIGINT        NULL,
        SettingsJson   NVARCHAR(MAX) NULL,
        Version        INT           NOT NULL CONSTRAINT DF_CadenceJobSchedule_Version DEFAULT (1),
        UpdatedAtUtc   DATETIME2(3)  NOT NULL,
        UpdatedBy      NVARCHAR(200) NULL,
        CONSTRAINT PK_CadenceJobSchedule PRIMARY KEY (JobName)
    );
END
GO

/*
    One row, bumped in the same transaction as any schedule write. The change token polls this
    instead of re-reading every schedule, so detecting "nothing changed" costs one single-row read
    per instance per poll interval rather than a full scan.
*/
IF OBJECT_ID(N'[{schema}].[CadenceScheduleVersion]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadenceScheduleVersion]
    (
        Id      TINYINT NOT NULL,
        Version BIGINT  NOT NULL,
        CONSTRAINT PK_CadenceScheduleVersion PRIMARY KEY (Id),
        CONSTRAINT CK_CadenceScheduleVersion_Singleton CHECK (Id = 1)
    );

    INSERT INTO [{schema}].[CadenceScheduleVersion] (Id, Version) VALUES (1, 1);
END
GO

/*
    Who is alive. The janitor uses LastHeartbeatUtc to decide whose Running rows have been
    abandoned -- a process that was killed never got to record an outcome, so those rows would
    otherwise claim a run is still in progress forever. That is what RunStatus.Lost is for.
*/
IF OBJECT_ID(N'[{schema}].[CadenceInstance]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadenceInstance]
    (
        InstanceId       NVARCHAR(200) NOT NULL,
        MachineName      NVARCHAR(200) NOT NULL,
        ProcessId        INT           NOT NULL,
        AssemblyVersion  NVARCHAR(50)  NULL,
        StartedAtUtc     DATETIME2(3)  NOT NULL,
        LastHeartbeatUtc DATETIME2(3)  NOT NULL,
        CONSTRAINT PK_CadenceInstance PRIMARY KEY (InstanceId)
    );

    CREATE INDEX IX_CadenceInstance_Heartbeat ON [{schema}].[CadenceInstance] (LastHeartbeatUtc);
END
GO
