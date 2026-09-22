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