(() => {
	function withQuery(address, name, value) {
		const url = new URL(address, window.location.href);
		for (const key of [...url.searchParams.keys()]) {
			if (key.toLowerCase() === name.toLowerCase()) url.searchParams.delete(key);
		}
		url.searchParams.set(name, value);
		return url;
	}

	const formSnapshot = form => JSON.stringify([...new FormData(form)].map(([name, value]) =>
		[name, value instanceof File ? (value.name ? [value.name, value.size, value.lastModified] : null) : value]));
	const originals = new Map([...document.querySelectorAll('main form[method="post"]')]
		.map(form => [form, formSnapshot(form)]));
	let roleDraftsDirty = () => false;
	let leaving = false;
	function hasUnsavedChanges(submittedForm = null) {
		if (roleDraftsDirty(submittedForm)) return true;
		return [...originals].some(([form, original]) => form !== submittedForm && form !== requirements
			&& (formSnapshot(form) !== original || form.querySelector('.validation-summary-errors, .field-validation-error')));
	}
	window.addEventListener('beforeunload', event => {
		if (!leaving && hasUnsavedChanges()) {
			event.preventDefault();
			event.returnValue = '';
		}
	});
	window.addEventListener('pageshow', () => { leaving = false; });

	document.addEventListener('shown.bs.collapse', event => {
		if (!event.target.matches('.document-editor')) return;
		event.target.scrollIntoView({ block: 'start' });
		event.target.querySelector('select:not(:disabled), input:not([type="hidden"]):not(:disabled)')?.focus({ preventScroll: true });
	});

	const roleSelector = document.querySelector('[data-role-selector]');
	const requirements = document.querySelector('[data-role-requirements]');
	if (roleSelector && requirements) {
		const select = roleSelector.querySelector('select');
		const checkboxes = [...requirements.querySelectorAll('[data-role-ids]')];
		const drafts = new Map();
		let selectedRole = select.value;
		const revision = requirements.querySelector('[data-role-revision]');
		const revisions = new Map([...select.options].map(option => [option.value, option.dataset.revision]));
		revisions.set(selectedRole, revision.value);
		const baseline = roleId => checkboxes.filter(checkbox => checkbox.dataset.roleIds.split(',').includes(roleId)).map(checkbox => checkbox.value);
		const selectedValues = () => checkboxes.filter(checkbox => checkbox.checked).map(checkbox => checkbox.value);
		roleDraftsDirty = submittedForm => {
			drafts.set(selectedRole, selectedValues());
			return [...drafts].some(([roleId, values]) => !(submittedForm === requirements && roleId === selectedRole)
				&& JSON.stringify(values) !== JSON.stringify(baseline(roleId)));
		};
		function changeRole() {
			drafts.set(selectedRole, checkboxes.filter(checkbox => checkbox.checked).map(checkbox => checkbox.value));
			selectedRole = select.value;
			revision.value = revisions.get(selectedRole);
			for (const checkbox of checkboxes) {
				checkbox.checked = drafts.has(selectedRole)
					? drafts.get(selectedRole).includes(checkbox.value)
					: checkbox.dataset.roleIds.split(',').includes(selectedRole);
			}
			requirements.querySelector('[data-role-name]').textContent = select.selectedOptions[0].textContent;
			const roleInput = requirements.elements.namedItem(select.name);
			if (roleInput) roleInput.value = selectedRole;
			requirements.action = withQuery(requirements.action, select.name, selectedRole);
			for (const link of document.querySelectorAll('[data-role-target]')) {
				link.href = withQuery(link.href, select.name, selectedRole);
			}
			for (const message of document.querySelectorAll('[data-selection-message]')) message.hidden = true;
			window.history.replaceState(null, '', withQuery(window.location.href, select.name, selectedRole));
		}
		select.addEventListener('change', changeRole);
		roleSelector.addEventListener('submit', event => {
			event.preventDefault();
			changeRole();
		});
	}

	document.addEventListener('submit', event => {
		const form = event.target;
		if (form.matches('[data-terms-selector]')) {
			event.preventDefault();
		}
		if (event.defaultPrevented) return;
		const submittedForm = form.method.toLowerCase() === 'post' ? form : null;
		if (hasUnsavedChanges(submittedForm) && !window.confirm('Other unsaved changes will be lost. Continue?')) {
			event.preventDefault();
			return;
		}
		queueMicrotask(() => { leaving = !event.defaultPrevented; });
	});

	document.addEventListener('change', event => {
		const form = event.target.form;
		if (event.target.matches('[data-role-select]')) {
			for (const hint of event.target.closest('[data-staff-entry]').querySelectorAll('[data-role-id]')) {
				hint.hidden = hint.dataset.roleId !== event.target.value;
			}
		} else if (form?.matches('[data-terms-selector]') && event.target.name === 'VersionId') {
			for (const version of document.querySelectorAll('[data-terms-version]')) {
				version.hidden = version.dataset.termsVersion !== event.target.value;
			}
			window.history.replaceState(null, '', withQuery(window.location.href, 'versionId', event.target.value));
		} else if (form?.matches('[data-auto-submit]')) {
			form.requestSubmit();
		}
	});

	const userEditor = document.querySelector('[data-user-editor]');
	if (userEditor) {
		const snapshot = () => new URLSearchParams(new FormData(userEditor)).toString();
		let original = snapshot();
		document.addEventListener('click', event => {
			const link = event.target.closest('[data-user]');
			if (!link || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
			event.preventDefault();
			if (snapshot() !== original && !window.confirm('Discard unsaved user changes?')) return;
			const user = JSON.parse(link.dataset.user);
			userEditor.elements.Id.value = user.Id ?? '';
			userEditor.elements['Input.Email'].value = user.Email ?? '';
			userEditor.elements['Input.Password'].value = '';
			userEditor.elements['Input.RoleId'].value = user.RoleId ?? userEditor.elements['Input.RoleId'].options[0]?.value ?? '';
			userEditor.querySelector('[name="Input.IsEnabled"][type="checkbox"]').checked = user.IsEnabled ?? true;
			userEditor.querySelector('[name="Input.MfaEnabled"][type="checkbox"]').checked = user.MfaEnabled ?? false;
			userEditor.action = link.href;
			const errors = userEditor.querySelector('[data-valmsg-summary]');
			if (errors) errors.hidden = true;
			document.querySelector('[data-user-heading]').textContent = user.Id ? 'Edit user' : 'Add user';
			window.history.replaceState(null, '', link.href);
			original = snapshot();
			originals.set(userEditor, formSnapshot(userEditor));
			userEditor.elements['Input.Email'].focus();
		});
	}

	const registration = document.querySelector('[data-multiple-registration]');
	if (registration) {
		const add = registration.querySelector('[data-add-staff]');
		const template = registration.querySelector('[data-staff-entry]').cloneNode(true);
		function reindexEntries() {
			const entries = [...registration.querySelectorAll('[data-staff-entry]')];
			entries.forEach((entry, index) => {
				entry.querySelector('legend').textContent = registration.dataset.entryLabel.replace('{0}', index + 1);
				for (const element of entry.querySelectorAll('[name], [id], [for], [data-valmsg-for]')) {
					for (const attribute of ['name', 'id', 'for', 'data-valmsg-for']) {
						if (element.hasAttribute(attribute)) element.setAttribute(attribute, element.getAttribute(attribute)
							.replace(/Staff\[\d+\]/g, `Staff[${index}]`).replace(/Staff_\d+__/g, `Staff_${index}__`));
					}
				}
				const remove = entry.querySelector('[data-remove-staff]');
				remove.textContent = registration.dataset.removeLabel.replace('{0}', index + 1);
				remove.formAction = withQuery(remove.formAction, 'index', index);
				remove.hidden = entries.length <= 1;
			});
			add.disabled = entries.length >= Number(registration.dataset.maximumStaff);
			const validation = window.jQuery;
			if (validation?.validator?.unobtrusive) {
				validation(registration).removeData('validator').removeData('unobtrusiveValidation');
				validation.validator.unobtrusive.parse(registration);
			}
		}
		registration.addEventListener('click', event => {
			const button = event.target.closest('[data-add-staff], [data-remove-staff]');
			if (!button) return;
			event.preventDefault();
			const entries = registration.querySelectorAll('[data-staff-entry]');
			if (button === add) {
				if (entries.length >= Number(registration.dataset.maximumStaff)) return;
				const entry = template.cloneNode(true);
				for (const input of entry.querySelectorAll('input')) { input.value = ''; input.classList.remove('input-validation-error'); }
				for (const select of entry.querySelectorAll('select')) { select.selectedIndex = 0; select.classList.remove('input-validation-error'); }
				for (const error of entry.querySelectorAll('[data-valmsg-for]')) { error.textContent = ''; error.className = 'text-danger field-validation-valid'; }
				for (const hint of entry.querySelectorAll('[data-role-id]')) hint.hidden = true;
				entries[entries.length - 1].after(entry);
				reindexEntries();
				entry.querySelector('input').focus();
			} else if (entries.length > 1) {
				button.closest('[data-staff-entry]').remove();
				reindexEntries();
			}
		});
	}
})();
