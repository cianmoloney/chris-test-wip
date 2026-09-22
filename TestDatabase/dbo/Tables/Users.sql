-- Employees who can sign in; each user belongs to a role (HR / Admin / Foreman).
-- The password is stored as a hash, never in plain text.
CREATE TABLE [dbo].[Users]
(
    [Id]           INT IDENTITY (1, 1) NOT NULL CONSTRAINT [PK_Users] PRIMARY KEY,
    [Email]        NVARCHAR (256)      NOT NULL,
    [PasswordHash] NVARCHAR (512)      NOT NULL,
    [Phone]        NVARCHAR (32)       NULL,
    [RoleId]       INT                 NOT NULL
        CONSTRAINT [FK_Users_Roles_RoleId]
        REFERENCES [dbo].[Roles] ([Id]),
    [DateCreated]  DATETIME2           NOT NULL CONSTRAINT [DF_Users_DateCreated] DEFAULT (SYSUTCDATETIME()),
    [IsEnabled]    BIT                 NOT NULL CONSTRAINT [DF_Users_IsEnabled] DEFAULT (1),
    [MfaEnabled]   BIT                 NOT NULL CONSTRAINT [DF_Users_MfaEnabled] DEFAULT (0),
    [FailedAttempts] INT NOT NULL CONSTRAINT [DF_Users_FailedAttempts] DEFAULT (0),
    [LockedUntil] DATETIMEOFFSET NULL,
    [LastChallengeAt] DATETIMEOFFSET NULL,
    [RowVersion] ROWVERSION NOT NULL
);
GO

CREATE UNIQUE INDEX [IX_Users_Email] ON [dbo].[Users] ([Email]);
