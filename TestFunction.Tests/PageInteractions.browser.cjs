const assert = require('node:assert/strict');
const http = require('node:http');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { createHash } = require('node:crypto');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

async function verify() {
    const calls = [];
    const permissions = ['Staff.Read', 'Staff.Write', 'Documents.Write', 'Documents.Validate', 'Links.Write', 'Users.Write', 'Terms.Write'];
    const account = { id: 1, email: 'admin@example.invalid', roleId: 1, role: 'Admin', isEnabled: true, mfaEnabled: false, permissions };
    const roles = [{ id: 1, name: 'Driver & Operator' }, { id: 2, name: 'Carpenter' }];
    const types = [{ id: 4, name: 'Induction', textIdentifier: 'INDUCTED', staffRoleIds: [1] }, { id: 5, name: 'Safety', textIdentifier: 'SAFE', staffRoleIds: [2] }];
    const officeRoles = [{ id: 1, name: 'Admin', responsibilities: permissions }, { id: 2, name: 'HR', responsibilities: ['Staff.Read'] }];
    const terms = [{ id: 1, title: 'Safety & Induction' }, { id: 2, title: 'Site rules' }, { id: 3, title: 'Empty document' }];
    const versions = documentId => documentId === 3 ? [] : [
        { id: documentId * 10 + 1, termsDocument: terms[documentId - 1], content: 'English <script>not executable</script>', language: 'en', version: 2, isActive: true, createdAt: '2026-09-01T00:00:00Z' },
        { id: documentId * 10 + 2, termsDocument: terms[documentId - 1], content: 'Polish edition', language: 'pl', version: 1, isActive: false, createdAt: '2026-08-01T00:00:00Z' }
    ];
    const staff = [
        { id: 1, staffId: 'PER1', staffNumber: 1, firstName: 'Anna', lastName: 'Alpha', email: 'z@example.invalid', staffTypeId: 1, staffType: { id: 1, name: 'Permanent' }, staffRoleId: 1, staffRole: roles[0], registeredAt: '2026-09-01T00:00:00Z' },
        { id: 2, staffId: 'PER2', staffNumber: 2, firstName: 'Ben', lastName: 'Beta', email: 'a@example.invalid', staffTypeId: 1, staffType: { id: 1, name: 'Permanent' }, staffRoleId: 2, staffRole: roles[1], registeredAt: '2026-09-02T00:00:00Z' }
    ];
    const documents = [
        { id: 1, name: 'Safety certificate', type: { id: 4, name: 'Induction' }, documentTypeId: 4, status: 'Validated', timestamp: '2026-09-02T00:00:00Z', staffId: 1, staff: staff[0], documentNumber: 'SAFE1', processingCompletedAt: '2026-09-02T00:00:00Z' },
        { id: 2, name: 'Licence', type: { id: 5, name: 'Safety' }, documentTypeId: 5, status: 'AwaitingProcessing', timestamp: '2026-09-01T00:00:00Z', staffId: 2, staff: staff[1], documentNumber: 'LIC2' }
    ];
    let revoked = false;
    let failTerms = false;
    const revision = values => createHash('sha256').update(JSON.stringify([...new Set(values)].sort((left, right) => typeof left === 'number' ? left - right : left < right ? -1 : left > right ? 1 : 0))).digest('hex').toUpperCase();
    const backend = http.createServer(async (request, response) => {
        let body = '';
        for await (const chunk of request) body += chunk;
        const url = new URL(request.url, 'http://localhost');
        const route = url.pathname.replace(/^\/api/, '');
        const payload = body ? JSON.parse(body) : null;
        calls.push({ method: request.method, route, payload });
        response.setHeader('Content-Type', 'application/json');
        function json(value, status = 200) { response.statusCode = status; response.end(JSON.stringify(value)); }
        if (route === '/accounts/login') return json({ token: 'fixture-session', requiresMfa: false, expiresAt: new Date(Date.now() + 3600000).toISOString(), user: account });
        if (route === '/accounts/me') return json(revoked ? {} : account, revoked ? 401 : 200);
        if (request.method === 'PUT' && /^\/staff-roles\/\d+\/document-types$/.test(route)) {
            const roleId = Number(route.split('/')[2]);
            const current = types.filter(type => type.staffRoleIds.includes(roleId)).map(type => type.id);
            if (payload.expectedRevision !== revision(current)) {
                response.setHeader('Content-Type', 'application/problem+json');
                return json({ status: 409, detail: 'Requirements changed. Reload before saving.' }, 409);
            }
            for (const type of types) type.staffRoleIds = [...type.staffRoleIds.filter(id => id !== roleId), ...(payload.documentTypeIds.includes(type.id) ? [roleId] : [])];
            return json({});
        }
        if (request.method === 'PUT' && /^\/roles\/\d+\/responsibilities$/.test(route)) {
            const role = officeRoles.find(role => role.id === Number(route.split('/')[2]));
            if (payload.expectedRevision !== revision(role.responsibilities)) {
                response.setHeader('Content-Type', 'application/problem+json');
                return json({ status: 409, detail: 'Responsibilities changed. Reload before saving.' }, 409);
            }
            role.responsibilities = payload.responsibilities;
            return json({});
        }
        if (request.method === 'PUT') return json({}, 200);
        if (route === '/document-types') return json({ documentTypes: types, staffRoles: roles });
        if (route === '/roles') return json({ roles: officeRoles, responsibilities: permissions });
        if (route === '/terms') return json(terms);
        if (/^\/terms\/\d+\/versions$/.test(route)) return json(failTerms ? {} : versions(Number(route.split('/')[2])), failTerms ? 500 : 200);
        if (route === '/staff') return json({ staff: staff.filter(worker => !url.searchParams.get('filter') || worker.firstName.includes(url.searchParams.get('filter'))), lastTermsAcceptedAt: {}, readiness: {} });
        if (route === '/documents') return json({ documents, staff, documentTypes: types });
        if (route === '/users') return json({ users: [account, { id: 2, email: 'hr@example.invalid', roleId: 2, role: 'HR', isEnabled: false, mfaEnabled: true, permissions: ['Staff.Read'] }], roles: officeRoles });
        if (route === '/public/resolve') return json({ purpose: 'register-many', lookups: { staffRoles: roles, staffTypes: [{ id: 1, name: 'Permanent' }], documentTypes: types, roleRequiredDocuments: { 1: ['Induction'], 2: ['Safety'] } } });
        if (route === '/public/register-many') return json({ staff: payload.staff.map((worker, index) => ({ ...staff[0], ...worker, id: index + 1 })) });
        json({}, 404);
    });
    let frontend;
    let browser;
    const storage = http.createServer((request, response) => {
        const url = new URL(request.url, 'http://localhost');
        const prefix = url.searchParams.get('prefix') || '';
        response.setHeader('Content-Type', 'application/xml');
        response.end(`<?xml version="1.0" encoding="utf-8"?><EnumerationResults ServiceEndpoint="http://127.0.0.1:10000/devstoreaccount1/" ContainerName="uploads"><Prefix>${prefix}</Prefix><Delimiter>/</Delimiter><Blobs>${prefix ? '' : '<BlobPrefix><Name>2026/</Name></BlobPrefix>'}</Blobs><NextMarker /></EnumerationResults>`);
    });
    try {
        storage.listen(10000, '127.0.0.1');
        await once(storage, 'listening');
        backend.listen(0, '127.0.0.1');
        await once(backend, 'listening');
        const root = path.resolve(__dirname, '../TestFrontend');
        const configuration = process.env.FRONTEND_TEST_CONFIGURATION || 'PageInteractionsVerification';
        frontend = spawn('dotnet', [path.join(root, `bin/${configuration}/net10.0/TestFrontend.dll`)], {
            cwd: root,
            env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'https://127.0.0.1:0', FunctionApi__BaseUrl: `http://127.0.0.1:${backend.address().port}/api/`, FunctionApi__AllowUnauthenticatedLocalRequests: 'true', AzureStorage__ConnectionString: 'UseDevelopmentStorage=true', APPLICATIONINSIGHTS_CONNECTION_STRING: '' },
            stdio: ['ignore', 'pipe', 'pipe']
        });
        const baseUrl = await new Promise((resolve, reject) => {
            const deadline = setTimeout(() => reject(new Error('Frontend startup timed out')), 30000);
            frontend.once('error', error => { clearTimeout(deadline); reject(error); });
            frontend.once('exit', code => { clearTimeout(deadline); reject(new Error(`Frontend exited: ${code}`)); });
            frontend.stderr.on('data', () => {});
            frontend.stdout.on('data', chunk => {
                const match = chunk.toString().match(/Now listening on: (https:\/\/127\.0\.0\.1:\d+)/);
                if (match) { clearTimeout(deadline); resolve(match[1]); }
            });
        });
        browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL || 'msedge', headless: true });
        const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 1000 } });
        const page = await context.newPage();
        page.setDefaultTimeout(10000);
        const errors = [];
        const requests = [];
        page.on('dialog', dialog => {
            if (dialog.type() === 'beforeunload') dialog.accept();
        });
        page.on('pageerror', error => errors.push(error.message));
        page.on('request', request => requests.push(request.url()));
        async function open(route) {
            await page.goto(baseUrl + route, { waitUntil: 'networkidle' });
            await page.evaluate(() => window.interactionMarker = 'same-document');
            calls.length = 0;
            requests.length = 0;
        }
        async function assertLocal() {
            assert.equal(await page.evaluate(() => window.interactionMarker), 'same-document');
            assert.equal(requests.length, 0, 'Local interaction must not make HTTP requests');
            assert.equal(calls.length, 0, 'Local interaction must not call the API');
        }
        await open('/Login');
        await page.locator('#Email').fill(account.email);
        await page.locator('#password').fill('fixture-password-only');
        await Promise.all([page.waitForURL(baseUrl + '/'), page.getByRole('button', { name: 'Sign in', exact: true }).click()]);

        await open('/DocumentTypes');
        await page.locator('#type-5').check();
        await page.locator('#RoleId').selectOption('2');
        assert.equal(await page.locator('#type-4').isChecked(), false);
        assert.equal(await page.locator('#type-5').isChecked(), true);
        assert.match(await page.locator('[data-role-requirements]').getAttribute('action'), /RoleId=2/i);
        await page.locator('#RoleId').selectOption('1');
        assert.equal(await page.locator('#type-5').isChecked(), true, 'Role draft must survive switching away and back');
        await page.locator('#RoleId').selectOption('2');
        await assertLocal();
        page.once('dialog', dialog => dialog.dismiss());
        await page.getByRole('button', { name: 'Save requirements' }).click();
        await assertLocal();
        await page.locator('#RoleId').selectOption('1');
        assert.equal(await page.locator('#type-5').isChecked(), true);
        await page.locator('#RoleId').selectOption('2');
        page.once('dialog', dialog => dialog.accept());
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save requirements' }).click()]);
        assert.deepEqual(calls.find(call => call.method === 'PUT'), { method: 'PUT', route: '/staff-roles/2/document-types', payload: { documentTypeIds: [5], expectedRevision: revision([5]) } });
        await open('/DocumentTypes?RoleId=1');
        const oldRevision = await page.locator('[data-role-revision]').inputValue();
        types[1].staffRoleIds.push(1);
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save requirements' }).click()]);
        await page.getByText('Requirements changed. Reload before saving.').waitFor();
        assert.equal(await page.locator('[data-role-revision]').inputValue(), oldRevision);
        assert.equal(await page.locator('#type-5').isChecked(), false);
        await page.locator('#RoleId').selectOption('2');
        await page.locator('#RoleId').selectOption('1');
        assert.equal(await page.locator('[data-role-revision]').inputValue(), oldRevision);
        types[1].staffRoleIds = [2];
        console.log('PASS: local role drafts warn before loss, save the selected role, and retain stale revisions on conflict.');

        await open('/Roles');
        await page.locator('#role-choice').selectOption('2');
        assert.equal(await page.locator('input[name="Id"]').inputValue(), '2');
        assert.equal(await page.locator('input[value="Documents.Write"]').isChecked(), false);
        await assertLocal();
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save responsibilities' }).click()]);
        assert.equal(calls.find(call => call.method === 'PUT').route, '/roles/2/responsibilities');
        console.log('PASS: office-role selection is local and saves to the selected role.');

        await open('/ManageTerms');
        await page.locator('#VersionId').selectOption('12');
        assert.equal(await page.locator('[data-terms-version]:visible [lang]').textContent(), 'Polish edition');
        await assertLocal();
        await page.locator('#DocumentId').selectOption('2');
        await page.locator('[data-terms-version="21"]:visible').waitFor();
        assert.equal(await page.evaluate(() => window.interactionMarker), undefined);
        assert.equal(await page.locator('[data-terms-version="21"] [lang]').textContent(), 'English <script>not executable</script>');
        assert.equal(await page.locator('[data-terms-version] script').count(), 0);
        await page.goBack();
        await page.locator('[data-terms-version="12"]:visible').waitFor();
        await page.goForward();
        await page.locator('[data-terms-version="21"]:visible').waitFor();
        await page.locator('#DocumentId').selectOption('3');
        await page.getByText('No published versions for this document.').waitFor();
        await page.locator('#DocumentId').selectOption('1');
        await page.locator('[data-terms-version="11"]:visible').waitFor();
        failTerms = true;
        await Promise.all([page.waitForNavigation(), page.locator('#DocumentId').selectOption('2')]);
        assert.equal(await page.locator('[data-terms-version]').count(), 0, 'A failed navigation must not show old terms under the new selection');
        failTerms = false;
        console.log('PASS: terms versions stay local; document navigation/history and failed loads cannot mix selections with stale content.');

        await open('/Staff');
        await page.locator('#Filter').fill('Anna');
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Apply', exact: true }).click()]);
        assert.equal(await page.locator('tbody tr').count(), 1);
        assert(calls.some(call => call.route === '/staff'));
        await page.goBack();
        assert.equal(await page.locator('tbody tr').count(), 2);
        await page.goForward();
        assert.equal(await page.locator('tbody tr').count(), 1);
        await page.locator('#Filter').fill('');
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Apply', exact: true }).click()]);
        assert.equal(await page.locator('tbody tr').count(), 2);
        console.log('PASS: server-backed filters retrieve fresh data and preserve Back/Forward behavior.');

        await open('/Documents');
        await page.locator('[name="name"]').first().fill('Unsaved certificate');
        await page.locator('#Filter').fill('Anna');
        page.once('dialog', dialog => dialog.dismiss());
        await page.getByRole('button', { name: 'Apply', exact: true }).click();
        await assertLocal();
        assert.equal(await page.locator('[name="name"]').first().inputValue(), 'Unsaved certificate');
        page.once('dialog', dialog => dialog.accept());
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Apply', exact: true }).click()]);
        console.log('PASS: filters cannot silently replace unsaved document forms.');

        await open('/Files');
        await Promise.all([page.waitForNavigation(), page.getByRole('link', { name: '2026', exact: true }).click()]);
        assert.match(page.url(), /prefix=2026/i);
        await page.goBack();
        await page.getByRole('link', { name: '2026', exact: true }).waitFor();
        await page.goForward();
        await page.locator('input[type="file"]').setInputFiles({ name: 'draft.pdf', mimeType: 'application/pdf', buffer: Buffer.from('fixture') });
        const unloadHandler = dialog => { if (dialog.type() === 'beforeunload') dialog.accept(); };
        page.removeAllListeners('dialog');
        page.once('dialog', dialog => dialog.dismiss());
        await page.getByRole('link', { name: 'Root', exact: true }).click();
        assert.match(page.url(), /prefix=2026/i);
        assert.equal(await page.locator('input[type="file"]').evaluate(input => input.files[0].name), 'draft.pdf');
        page.on('dialog', unloadHandler);
        await open('/Files');
        console.log('PASS: folders use browser history; cancelling navigation retains a selected upload.');

        await open('/Users');
        await page.locator('a[data-user]').filter({ hasText: 'Edit' }).nth(1).click();
        assert.equal(await page.locator('#Input_Email').inputValue(), 'hr@example.invalid');
        assert.equal(await page.locator('#Input_IsEnabled').isChecked(), false);
        await page.locator('#Input_Email').fill('changed@example.invalid');
        page.once('dialog', dialog => dialog.dismiss());
        await page.locator('a[data-user]').filter({ hasText: 'New user' }).click();
        assert.equal(await page.locator('#Input_Email').inputValue(), 'changed@example.invalid');
        page.once('dialog', dialog => dialog.accept());
        await page.locator('a[data-user]').filter({ hasText: 'New user' }).click();
        assert.equal(await page.locator('#Id').inputValue(), '');
        assert.equal(await page.locator('#Input_Password').inputValue(), '');
        await assertLocal();
        console.log('PASS: user selection is local and protects unsaved edits.');

        for (const language of ['en', 'pl', 'uk']) {
            await open(`/RegisterMultiple?token=fixture&lang=${language}`);
            await page.locator('[name="Staff[0].FirstName"]').fill('First');
            await page.locator('[data-add-staff]').click();
            await page.locator('[name="Staff[1].FirstName"]').fill('Second');
            await page.locator('[data-add-staff]').click();
            await page.locator('[name="Staff[2].FirstName"]').fill('Third');
            await page.locator('[data-staff-entry]').nth(1).locator('[data-remove-staff]').click();
            assert.equal(await page.locator('[name="Staff[1].FirstName"]').inputValue(), 'Third');
            await page.locator('[name="Staff[1].StaffRoleId"]').selectOption('2');
            assert.equal(await page.locator('[data-staff-entry]').nth(1).locator('[data-role-id="2"]').isVisible(), true);
            const ids = await page.locator('[data-staff-entry] [id]').evaluateAll(elements => elements.map(element => element.id));
            assert.equal(new Set(ids).size, ids.length);
            await assertLocal();
        }
        await page.locator('[data-staff-entry]').nth(1).locator('[data-remove-staff]').click();
        for (let count = 1; count < 25; count++) await page.locator('[data-add-staff]').click();
        assert.equal(await page.locator('[data-add-staff]').isDisabled(), true);
        assert.equal(await page.locator('[data-staff-entry]').count(), 25);
        await open('/RegisterMultiple?token=fixture');
        await page.locator('[data-add-staff]').click();
        for (let index = 0; index < 2; index++) {
            await page.locator(`[name="Staff[${index}].FirstName"]`).fill(`Person${index}`);
            await page.locator(`[name="Staff[${index}].LastName"]`).fill('Test');
            await page.locator(`[name="Staff[${index}].Email"]`).fill(`person${index}@example.invalid`);
            await page.locator(`[name="Staff[${index}].StaffRoleId"]`).selectOption('1');
            await page.locator(`[name="Staff[${index}].StaffTypeId"]`).selectOption('1');
        }
        await Promise.all([page.waitForNavigation(), page.locator('[data-multiple-registration] button:not([formnovalidate])').click()]);
        assert.equal(calls.find(call => call.route === '/public/register-many').payload.staff.length, 2);
        console.log('PASS: localized Add/Remove preserves inputs, unique indexes, role hints, 25-person limit and valid final submission.');

        const noScript = await browser.newContext({ ignoreHTTPSErrors: true, javaScriptEnabled: false, storageState: await context.storageState() });
        const fallback = await noScript.newPage();
        await fallback.goto(baseUrl + '/DocumentTypes');
        await fallback.locator('#RoleId').selectOption('2');
        await Promise.all([fallback.waitForNavigation(), fallback.getByRole('button', { name: 'Open role', exact: true }).click()]);
        assert.equal(await fallback.locator('#type-5').isChecked(), true);
        await fallback.goto(baseUrl + '/RegisterMultiple?token=fixture');
        await Promise.all([fallback.waitForNavigation(), fallback.locator('[data-add-staff]').click()]);
        assert.equal(await fallback.locator('[data-staff-entry]').count(), 2);
        await noScript.close();
        console.log('PASS: no-JavaScript role selection and registration Add fallback remain functional.');

        await page.setViewportSize({ width: 390, height: 844 });
        for (const route of ['/DocumentTypes', '/Roles', '/ManageTerms', '/RegisterMultiple?token=fixture&lang=uk', '/Users']) {
            await open(route);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), `Mobile overflow on ${route}`);
        }
        revoked = true;
        await page.goto(baseUrl + '/DocumentTypes');
        assert.match(page.url(), /\/Login/);
        assert.deepEqual(errors, []);
        console.log('PASS: mobile layouts fit; revoked sessions remain blocked; no browser JavaScript errors.');
    } finally {
        if (browser) await browser.close();
        if (frontend && frontend.exitCode === null) {
            const stopped = once(frontend, 'exit');
            frontend.kill();
            await stopped;
        }
        backend.closeAllConnections();
        await new Promise(resolve => backend.close(resolve));
        storage.closeAllConnections();
        await new Promise(resolve => storage.close(resolve));
    }
}

verify().catch(error => { console.error(error); process.exitCode = 1; });