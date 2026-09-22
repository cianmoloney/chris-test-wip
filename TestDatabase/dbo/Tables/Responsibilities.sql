-- A capability granted to a role (e.g. "Generate links"). When enabled the
-- feature is visible to users in that role, otherwise it is hidden.
CREATE TABLE [dbo].[Responsibilities]
(
    [Id]        INT IDENTITY (1, 1) NOT NULL CONSTRAINT [PK_Responsibilities] PRIMARY KEY,
    [RoleId]    INT                 NOT NULL
        CONSTRAINT [FK_Responsibilities_Roles_RoleId]
        REFERENCES [dbo].[Roles] ([Id]) ON DELETE CASCADE,
    [Name]      NVARCHAR (128)      NOT NULL,
    [IsEnabled] BIT                 NOT NULL CONSTRAINT [DF_Responsibilities_IsEnabled] DEFAULT (1)
);
GO

CREATE UNIQUE INDEX [IX_Responsibilities_RoleId_Name] ON [dbo].[Responsibilities] ([RoleId], [Name]);
