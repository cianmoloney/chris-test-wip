CREATE TABLE [dbo].[UserSessions]
(
    [TokenHash] NVARCHAR(64) NOT NULL CONSTRAINT [PK_UserSessions] PRIMARY KEY,
    [UserId] INT NOT NULL REFERENCES [dbo].[Users]([Id]) ON DELETE CASCADE,
    [ExpiresAt] DATETIMEOFFSET NOT NULL,
    [MfaPending] BIT NOT NULL,
    [CodeHash] NVARCHAR(64) NULL,
    [Attempts] INT NOT NULL,
    [RowVersion] ROWVERSION NOT NULL
);