CREATE TABLE [dbo].[EmailDispatches]
(
    [Id] NVARCHAR(64) NOT NULL CONSTRAINT [PK_EmailDispatches] PRIMARY KEY,
    [LeaseUntil] DATETIMEOFFSET NOT NULL,
    [SentAt] DATETIMEOFFSET NULL,
    [RowVersion] ROWVERSION NOT NULL
);