# rossmore-civil-wip

## Application Boundary

`TestFrontend` is the Razor Pages website. It calls the HTTP triggers in
`TestFunction/API.cs` through `FunctionApiClient`; it has no EF Core, SQL client,
database configuration, or reference to the Function project. `TestShared`
contains request/response contracts only. All entities, the EF context, SQL
scripts, queries, and database updates live in `TestFunction`.

The website owns protected login cookies and reads authorized blob downloads.
The Function owns accounts, hashed passwords, revocable sessions, optional email
MFA, permissions, persisted links, uploads, assignments and eligibility.
Every API call requires the configured application's identity. Office operations
additionally require a valid user session; public worker operations require a
purpose-scoped capability. Browser-supplied role or staff claims are not trusted.

Successful office-session validation is reused only within the current
`HttpContext`, keyed by the session-token hash. The API wrapper and handler
therefore share one session lookup; each permission/Admin check and expiry
check still runs. A new HTTP request reads current session/account/permission
data from SQL without EF tracking, so account disabling, revocation and role
changes take effect on the next request. Failed authentication is not cached;
logout clears the current request's validated session. No cross-request or
time-based authentication cache is used.

## Azure Configuration

1. Enable a system-assigned managed identity on the frontend App Service, or
	attach a user-assigned identity. Find its **application/client ID**, not its
	principal/object ID.
2. Create a single-tenant Entra app registration representing the Function API.
	Expose the application ID URI `api://<api-application-client-id>` and set
	`api.requestedAccessTokenVersion` to `2` in its manifest. Create an application
	role (for example `Api.Access`, allowed member type `Applications`), assign it
	to the frontend identity, and require assignment on the API enterprise app.
3. Configure the following application settings. No Function key or client
	secret is used by the frontend in Azure.

| Application | Setting | Value |
| --- | --- | --- |
| Frontend | `FunctionApi__BaseUrl` | `https://<function-app>.azurewebsites.net/api/` |
| Frontend | `FunctionApi__Scope` | `api://<api-application-client-id>/.default` |
| Frontend | `FunctionApi__ManagedIdentityClientId` | User-assigned identity client ID only; omit for system-assigned |
| Function | `ApiAuth__TenantId` | Your Entra tenant GUID |
| Function | `ApiAuth__Audience` | API application client ID GUID for v2 access tokens |
| Function | `ApiAuth__AllowedClientId` | Frontend managed identity application/client ID GUID |
| Function | `ConnectionStrings__DefaultConnection` | SQL connection string, configured **only here** |

For a v1 API registration, set `ApiAuth__Audience` to the actual token audience
(normally its `api://...` application ID URI). Both same-tenant v1 and v2 issuers
are supported. Tokens must have a valid Entra signature, issuer, audience, and
lifetime. The application ID must match `AllowedClientId`; delegated user
tokens are rejected. Authorization is enforced in every API trigger before
resolving the data service. `AuthorizationLevel.Anonymous` disables Functions
key authentication, **not** the application's bearer-token checks. App Service
Authentication may additionally be enabled with matching tenant/audience settings.

Use HTTPS-only hosting for both applications. For SQL managed identity, enable
an identity on the Function, grant that identity the required database
permissions, and use a connection string such as:

```text
Server=tcp:<server>.database.windows.net,1433;Database=<database>;Authentication=Active Directory Managed Identity;Encrypt=True;
```

Remove any SQL connection strings, SQL credentials, database users/grants, and
SQL network access previously assigned to the frontend. Restrict database
network access to the Function where practical. These Azure permissions and
network changes must be applied to the deployment; removing the SDK from the
website does not revoke existing infrastructure permissions. Blob storage
settings and permissions remain separate and unchanged.

### Application Insights

