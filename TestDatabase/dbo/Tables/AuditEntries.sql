CREATE TABLE [dbo].[AuditEntries]
(
    [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AuditEntries] PRIMARY KEY,
    [UserId] INT NULL,
    [Action] NVARCHAR(128) NOT NULL,
    [Subject] NVARCHAR(256) NOT NULL,
    [OccurredAt] DATETIMEOFFSET NOT NULL
);