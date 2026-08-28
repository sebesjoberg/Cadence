/*
    Cadence schema, revision 003: API tokens, and the Data Protection key ring the OIDC cookie
    needs.

    Digest is BINARY(32) rather than a hex string: it is the lookup key on every authenticated
    request, and 32 bytes indexes tighter than 64 characters. The unique index on it is what makes
    resolution a single seek.
*/

IF OBJECT_ID(N'[{schema}].[CadenceApiToken]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadenceApiToken]
    (
        Id               UNIQUEIDENTIFIER NOT NULL,
        Name             NVARCHAR(200)    NOT NULL,
        Digest           BINARY(32)       NOT NULL,
        Fingerprint      CHAR(8)          NOT NULL,
        Scope            TINYINT          NOT NULL,
        CreatedAtUtc     DATETIME2(3)     NOT NULL,
        CreatedBySubject NVARCHAR(400)    NULL,
        CreatedByName    NVARCHAR(256)    NULL,
        ExpiresAtUtc     DATETIME2(3)     NULL,
        CONSTRAINT PK_CadenceApiToken PRIMARY KEY (Id)
    );

    -- Covering: resolution reads exactly these on every authenticated request, so the seek answers
    -- from the index rather than following a key lookup into the table.
    CREATE UNIQUE INDEX UX_CadenceApiToken_Digest
        ON [{schema}].[CadenceApiToken] (Digest)
        INCLUDE (Name, Fingerprint, Scope);

    -- The janitor deletes by expiry, and this keeps that a range scan rather than a table scan.
    CREATE INDEX IX_CadenceApiToken_ExpiresAtUtc
        ON [{schema}].[CadenceApiToken] (ExpiresAtUtc)
        WHERE ExpiresAtUtc IS NOT NULL;
END
GO

/*
    Keys are stored as the XML the framework hands us, unencrypted at rest and protected by this
    database's own access controls -- the same ones already trusted with schedules and run history.
    ProtectKeysWithCertificate is the documented hardening step for anyone who wants more.
*/
IF OBJECT_ID(N'[{schema}].[CadenceDataProtectionKey]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[CadenceDataProtectionKey]
    (
        FriendlyName NVARCHAR(200) NOT NULL,
        Xml          NVARCHAR(MAX) NOT NULL,
        CreatedAtUtc DATETIME2(3)  NOT NULL,
        CONSTRAINT PK_CadenceDataProtectionKey PRIMARY KEY (FriendlyName)
    );
END
GO