The frontend uses Azure Monitor OpenTelemetry when
`APPLICATIONINSIGHTS_CONNECTION_STRING` is non-empty. It can share the Function's
Application Insights resource; frontend telemetry has cloud role name
`TestFrontend`. The value in `TestFrontend/appsettings.json` is published with the
website, and an Azure app setting of the same name overrides it. Keep private
configuration out of source control. Incoming request query strings are explicitly
removed from telemetry before export because worker links contain access tokens.
Keep the SDK's other URL redaction defaults enabled. Use this code-based integration instead of
enabling a second App Service auto-instrumentation agent alongside it.

## Local Development

Requires .NET 10, Azure Functions Core Tools v4 with .NET 10 isolated-worker
support, and a SQL database with the existing application schema. For the
background worker, also configure its storage and other service settings.
Publish the updated SQL project before starting this version. It adds account
sessions, persisted links, terms assignments, processing metadata and audit tables.

Use `TestFunction/local.settings.example.json` as the template for the ignored
`TestFunction/local.settings.json`. Configure the SQL connection string there,
not in the frontend. Use Azurite for `UseDevelopmentStorage=true`, or configure
development storage. The example disables all background functions for API-only work.

Authentication is on by default. For an isolated local smoke test without
Entra credentials, explicitly set both:

- Function: `ApiAuth__AllowUnauthenticatedLocalRequests=true` with
  `AZURE_FUNCTIONS_ENVIRONMENT=Development`.
- Frontend: `FunctionApi__AllowUnauthenticatedLocalRequests=true` with
  `ASPNETCORE_ENVIRONMENT=Development` and a loopback API URL.

The Function bypass is refused on Azure App Service/Functions hosting. Never
expose a local host with the bypass enabled to an untrusted network. For
authenticated local development, use an HTTPS Function URL and a permitted
application credential through `DefaultAzureCredential`; an interactive
developer user token is intentionally insufficient. Azure deployments use
`ManagedIdentityCredential` exclusively.

Run the Function from its directory:

```powershell
func start --port 7024
```

Run the website from the solution directory:

```powershell
dotnet run --project TestFrontend --launch-profile https
```

The website is at `https://localhost:7124`; its development API URL is
`http://localhost:7024/api/`. Keep secrets out of source control. The old
`Auth:Password` and `ShareLinks:SigningKey` settings are no longer used.
API request examples are in `TestFunction/API.http`.

## HTTP API

Every endpoint requires the permitted application's bearer token, except when
the explicit local-only bypass above is enabled.

| Method | Route (under `/api`) | Purpose |
| --- | --- | --- |
| GET | `/lookups` | Staff types, roles, document types, role requirements |
| GET / POST | `/document-types` | HR/Admin document type catalog and creation, including text identifiers and required roles |
| GET / PUT | `/document-types/{id}` | Read or update one document type and its role requirements |
| POST | `/staff-roles` | Add a staff job role; requires Documents.Write |
| GET | `/staff-roles/{id}` | Read a staff job role; requires Documents.Write |
| PUT | `/staff-roles/{id}/document-types` | Replace one staff role's document requirements; requires Documents.Write |
| GET | `/staff?filter=&sortBy=` | Filtered/sorted staff and latest acceptance times |
| POST | `/staff` | Authorized office staff creation |
| GET / PUT | `/staff/{id}` | Staff details with documents/history; update editable details |
| GET | `/documents?status=&filter=&sortBy=` | Review, filter and sort documents |
| PUT | `/documents/{id}` | Edit metadata and staff association |
| PUT | `/documents/{id}/status` | Review status and matching validity flag |
| GET / PUT | `/staff/{staffId}/documents/{documentId}` | Staff-owned download metadata or metadata update |
| GET | `/staff/{staffId}/terms?language=` | Current terms with English fallback and latest acceptance |
| DELETE | `/staff/{id}`, `/documents/{id}` | Archive records, preserving history |
| POST | `/accounts/login`, `/accounts/mfa`, `/accounts/logout` | Account session lifecycle |
| GET / PUT | `/accounts/me`, `/accounts/me/mfa` | Current account / change MFA preference |
| GET / POST / PUT | `/users`, `/users/{id}` | Authorized account administration |
| GET | `/roles` | Admin-only role responsibilities and supported permission names |
| PUT | `/roles/{id}/responsibilities` | Admin-only replacement of a role's enabled responsibilities |
| GET / POST | `/terms` | List documents / publish a translated edition |
| GET | `/terms/{id}/versions` | Terms management history including previous versions and all languages; requires Terms.Write |
| PUT | `/terms/{id}/staff-roles` | Replace the roles requiring this shared document; requires Terms.Write, explicit StaffRoleIds and ExpectedRevision |
| POST | `/links` | Issue a purpose-scoped link and allocate terms |
| DELETE | `/links/{id}` | Revoke an issued link |
| POST | `/public/resolve`, `/public/register`, `/public/accept` | Capability-authorized worker operations |
| POST | `/public/register-many` | Atomically register 1-25 staff using a single-use multiple registration link |
| POST | `/public/upload` | Multipart `token`, `file`, optional `documentTypeId` |
| GET | `/files/access?container=&blob=` | Authorize a file download, blocking archived or quarantined documents |

