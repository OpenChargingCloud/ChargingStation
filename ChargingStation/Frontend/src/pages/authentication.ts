import { api, type LoginToSave, type StationConnections, type StationLogin } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, field, formatTimestamp, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * The credentials this station proves itself with.
 *
 * Several, because a station in a fleet normally has more than one: a login for
 * the back end and another for the local controller in the same cabinet, and
 * the two are not interchangeable. Which is which is written down rather than
 * guessed at from a URL a year later - that is what the description is for, and
 * why it is the one field that cannot be left empty.
 *
 * A secret goes in and never comes back out. The station is told a password, it
 * writes it where only its owner may read it, and what this page is handed
 * afterwards is whether one is set. So the secret field on an existing record
 * is empty and means "leave it alone" - which is what makes it possible to
 * correct a description without having to find the password again.
 */
export const authenticationPage: Page = {

    title: 'Authentication',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/authentication',
            title:     'Authentication',
            subtitle:  'How this charging station proves who it is when it dials a back end.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayManage = auth.can('changeNetworkSettings');

        let cancelled = false;
        let store: StationConnections | null = null;

        /** Which records have their details open, so a redraw does not close them. */
        const opened = new Set<string>();


        function draw(): void {

            if (store === null)
                return;

            const state = store;

            render(content, html`

                ${mayManage ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the
                        credentials but not change them. That needs the role that changes how this station
                        reaches the outside world.
                    </div>
                `}

                <section class="card">

                    <h2><i class="fa-solid fa-user-plus"></i> Write down a set of credentials</h2>

                    <form id="add-form" class="form-stack" data-kind-form="add">

                        <label>What they are for
                            <input type="text" name="description" maxlength="${state.maxDescriptionLength}"
                                   placeholder="CSMS login" ${mayManage ? '' : html`disabled`} />
                            <span class="hint">
                                The one field worth taking seriously. Six logins on a page a year from now are
                                told apart by this and nothing else.
                            </span>
                        </label>

                        <label>How
                            <select name="kind" data-kind ${mayManage ? '' : html`disabled`}>
                                <option value="basic">HTTP Basic - a name and a password</option>
                                <option value="totp">HTTP TOTP - a secret that never travels</option>
                            </select>
                        </label>

                        <label>Login
                            <input type="text" name="login" placeholder="cs001"
                                   ${mayManage ? '' : html`disabled`} />
                            <span class="hint">
                                A role rather than a person: it is what the other end calls this station.
                            </span>
                        </label>

                        ${secretField('add', null)}

                        ${totpFields('add', null, state)}

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${mayManage ? '' : html`disabled`}>
                                Write them down
                            </button>
                            <span id="add-note"  class="form-notice" role="status"></span>
                            <span id="add-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>

                <section class="card">

                    <h2><i class="fa-solid fa-list"></i> What is configured</h2>

                    <p class="hint">
                        Newest first. Written to ${state.directory}, the secrets readable by nobody else.
                    </p>

                    ${state.authentications.length === 0
                          ? html`
                                <p class="hint">
                                    Nothing yet. This station can only dial a back end that asks it for
                                    nothing, or one it shows a client certificate to.
                                </p>
                            `
                          : html`<div class="cards">${state.authentications.map(entry => entryCard(entry, state))}</div>`}

                </section>

            `);

            wire();

        }

        /**
         * The secret, which is write-only.
         *
         * On an existing record it is empty and stays empty: this station
         * cannot show it back, and pretending otherwise with a row of dots
         * would suggest that it could.
         */
        function secretField(id: string, entry: StationLogin | null): HTMLFragment {

            const isTOTP = (entry?.kind ?? 'basic') === 'totp';

            return html`
                <label data-secret-label="${id}">
                    <span data-secret-title="${id}">${isTOTP ? 'Shared secret' : 'Password'}</span>
                    <input type="password" name="secret" autocomplete="new-password"
                           placeholder="${entry === null ? '' : 'unchanged'}"
                           ${mayManage ? '' : html`disabled`} />
                    <span class="hint">
                        ${entry === null
                              ? html`
                                    It is written to this station and never read back out - not by this page
                                    and not by anything else. Keep your own copy.
                                `
                              : entry.hasSecret
                                    ? html`
                                          One is set. Leave this empty to keep it; type here only to replace it.
                                          It cannot be shown back.
                                      `
                                    : html`
                                          <strong>None is set</strong>, so nothing using these credentials can
                                          connect. Type one here.
                                      `}
                    </span>
                </label>
            `;

        }

        /** What a time-based password is made of - shown only when that is what it is. */
        function totpFields(id: string, entry: StationLogin | null, state: StationConnections): HTMLFragment {

            const on = (entry?.kind ?? 'basic') === 'totp';

            return html`
                <div data-totp="${id}" class="form-stack" ${on ? '' : html`hidden`}>

                    <label class="checkbox">
                        <input type="checkbox" name="tlsChannelBinding"
                               ${(entry?.tlsChannelBinding ?? true) ? html`checked` : ''}
                               ${mayManage ? '' : html`disabled`} />
                        Bind the password to the TLS session
                    </label>

                    <span class="hint">
                        On, unless there is a reason. A bound password is no use to anybody who copies it off
                        one connection and tries it on another - but it needs a TLS session to bind to, so a
                        connection over plain ws:// has to have this off. The Connections page says so where it
                        applies.
                    </span>

                    <div class="form-row">

                        <label>Good for
                            <input type="number" name="validitySeconds" min="5" max="3600"
                                   placeholder="${state.totpDefaults.validitySeconds}"
                                   value="${entry?.validitySeconds ?? ''}"
                                   ${mayManage ? '' : html`disabled`} />
                            <span class="hint">seconds</span>
                        </label>

                        <label>Length
                            <input type="number" name="length" min="4" max="64"
                                   placeholder="${state.totpDefaults.length}"
                                   value="${entry?.length ?? ''}"
                                   ${mayManage ? '' : html`disabled`} />
                            <span class="hint">characters</span>
                        </label>

                        <label>HMAC
                            <select name="hashAlgorithm" ${mayManage ? '' : html`disabled`}>
                                ${['SHA256', 'SHA384', 'SHA512'].map(one => html`
                                    <option value="${one}" ${one === (entry?.hashAlgorithm ?? 'SHA256') ? html`selected` : ''}>
                                        ${one}
                                    </option>
                                `)}
                            </select>
                        </label>

                    </div>

                    <label>Alphabet
                        <input type="text" name="alphabet" class="mono"
                               placeholder="${state.totpDefaults.alphabet}"
                               value="${entry?.alphabet ?? ''}"
                               ${mayManage ? '' : html`disabled`} />
                        <span class="hint">
                            Left empty, the default. Both ends have to agree on every one of these, so
                            changing any of them here means changing them at the back end too.
                        </span>
                    </label>

                </div>
            `;

        }

        function entryCard(entry: StationLogin, state: StationConnections): HTMLFragment {

            const usedBy = state.connections.filter(one => one.authenticationId === entry.id);

            return html`
                <div class="card">

                    <div class="key-head">
                        <strong>${entry.description}</strong>
                        <span class="chip">${entry.kind === 'basic' ? 'HTTP Basic' : 'HTTP TOTP'}</span>
                        ${entry.hasSecret ? '' : html`<span class="chip warn">no secret</span>`}
                    </div>

                    <dl class="kv">
                        <dt>Login</dt>   <dd><code>${entry.login}</code></dd>
                        <dt>Written</dt> <dd>${formatTimestamp(entry.createdAt)}</dd>
                        <dt>Used by</dt> <dd>
                            ${usedBy.length === 0
                                  ? html`<span class="hint">nothing yet</span>`
                                  : usedBy.map(one => one.description).join(', ')}
                        </dd>
                    </dl>

                    <details ${opened.has(entry.id) ? html`open` : ''} data-details="${entry.id}">

                        <summary>Change them</summary>

                        <form class="form-stack" data-edit="${entry.id}" data-kind-form="${entry.id}">

                            <label>What they are for
                                <input type="text" name="description" maxlength="${state.maxDescriptionLength}"
                                       value="${entry.description}" ${mayManage ? '' : html`disabled`} />
                            </label>

                            <label>How
                                <select name="kind" data-kind ${mayManage ? '' : html`disabled`}>
                                    <option value="basic" ${entry.kind === 'basic' ? html`selected` : ''}>
                                        HTTP Basic - a name and a password
                                    </option>
                                    <option value="totp"  ${entry.kind === 'totp'  ? html`selected` : ''}>
                                        HTTP TOTP - a secret that never travels
                                    </option>
                                </select>
                            </label>

                            <label>Login
                                <input type="text" name="login" value="${entry.login}"
                                       ${mayManage ? '' : html`disabled`} />
                            </label>

                            ${secretField(entry.id, entry)}

                            ${totpFields(entry.id, entry, state)}

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayManage ? '' : html`disabled`}>
                                    Save
                                </button>
                                <button type="button" class="btn small" data-remove="${entry.id}"
                                        ${mayManage ? '' : html`disabled`}>
                                    Remove
                                </button>
                                <span class="form-notice" data-note="${entry.id}"  role="status"></span>
                                <span class="form-error"  data-error="${entry.id}" role="alert"></span>
                            </div>

                            ${usedBy.length > 0 ? html`
                                <span class="hint">
                                    ${usedBy.length === 1 ? 'One connection uses' : `${usedBy.length} connections use`}
                                    these, so they cannot be removed until those point somewhere else.
                                </span>
                            ` : ''}

                        </form>

                    </details>

                </div>
            `;

        }

        function wire(): void {

            // Which fields are shown follows what kind it is, and it follows it
            // straight away rather than after a save: somebody who picks TOTP
            // and sees no shared secret field concludes the page is broken.
            content.querySelectorAll<HTMLSelectElement>('[data-kind]').forEach(select => {

                const form = select.closest('form');

                select.addEventListener('change', () => {

                    const id     = form?.dataset.kindForm ?? '';
                    const totp   = content.querySelector<HTMLElement>(`[data-totp="${id}"]`);
                    const title  = content.querySelector<HTMLElement>(`[data-secret-title="${id}"]`);

                    if (totp !== null)
                        totp.hidden = select.value !== 'totp';

                    if (title !== null)
                        title.textContent = select.value === 'totp' ? 'Shared secret' : 'Password';

                });

            });

            // A details element that was open stays open across a redraw.
            content.querySelectorAll<HTMLDetailsElement>('[data-details]').forEach(details => {
                details.addEventListener('toggle', () => {
                    const id = details.dataset.details ?? '';
                    if (details.open)
                        opened.add(id);
                    else
                        opened.delete(id);
                });
            });

            must<HTMLFormElement>(content, '#add-form').addEventListener('submit', event => {
                event.preventDefault();
                void add();
            });

            content.querySelectorAll<HTMLFormElement>('[data-edit]').forEach(form => {
                form.addEventListener('submit', event => {
                    event.preventDefault();
                    void save(form.dataset.edit ?? '', form);
                });
            });

            content.addEventListener('click', event => {

                const button = (event.target as Element | null)?.closest<HTMLButtonElement>('[data-remove]');

                if (button && !button.disabled)
                    void remove(button.dataset.remove ?? '');

            });

        }

        /** What a form says, in the shape the station takes. */
        function readFrom(form: HTMLFormElement, id?: string): LoginToSave {

            const kind    = field(form, 'kind') === 'totp' ? 'totp' : 'basic';
            const number  = (name: string): number | undefined => {
                const written = field(form, name);
                return written === '' ? undefined : Number(written);
            };

            const written: LoginToSave = {
                id,
                description:  field(form, 'description'),
                kind,
                login:        field(form, 'login'),
                // Not trimmed: a password may legitimately start or end with a
                // space, and silently taking it off would leave somebody with
                // credentials that are refused for no visible reason.
                secret:       field(form, 'secret', false)
            };

            if (kind === 'totp') {
                written.validitySeconds    = number('validitySeconds');
                written.length             = number('length');
                written.alphabet           = field(form, 'alphabet');
                written.hashAlgorithm      = field(form, 'hashAlgorithm');
                written.tlsChannelBinding  = form.querySelector<HTMLInputElement>('[name="tlsChannelBinding"]')?.checked ?? true;
            }

            return written;

        }


        async function add(): Promise<void> {

            const form = must<HTMLFormElement>(content, '#add-form');
            const note = must<HTMLElement>(content, '#add-note');

            note.textContent = '';
            must<HTMLElement>(content, '#add-error').textContent = '';

            const written = readFrom(form);

            try
            {
                const made = await whileSaving(content, note, () => api.authentications.add(written));

                store = made.connections;

                draw();

                must<HTMLElement>(content, '#add-note').textContent = 'Written down.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#add-error').textContent = errorMessage(problem);
            }

        }

        async function save(id: string, form: HTMLFormElement): Promise<void> {

            if (id === '')
                return;

            const note  = must<HTMLElement>(content, `[data-note="${id}"]`);
            const error = must<HTMLElement>(content, `[data-error="${id}"]`);

            note. textContent = '';
            error.textContent = '';

            const written = readFrom(form, id);

            try
            {
                store = await whileSaving(content, note, () => api.authentications.update(written));

                // Stays open, because somebody who just saved is frequently
                // about to change something else here.
                opened.add(id);

                draw();

                must<HTMLElement>(content, `[data-note="${id}"]`).textContent = 'Saved.';
            }
            catch (problem)
            {
                error.textContent = errorMessage(problem);
            }

        }

        async function remove(id: string): Promise<void> {

            if (id === '')
                return;

            const error = must<HTMLElement>(content, `[data-error="${id}"]`);
            const note  = must<HTMLElement>(content, `[data-note="${id}"]`);

            error.textContent = '';

            try
            {
                store = await whileSaving(content, note, () => api.authentications.remove(id));

                opened.delete(id);

                draw();
            }
            catch (problem)
            {
                error.textContent = errorMessage(problem);
            }

        }

        async function load(): Promise<void> {

            try
            {
                const loaded = await api.authentications.get();

                if (cancelled)
                    return;

                store = loaded;
                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The credentials could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        // A half-typed password is work like any other, and losing one is worse
        // than losing most: it cannot be read back off the page to try again.
        const release = unsaved.heldBy(() => Array.from(content.querySelectorAll<HTMLFormElement>('form')).
                                                   some(form => typedSinceDrawn(form)));

        void load();

        return () => { cancelled = true; release(); };

    }

};
