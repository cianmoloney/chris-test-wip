-- Staff registered via the registration page. StaffId is a human-readable
-- identifier prefixed by the Staff type ("P" for permanent, "C" for contractor),
-- distinct from the surrogate Id used for SQL joins.
CREATE TABLE [dbo].[Staff]
(
    [Id]           INT IDENTITY (1, 1) NOT NULL CONSTRAINT [PK_Staff] PRIMARY KEY,
    [FirstName]    NVARCHAR (128)      NOT NULL,
    [LastName]     NVARCHAR (128)      NOT NULL,
    [Email]        NVARCHAR (256)      NOT NULL,
    [PhoneNumber]  NVARCHAR (32)       NULL,
    [StaffRoleId]  INT NULL CONSTRAINT [FK_Staff_StaffRoles] REFERENCES [dbo].[StaffRoles] ([Id]) ON DELETE SET NULL,
    [Role] NVARCHAR(128) NULL,
    [IsArchived] BIT NOT NULL CONSTRAINT [DF_Staff_IsArchived] DEFAULT (0),
    [RegisteredAt] DATETIMEOFFSET      NOT NULL CONSTRAINT [DF_Staff_RegisteredAt] DEFAULT (SYSDATETIMEOFFSET()),
    [StaffTypeId] INT                 NULL
        CONSTRAINT [FK_Staff_StaffTypes_StaffTypeId]
        REFERENCES [dbo].[StaffTypes] ([Id]) ON DELETE SET NULL,
    [StaffId]     NVARCHAR (16)       NULL,
    [StaffNumber] INT                 NOT NULL
        CONSTRAINT [DF_Staff_StaffNumber] DEFAULT (NEXT VALUE FOR [dbo].[StaffNumbers])
);
GO

CREATE UNIQUE INDEX [IX_Staff_Email] ON [dbo].[Staff] ([Email]);
GO

CREATE UNIQUE INDEX [IX_Staff_StaffNumber] ON [dbo].[Staff] ([StaffNumber]);
GO

CREATE UNIQUE INDEX [IX_Staff_StaffId] ON [dbo].[Staff] ([StaffId]) WHERE [StaffId] IS NOT NULL;