Office requests also carry the opaque `X-User-Session` header. Do not send it
from a browser directly to the Function. The legacy office acceptance endpoint
is denied: a worker terms link is required.

Creates return `201`, updates `204`, missing records `404`, invalid requests
`400` (or `415` for unsupported content types), and conflicts `409`. Errors use
Problem Details. Document status is serialized as an enum name. Registration
creates the database-generated staff number and prefixed staff ID within one
transaction. Request DTOs cannot overwrite server-owned IDs or timestamps.

## Verification

```powershell
dotnet build TestFrontend/TestFrontend.csproj
dotnet build TestFunction/TestFunction.csproj
dotnet test TestFunction.Tests/TestFunction.Tests.csproj
```

The focused tests validate application-token checks, API error handling,
query/update behavior, document ownership, terms acceptance, and frontend token
handling. Data-service tests use EF's in-memory provider, not SQL Server; SQL
sequences, uniqueness constraints, transactions, and deployed managed identity
still require integration verification against the configured services.

### Browser Interaction Checks

Staff Roles & Document Types, Access Editor, Accounts, and loaded terms versions switch locally.
Staff/document filters, other terms documents, and file folders use normal
server navigation, preserving fresh data, server search semantics and browser
Back/Forward behavior. Registration Add/Remove updates the form locally.
Unsaved forms are protected on navigation; saving one role warns before
discarding drafts for another role. Drafts live only in page memory, never in
local storage, and are not restored after a confirmed departure or reload.
Saves, permissions, public-link validity, and final validation remain
server-side; ordinary form navigation still works without JavaScript.

Role requirements and office-role saves require `ExpectedRevision`, obtained
from the loaded assignment set. The API compares it within a serializable SQL
transaction and returns 409 for stale sets. A rejected draft retains its
original revision; reload and review the latest assignments before saving.
Deploy the frontend and Function together (no database schema change). Older
clients without the required revision must refresh/update before saving.
The static-asset authentication bypass remains enabled for public CSS/JS only.

The browser regression check uses an isolated frontend, fake API/storage and
separate browser context; it never changes the real database. It requires
Node.js, Microsoft Edge, and port 10000 free for its temporary Blob fixture
(stop Azurite yourself first if necessary). From the repository root:

```powershell
dotnet build TestFrontend/TestFrontend.csproj -c PageInteractionsVerification
npm install --no-save --prefix "$env:TEMP/hr-browser-tests" playwright
$env:PLAYWRIGHT_MODULE = Join-Path $env:TEMP "hr-browser-tests/node_modules/playwright"
node TestFunction.Tests/PageInteractions.browser.cjs
```

