/*
    Cadence schema, revision 002: the cluster-wide pause switches.

    One row, like CadenceScheduleVersion, and written in the same transaction as a bump of that
    row -- so a pause reaches other instances on the change detection that already exists rather
    than needing a poller of its own.
*/

IF OBJECT_ID(N'[{schema}].[CadencePause]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadencePause]
    (
        Id       TINYINT       NOT NULL,
        Scope    TINYINT       NOT NULL,
        Reason   NVARCHAR(500) NULL,
        SetBy    NVARCHAR(200) NULL,
        SetAtUtc DATETIME2(3)  NULL,
        CONSTRAINT PK_CadencePause PRIMARY KEY (Id),
        CONSTRAINT CK_CadencePause_Singleton CHECK (Id = 1)
    );

    INSERT INTO [{schema}].[CadencePause] (Id, Scope) VALUES (1, 0);
END
GO
