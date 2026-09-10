-- A user role (HR, Admin, Foreman). Each role owns a set of
-- responsibilities that can be individually enabled or disabled.
CREATE TABLE [dbo].[Roles]
(
    [Id]   INT IDENTITY (1, 1) NOT NULL CONSTRAINT [PK_Roles] PRIMARY KEY,
    [Name] NVARCHAR (64)       NOT NULL
);
GO

CREATE UNIQUE INDEX [IX_Roles_Name] ON [dbo].[Roles] ([Name]);