Set `FRONTEND_TEST_CONFIGURATION` to test a different build configuration or
`PLAYWRIGHT_CHANNEL` to use another installed Playwright browser channel.
Set `BROWSER_TEST_SUITE=navigation` to run only the header checks.
Set `BROWSER_TEST_SUITE=quick-actions` for the header, homepage and signed-in
upload checks, including selected types, validation, errors and permission denial.
Set `BROWSER_TEST_SUITE=staff-member` to also check work-status banners and
missing-terms summaries, acceptance history, permissions and mobile layouts.
Set `BROWSER_TEST_SUITE=role-terms` to also check role-required terms assignment,
clearing, moving, stale-save handling and permissions on desktop/mobile.
The check covers navigation order, permission visibility, account access,
responsive header layouts, request counts, warnings before draft loss, role-specific
save targets, stale revisions, native folder/filter history, failed terms
navigation, selected uploads, user edits, translated registration,
no-JavaScript fallbacks, mobile widths and revoked sessions. Live Azure Storage
latency is not measured. SQL concurrency tests require `HR_TEST_SQL` pointing
to a published disposable database named `HrImplementationVerification_*`.

### Desktop and Phone Layout Checks

The same Razor pages serve desktop and phone browsers. Forms stack on narrow
screens, text inputs remain at least 16px, and standalone phone controls have
44px touch targets. Staff, files, accounts and staff history retain all columns
in labelled, keyboard-focusable horizontal scroll regions. Staff document
editors open below the table at the available page width. No columns are hidden
and authentication, permissions, form handlers and draft protection are unchanged.

After the browser-test prerequisites above, run the all-page responsive suite:

```powershell
dotnet build TestFrontend/TestFrontend.csproj -c PageInteractionsVerification
$env:FRONTEND_TEST_CONFIGURATION = "PageInteractionsVerification"
$env:BROWSER_TEST_SUITE = "mobile"
$env:BROWSER_TEST_ARTIFACTS = Join-Path $env:TEMP "hr-responsive-screenshots"
node TestFunction.Tests/PageInteractions.browser.cjs
Remove-Item Env:BROWSER_TEST_SUITE
node TestFunction.Tests/PageInteractions.browser.cjs
```

The responsive suite checks all 22 routes, office editor modes, long synthetic
names/filenames/identifiers, English/Polish/Ukrainian worker pages, 320-1440px
viewports, landscape, tablet, touch actions, keyboard table scrolling, 200%
text sizing, footer placement and no-JavaScript layouts. It also exercises
document editing, generated links/revocation, terms acceptance, upload errors
and success, registration validation, empty lists and login/MFA. Screenshots
are optional; omit `BROWSER_TEST_ARTIFACTS` to disable them. Narrower development
checks are available as `mobile-shell`, `mobile-tables` and `mobile-forms`.
Run the default suite as well to cover permission gates, concurrency revisions,
unsaved drafts, native history and translated multiple-registration behavior.

Edge passed both suites during this change, along with 86 focused page-model
tests. WebKit can be selected with `BROWSER_TEST_ENGINE=webkit` after installing
it using `node "$env:PLAYWRIGHT_MODULE/cli.js" install webkit`; this workstation's
WebKit run failed to connect to the local HTTPS fixture before loading a page.
Safari/WebKit is therefore not verified. Remove `BROWSER_TEST_ENGINE` to return
to Edge. Physical iPhone/Android keyboard, native picker and swipe behavior
still require device checks; desktop mobile emulation is not equivalent.

## Workflow Rules

- The header shows Manage Staff, Document Explorer, Generate Link,
	Manage Terms, Accounts, then Admin-only Access Editor, subject to existing permissions.
  The app name links home; the person icon beside Sign out opens My account.
	Uploaded Files (`/Files`) is a button on Document Explorer, not a header link.
	The footer credits MSTMC with a mailto link to moloneysheehanltd@gmail.com.
  Staff Roles & Document Types is available from Staff with `Documents.Write`.
  Header labels and page titles change without changing routes or permissions.
	The icons are locally hosted from `lucide-static` 0.468.0, with their
  upstream license in `TestFrontend/wwwroot/lib/lucide/LICENSE`.
