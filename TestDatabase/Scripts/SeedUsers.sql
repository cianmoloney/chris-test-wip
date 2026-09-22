SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT N'Development/test only: these accounts use the public password LocalDemo!2026. Never run this in production.';

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 50000, 'Select your development application database before running this script.', 1;

IF @@TRANCOUNT <> 0
    THROW 50001, 'Run this script outside an existing transaction.', 1;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL OR OBJECT_ID(N'dbo.Roles', N'U') IS NULL
    THROW 50002, 'Create the application schema before seeding users.', 1;

DECLARE @PasswordHash NVARCHAR(512) = N'AQAAAAIAAYagAAAAEPKUpcqqOM5lyyAz6Gy1ko3n7LfxEw/FyOaQ1WcdjNgr8vXs1uDiiJKsXbr1eXRxtQ==';
DECLARE @Users TABLE ([Email] NVARCHAR(256) PRIMARY KEY, [RoleName] NVARCHAR(100));

INSERT INTO @Users ([Email], [RoleName])
VALUES (N'admin@example.test', N'Admin'),
       (N'hr@example.test', N'HR'),
       (N'foreman@example.test', N'Foreman');

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT 1 FROM @Users AS seed
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles AS role WHERE role.Name = seed.RoleName)
    )
        THROW 50003, 'Required Admin, HR or Foreman role is missing. Apply the database reference data first.', 1;

    INSERT INTO dbo.Users ([Email], [PasswordHash], [RoleId], [IsEnabled], [MfaEnabled])
    SELECT seed.Email, @PasswordHash, role.Id, 1, 0
    FROM @Users AS seed
    INNER JOIN dbo.Roles AS role ON role.Name = seed.RoleName
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.Users AS existing WITH (UPDLOCK, HOLDLOCK)
        WHERE existing.Email = seed.Email
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT account.Email, role.Name AS [Role], account.IsEnabled, account.MfaEnabled
FROM dbo.Users AS account
INNER JOIN dbo.Roles AS role ON role.Id = account.RoleId
INNER JOIN @Users AS seed ON seed.Email = account.Email
ORDER BY account.Email;