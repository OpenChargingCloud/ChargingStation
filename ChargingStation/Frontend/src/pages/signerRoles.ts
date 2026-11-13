import { api, type SignatureSettingsUpdate, type Signatures, type SignerRole } from '../api/client';
import { auth } from '../auth';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, numberField, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render } from '@node/view';

/**
 * Signer roles: which keys sign as which role of this station, and how the
 * station holds what a CSMS sends to its signature policy.
 *
 * A key gets its role by a signed role assignment - a file the owner of the
 * station signed. Read here, it is checked exactly as one received over OCPP
 * is: by its signatures, by its version, and by what the station says of the
 * role. The first owner is the one exception, because nobody could have signed
 * it yet: its public key is all that is needed, once.
 */
export const signerRolesPage: Page = {

    title: 'Signer roles',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/signer-roles',
            title:     'Signer roles',
            subtitle:  'Which keys sign as which role, and how signed messages are held to the policy.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('signatures', 'edit');

        let cancelled = false;
        let current: Signatures | null = null;


        function keysOf(role: SignerRole): unknown {

            if (role.keys.length === 0)
                return html`<span class="hint">no keys</span>`;

            return role.keys.map(key => html`
                <div class="signer-key">
                    <code>${key.kid}</code>${key.label !== undefined ? html` - ${key.label}` : nothing}
                    ${key.notAfter !== undefined ? html`<span class="hint"> until ${key.notAfter}</span>` : nothing}
                </div>
            `);

        }

        function draw(): void {

            if (current === null)
                return;

            const signatures = current;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">${mayButNot('look at the signer roles', 'change them')}</div>
                `}

                ${signatures.notSaved !== undefined ? html`
                    <div class="error-box" id="not-saved">${signatures.notSaved}</div>
                ` : nothing}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-scale-balanced"></i> Signature policy</h2>

                        <p class="hint" id="policy-state">
                            ${signatures.settings.mode === 'Enforce'
                                  ? html`<strong>Enforced</strong>: a request is refused unless its signers may do what it asks.`
                                  : signatures.settings.mode === 'Observe'
                                        ? html`<strong>Observed</strong>: what the policy would refuse is done all the same, and reported.`
                                        : html`<strong>Off</strong>: signatures are not looked at, and every request is taken.`}
                            ${signatures.policyVersion > 0
                                  ? html` The policy table is of version ${signatures.policyVersion}.`
                                  : html` The station was given no policy table.`}
                        </p>

                        <form id="settings-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void saveSettings(); }}>

                            <label>
                                Mode
                                <select name="mode" ?disabled=${!mayChange}>
                                    ${[ 'Off', 'Observe', 'Enforce' ].map(mode => html`
                                        <option value="${mode}" ?selected=${signatures.settings.mode === mode}>${mode}</option>
                                    `)}
                                </select>
                            </label>

                            <label>
                                Owners that have to sign a role assignment
                                <input type="number" name="quorum" min="1" max="16" .value=${String(signatures.settings.quorum)} ?disabled=${!mayChange} />
                            </label>

                            <label class="checkbox">
                                <input type="checkbox" name="allowFirstOwnerByCSMS" ?checked=${signatures.settings.allowFirstOwnerByCSMS} ?disabled=${!mayChange} />
                                While there is no owner, take the first one from the CSMS
                            </label>

                            <label class="checkbox">
                                <input type="checkbox" name="allowPermissionsByCSMS" ?checked=${signatures.settings.allowPermissionsByCSMS} ?disabled=${!mayChange} />
                                A role assignment may say what a role may do beyond what this station says of it
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <span id="settings-note"  class="form-notice" role="status"></span>
                                <span id="settings-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                In force from that moment, for OCPP 2.1 and OCPP 1.6 alike. Signatures of
                                ${signatures.algorithms.join(', ')} are verified.
                            </span>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-user-shield"></i> Roles and their keys</h2>

                        <p class="hint" id="owner-state">
                            ${signatures.hasOwner
                                  ? html`The owner is the role <strong>${signatures.ownerRole}</strong>: only what its keys signed changes a role.`
                                  : html`<strong>This station has no owner yet.</strong> Its first owner is made below, from the owner's public key.`}
                        </p>

                        <div class="table-scroll">
                            <table class="records" id="signer-roles">
                                <thead>
                                    <tr><th>Role</th><th>Version</th><th>Keys</th><th>Last used</th><th></th></tr>
                                </thead>
                                <tbody>
                                    ${signatures.roles.map(role => html`
                                        <tr data-role="${role.role}">
                                            <td><strong>${role.role}</strong>${role.description !== undefined ? html`<div class="hint">${role.description}</div>` : nothing}</td>
                                            <td>${role.keys.length > 0 ? role.version : ''}</td>
                                            <td>${keysOf(role)}</td>
                                            <td>${role.lastUsed ?? ''}</td>
                                            <td>
                                                ${role.keys.length > 0 && mayChange ? html`
                                                    <button type="button" class="btn small" data-remove="${role.role}"
                                                            @click=${() => void removeRole(role)}>Remove keys</button>
                                                ` : nothing}
                                            </td>
                                        </tr>
                                    `)}
                                </tbody>
                            </table>
                        </div>

                        <span id="roles-note"  class="form-notice" role="status"></span>
                        <span id="roles-error" class="form-error"  role="alert"></span>

                    </section>

                    ${signatures.hasOwner ? nothing : html`

                        <section class="card">

                            <h2><i class="fa-solid fa-key"></i> First owner</h2>

                            <form id="owner-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void installOwner(); }}>

                                <label>
                                    The owner's public key, as a JSON Web Key
                                    <textarea name="jwk" rows="6" spellcheck="false" ?disabled=${!mayChange}
                                              placeholder='{ "kty": "EC", "crv": "P-256", "x": "...", "y": "..." }'></textarea>
                                </label>

                                <label>
                                    Whose key it is
                                    <input type="text" name="label" maxlength="100" ?disabled=${!mayChange} />
                                </label>

                                <div class="form-actions">
                                    <button type="submit" class="btn primary" ?disabled=${!mayChange}>Make the first owner</button>
                                    <span id="owner-note"  class="form-notice" role="status"></span>
                                    <span id="owner-error" class="form-error"  role="alert"></span>
                                </div>

                                <span class="hint">
                                    Whoever holds this key decides who may sign what on this station. A role
                                    assignment for ${signatures.ownerRole} can be pasted here as well.
                                </span>

                            </form>

                        </section>

                    `}

                    <section class="card">

                        <h2><i class="fa-solid fa-file-signature"></i> Role assignment</h2>

                        <form id="import-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void importRole(); }}>

                            <label>
                                A role assignment the owner signed
                                <textarea name="assignment" rows="8" spellcheck="false" ?disabled=${!mayChange}
                                          placeholder='{ "role": "support", "keys": [ ... ], "version": 2, "signatures": [ ... ] }'></textarea>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Import</button>
                                <span id="import-note"  class="form-notice" role="status"></span>
                                <span id="import-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Checked as one a CSMS sends: signed by the owner, of a version greater than the
                                one that is stored, and saying no more than this station says of the role.
                            </span>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-table-list"></i> Policy table</h2>

                        <form id="policy-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void importPolicy(); }}>

                            <label>
                                A policy table the owner signed
                                <textarea name="policy" rows="8" spellcheck="false" ?disabled=${!mayChange}
                                          placeholder='{ "version": 1, "entries": [ ... ], "signatures": [ ... ] }'
                                          .defaultValue=${signatures.policy !== undefined ? JSON.stringify(signatures.policy, null, 2) : ''}></textarea>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Set the policy</button>
                                <span id="policy-note"  class="form-notice" role="status"></span>
                                <span id="policy-error" class="form-error"  role="alert"></span>
                            </div>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-triangle-exclamation"></i> Forget every key</h2>

                        <form id="reset-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void reset(); }}>

                            <label>
                                Type <code>reset</code> to forget every key of every role and the policy table
                                <input type="text" name="confirm" autocomplete="off" ?disabled=${!mayChange} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn danger" ?disabled=${!mayChange}>Forget every key</button>
                                <span id="reset-note"  class="form-notice" role="status"></span>
                                <span id="reset-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                The way out for a station whose owners lost their keys. Nobody signs as anybody
                                afterwards, a first owner can be made again, and the CSMS is told.
                            </span>

                        </form>

                    </section>

                </div>

            `);

        }

        /**
         * One change sent, and the page drawn again as the station took it.
         */
        async function change(form: string, note: string, error: string, said: (taken: Signatures) => string,
                              send: () => Promise<Signatures>): Promise<void> {

            const noteElement   = must<HTMLElement>(content, note);
            const errorElement  = must<HTMLElement>(content, error);

            noteElement.textContent   = '';
            errorElement.textContent  = '';

            try
            {
                current = await whileSaving(content, noteElement, send);

                if (cancelled)
                    return;

                draw();

                content.querySelector<HTMLFormElement>(form)?.reset();

                const drawnNote = content.querySelector<HTMLElement>(note);
                if (drawnNote !== null)
                    drawnNote.textContent = said(current);
            }
            catch (problem)
            {
                if (!cancelled)
                    errorElement.textContent = errorMessage(problem);
            }

        }

        /**
         * What was pasted, as the JSON object it is; undefined, and said, where it is none.
         */
        function pasted(form: string, field: string, error: string): Record<string, unknown> | undefined {

            const text = String(new FormData(must<HTMLFormElement>(content, form)).get(field) ?? '').trim();

            try
            {
                const json: unknown = JSON.parse(text);

                if (typeof json === 'object' && json !== null && !Array.isArray(json))
                    return json as Record<string, unknown>;
            }
            catch
            {
                // Said below.
            }

            must<HTMLElement>(content, error).textContent = 'This is no JSON object.';
            return undefined;

        }

        function saveSettings(): Promise<void> {

            const form  = must<HTMLFormElement>(content, '#settings-form');
            const data  = new FormData(form);

            const update: SignatureSettingsUpdate = {
                mode:                    String(data.get('mode')),
                quorum:                  numberField(form, 'quorum'),
                allowFirstOwnerByCSMS:   data.get('allowFirstOwnerByCSMS')  !== null,
                allowPermissionsByCSMS:  data.get('allowPermissionsByCSMS') !== null
            };

            return change('#settings-form', '#settings-note', '#settings-error',
                          taken => `Saved. The signature policy is ${taken.settings.mode}.`,
                          () => api.signatures.saveSettings(update));

        }

        function installOwner(): Promise<void> {

            const json = pasted('#owner-form', 'jwk', '#owner-error');

            if (json === undefined)
                return Promise.resolve();

            const label = String(new FormData(must<HTMLFormElement>(content, '#owner-form')).get('label') ?? '').trim();

            // A role assignment is sent as it is; a key alone with whose it is.
            const body = 'role' in json
                             ? json
                             : label === '' ? { jwk: json } : { jwk: json, label };

            return change('#owner-form', '#roles-note', '#owner-error',
                          () => 'This station has its first owner.',
                          () => api.signatures.installFirstOwner(body));

        }

        function importRole(): Promise<void> {

            const json = pasted('#import-form', 'assignment', '#import-error');

            return json === undefined
                       ? Promise.resolve()
                       : change('#import-form', '#import-note', '#import-error',
                                () => `Imported: the role '${String(json['role'])}' has the keys of version ${String(json['version'])}.`,
                                () => api.signatures.importRole(json));

        }

        function importPolicy(): Promise<void> {

            const json = pasted('#policy-form', 'policy', '#policy-error');

            return json === undefined
                       ? Promise.resolve()
                       : change('#policy-form', '#policy-note', '#policy-error',
                                taken => `The policy table of version ${taken.policyVersion} is the station's.`,
                                () => api.signatures.setPolicy(json));

        }

        function removeRole(role: SignerRole): Promise<void> {
            return change('#no-form', '#roles-note', '#roles-error',
                          () => `The role '${role.role}' has no keys any more.`,
                          () => api.signatures.removeRole({ role: role.role, version: role.version }));
        }

        function reset(): Promise<void> {

            const confirm = String(new FormData(must<HTMLFormElement>(content, '#reset-form')).get('confirm') ?? '').trim();

            if (confirm !== 'reset') {
                must<HTMLElement>(content, '#reset-error').textContent = 'Type "reset" to forget every key.';
                return Promise.resolve();
            }

            return change('#reset-form', '#reset-note', '#reset-error',
                          () => 'Every key of every role and the policy table are forgotten.',
                          () => api.signatures.reset());

        }

        async function load(): Promise<void> {

            try
            {
                const loaded = await api.signatures.get();

                if (cancelled)
                    return;

                current = loaded;
                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The signer roles could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        async function reload(): Promise<void> {
            await load();
            if (!cancelled)
                content.querySelectorAll('form').forEach(form => form.reset());
        }

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};