- Home displays **Quick Actions**: Generate Link for Staff, Document Explorer, Manage Staff,
	and Upload File, subject to permissions. Document Explorer opens `/Documents`,
	not the raw Uploaded Files browser. **Upload File** (`/UploadFile`) is also
	linked from Document Explorer and requires `Staff.Read`, `Documents.Write`
	and `Links.Write` on both GET and POST. It offers document-type selection or
	automatic detection and accepts the same PDF/image formats up to 20 MB as
	the public upload. It issues an unbound one-hour upload capability internally
	and uses the existing upload API; the token is never included in the page.
	Successful uploads await normal checks, extraction and manual review.
	This homepage/upload change requires frontend deployment only.
- HR/Admin can create, edit and archive staff/documents, reassign documents,
	manage non-Admin accounts, publish translated terms and issue secure links.
- HR/Admin can use **Staff Roles & Document Types** (retaining the
	`/DocumentTypes` route) to select a staff job role, check its required document
	types, and save. Saving replaces only that role's requirements; an empty
	selection clears them. **Add staff role** persists a new job role and selects
	it in the dropdown. **Add document type** creates a new unchecked option,
	including its literal `TextIdentifier`. Editing a document type's name,
	identifier or field mappings preserves its existing role assignments. Role selections persist in
	`StaffRoleDocumentTypes` and immediately affect readiness and required-document
	lists, including registration lookups. This manages staff job roles, not the
	Admin-only office **Access Editor** page. New role-manager operations
	reuse the staff-role association tables; deploy both the Function and frontend.
	Processing matches identifiers case-insensitively with normalized
	whitespace. Missing or ambiguous matches are flagged for human review;
	matching text never automatically validates a document. Existing types with
	no identifier retain legacy name matching until HR configures them. Changes
	apply to subsequent processing, not already-processed documents.
	The document-type editor also provides six optional source labels, stored as
	nullable `NVARCHAR(128)` columns on `DocumentTypes`: `StartDateLabel`,
	`ExpiryDateLabel`, `DocumentNumberLabel`, `ExtractedNameLabel`, `EmailLabel`
	and `PhoneLabel`. For example, map `From` to StartDate and `To` to ExpiryDate,
	or `Start` and `End`; map `Registration ID`, `Participant`, `Personal email`
	and `Mobile` to the remaining fields. Enter the printed label, not the value
	or a regular expression. Labels match literally, ignoring case and whitespace.
	PDF line boundaries are preserved; labels and values can be on adjacent lines.
	The worker identifies the type first, then applies its labels. An explicit
	mapping never falls back to unrelated text when its value is absent or invalid.
	Blank mappings keep the existing defaults: `Start Date`, `End Date`,
	`Document Number` / `Certificate Number`, `Name`, the first email address,
	and `Phone` / `Phone Number` / `Tel` (then phone-shaped text).
	Dates retain invariant-culture parsing; ISO `yyyy-MM-dd` avoids numeric date
	ambiguity. Extracted dates are stored in UTC. Mappings affect future processing
	only and never bypass manual validation or type-mismatch review.
	Publish the database first to add nullable `DocumentTypes.TextIdentifier`
	(`NVARCHAR(256)`) and the six label columns, then deploy the Function and frontend. No data backfill is
	required. Subsequent database publishes preserve HR-managed types and role
	requirements; defaults are seeded only into an empty document-type catalog.
- Foreman can view staff/documents, validate documents and issue all four link
	types. Backend permissions enforce these boundaries, not just hidden buttons.
- Users have one application role with configurable role responsibilities.
	Admin uses email as their username. Disabling an account or changing its
	credentials revokes sessions. HR cannot grant or modify Admin accounts.
- Admin can use **Access Editor** to enable or disable the supported
	permissions for existing roles. Changes are audited and apply to signed-in
	users on their next request. Disabled permissions remain stored with
	`IsEnabled = 0`, so publishing reference data does not silently re-enable them.
	Role management itself is reserved for the Admin role, not a grantable
	responsibility; Admin retains that page even with all optional permissions
	disabled. Account details, MFA settings and sign-out remain available without
	`Staff.Read`. Staff editing, document editing/validation, links and terms require
	`Staff.Read`; invalid combinations are rejected. File uploads additionally need
	both `Documents.Write` and `Links.Write`. The page edits grants, not role names
	or new permission definitions. This feature needs frontend and Function
	deployment only; it reuses the existing Roles and Responsibilities tables.
