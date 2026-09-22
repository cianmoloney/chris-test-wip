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
    let failMappings = false;
    let failUpload = false;
    let failUploadTypes = false;
    const revision = values => createHash('sha256').update(JSON.stringify([...new Set(values)].sort((left, right) => typeof left === 'number' ? left - right : left < right ? -1 : left > right ? 1 : 0))).digest('hex').toUpperCase();
    const backend = http.createServer(async (request, response) => {
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        const body = Buffer.concat(chunks);
        const url = new URL(request.url, 'http://localhost');
        const route = url.pathname.replace(/^\/api/, '');
        const payload = !body.length ? null : request.headers['content-type']?.startsWith('multipart/form-data')
            ? Object.fromEntries(await new Response(body, { headers: { 'Content-Type': request.headers['content-type'] } }).formData())
            : JSON.parse(body.toString());
        calls.push({ method: request.method, route, payload });
        response.setHeader('Content-Type', 'application/json');
        function json(value, status = 200) { response.statusCode = status; response.end(JSON.stringify(value)); }
        if (route === '/accounts/login') return json({ token: 'fixture-session', requiresMfa: false, expiresAt: new Date(Date.now() + 3600000).toISOString(), user: account });
        if (route === '/accounts/logout') return json({});
        if (route === '/accounts/me') return json(revoked ? {} : account, revoked ? 401 : 200);
        if (route === '/links' && request.method === 'POST') return json({ token: 'fixture-upload-token', purpose: 'upload', expiresAt: new Date(Date.now() + 3600000).toISOString() });
        if (route === '/public/upload') {
            if (failUpload) {
                response.setHeader('Content-Type', 'application/problem+json');
                return json({ status: 400, detail: 'Upload rejected by fixture.' }, 400);
            }
            return json({ containerName: 'uploads', blobName: '2026/09/fixture.pdf' });
        }
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
        if (request.method === 'PUT' && /^\/document-types\/\d+$/.test(route)) {
            if (failMappings) {
                response.setHeader('Content-Type', 'application/problem+json');
                return json({ status: 400, errors: { StartDateLabel: ['Mapping rejected by fixture.'] } }, 400);
            }
            Object.assign(types.find(type => type.id === Number(route.split('/')[2])), payload);
            return json({});
        }
        if (request.method === 'POST' && route === '/document-types') {
            const created = { id: 6, ...payload };
            types.push(created);
            return json(created, 201);
        }
        if (request.method === 'PUT' && /^\/terms\/\d+\/staff-roles$/.test(route)) {
            const termsDocument = terms.find(document => document.id === Number(route.split('/')[2]));
            const currentRevision = revision(termsDocument.staffRoleIds || []);
            if (payload.expectedRevision !== currentRevision) {
                response.setHeader('Content-Type', 'application/problem+json');
                return json({ status: 409, detail: 'Required staff roles changed. Reload before saving.' }, 409);
            }
            termsDocument.staffRoleIds = payload.staffRoleIds;
            return json({});
        }
        if (request.method === 'PUT') return json({}, 200);
        if (route === '/lookups') return json({ staffRoles: roles, staffTypes: [], documentTypes: types, roleRequiredDocuments: {} });
        if (route === '/document-types') return json(failUploadTypes ? {} : { documentTypes: types, staffRoles: roles }, failUploadTypes ? 503 : 200);
        if (route === '/roles') return json({ roles: officeRoles, responsibilities: permissions });
        if (route === '/terms') return json(terms.map(document => ({ ...document, staffRoleIds: document.staffRoleIds || [], roleRevision: revision(document.staffRoleIds || []) })));
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

        const menuLabels = ['Uploaded Files', 'Staff', 'Document Explorer', 'Generate Link', 'Terms', 'Accounts', 'Access Editor'];
        const menuRoutes = ['/Files', '/Staff', '/Documents', '/GenerateLink', '/ManageTerms', '/Users', '/Roles'];
        const navigation = page.getByRole('navigation', { name: 'Main navigation' });
        const primaryLinks = navigation.locator('.navbar-nav a');
        assert.deepEqual(await primaryLinks.allTextContents(), menuLabels);
        assert.deepEqual(await primaryLinks.evaluateAll(links => links.map(link => link.getAttribute('href'))), menuRoutes);
        assert.equal(await navigation.getByRole('link', { name: 'TestFrontend', exact: true }).getAttribute('href'), '/');
        const accountLink = navigation.getByRole('link', { name: 'My account', exact: true });
        assert.equal(await accountLink.getAttribute('href'), '/Account');
        assert.equal(await accountLink.getAttribute('title'), 'My account');
        const accountBounds = await accountLink.boundingBox();
        assert.equal(accountBounds.width, 44, 'Account icon must have a stable 44px click target');
        assert.equal(accountBounds.height, 44, 'Account icon must have a stable 44px click target');
        assert.equal(await accountLink.locator('img').evaluate(image => image.complete && image.naturalWidth > 0), true);
        assert.equal(await accountLink.evaluate(link => link.nextElementSibling.querySelector('button').textContent.trim()), 'Sign out');
        await accountLink.focus();
        assert.equal(await accountLink.evaluate(link => link.matches(':focus-visible')), true);
        await Promise.all([page.waitForURL(baseUrl + '/Account'), page.keyboard.press('Enter')]);
        await page.getByRole('heading', { name: 'Account', exact: true }).waitFor();

        for (const [route, heading] of [['/Roles', 'Access Editor'], ['/Users', 'Accounts'], ['/ManageTerms', 'Terms']]) {
            await open(route);
            await page.getByRole('heading', { name: heading, exact: true }).waitFor();
            assert.equal(await page.title(), `${heading} - TestFrontend`);
        }
        await open('/Staff');
        await Promise.all([page.waitForURL(baseUrl + '/DocumentTypes'), page.getByRole('link', { name: 'Staff Roles & Document Types', exact: true }).click()]);
        await page.getByRole('heading', { name: 'Staff Roles & Document Types', exact: true }).waitFor();

        for (const width of [1440, 1200, 1024, 768, 390, 320]) {
            await page.setViewportSize({ width, height: 900 });
            await open('/');
            const toggle = navigation.getByRole('button', { name: 'Toggle navigation' });
            if (width < 1200) {
                assert.equal(await primaryLinks.first().isVisible(), false);
                await toggle.click();
                await page.waitForFunction(() => document.querySelector('#mainNavigation').classList.contains('show'));
                assert.equal(await toggle.getAttribute('aria-expanded'), 'true');
            } else {
                assert.equal(await toggle.isVisible(), false);
                const tops = await primaryLinks.evaluateAll(links => links.map(link => link.getBoundingClientRect().top));
                assert(tops.every(top => Math.abs(top - tops[0]) < 1), 'Desktop navigation must stay on one row');
            }
            for (const label of menuLabels) assert.equal(await navigation.getByRole('link', { name: label, exact: true }).isVisible(), true);
            assert.equal(await accountLink.isVisible(), true);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), `Navigation overflow at ${width}px`);
            const overlaps = await navigation.locator('a, button').evaluateAll(elements => {
                const boxes = elements.map(element => element.getBoundingClientRect()).filter(box => box.width && box.height);
                return boxes.some((box, index) => boxes.slice(index + 1).some(other =>
                    box.left < other.right - 1 && box.right > other.left + 1 && box.top < other.bottom - 1 && box.bottom > other.top + 1));
            });
            assert.equal(overlaps, false, `Navigation controls overlap at ${width}px`);
            if (process.env.BROWSER_TEST_ARTIFACTS && [1440, 390].includes(width))
                await page.screenshot({ path: path.join(process.env.BROWSER_TEST_ARTIFACTS, `navigation-${width}.png`), fullPage: true });
            if (width < 1200) {
                await toggle.click();
                await page.waitForFunction(() => !document.querySelector('#mainNavigation').classList.contains('collapsing'));
                assert.equal(await primaryLinks.first().isVisible(), false);
            }
        }
        await page.setViewportSize({ width: 1440, height: 1000 });
        account.role = 'HR';
        await open('/Staff');
        assert.deepEqual(await primaryLinks.allTextContents(), menuLabels.slice(0, -1));
        assert.equal(await page.getByRole('link', { name: 'Staff Roles & Document Types', exact: true }).isVisible(), true);
        await open('/Roles');
        assert.equal(await page.getByRole('heading', { name: 'Access Editor', exact: true }).count(), 0, 'Non-Admin must not open Access Editor directly');
        account.role = 'Foreman';
        account.permissions = ['Staff.Read', 'Links.Write'];
        await open('/Staff');
        assert.deepEqual(await primaryLinks.allTextContents(), menuLabels.slice(0, 4));
        assert.equal(await page.getByRole('link', { name: 'Staff Roles & Document Types', exact: true }).count(), 0);
        account.role = 'Admin';
        account.permissions = [];
        await open('/');
        assert.deepEqual(await primaryLinks.allTextContents(), ['Access Editor']);
        assert.equal(await accountLink.isVisible(), true);
        account.permissions = permissions;
        await open('/');
        await Promise.all([page.waitForURL(/\/Login/), navigation.getByRole('button', { name: 'Sign out', exact: true }).click()]);
        assert.equal(await navigation.locator('.navbar-nav a, .account-link, .navbar-toggler').count(), 0);
        assert.deepEqual(errors, []);
        console.log('PASS: navigation order, headings, account icon, logout, permission gates and desktop/tablet/mobile layouts.');
        if (process.env.BROWSER_TEST_SUITE === 'navigation') return;
        await page.locator('#Email').fill(account.email);
        await page.locator('#password').fill('fixture-password-only');
        await Promise.all([page.waitForURL(baseUrl + '/'), page.getByRole('button', { name: 'Sign in', exact: true }).click()]);

        const actionNames = ['Staff Links', 'Document Explorer', 'Staff Viewer', 'Upload File'];
        const actionRoutes = ['/GenerateLink', '/Documents', '/Staff', '/UploadFile'];
        for (const width of [1440, 768, 390, 320]) {
            await page.setViewportSize({ width, height: 900 });
            await open('/');
            await page.getByRole('heading', { name: 'Quick Actions', exact: true }).waitFor();
            const tiles = page.locator('main .quick-action');
            assert.deepEqual((await tiles.allTextContents()).map(text => text.trim()), actionNames);
            assert.deepEqual(await tiles.evaluateAll(links => links.map(link => link.getAttribute('href'))), actionRoutes);
            const tileStyles = await tiles.evaluateAll(links => links.map(link => ({
                height: link.getBoundingClientRect().height,
                background: getComputedStyle(link).backgroundColor,
                iconLoaded: link.querySelector('img').complete && link.querySelector('img').naturalWidth > 0,
                iconWhite: getComputedStyle(link.querySelector('img')).filter.includes('invert(1)'),
                textFits: link.querySelector('span').getBoundingClientRect().right <= link.getBoundingClientRect().right
            })));
            assert(tileStyles.every(tile => tile.height >= 136 && tile.background === 'rgb(18, 97, 181)' && tile.iconLoaded && tile.iconWhite && tile.textFits));
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Quick Actions overflow at ${width}px`);
            if (process.env.BROWSER_TEST_ARTIFACTS && [1440, 390].includes(width))
                await page.screenshot({ path: path.join(process.env.BROWSER_TEST_ARTIFACTS, `quick-actions-${width}.png`), fullPage: true });
        }
        await page.setViewportSize({ width: 1440, height: 1000 });
        await open('/');
        await Promise.all([page.waitForURL(baseUrl + '/Documents'), page.locator('main').getByRole('link', { name: 'Document Explorer', exact: true }).click()]);
        await Promise.all([page.waitForURL(baseUrl + '/UploadFile'), page.getByRole('link', { name: 'Upload File', exact: true }).click()]);
        await page.getByRole('heading', { name: 'Upload File', exact: true }).waitFor();
        assert(await page.locator('.upload-icon').evaluate(icon => getComputedStyle(icon).filter.includes('invert(1)')));
        assert.deepEqual(await page.locator('#DocumentTypeId option').allTextContents(), ['Auto-detect', 'Induction', 'Safety']);
        await page.getByRole('button', { name: 'Upload', exact: true }).click();
        assert.equal(await page.locator('#Upload').evaluate(input => input.validity.valueMissing), true);
        const fixtureFile = { name: 'certificate.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\nfixture upload') };
        await page.locator('#DocumentTypeId').selectOption('5');
        await page.locator('#Upload').setInputFiles(fixtureFile);
        if (process.env.BROWSER_TEST_ARTIFACTS)
            await page.screenshot({ path: path.join(process.env.BROWSER_TEST_ARTIFACTS, 'upload-file-desktop.png'), fullPage: true });
        calls.length = 0;
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Upload', exact: true }).click()]);
        await page.getByRole('status').getByText('File uploaded. Awaiting file checks and extraction.', { exact: true }).waitFor();
        assert.deepEqual(calls.find(call => call.route === '/links').payload, { purpose: 'upload', staffId: null, termsDocumentId: null, validHours: 1 });
        const uploaded = calls.find(call => call.route === '/public/upload').payload;
        assert.equal(uploaded.documentTypeId, '5');
        assert.equal(uploaded.token, 'fixture-upload-token');
        assert.equal(uploaded.file.name, fixtureFile.name);
        assert.deepEqual(Buffer.from(await uploaded.file.arrayBuffer()), fixtureFile.buffer);
        assert.equal(await page.locator('#Upload').inputValue(), '');
        await page.reload({ waitUntil: 'networkidle' });
        assert.equal(calls.filter(call => call.route === '/public/upload').length, 1, 'Refresh must not submit the upload again');

        failUpload = true;
        await page.locator('#DocumentTypeId').selectOption('4');
        await page.locator('#Upload').setInputFiles(fixtureFile);
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Upload', exact: true }).click()]);
        await page.getByRole('alert').getByText('Upload rejected by fixture.', { exact: true }).waitFor();
        assert.equal(await page.locator('#DocumentTypeId').inputValue(), '4');
        failUpload = false;
        await page.setViewportSize({ width: 390, height: 844 });
        if (process.env.BROWSER_TEST_ARTIFACTS)
            await page.screenshot({ path: path.join(process.env.BROWSER_TEST_ARTIFACTS, 'upload-file-mobile.png'), fullPage: true });
        for (const width of [390, 320]) {
            await page.setViewportSize({ width, height: 844 });
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Upload form overflow at ${width}px`);
        }
        await open('/UploadFile');
        await page.locator('#Upload').setInputFiles({ name: 'bad.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('fixture') });
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Upload', exact: true }).click()]);
        await page.locator('[data-valmsg-for="Upload"]').getByText('Upload a PDF or supported image.', { exact: true }).waitFor();
        assert.equal(calls.filter(call => ['/links', '/public/upload'].includes(call.route)).length, 0);
        failUploadTypes = true;
        await open('/UploadFile');
        assert.equal(await page.getByRole('button', { name: 'Upload', exact: true }).isDisabled(), true);
        await page.getByRole('alert').getByText('Could not load document types. Please try again.', { exact: true }).waitFor();
        failUploadTypes = false;

        for (const missingPermission of ['Staff.Read', 'Documents.Write', 'Links.Write']) {
            await open('/UploadFile');
            const antiforgery = await page.locator('main [name="__RequestVerificationToken"]').inputValue();
            account.permissions = permissions.filter(permission => permission !== missingPermission);
            await open('/');
            assert.equal(await page.locator('main').getByRole('link', { name: 'Upload File', exact: true }).count(), 0);
            const deniedPost = await page.request.post(baseUrl + '/UploadFile', {
                form: { __RequestVerificationToken: antiforgery, DocumentTypeId: '4' }, maxRedirects: 0
            });
            assert([302, 403].includes(deniedPost.status()));
            assert.equal(calls.filter(call => ['/links', '/public/upload'].includes(call.route)).length, 0);
            await open('/UploadFile');
            assert.equal(await page.getByRole('heading', { name: 'Upload File', exact: true }).count(), 0);
            if (missingPermission !== 'Staff.Read') {
                await open('/Documents');
                assert.equal(await page.getByRole('link', { name: 'Upload File', exact: true }).count(), 0);
            }
            account.permissions = permissions;
        }
        const anonymous = await browser.newContext({ ignoreHTTPSErrors: true });
        const anonymousPage = await anonymous.newPage();
        await anonymousPage.goto(baseUrl + '/UploadFile');
        assert.match(anonymousPage.url(), /\/Login/);
        await anonymous.close();
        account.role = 'HR';
        await open('/UploadFile');
        await page.locator('#Upload').setInputFiles(fixtureFile);
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Upload', exact: true }).click()]);
        await page.getByRole('status').waitFor();
        assert.equal(calls.find(call => call.route === '/public/upload').payload.documentTypeId, undefined);
        account.role = 'Admin';
        await page.setViewportSize({ width: 1440, height: 1000 });
        assert.deepEqual(errors, []);
        console.log('PASS: Quick Actions layout/links/icons; typed and automatic uploads, validation, errors, permissions, PRG and mobile form.');
        if (process.env.BROWSER_TEST_SUITE === 'quick-actions') return;

        await open('/ManageTerms?DocumentId=1');
        assert.equal(await page.locator('[name="RoleInput.StaffRoleIds"]:checked').count(), 0);
        await page.locator('#terms-role-1').check();
        await page.locator('#terms-role-2').check();
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save required roles', exact: true }).click()]);
        assert.equal(await page.locator('main .validation-summary-errors').count(), 0,
            await page.locator('main').innerText());
        await page.getByRole('status').getByText('Required staff roles saved.', { exact: true }).waitFor();
        assert.equal(await page.locator('#terms-role-1').isChecked(), true);
        assert.equal(await page.locator('#terms-role-2').isChecked(), true);
        assert.deepEqual(terms[0].staffRoleIds, [1, 2]);
        assert.deepEqual(calls.find(call => call.route === '/terms/1/staff-roles').payload, { staffRoleIds: [1, 2], expectedRevision: revision([]) });
        assert.equal(calls.filter(call => call.route === '/terms' && call.method === 'POST').length, 0);
        await open('/ManageTerms?DocumentId=2');
        await page.locator('#terms-role-1').check();
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save required roles', exact: true }).click()]);
        assert.equal(terms.filter(document => document.staffRoleIds?.includes(1)).length, 2, 'One role can require multiple shared terms documents');
        await open('/ManageTerms?DocumentId=1');
        await page.locator('#terms-role-1').uncheck();
        terms[0].staffRoleIds = [1];
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save required roles', exact: true }).click()]);
        await page.getByRole('alert').getByText('Required staff roles changed. Reload before saving.', { exact: true }).waitFor();
        assert.equal(await page.locator('#terms-role-1').isChecked(), false);
        assert.equal(await page.locator('#terms-role-2').isChecked(), true);
        assert.equal(await page.locator('#RoleInput_ExpectedRevision').inputValue(), revision([1, 2]));
        await page.setViewportSize({ width: 390, height: 844 });
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'Required terms form must fit mobile');
        if (process.env.BROWSER_TEST_ARTIFACTS)
            await page.screenshot({ path: path.join(process.env.BROWSER_TEST_ARTIFACTS, 'role-terms-mobile.png'), fullPage: true });
        await open('/ManageTerms?DocumentId=1');
        await page.locator('#terms-role-1').uncheck();
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save required roles', exact: true }).click()]);
        assert.deepEqual(terms[0].staffRoleIds, []);
        assert.deepEqual(terms[1].staffRoleIds, [1], 'Clearing one document must not change another');
        account.role = 'HR';
        await open('/ManageTerms?DocumentId=2');
        await page.locator('#terms-role-2').check();
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save required roles', exact: true }).click()]);
        assert.deepEqual(terms[1].staffRoleIds, [1, 2]);
        await page.setViewportSize({ width: 1440, height: 1000 });
        if (process.env.BROWSER_TEST_ARTIFACTS)
            await page.screenshot({ path: path.join(process.env.BROWSER_TEST_ARTIFACTS, 'role-terms-desktop.png'), fullPage: true });
        const roleAntiforgery = await page.locator('main [name="__RequestVerificationToken"]').inputValue();
        account.role = 'Foreman';
        account.permissions = ['Staff.Read', 'Links.Write'];
        calls.length = 0;
        const roleDenied = await page.request.post(baseUrl + '/ManageTerms?handler=Role&DocumentId=2', {
            form: { __RequestVerificationToken: roleAntiforgery, 'RoleInput.StaffRoleIds': '1', 'RoleInput.ExpectedRevision': revision([1, 2]) }, maxRedirects: 0
        });
        assert([302, 403].includes(roleDenied.status()));
        assert.equal(calls.filter(call => call.method === 'PUT').length, 0);
        await open('/ManageTerms');
        assert.equal(await page.getByRole('button', { name: 'Save required roles', exact: true }).count(), 0);
        account.role = 'Admin';
        account.permissions = permissions;
        assert.deepEqual(errors, []);
        console.log('PASS: shared terms role checkboxes, many-to-many assignments, clear, stale drafts, permission denial and mobile.');
        if (process.env.BROWSER_TEST_SUITE === 'role-terms') return;

        const mappings = { StartDateLabel: 'From', ExpiryDateLabel: 'To', DocumentNumberLabel: 'Registration ID',
            ExtractedNameLabel: 'Participant', EmailLabel: 'Personal email', PhoneLabel: 'Mobile' };
        const jsonField = field => field[0].toLowerCase() + field.slice(1);
        await open('/DocumentTypes?Mode=type&Id=4');
        for (const [field, value] of Object.entries(mappings)) {
            const input = page.locator(`#Input_${field}`);
            assert.equal(await input.inputValue(), '');
            assert.equal(await input.getAttribute('maxlength'), '128');
            await input.fill(value);
        }
        if (process.env.BROWSER_TEST_ARTIFACTS)
            await page.screenshot({ path: path.join(process.env.BROWSER_TEST_ARTIFACTS, 'field-mappings-desktop.png'), fullPage: true });
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save changes', exact: true }).click()]);
        const savedMappings = calls.find(call => call.route === '/document-types/4' && call.method === 'PUT').payload;
        for (const [field, value] of Object.entries(mappings)) assert.equal(savedMappings[jsonField(field)], value);
        assert.deepEqual(savedMappings.staffRoleIds, [1]);
        account.role = 'HR';
        await open('/DocumentTypes?Mode=type&Id=4');
        for (const [field, value] of Object.entries(mappings)) assert.equal(await page.locator(`#Input_${field}`).inputValue(), value);
        failMappings = true;
        await page.locator('#Input_StartDateLabel').fill('Valid from');
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save changes', exact: true }).click()]);
        await page.locator('[data-valmsg-for="Input.StartDateLabel"]').getByText('Mapping rejected by fixture.').waitFor();
        assert.equal(await page.locator('#Input_StartDateLabel').inputValue(), 'Valid from');
        for (const [field, value] of Object.entries(mappings))
            if (field !== 'StartDateLabel') assert.equal(await page.locator(`#Input_${field}`).inputValue(), value);
        failMappings = false;
        await page.setViewportSize({ width: 390, height: 844 });
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'Mapping editor must fit mobile');
        if (process.env.BROWSER_TEST_ARTIFACTS)
            await page.screenshot({ path: path.join(process.env.BROWSER_TEST_ARTIFACTS, 'field-mappings-mobile.png'), fullPage: true });
        for (const field of Object.keys(mappings)) await page.locator(`#Input_${field}`).fill('');
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Save changes', exact: true }).click()]);
        await open('/DocumentTypes?Mode=type&Id=4');
        for (const field of Object.keys(mappings)) assert.equal(await page.locator(`#Input_${field}`).inputValue(), '');
        await open('/DocumentTypes?Mode=type');
        await page.locator('#Input_Name').fill('Mapped certificate');
        await page.locator('#Input_TextIdentifier').fill('MAPPED CERTIFICATE');
        for (const [field, value] of Object.entries(mappings)) await page.locator(`#Input_${field}`).fill(value);
        await Promise.all([page.waitForNavigation(), page.getByRole('button', { name: 'Add document type', exact: true }).click()]);
        const createdMappings = calls.find(call => call.route === '/document-types' && call.method === 'POST').payload;
        for (const [field, value] of Object.entries(mappings)) assert.equal(createdMappings[jsonField(field)], value);
        assert.deepEqual(createdMappings.staffRoleIds, []);
        account.role = 'Admin';
        await page.setViewportSize({ width: 1440, height: 1000 });
        console.log('PASS: HR/Admin can create, edit, clear and retain all six field mappings on desktop/mobile without changing role assignments.');

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