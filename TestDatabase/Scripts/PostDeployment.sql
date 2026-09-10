-- Post-deployment seed data. Runs after every publish, so all inserts are
-- guarded to stay idempotent.

-- Staff types used to build the human-readable StaffId prefix.
IF NOT EXISTS (SELECT 1 FROM dbo.StaffTypes)
BEGIN
    SET IDENTITY_INSERT dbo.StaffTypes ON;
    INSERT INTO dbo.StaffTypes (Id, Name, Prefix)
    VALUES (1, N'Permanent', N'P'),
           (2, N'External', N'E');
    SET IDENTITY_INSERT dbo.StaffTypes OFF;
END;

-- Application roles.
IF NOT EXISTS (SELECT 1 FROM dbo.Roles)
BEGIN
    SET IDENTITY_INSERT dbo.Roles ON;
    INSERT INTO dbo.Roles (Id, Name)
    VALUES (1, N'HR'),
           (2, N'Admin'),
           (3, N'Foreman');
    SET IDENTITY_INSERT dbo.Roles OFF;
END;

BEGIN
    SET IDENTITY_INSERT dbo.Staff ON;
    INSERT INTO dbo.Staff (Id, Email, PasswordHash, Phone, RoleId, DateCteated, IsEnabled, MfaEnabled)
    VALUES (1, 'cianmoloney05@gmail.com', 'pass', '0879752908', 2, SYSUTCDATETIME(), 1, 0);
    SET IDENTITY_INSERT dbo.Staff OFF;
END;


-- Seed a default terms document with one version per language if none exist yet.
IF NOT EXISTS (SELECT 1 FROM dbo.TermsDocuments)
BEGIN
    DECLARE @TermsId INT;

    INSERT INTO dbo.TermsDocuments (Title)
    VALUES (N'General Staff terms');

    SET @TermsId = SCOPE_IDENTITY();

    INSERT INTO dbo.TermsDocumentVersions (TermsDocumentId, Content, Language, Version, IsActive)
    VALUES
        (@TermsId, N'By agreeing, you confirm that the information you provided is accurate, that any certificates you upload are genuine, and that you will comply with all workplace safety and conduct requirements applicable to your role.', N'en', 1, 1),
        (@TermsId, N'Wyrażając zgodę, potwierdzasz, że podane informacje są dokładne, że wszelkie przesłane certyfikaty są autentyczne oraz że będziesz przestrzegać wszystkich wymogów bezpieczeństwa i zasad postępowania obowiązujących na Twoim stanowisku.', N'pl', 1, 1),
        (@TermsId, N'Погоджуючись, ви підтверджуєте, що надана вами інформація є точною, що всі завантажені сертифікати є справжніми, і що ви дотримуватиметеся всіх вимог безпеки та правил поведінки, які застосовуються до вашої ролі.', N'uk', 1, 1);
END;


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