- MFA is optional for every role and disabled by default. Codes expire after
	five minutes, permit five attempts and are rate-limited. Five password failures
	lock sign-in for fifteen minutes.
- Registration links are unbound and consumed atomically with successful
	registration. Multiple registration links use purpose `register-many` and open
	`/RegisterMultiple`. HR, Foreman and Admin can issue them without selecting
	existing staff. Add/remove entries before choosing **Register all**; a batch
	contains 1-25 people and either all are saved or none are. Validation failures
	preserve the entries and leave the link available for correction; a successful
	batch consumes it once. Single registration links continue to work separately.
	Upload links can be generic or worker-specific. Reservations are
	bound to a content hash; retries cannot replace another file. Successful
	uploads consume the link. Previously issued stateless links no longer work.
- Terms links bind a worker and edition. Published text is immutable, with
	English/Polish/Ukrainian versions. One language acceptance satisfies an edition.
	**Terms** opens in read-only browsing mode: choose a terms document,
	then a version/language to view its exact published text. **Add a new version**
	opens a separate draft prefilled from the latest translations; publishing
	creates a new version rather than editing history. **Add terms document**
	starts a separate document. Cancel leaves published terms unchanged.
	Publishing updates existing assignments and requires fresh acceptance; issue
	new terms links. Acceptance records the version actually displayed, in UTC.
	A bearer link is not independent identity proof or a qualified e-signature.
- **Terms** provides **Required for staff roles** checkboxes for each terms document.
	Roles and terms documents are many-to-many through `StaffRoleTermsDocuments`.
	A role can require several terms documents, and one document can be required
	by several roles without duplicating its text, versions or acceptance history.
	The composite primary key prevents duplicate role/document mappings. Unchecking
	all roles removes only the role mappings, not individual assignments or history.
	Saving roles does not publish a new edition. Saves require `Terms.Write`,
	are audited, and reject stale `expectedRevision` values with HTTP 409.
	The API requires an explicit `staffRoleIds` array; use `[]` to clear all roles.
	Publish the database first, then deploy the Function and frontend together.
	If the earlier single-role `TermsDocuments.StaffRoleId` column was deployed,
	stop the old application and run
	[TestDatabase/Scripts/MigrateStaffRoleTerms.sql](TestDatabase/Scripts/MigrateStaffRoleTerms.sql)
	before publishing the updated SQL project. It transactionally copies the old
	assignments into the junction table, then removes the obsolete column/FK/index.
	It can be rerun safely and preserves document IDs, versions, individual
	assignments and acceptance records. Do not bypass DACPAC data-loss protection
	to drop the old column before this migration. The standalone setup includes
	the junction table for new databases and must not be rerun on existing ones.
- Readiness requires a current, human-validated document for each job
	requirement and acceptance of the latest published edition of every terms
	document required by the staff member's current role or individually assigned
	to them. A required document with no published version blocks readiness.
	Role requirements apply immediately, including to existing staff and new
	registrations before any terms link is issued. Acceptance in any language of
	that same edition qualifies; acceptance of an older edition does not. Publishing
	a new edition immediately removes readiness until it is accepted. Use Staff
	Links to issue a terms link for each outstanding document. Dates are inclusive
	UTC calendar dates; missing expiry means no expiry. Metadata or association
	edits require renewed document validation.
- Staff Member shows a green **Ready for work** or red **Not Ready** banner
	with blocking reasons. Agreements lists missing required terms and editions,
	including unpublished requirements, from the same readiness calculation.
	Role-required and individually assigned terms are combined without duplicates;
	acceptance history remains visible. Deploy the Function and frontend together
	for the structured missing-terms response; no database change is required.
- Removal archives staff/documents or disables office accounts, preserving
	history. Hard deletion and jurisdiction-specific retention remain policy
	decisions.
