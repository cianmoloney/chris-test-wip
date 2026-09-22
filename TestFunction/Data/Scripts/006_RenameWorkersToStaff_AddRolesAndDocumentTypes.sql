-- Renames Workers to Staff and adds staff roles, document types, and the
-- role/document-type requirement association table.

-- 1. Rename tables and columns.
EXEC sp_rename 'dbo.Workers', 'Staff';
EXEC sp_rename 'dbo.Staff.WorkerId', 'StaffId', 'COLUMN';
EXEC sp_rename 'dbo.Staff.WorkerNumber', 'StaffNumber', 'COLUMN';
EXEC sp_rename 'dbo.Staff.WorkerTypeId', 'StaffTypeId', 'COLUMN';

EXEC sp_rename 'dbo.WorkerTypes', 'StaffTypes';

EXEC sp_rename 'dbo.WorkerTermsAcceptances', 'StaffTermsAcceptances';
EXEC sp_rename 'dbo.StaffTermsAcceptances.WorkerId', 'StaffId', 'COLUMN';

EXEC sp_rename 'dbo.Documents.WorkerId', 'StaffId', 'COLUMN';

EXEC sp_rename 'dbo.WorkerNumbers', 'StaffNumbers';

-- 2. Staff roles (one role per staff member for now).
CREATE TABLE dbo.StaffRoles
(
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StaffRoles PRIMARY KEY,
    Name NVARCHAR(128) NOT NULL,
    CONSTRAINT UQ_StaffRoles_Name UNIQUE (Name)
);

INSERT INTO dbo.StaffRoles (Name) VALUES (N'Driver'), (N'Carpenter'), (N'Electrician');

ALTER TABLE dbo.Staff ADD StaffRoleId INT NULL
    CONSTRAINT FK_Staff_StaffRoles FOREIGN KEY REFERENCES dbo.StaffRoles(Id) ON DELETE SET NULL;

-- The old free-text Role column is superseded by StaffRoleId.
ALTER TABLE dbo.Staff DROP COLUMN Role;

-- 3. Document types (e.g. Safe Pass, Forklift).
CREATE TABLE dbo.DocumentTypes
(
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DocumentTypes PRIMARY KEY,
    Name NVARCHAR(128) NOT NULL,
    CONSTRAINT UQ_DocumentTypes_Name UNIQUE (Name)
);

INSERT INTO dbo.DocumentTypes (Name) VALUES (N'Safe Pass'), (N'Forklift');

ALTER TABLE dbo.Documents ADD DocumentTypeId INT NULL
    CONSTRAINT FK_Documents_DocumentTypes FOREIGN KEY REFERENCES dbo.DocumentTypes(Id) ON DELETE SET NULL;

-- 4. One role can require many document types (and vice versa).
-- A staff member is expected to upload ALL documents for their role.
CREATE TABLE dbo.StaffRoleDocumentTypes
(
    StaffRoleId INT NOT NULL
        CONSTRAINT FK_StaffRoleDocumentTypes_StaffRoles FOREIGN KEY REFERENCES dbo.StaffRoles(Id) ON DELETE CASCADE,
    DocumentTypeId INT NOT NULL
        CONSTRAINT FK_StaffRoleDocumentTypes_DocumentTypes FOREIGN KEY REFERENCES dbo.DocumentTypes(Id) ON DELETE CASCADE,
    CONSTRAINT PK_StaffRoleDocumentTypes PRIMARY KEY (StaffRoleId, DocumentTypeId)
);

-- Example: drivers require a Safe Pass and a Forklift licence.
INSERT INTO dbo.StaffRoleDocumentTypes (StaffRoleId, DocumentTypeId)
SELECT r.Id, t.Id
FROM dbo.StaffRoles r
CROSS JOIN dbo.DocumentTypes t
WHERE r.Name = N'Driver';
