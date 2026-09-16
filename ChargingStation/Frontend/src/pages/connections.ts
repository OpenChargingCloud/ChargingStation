import { api, type ConnectionTest, type ConnectionToSave, type StationConnection, type StationConnections } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, field, formatTimestamp, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * The places this charging station dials.
 *
 * A connection names the credentials it uses rather than carrying them: a
 * station that reaches its management system twice - normally and through a
 * spare address - proves itself the same way both times, and a password written
 * down twice is a password that only gets changed once.
 *
 * How it proves itself is one decision, so it is one control: nothing, a set of
 * credentials from the Authentication page, or one of this station's client
 * certificates. Two at once would leave somebody guessing which was used.
 *
 * What is configured but cannot work is said here rather than discovered at the
 * next connection - a password going out over a connection without TLS, a
 * certificate on a URL that makes no TLS handshake, credentials that were
 * removed next door. None of it is refused, because a station on a bench
 * talking to a test back end over ws:// is a real thing to want.
 */
export const connectionsPage: Page = {

    title: 'Connections',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/connections',
            title:     'Connections',
            subtitle:  'Where this charging station dials, and what it proves itself with.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayManage = auth.can('changeNetworkSettings');

        // Its own permission: a test makes this station open a connection to a
        // host and show it a credential, which is more than reading a page and
        // less than changing what the station does.
        const mayTest   = auth.can('runDiagnostics');

        let cancelled = false;
        let store: StationConnections | null = null;

        const opened = new Set<string>();

        /** What the one authentication control calls each choice. */
        function chosenOf(entry: StationConnection | null): string {
            if (entry?.authenticationId !== undefined && entry.authenticationId !== null)
                return `auth:${entry.authenticationId}`;
            if (entry?.certificateId !== undefined && entry.certificateId !== null)
                return `cert:${entry.certificateId}`;
            return '';
        }


        function draw(): void {

            if (store === null)
                return;

            const state = store;

            render(content, html`

                ${mayManage ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the
                        connections but not change them. That needs the role that changes how this station
                        reaches the outside world.
                    </div>
                `}

                <section class="card">

                    <h2><i class="fa-solid fa-plus"></i> Write down a connection</h2>

                    <form id="add-form" class="form-stack">
                        ${theFields('add', null, state)}
                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${mayManage ? '' : html`disabled`}>
                                Write it down
                            </button>
                            <button type="button" class="btn small" data-test="add"
                                    ${mayTest ? '' : html`disabled`}>
                                <i class="fa-solid fa-plug-circle-check"></i> Test it
                            </button>
                            <span id="add-note"  class="form-notice" role="status"></span>
                            <span id="add-error" class="form-error"  role="alert"></span>
                        </div>
                        <span class="hint">
                            Testing does not write anything down: it makes the connection once, says what
                            happened, and lets go. Worth doing before saving rather than after.
                        </span>
                    </form>

                </section>

                <section class="card">

                    <h2><i class="fa-solid fa-list"></i> What is configured</h2>

                    <p class="hint">Newest first. Written to ${state.directory}.</p>

                    ${state.connections.length === 0
                          ? html`
                                <p class="hint">
                                    Nothing yet. This station dials nowhere and waits to be dialled.
                                </p>
                            `
                          : html`<div class="cards">${state.connections.map(entry => entryCard(entry, state))}</div>`}

                </section>

            `);

            wire();

        }

        /** The same fields whether it is new or being changed, so the two cannot drift apart. */
        function theFields(id: string, entry: StationConnection | null, state: StationConnections): HTMLFragment {

            const chosen = chosenOf(entry);

            return html`

                <label>What it is
                    <input type="text" name="description" maxlength="${state.maxDescriptionLength}"
                           placeholder="CSMS, main" value="${entry?.description ?? ''}"
                           ${mayManage ? '' : html`disabled`} />
                </label>

                <label>Where it goes
                    <input type="text" name="url" class="mono"
                           placeholder="wss://csms.example.org/cs001" value="${entry?.url ?? ''}"
                           ${mayManage ? '' : html`disabled`} />
                    <span class="hint">
                        Write the scheme. <code>wss://</code> is encrypted and <code>ws://</code> is not, and
                        this station will not decide that for you - everything below depends on which it is.
                    </span>
                </label>

                <div class="form-row">

                    <label>What is at the other end
                        <select name="connectionType" ${mayManage ? '' : html`disabled`}>
                            ${state.connectionTypes.map(one => html`
                                <option value="${one}" ${one === (entry?.connectionType ?? 'CSMS') ? html`selected` : ''}>
                                    ${one === 'CSMS'       ? 'Charging station management system'
                                      : one === 'CSMSBackup' ? 'Management system, spare'
                                      : 'Local controller'}
                                </option>
                            `)}
                        </select>
                        <span class="hint">
                            What it is, for whoever reads this page. Every connection is dialled on its own;
                            nothing here waits for anything else.
                        </span>
                    </label>

                    <label>Which OCPP
                        <select name="ocppVersion" ${mayManage ? '' : html`disabled`}>
                            ${state.ocppVersions.map(one => html`
                                <option value="${one}" ${one === (entry?.ocppVersion ?? 'OCPP2.1') ? html`selected` : ''}>
                                    ${one}
                                </option>
                            `)}
                        </select>
                        <span class="hint">
                            This station is two nodes and a URL does not say which one should dial, so it
                            is said here.
                        </span>
                    </label>

                </div>

                <label class="checkbox">
                    <input type="checkbox" name="autoConnect"
                           ${(entry?.autoConnect ?? false) ? html`checked` : ''}
                           ${mayManage ? '' : html`disabled`} />
                    Connect by itself, and again after a drop
                    <span class="hint">
                        This is what decides whether the connection is used at all. Off, it stays written
                        down and nothing happens - an address kept ready for the day somebody needs it,
                        which can still be tried by hand with the button below.
                    </span>
                </label>

                <label>How it proves itself
                    <select name="proves" ${mayManage ? '' : html`disabled`}>

                        <option value="" ${chosen === '' ? html`selected` : ''}>
                            Nothing - the back end must not ask
                        </option>

                        ${state.authentications.length === 0 ? '' : html`
                            <optgroup label="Credentials">
                                ${state.authentications.map(one => html`
                                    <option value="auth:${one.id}" ${chosen === `auth:${one.id}` ? html`selected` : ''}>
                                        ${one.description} (${one.kind === 'basic' ? 'HTTP Basic' : 'HTTP TOTP'})${one.hasSecret ? '' : ' - no secret set'}
                                    </option>
                                `)}
                            </optgroup>
                        `}

                        ${state.certificates.length === 0 ? '' : html`
                            <optgroup label="TLS client certificates">
                                ${state.certificates.map(one => html`
                                    <option value="cert:${one.id}" ${chosen === `cert:${one.id}` ? html`selected` : ''}>
                                        ${one.subject || one.id} (${one.algorithm})${one.hasCertificate ? '' : ' - request still out'}
                                    </option>
                                `)}
                            </optgroup>
                        `}

                    </select>
                    <span class="hint">
                        One of them. Credentials come from the Authentication page and certificates from the
                        Certificates page; anything missing here is missing there.
                    </span>
                </label>

            `;

        }

        function entryCard(entry: StationConnection, state: StationConnections): HTMLFragment {

            const credentials = state.authentications.find(one => one.id === entry.authenticationId);
            const certificate = state.certificates.   find(one => one.id === entry.certificateId);

            return html`
                <div class="card">

                    <div class="key-head">
                        <strong>${entry.description}</strong>
                        <span class="chip">${entry.connectionType}</span>
                        <span class="chip">${entry.ocppVersion}</span>
                        ${entry.secure
                              ? html`<span class="chip on">TLS</span>`
                              : html`<span class="chip warn">no TLS</span>`}
                        ${entry.autoConnect
                              ? html`<span class="chip on">connects by itself</span>`
                              : html`<span class="chip">configured only</span>`}
                    </div>

                    <dl class="kv">
                        <dt>Where</dt>   <dd><code>${entry.url}</code></dd>
                        <dt>Proves itself</dt>
                        <dd>
                            ${credentials !== undefined
                                  ? html`${credentials.description} (${credentials.kind === 'basic' ? 'HTTP Basic' : 'HTTP TOTP'})`
                                  : certificate !== undefined
                                        ? html`Client certificate ${certificate.subject || certificate.id}`
                                        : entry.authenticationId !== undefined || entry.certificateId !== undefined
                                              ? html`<span class="hint">something that is no longer here</span>`
                                              : html`<span class="hint">nothing</span>`}
                        </dd>
                        <dt>Written</dt> <dd>${formatTimestamp(entry.createdAt)}</dd>
                    </dl>

                    ${(entry.warnings ?? []).map(warning => html`<div class="notice">${warning}</div>`)}

                    <details ${opened.has(entry.id) ? html`open` : ''} data-details="${entry.id}">

                        <summary>Change it</summary>

                        <form class="form-stack" data-edit="${entry.id}">

                            ${theFields(entry.id, entry, state)}

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayManage ? '' : html`disabled`}>
                                    Save
                                </button>
                                <button type="button" class="btn small" data-remove="${entry.id}"
                                        ${mayManage ? '' : html`disabled`}>
                                    Remove
                                </button>
                                <button type="button" class="btn small" data-test="${entry.id}"
                                        ${mayTest ? '' : html`disabled`}>
                                    <i class="fa-solid fa-plug-circle-check"></i> Test it
                                </button>
                                <span class="form-notice" data-note="${entry.id}"  role="status"></span>
                                <span class="form-error"  data-error="${entry.id}" role="alert"></span>
                            </div>

                        </form>

                    </details>

                </div>
            `;

        }

        function wire(): void {

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

                const target = event.target as Element | null;

                const removing = target?.closest<HTMLButtonElement>('[data-remove]');

                if (removing && !removing.disabled)
                {
                    void remove(removing.dataset.remove ?? '');
                    return;
                }

                const testing = target?.closest<HTMLButtonElement>('[data-test]');

                if (testing && !testing.disabled)
                    void test(testing.closest('form'));

            });

        }

        /**
         * What a form says, in the shape the station takes.
         *
         * The one control that picks how a connection proves itself becomes the
         * two fields the station stores, here and nowhere else - which is what
         * keeps "one of them" true by construction rather than by a check
         * somebody has to remember.
         */
        function readFrom(form: HTMLFormElement, id?: string): ConnectionToSave {

            const proves = field(form, 'proves');

            return {
                id,
                description:         field(form, 'description'),
                url:                 field(form, 'url'),
                connectionType:      field(form, 'connectionType'),
                ocppVersion:         field(form, 'ocppVersion'),
                autoConnect:  form.querySelector<HTMLInputElement>('[name="autoConnect"]')?.checked ?? false,
                authenticationId:    proves.startsWith('auth:') ? proves.slice(5) : null,
                certificateId:       proves.startsWith('cert:') ? proves.slice(5) : null
            };

        }


        async function add(): Promise<void> {

            const form = must<HTMLFormElement>(content, '#add-form');
            const note = must<HTMLElement>(content, '#add-note');

            note.textContent = '';
            must<HTMLElement>(content, '#add-error').textContent = '';

            const written = readFrom(form);

            try
            {
                const made = await whileSaving(content, note, () => api.connections.add(written));

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
                store = await whileSaving(content, note, () => api.connections.update(written));

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
                store = await whileSaving(content, note, () => api.connections.remove(id));

                opened.delete(id);

                draw();
            }
            catch (problem)
            {
                error.textContent = errorMessage(problem);
            }

        }

        /**
         * Test one connection, in a dialog that stays put until it is closed.
         *
         * The dialog is opened before the request rather than after it comes
         * back, because the whole thing takes some seconds and a button that
         * does nothing visible for that long is a button somebody presses
         * again. What is shown while waiting says what is being tried, so the
         * wait is legible rather than merely long.
         */
        async function test(form: HTMLFormElement | null): Promise<void> {

            if (form === null)
                return;

            // What is on the screen, not what is on disk. Beside the add form
            // there is nothing on disk yet, and beside an entry being edited
            // the two are frequently different - and the one somebody is
            // looking at is the one they mean.
            const entry  = readFrom(form);

            const dialog = document.createElement('dialog');

            dialog.className = 'test-dialog';

            render(dialog, html`
                <h2>Testing ${entry.description || 'this connection'}</h2>
                <p class="hint"><code>${entry.url || 'no URL given'}</code></p>
                <div class="test-steps" id="test-steps">
                    <div class="loading">Connecting, and staying connected for two seconds ...</div>
                </div>
                <div class="form-actions">
                    <button type="button" class="btn" id="test-close" disabled>Close</button>
                </div>
            `);

            document.body.appendChild(dialog);
            dialog.showModal();

            /**
             * Shut it and take it away.
             *
             * Both halves explicitly, rather than removing it when the dialog
             * reports that it closed. Measured: the browser this station's
             * pages were tried in does not fire `close` at all - not on
             * `dialog.close()` and not on Escape - so a dialog cleaned up that
             * way stays in the page, closed and invisible, one more of them
             * after every test. Calling this twice is harmless, which is what
             * lets it be wired to everything that might end the dialog.
             */
            const dismiss = (): void => {
                dialog.close();
                dialog.remove();
            };

            // Whichever of these the browser actually delivers.
            dialog.addEventListener('close',  dismiss);
            dialog.addEventListener('cancel', dismiss);

            const close = must<HTMLButtonElement>(dialog, '#test-close');

            close.addEventListener('click', dismiss);

            let result: ConnectionTest;

            try
            {
                result = await api.connections.test(entry);
            }
            catch (problem)
            {
                render(must<HTMLElement>(dialog, '#test-steps'), html`
                    <div class="error-box">The test could not be run: ${errorMessage(problem)}</div>
                `);
                close.disabled = false;
                close.focus();
                return;
            }

            render(must<HTMLElement>(dialog, '#test-steps'), html`
                <div class="${result.ok ? 'notice' : 'error-box'}">
                    ${result.ok
                          ? html`The connection can be made. ${result.runtime_ms} ms altogether.`
                          : html`The connection could not be made. ${result.runtime_ms} ms altogether.`}
                </div>
                <ol class="test-log">
                    ${result.steps.map(step => html`
                        <li class="level-${step.level}">
                            <span class="at">+${step.at_ms} ms</span>
                            <span class="text">${step.text}</span>
                        </li>
                    `)}
                </ol>
            `);

            close.disabled = false;
            close.focus();

        }

        async function load(): Promise<void> {

            try
            {
                const loaded = await api.connections.get();

                if (cancelled)
                    return;

                store = loaded;
                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The connections could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => Array.from(content.querySelectorAll<HTMLFormElement>('form')).
                                                   some(form => typedSinceDrawn(form)));

        void load();

        return () => { cancelled = true; release(); };

    }

};