- Staff IDs use `P` for Permanent and `C` for Contract/Contractor, followed by
	the database-generated staff number. Publish the SQL project to update an
	existing Contract/External staff-type prefix from `E` to `C`; new standalone
	databases seed `C` directly. No application change is required. Existing
	E-prefixed staff IDs are not bulk-renumbered; saving with the Contract staff
	type regenerates the ID with `C` and preserves the staff number.
- Worker pages use local en/pl/uk resources. Legal text comes from approved
	stored translations, not machine translation.

## Standalone SQL Setup

For a new database without a DACPAC, open
[TestDatabase/Scripts/SetupDatabase.sql](TestDatabase/Scripts/SetupDatabase.sql)
in SSMS or Azure Data Studio, select your empty application database, and execute
the entire script. It creates all application tables, the staff-number sequence, indexes,
constraints and reference data in one transaction. SQLCMD mode is not required.

The database must already exist. This is initial setup, not an upgrade script:
it refuses databases with existing tables or sequences and never drops data.
Database users, managed-identity grants and Azure settings remain deployment
configuration. No default account/password is seeded; create the first Admin
using the command below. Keep this standalone script aligned with the SQL project
when changing the schema.

## Initial Admin

After publishing an empty database, set `ConnectionStrings__DefaultConnection`
in your terminal environment to the intended SQL connection, then run:

```powershell
dotnet run --project TestFunction -- --create-admin
```

This command refuses a database containing accounts. Enter the Admin email and
password directly in the terminal; password input is not echoed. Only an ASP.NET
Core password hash is stored. It does not create infrastructure or start the
Function host. Existing passwords must be valid ASP.NET Core password hashes;
no default account or password is seeded.

## Development Users

For local testing, after creating the database schema, open
[TestDatabase/Scripts/SeedUsers.sql](TestDatabase/Scripts/SeedUsers.sql) in SSMS or
Azure Data Studio, select your development database, and execute the whole script.
SQLCMD mode is not required. This is optional and does not run during deployment.

| Email | Role |
| --- | --- |
| `admin@example.test` | Admin |
| `hr@example.test` | HR |
| `foreman@example.test` | Foreman |

All three accounts use the password `LocalDemo!2026`, are enabled, and have MFA
disabled. Only an ASP.NET Core password hash is stored. Rerunning the script
skips existing emails without changing their password, role, or account settings.
These are office sign-in accounts, not staff records.

**Development/test only: never run this script in production.** Use this instead
of `--create-admin` for a demo database; that command refuses to run once any
accounts exist. For real accounts, use Initial Admin and account management.

## Deployment Gates

Use Visual Studio publishing and Azure configuration, not provisioning scripts.
`TestFrontend/appsettings.Azure.example.json` and
`TestFunction/azure.settings.example.json` contain environment-specific
placeholders. These are reference templates, not automatically loaded settings.
The Function template uses flat Azure setting names. Replace placeholders on the
actual resources before deployment. Existing settings have not been overwritten.
Empty identity client IDs mean system-assigned; for user-assigned identities,
also configure the relevant host/trigger `clientId`.

1. Publish SQL after reviewing the deployment report and taking a backup. Set
	 SQLCMD variable `LegacyUploadsContainer` to the old container (default
	 `uploads`). Legacy role text is retained and mapped to job roles. Legacy blob
	 rows receive a container locator; no blobs are renamed. Resolve duplicate
	 legacy blob references before applying the unique container/blob index.
	 Test upgrades against a copy of production data.
2. Enable managed identities. Frontend needs Storage Blob Data Reader and the
	 API application permission. Function needs SQL database permissions and the
	 Storage data-plane roles for uploads, triggers and blob reads. Blob
	 triggers also need queue access. Configure host storage identity separately.
	 Remove storage keys and frontend SQL access. Control-plane Contributor is
	 not equivalent to a SQL database grant.
3. Configure ACS endpoint/verified sender and the appropriate email-sending
	 permission for the Function identity. Configure Vision endpoint and Cognitive
	 Services data permission. Clients use identity, not keys. Verify each service
	 connection in Azure.
