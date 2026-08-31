/*
    Cadence schema, revision 005: what makes OverlapPolicy.Skip strict across a cluster.

    A run of a Skip job carries its job name in ExclusiveKey while it is running, and the filtered
    unique index below is what lets at most one run hold a given key at a time. It is the same
    mechanism as UX_CadenceJobRun_Occurrence one question over: the occurrence index answers "has
    this slot been taken", and this one answers "is this job running anywhere".

    Filtered on IS NOT NULL because a unique index would otherwise allow exactly one non-exclusive
    run in the whole table -- SQL Server treats NULLs as equal for uniqueness.

    The key is released by the same write that records a run's outcome, and by the janitor's reap
    for a run whose instance died holding it. That is the cost of the guarantee: a Skip job whose
    instance is killed mid-run stays blocked until the reap, bounded by HeartbeatTimeout.
*/

IF COL_LENGTH(N'[{schema}].[CadenceJobRun]', 'ExclusiveKey') IS NULL
BEGIN
    ALTER TABLE [{schema}].[CadenceJobRun] ADD ExclusiveKey NVARCHAR(200) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
     WHERE name = N'UX_CadenceJobRun_Exclusive'
       AND object_id = OBJECT_ID(N'[{schema}].[CadenceJobRun]'))
BEGIN
    CREATE UNIQUE INDEX UX_CadenceJobRun_Exclusive
        ON [{schema}].[CadenceJobRun] (ExclusiveKey)
        WHERE ExclusiveKey IS NOT NULL;
END
GO
