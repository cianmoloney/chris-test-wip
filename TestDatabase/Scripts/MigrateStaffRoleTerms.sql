SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 50000, 'Select the application database before running this migration.', 1;
IF @@TRANCOUNT <> 0
    THROW 50001, 'Run this migration outside an existing transaction.', 1;
IF OBJECT_ID(N'dbo.TermsDocuments', N'U') IS NULL OR OBJECT_ID(N'dbo.StaffRoles', N'U') IS NULL
    THROW 50002, 'Create the application schema before migrating role terms.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.StaffRoleTermsDocuments', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[StaffRoleTermsDocuments]
        (
            [StaffRoleId] INT NOT NULL,
            [TermsDocumentId] INT NOT NULL,
            CONSTRAINT [PK_StaffRoleTermsDocuments] PRIMARY KEY ([StaffRoleId], [TermsDocumentId]),
            CONSTRAINT [FK_StaffRoleTermsDocuments_StaffRoles] FOREIGN KEY ([StaffRoleId])
                REFERENCES [dbo].[StaffRoles] ([Id]) ON DELETE CASCADE,
            CONSTRAINT [FK_StaffRoleTermsDocuments_TermsDocuments] FOREIGN KEY ([TermsDocumentId])
                REFERENCES [dbo].[TermsDocuments] ([Id]) ON DELETE CASCADE
        );
        CREATE INDEX [IX_StaffRoleTermsDocuments_TermsDocumentId] ON [dbo].[StaffRoleTermsDocuments] ([TermsDocumentId]);
    END;

    IF COL_LENGTH(N'dbo.TermsDocuments', N'StaffRoleId') IS NOT NULL
    BEGIN
        EXEC sys.sp_executesql N'
            INSERT INTO dbo.StaffRoleTermsDocuments (StaffRoleId, TermsDocumentId)
            SELECT terms.StaffRoleId, terms.Id
            FROM dbo.TermsDocuments AS terms WITH (TABLOCKX, HOLDLOCK)
            WHERE terms.StaffRoleId IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1 FROM dbo.StaffRoleTermsDocuments AS existing WITH (UPDLOCK, HOLDLOCK)
                    WHERE existing.StaffRoleId = terms.StaffRoleId AND existing.TermsDocumentId = terms.Id
                );';
        IF OBJECT_ID(N'dbo.FK_TermsDocuments_StaffRoles', N'F') IS NOT NULL
            ALTER TABLE [dbo].[TermsDocuments] DROP CONSTRAINT [FK_TermsDocuments_StaffRoles];
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TermsDocuments') AND name = N'IX_TermsDocuments_StaffRoleId')
            DROP INDEX [IX_TermsDocuments_StaffRoleId] ON [dbo].[TermsDocuments];
        ALTER TABLE [dbo].[TermsDocuments] DROP COLUMN [StaffRoleId];
    END;

    COMMIT TRANSACTION;
    PRINT N'Role terms migration complete. Shared mappings retained; terms and acceptance history unchanged.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;