4. External malware scanning is not required or performed. Defender for Storage
	 and scanner tags are not used. Upload type/size limits, basic PDF/image checks,
	 access permissions and manual document approval remain in place; these are
	 **not antivirus protection**. Downloaded files may be unscanned. Existing
	 quarantined/archived documents remain blocked and cannot be cleared through
	 approval, rejection or metadata edits. Upload a replacement for an unsafe file.
	 `ScanPassed` is retained as historical data and is neither required nor set by
	 the current processing path. New uploads are `AwaitingProcessing`; legacy
	 `AwaitingScan` records are displayed as awaiting processing and retried by the
	 five-minute recovery worker when processing is incomplete. File checks and
	 extraction must complete before manual approval; parse failures can then be
	 corrected and reviewed manually. Rejection does not bypass processing.
	 **Uploaded Files** lists blobs independently of the Documents catalog. Existing
	 untracked blobs can be downloaded only from `UploadsContainer`, with no scan-tag
	 request. Older document rows without a container resolve only against that
	 container. Keep frontend `AzureStorage:ContainerName` aligned with Function
	 `UploadsContainer` and point both apps at the same storage account.
	 Deploy both Function and frontend for these changes; no schema migration or
	 bulk update of scan flags is needed. Azure services are not enabled or disabled
	 by this code; separately turn off any paid scanning plan you previously enabled.
5. Set `UploadsContainer=uploads` for both new uploads and the blob trigger.
	 Blob names use the UTC path `YYYY/MM/`, optional `SID{Staff.Id}_` and
	 `DTID_{DocumentTypeId}_`, a unique upload ID, and a sanitized filename.
	 No container setting change is needed at year rollover. Existing stored
	 container/blob locators are preserved; legacy `MM/` paths still parse.
	 The recovery timer handles persisted pending uploads in any container,
	 including events arriving before upload finalization. It is not a full
	 account scan. Out-of-band uploads to other containers require explicit ingestion.
6. Enable SQL TDE, Storage encryption, secure transfer/TLS and private containers;
	 enforce HTTPS-only on both apps. Configure a SQL Entra administrator and a
	 separate administrative database principal/group for direct access. Restrict
	 networks and grants. Verify these settings in Azure; source code does not
	 establish encryption or access-policy compliance.
7. Use persistent, encrypted ASP.NET Core Data Protection keys shared by frontend
	 instances. Configure App Service key persistence/protection for the selected
	 OS. Exclude query-string capability tokens from platform logs and telemetry.
	 Pages send no-referrer/no-store headers. Apply retention to audit/email records.
8. Enable background functions only after dependencies are configured. Use a
	 supported .NET 10 isolated Functions host and verify PDF native libraries on
	 the hosting OS. Limits: 20 MB per upload, 50 bounded-size PDF pages, 40 million
	 image pixels. File-check/extraction issues appear in the review queue. OCR
	 failures can be manually reviewed once processing finishes, unless quarantined.

Expiry emails run at 05:00 UTC to enabled HR accounts. A validated replacement
must cover the same worker/type without a gap after the old certificate. Daily
dispatch records suppress completed sends on retries. A crash after delivery but
before recording success can still cause duplicate email; monitor delivery and
failures rather than assuming exactly-once delivery.

## Additional Verification

Tests cover MFA replay/lockout, Foreman restrictions, readiness, link lifecycle,
upload naming, safety gates and replacement suppression. `SqlWorkflowTests` run
only when `HR_TEST_SQL` points to a disposable database whose name starts with
`HrImplementationVerification_`, published from this SQL project. They test
concurrent registration, row-version conflicts and immutable translated terms;
otherwise they are explicitly skipped.

```powershell
dotnet test TestFunction.Tests/TestFunction.Tests.csproj -c Release
```

Release output avoids conflicts with an existing Debug server. Deployed identity,
blob events, OCR and ACS delivery require end-to-end tests
with configured Azure resources; placeholders do not verify those integrations.