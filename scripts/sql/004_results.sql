/*
    Cadence schema, revision 004: what runs produce.

    A table of its own rather than a column on CadenceJobRun, for two reasons. A VARBINARY(MAX)
    column drags its row off-page and makes every history query pay for a blob nobody selected;
    and results have their own retention -- far shorter than history's, because a month of rows is
    free and a month of spreadsheets is not -- so they have to be deletable without touching the
    run they belong to.

    No foreign key to CadenceJobRun. The janitor purges runs and results on separate schedules and
    in separate batches, and a cascade would make deleting a run's history depend on how large the
    blob attached to it happens to be. An orphaned result is swept by its own expiry.
*/

IF OBJECT_ID(N'[{schema}].[CadenceJobResult]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadenceJobResult]
    (
        RunId       UNIQUEIDENTIFIER NOT NULL,
        ContentType NVARCHAR(200)    NOT NULL,
        FileName    NVARCHAR(260)    NULL,
        Length      BIGINT           NOT NULL,
        Content     VARBINARY(MAX)   NOT NULL,
        CreatedAtUtc DATETIME2(3)    NOT NULL,
        ExpiresAtUtc DATETIME2(3)    NOT NULL,
        CONSTRAINT PK_CadenceJobResult PRIMARY KEY (RunId)
    );

    -- The janitor's sweep is the only query that does not go by primary key.
    CREATE INDEX IX_CadenceJobResult_Expires
        ON [{schema}].[CadenceJobResult] (ExpiresAtUtc);
END
GO
