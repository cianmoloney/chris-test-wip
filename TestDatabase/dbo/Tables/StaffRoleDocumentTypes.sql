CREATE TABLE [dbo].[StaffRoleDocumentTypes]
(
    [StaffRoleId] INT NOT NULL REFERENCES [dbo].[StaffRoles] ([Id]) ON DELETE CASCADE,
    [DocumentTypeId] INT NOT NULL REFERENCES [dbo].[DocumentTypes] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [PK_StaffRoleDocumentTypes] PRIMARY KEY ([StaffRoleId], [DocumentTypeId])
);