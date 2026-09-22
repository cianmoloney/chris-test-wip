-- Run this entire script against an existing, empty application database.
-- SQL Server / Azure SQL Database; no DACPAC or SQLCMD mode required.
-- Creates the schema and reference data, not logins or Azure identity grants.
-- Create the first Admin afterwards using the application's --create-admin command.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 50000, 'Select an empty application database before running this script.', 1;

IF @@TRANCOUNT <> 0
    THROW 50001, 'Run this script outside an existing transaction.', 1;

IF EXISTS (SELECT 1 FROM sys.tables WHERE is_ms_shipped = 0)
    OR EXISTS (SELECT 1 FROM sys.sequences)
    THROW 50002, 'This setup requires an empty database. Existing objects have not been changed.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE SEQUENCE [dbo].[StaffNumbers]
        AS INT
        START WITH 1
        INCREMENT BY 1;

    CREATE TABLE [dbo].[StaffTypes]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_StaffTypes] PRIMARY KEY,
        [Name] NVARCHAR(64) NOT NULL,
        [Prefix] NVARCHAR(1) NOT NULL
    );
    CREATE UNIQUE INDEX [IX_StaffTypes_Name] ON [dbo].[StaffTypes] ([Name]);
    CREATE UNIQUE INDEX [IX_StaffTypes_Prefix] ON [dbo].[StaffTypes] ([Prefix]);

    CREATE TABLE [dbo].[StaffRoles]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_StaffRoles] PRIMARY KEY,
        [Name] NVARCHAR(128) NOT NULL CONSTRAINT [UQ_StaffRoles_Name] UNIQUE
    );

    CREATE TABLE [dbo].[DocumentTypes]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_DocumentTypes] PRIMARY KEY,
        [Name] NVARCHAR(128) NOT NULL CONSTRAINT [UQ_DocumentTypes_Name] UNIQUE
    );

    CREATE TABLE [dbo].[Roles]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Roles] PRIMARY KEY,
        [Name] NVARCHAR(64) NOT NULL
    );
    CREATE UNIQUE INDEX [IX_Roles_Name] ON [dbo].[Roles] ([Name]);

    CREATE TABLE [dbo].[Staff]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Staff] PRIMARY KEY,
        [FirstName] NVARCHAR(128) NOT NULL,
        [LastName] NVARCHAR(128) NOT NULL,
        [Email] NVARCHAR(256) NOT NULL,
        [PhoneNumber] NVARCHAR(32) NULL,
        [StaffRoleId] INT NULL CONSTRAINT [FK_Staff_StaffRoles]
            REFERENCES [dbo].[StaffRoles] ([Id]) ON DELETE SET NULL,
        [Role] NVARCHAR(128) NULL,
        [IsArchived] BIT NOT NULL CONSTRAINT [DF_Staff_IsArchived] DEFAULT (0),
        [RegisteredAt] DATETIMEOFFSET NOT NULL CONSTRAINT [DF_Staff_RegisteredAt] DEFAULT (SYSDATETIMEOFFSET()),
        [StaffTypeId] INT NULL CONSTRAINT [FK_Staff_StaffTypes_StaffTypeId]
            REFERENCES [dbo].[StaffTypes] ([Id]) ON DELETE SET NULL,
        [StaffId] NVARCHAR(16) NULL,
        [StaffNumber] INT NOT NULL CONSTRAINT [DF_Staff_StaffNumber] DEFAULT (NEXT VALUE FOR [dbo].[StaffNumbers])
    );
    CREATE UNIQUE INDEX [IX_Staff_Email] ON [dbo].[Staff] ([Email]);
    CREATE UNIQUE INDEX [IX_Staff_StaffNumber] ON [dbo].[Staff] ([StaffNumber]);
    CREATE UNIQUE INDEX [IX_Staff_StaffId] ON [dbo].[Staff] ([StaffId]) WHERE [StaffId] IS NOT NULL;

    CREATE TABLE [dbo].[StaffRoleDocumentTypes]
    (
        [StaffRoleId] INT NOT NULL REFERENCES [dbo].[StaffRoles] ([Id]) ON DELETE CASCADE,
        [DocumentTypeId] INT NOT NULL REFERENCES [dbo].[DocumentTypes] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [PK_StaffRoleDocumentTypes] PRIMARY KEY ([StaffRoleId], [DocumentTypeId])
    );

    CREATE TABLE [dbo].[Documents]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Documents] PRIMARY KEY,
        [Name] NVARCHAR(512) NOT NULL CONSTRAINT [DF_Documents_Name] DEFAULT (N''),
        [DocumentTypeId] INT NULL CONSTRAINT [FK_Documents_DocumentTypes]
            REFERENCES [dbo].[DocumentTypes] ([Id]) ON DELETE SET NULL,
        [BlobName] NVARCHAR(512) NULL,
        [ContainerName] NVARCHAR(63) NULL,
        [ScanPassed] BIT NOT NULL CONSTRAINT [DF_Documents_ScanPassed] DEFAULT (0),
        [ProcessingCompletedAt] DATETIMEOFFSET NULL,
        [LastProcessingAttempt] DATETIMEOFFSET NULL,
        [Issue] NVARCHAR(512) NULL,
        [IsArchived] BIT NOT NULL CONSTRAINT [DF_Documents_IsArchived] DEFAULT (0),
        [RowVersion] ROWVERSION NOT NULL,
        [DocumentType] NVARCHAR(128) NULL,
        [DocumentNumber] NVARCHAR(128) NULL,
        [ExtractedName] NVARCHAR(256) NULL,
        [Email] NVARCHAR(256) NULL,
        [Phone] NVARCHAR(64) NULL,
        [StartDate] DATETIMEOFFSET NULL,
        [ExpiryDate] DATETIMEOFFSET NULL,
        [Status] INT NOT NULL CONSTRAINT [DF_Documents_Status] DEFAULT (0),
        [IsValid] BIT NOT NULL CONSTRAINT [DF_Documents_IsValid] DEFAULT (0),
        [Timestamp] DATETIMEOFFSET NOT NULL CONSTRAINT [DF_Documents_Timestamp] DEFAULT (SYSDATETIMEOFFSET()),
        [StaffId] INT NULL CONSTRAINT [FK_Documents_Staff_StaffId]
            REFERENCES [dbo].[Staff] ([Id]) ON DELETE SET NULL
    );
    CREATE INDEX [IX_Documents_ExpiryDate] ON [dbo].[Documents] ([ExpiryDate]);
    CREATE INDEX [IX_Documents_BlobName] ON [dbo].[Documents] ([BlobName]);
    CREATE INDEX [IX_Documents_StaffId] ON [dbo].[Documents] ([StaffId]);
    CREATE UNIQUE INDEX [IX_Documents_ContainerName_BlobName] ON [dbo].[Documents] ([ContainerName], [BlobName])
        WHERE [ContainerName] IS NOT NULL AND [BlobName] IS NOT NULL;

    CREATE TABLE [dbo].[TermsDocuments]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TermsDocuments] PRIMARY KEY,
        [Title] NVARCHAR(256) NOT NULL,
        [CreatedAt] DATETIMEOFFSET NOT NULL CONSTRAINT [DF_TermsDocuments_CreatedAt] DEFAULT (SYSDATETIMEOFFSET())
    );

    CREATE TABLE [dbo].[TermsDocumentVersions]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TermsDocumentVersions] PRIMARY KEY,
        [TermsDocumentId] INT NOT NULL CONSTRAINT [FK_TermsDocumentVersions_TermsDocuments]
            REFERENCES [dbo].[TermsDocuments] ([Id]) ON DELETE CASCADE,
        [Content] NVARCHAR(MAX) NOT NULL,
        [Language] NVARCHAR(8) NOT NULL CONSTRAINT [DF_TermsDocumentVersions_Language] DEFAULT (N'en'),
        [Version] INT NOT NULL CONSTRAINT [DF_TermsDocumentVersions_Version] DEFAULT (1),
        [IsActive] BIT NOT NULL CONSTRAINT [DF_TermsDocumentVersions_IsActive] DEFAULT (1),
        [CreatedAt] DATETIMEOFFSET NOT NULL CONSTRAINT [DF_TermsDocumentVersions_CreatedAt] DEFAULT (SYSDATETIMEOFFSET())
    );
    CREATE INDEX [IX_TermsDocumentVersions_Document_Language_IsActive]
        ON [dbo].[TermsDocumentVersions] ([TermsDocumentId], [Language], [IsActive]);

    CREATE TABLE [dbo].[StaffTermsAcceptances]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_StaffTermsAcceptances] PRIMARY KEY,
        [StaffId] INT NOT NULL CONSTRAINT [FK_StaffTermsAcceptances_Staffs]
            REFERENCES [dbo].[Staff] ([Id]) ON DELETE CASCADE,
        [TermsDocumentVersionId] INT NOT NULL CONSTRAINT [FK_StaffTermsAcceptances_TermsDocumentVersions]
            REFERENCES [dbo].[TermsDocumentVersions] ([Id]),
        [AcceptedAt] DATETIMEOFFSET NOT NULL CONSTRAINT [DF_StaffTermsAcceptances_AcceptedAt] DEFAULT (SYSDATETIMEOFFSET())
    );
    CREATE INDEX [IX_StaffTermsAcceptances_Staff_Version]
        ON [dbo].[StaffTermsAcceptances] ([StaffId], [TermsDocumentVersionId]);

    CREATE TABLE [dbo].[StaffTermsAssignments]
    (
        [StaffId] INT NOT NULL REFERENCES [dbo].[Staff] ([Id]) ON DELETE CASCADE,
        [TermsDocumentId] INT NOT NULL REFERENCES [dbo].[TermsDocuments] ([Id]),
        [Version] INT NOT NULL,
        [AssignedAt] DATETIMEOFFSET NOT NULL,
        CONSTRAINT [PK_StaffTermsAssignments] PRIMARY KEY ([StaffId], [TermsDocumentId])
    );

    CREATE TABLE [dbo].[Responsibilities]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Responsibilities] PRIMARY KEY,
        [RoleId] INT NOT NULL CONSTRAINT [FK_Responsibilities_Roles_RoleId]
            REFERENCES [dbo].[Roles] ([Id]) ON DELETE CASCADE,
        [Name] NVARCHAR(128) NOT NULL,
        [IsEnabled] BIT NOT NULL CONSTRAINT [DF_Responsibilities_IsEnabled] DEFAULT (1)
    );
    CREATE UNIQUE INDEX [IX_Responsibilities_RoleId_Name] ON [dbo].[Responsibilities] ([RoleId], [Name]);

    CREATE TABLE [dbo].[Users]
    (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Users] PRIMARY KEY,
        [Email] NVARCHAR(256) NOT NULL,
        [PasswordHash] NVARCHAR(512) NOT NULL,
        [Phone] NVARCHAR(32) NULL,
        [RoleId] INT NOT NULL CONSTRAINT [FK_Users_Roles_RoleId] REFERENCES [dbo].[Roles] ([Id]),
        [DateCreated] DATETIME2 NOT NULL CONSTRAINT [DF_Users_DateCreated] DEFAULT (SYSUTCDATETIME()),
        [IsEnabled] BIT NOT NULL CONSTRAINT [DF_Users_IsEnabled] DEFAULT (1),
        [MfaEnabled] BIT NOT NULL CONSTRAINT [DF_Users_MfaEnabled] DEFAULT (0),
        [FailedAttempts] INT NOT NULL CONSTRAINT [DF_Users_FailedAttempts] DEFAULT (0),
        [LockedUntil] DATETIMEOFFSET NULL,
        [LastChallengeAt] DATETIMEOFFSET NULL,
        [RowVersion] ROWVERSION NOT NULL
    );
    CREATE UNIQUE INDEX [IX_Users_Email] ON [dbo].[Users] ([Email]);

    CREATE TABLE [dbo].[UserSessions]
    (
        [TokenHash] NVARCHAR(64) NOT NULL CONSTRAINT [PK_UserSessions] PRIMARY KEY,
        [UserId] INT NOT NULL REFERENCES [dbo].[Users] ([Id]) ON DELETE CASCADE,
        [ExpiresAt] DATETIMEOFFSET NOT NULL,
        [MfaPending] BIT NOT NULL,
        [CodeHash] NVARCHAR(64) NULL,
        [Attempts] INT NOT NULL,
        [RowVersion] ROWVERSION NOT NULL
    );

    CREATE TABLE [dbo].[AuditEntries]
    (
        [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AuditEntries] PRIMARY KEY,
        [UserId] INT NULL,
        [Action] NVARCHAR(128) NOT NULL,
        [Subject] NVARCHAR(256) NOT NULL,
        [OccurredAt] DATETIMEOFFSET NOT NULL
    );

    CREATE TABLE [dbo].[ShareLinks]
    (
        [TokenHash] NVARCHAR(64) NOT NULL CONSTRAINT [PK_ShareLinks] PRIMARY KEY,
        [UploadId] UNIQUEIDENTIFIER NOT NULL,
        [Purpose] NVARCHAR(16) NOT NULL,
        [StaffId] INT NULL,
        [TermsDocumentId] INT NULL,
        [TermsVersion] INT NULL,
        [CreatedBy] INT NOT NULL,
        [ExpiresAt] DATETIMEOFFSET NOT NULL,
        [UsedAt] DATETIMEOFFSET NULL,
        [RevokedAt] DATETIMEOFFSET NULL,
        [LastUploadCheck] DATETIMEOFFSET NULL,
        [ContainerName] NVARCHAR(63) NULL,
        [BlobName] NVARCHAR(512) NULL,
        [ContentHash] NVARCHAR(64) NULL,
        [DocumentTypeId] INT NULL,
        [RowVersion] ROWVERSION NOT NULL
    );

    CREATE TABLE [dbo].[EmailDispatches]
    (
        [Id] NVARCHAR(64) NOT NULL CONSTRAINT [PK_EmailDispatches] PRIMARY KEY,
        [LeaseUntil] DATETIMEOFFSET NOT NULL,
        [SentAt] DATETIMEOFFSET NULL,
        [RowVersion] ROWVERSION NOT NULL
    );

    SET IDENTITY_INSERT [dbo].[StaffTypes] ON;
    INSERT INTO [dbo].[StaffTypes] ([Id], [Name], [Prefix])
    VALUES (1, N'Permanent', N'P'), (2, N'Contract', N'E');
    SET IDENTITY_INSERT [dbo].[StaffTypes] OFF;

    SET IDENTITY_INSERT [dbo].[Roles] ON;
    INSERT INTO [dbo].[Roles] ([Id], [Name])
    VALUES (1, N'HR'), (2, N'Admin'), (3, N'Foreman');
    SET IDENTITY_INSERT [dbo].[Roles] OFF;

    INSERT INTO [dbo].[StaffRoles] ([Name])
    VALUES (N'Driver'), (N'Carpenter'), (N'Electrician');

    INSERT INTO [dbo].[DocumentTypes] ([Name])
    VALUES (N'Safe Pass'), (N'Forklift');

    INSERT INTO [dbo].[StaffRoleDocumentTypes] ([StaffRoleId], [DocumentTypeId])
    SELECT role.Id, documentType.Id
    FROM dbo.StaffRoles role CROSS JOIN dbo.DocumentTypes documentType
    WHERE role.Name = N'Driver' AND documentType.Name IN (N'Safe Pass', N'Forklift');

    INSERT INTO [dbo].[Responsibilities] ([RoleId], [Name], [IsEnabled])
    SELECT role.Id, permission.Name, 1
    FROM dbo.Roles role
    CROSS JOIN (VALUES (N'Staff.Read'), (N'Staff.Write'), (N'Documents.Write'), (N'Documents.Validate'),
                       (N'Links.Write'), (N'Users.Write'), (N'Terms.Write')) permission(Name)
    WHERE role.Name IN (N'HR', N'Admin')
       OR (role.Name = N'Foreman' AND permission.Name IN (N'Staff.Read', N'Documents.Validate', N'Links.Write'));

    INSERT INTO [dbo].[TermsDocuments] ([Title]) VALUES (N'General Staff terms');
    DECLARE @TermsId INT = CONVERT(INT, SCOPE_IDENTITY());

    INSERT INTO [dbo].[TermsDocumentVersions] ([TermsDocumentId], [Content], [Language], [Version], [IsActive])
    VALUES
        (@TermsId, N'By agreeing, you confirm that the information you provided is accurate, that any certificates you upload are genuine, and that you will comply with all workplace safety and conduct requirements applicable to your role.', N'en', 1, 1),
        (@TermsId, N'Wyrażając zgodę, potwierdzasz, że podane informacje są dokładne, że wszelkie przesłane certyfikaty są autentyczne oraz że będziesz przestrzegać wszystkich wymogów bezpieczeństwa i zasad postępowania obowiązujących na Twoim stanowisku.', N'pl', 1, 1),
        (@TermsId, N'Погоджуючись, ви підтверджуєте, що надана вами інформація є точною, що всі завантажені сертифікати є справжніми, і що ви дотримуватиметеся всіх вимог безпеки та правил поведінки, які застосовуються до вашої ролі.', N'uk', 1, 1);

    COMMIT TRANSACTION;
    PRINT N'Setup complete: 17 tables, StaffNumbers sequence, constraints, indexes and reference data created.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;