-- The kind of Staff (e.g. Permanent, External/contractor). The prefix is
-- used to build the human-readable StaffId (P... or E...).
CREATE TABLE [dbo].[StaffTypes]
(
    [Id]     INT IDENTITY (1, 1) NOT NULL CONSTRAINT [PK_StaffTypes] PRIMARY KEY,
    [Name]   NVARCHAR (64)       NOT NULL,
    [Prefix] NVARCHAR (1)        NOT NULL
);
GO

CREATE UNIQUE INDEX [IX_StaffTypes_Name] ON [dbo].[StaffTypes] ([Name]);
GO

CREATE UNIQUE INDEX [IX_StaffTypes_Prefix] ON [dbo].[StaffTypes] ([Prefix]);
