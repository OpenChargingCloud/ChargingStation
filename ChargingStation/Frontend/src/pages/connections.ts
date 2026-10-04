import { api, type ConnectionState, type ConnectionTest, type ConnectionToSave, type StationConnection, type StationConnections } from '../api/client';
import { auth } from '../auth';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, field, formatTime, formatTimestamp, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';
import { noLongerWrittenDown, stateLine, type StateLine } from './connectionStates';

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
 *
 * And where each connection stands - connected, lost and coming back, turned
 * away - asked again every few seconds while the page is in view, because this
 * is where somebody looks to find out whether the station is on its back end,
 * and a page that was right when it was opened is not the answer to that.
 */
export const connectionsPage: Page = {

    title: 'Connections',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/connections',
            title:     'Connections',
            subtitle:  'Where this charging station dials, and what it proves itself with.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayManage = auth.can('connections', 'edit');

        // Its own permission: a test makes this station open a connection to a
        // host and show it a credential, which is more than reading a page and
        // less than changing what the station does.
        const mayTest   = auth.can('connections', 'run');

        let cancelled = false;
        let store: StationConnections | null = null;

        // Where the connections stand, and the station's clock when it said
        // so - kept apart from the store, because they are asked again on
        // their own. Drawn with the rest of the page: a draw leaves what is
        // typed into the forms where it is.
        let states:     Record<string, ConnectionState> = {};
        let timestamp   = '';
        let notAnswered: string | null = null;

        const opened = new Set<string>();

        /**
         * The connections as the station has them now. Whatever the store
         * came with is the newest there is of where they stand: it was
         * loaded, or written, a moment ago.
         */
        function took(State: StationConnections): void {
            store        = State;
            states       = State.states ?? {};
            timestamp    = State.timestamp ?? timestamp;
            notAnswered  = null;
        }

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

                ${mayManage ? nothing : html`
                    <div class="notice">${mayButNot('look at the connections', 'change them')}</div>
                `}

                <section class="card">

                    <h2><i class="fa-solid fa-plus"></i> Write down a connection</h2>

                    <form id="add-form" class="form-stack" @submit=${add}>
                        ${theFields('add', null, state)}
                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayManage}>
                                Write it down
                            </button>
                            <button type="button" class="btn small" data-test="add"
                                    ?disabled=${!mayTest} @click=${testThisForm}>
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

                    <p class="hint" id="states-note" role="status">${notAnswered === null
                        ? nothing
                        : `Where the connections stand is as it was at ${formatTime(timestamp)}: ` +
                          `asking again did not work (${notAnswered}).`}</p>

                    ${state.connections.length === 0
                          ? Object.keys(states).length === 0
                                ? html`
                                      <p class="hint">
                                          Nothing yet. This station dials nowhere and waits to be dialled.
                                      </p>
                                  `
                                : html`<p class="hint">Nothing written down any more.</p>`
                          : html`<div class="cards">${repeat(state.connections, entry => entry.id, entry => entryCard(entry, state))}</div>`}

                    <div id="no-longer-written-down">${noLongerWrittenDownView()}</div>

                </section>

            `);

        }

        /** The same fields whether it is new or being changed, so the two cannot drift apart. */
        function theFields(id: string, entry: StationConnection | null, state: StationConnections): TemplateResult {

            const chosen = chosenOf(entry);

            return html`

                <label>What it is
                    <input type="text" name="description" maxlength="${state.maxDescriptionLength}"
                           placeholder="CSMS, main" value="${entry?.description ?? ''}"
                           ?disabled=${!mayManage} />
                </label>

                <label>Where it goes
                    <input type="text" name="url" class="mono"
                           placeholder="wss://csms.example.org/cs001" value="${entry?.url ?? ''}"
                           ?disabled=${!mayManage} />
                    <span class="hint">
                        Write the scheme. <code>wss://</code> is encrypted and <code>ws://</code> is not, and
                        this station will not decide that for you - everything below depends on which it is.
                    </span>
                </label>

                <div class="form-row">

                    <label>What is at the other end
                        <select name="connectionType" ?disabled=${!mayManage}>
                            ${state.connectionTypes.map(one => html`
                                <option value="${one}" ?selected=${one === (entry?.connectionType ?? 'CSMS')}>
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
                        <select name="ocppVersion" ?disabled=${!mayManage}>
                            ${state.ocppVersions.map(one => html`
                                <option value="${one}" ?selected=${one === (entry?.ocppVersion ?? 'OCPP2.1')}>
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
                           ?checked=${entry?.autoConnect ?? false}
                           ?disabled=${!mayManage} />
                    Connect by itself, and again after a drop
                    <span class="hint">
                        This is what decides whether the connection is used at all. Off, it stays written
                        down and nothing happens - an address kept ready for the day somebody needs it,
                        which can still be tried by hand with the button below.
                    </span>
                </label>

                <label>How it proves itself
                    <select name="proves" ?disabled=${!mayManage}>

                        <option value="" ?selected=${chosen === ''}>
                            Nothing - the back end must not ask
                        </option>

                        ${state.authentications.length === 0 ? nothing : html`
                            <optgroup label="Credentials">
                                ${state.authentications.map(one => html`
                                    <option value="auth:${one.id}" ?selected=${chosen === `auth:${one.id}`}>
                                        ${one.description} (${one.kind === 'basic' ? 'HTTP Basic' : 'HTTP TOTP'})${one.hasSecret ? '' : ' - no secret set'}
                                    </option>
                                `)}
                            </optgroup>
                        `}

                        ${state.certificates.length === 0 ? nothing : html`
                            <optgroup label="TLS client certificates">
                                ${state.certificates.map(one => html`
                                    <option value="cert:${one.id}" ?selected=${chosen === `cert:${one.id}`}>
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

        function entryCard(entry: StationConnection, state: StationConnections): TemplateResult {

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

                    <div class="connection-state" data-state="${entry.id}">${stateView(entry)}</div>

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

                    <details ?open=${opened.has(entry.id)} data-details="${entry.id}"
                             @toggle=${(event: Event) => toggled(entry.id, (event.target as HTMLDetailsElement).open)}>

                        <summary>Change it</summary>

                        <form class="form-stack" data-id="${entry.id}" data-edit="${entry.id}"
                              @submit=${(event: SubmitEvent) => { event.preventDefault(); void save(entry.id, event.currentTarget as HTMLFormElement); }}>

                            ${theFields(entry.id, entry, state)}

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayManage}>
                                    Save
                                </button>
                                <button type="button" class="btn small" data-remove="${entry.id}"
                                        ?disabled=${!mayManage} @click=${() => void remove(entry.id)}>
                                    Remove
                                </button>
                                <button type="button" class="btn small" data-test="${entry.id}"
                                        ?disabled=${!mayTest} @click=${testThisForm}>
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

        /** Where one connection stands, as a chip, since when, and what the station said. */
        function stateView(entry: StationConnection): TemplateResult | typeof nothing {

            const line = stateLine(entry, states[entry.id], timestamp);

            return line === null ? nothing : lineView(line);

        }

        function lineView(line: StateLine): TemplateResult {

            return html`
                <div class="state-head">
                    <span class="chip ${line.tone}">${line.chip}</span>
                    <span class="hint">${line.when}</span>
                </div>
                <div class="hint">${line.said}</div>
                ${line.notes.map(note => html`<div class="notice small">${note}</div>`)}
            `;

        }

        /**
         * The connections removed here that the station still dials: it hangs
         * them up at its next start, and until then it may well be on a back
         * end this page no longer mentions.
         */
        function noLongerWrittenDownView(): TemplateResult | typeof nothing {

            const left = noLongerWrittenDown(store?.connections ?? [], states);

            if (left.length === 0)
                return nothing;

            return html`
                <div class="notice">
                    Removed here, and dialled as before until this station starts again:
                    ${left.map(([ , state ]) => {
                        const line = stateLine({ url: state.url, ocppVersion: state.ocppVersion, autoConnect: true }, state, timestamp);
                        return html`
                            <div class="removed-state">
                                <strong>${state.description}</strong> <code>${state.url}</code>
                                ${line === null ? nothing : lineView(line)}
                            </div>
                        `;
                    })}
                </div>
            `;

        }

        let asking = false;

        /**
         * Ask where the connections stand, while the page is in view.
         *
         * Not while it is hidden: nobody is looking, and a tab left open in the
         * background has no business asking the station something every few
         * seconds for as long as the browser runs. Not twice at once either -
         * a station slow to answer is not helped by being asked again.
         */
        async function askAgain(): Promise<void> {

            if (asking || store === null || document.hidden)
                return;

            asking = true;

            try
            {
                const answer = await api.connections.states();

                if (cancelled)
                    return;

                states       = answer.states;
                timestamp    = answer.timestamp;
                notAnswered  = null;
            }
            catch (problem)
            {
                notAnswered  = errorMessage(problem);
            }
            finally
            {
                asking = false;
            }

            // The whole page, which leaves what is typed into its forms where
            // it is: only what differs is drawn.
            if (!cancelled)
                draw();

        }

        /** A details element that was open stays open across a redraw. */
        function toggled(id: string, open: boolean): void {
            if (open)
                opened.add(id);
            else
                opened.delete(id);
        }

        /** Test what the form the button is in says. */
        function testThisForm(event: Event): void {
            void test((event.currentTarget as HTMLElement).closest('form'));
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


        async function add(event: SubmitEvent): Promise<void> {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;
            const note = must<HTMLElement>(content, '#add-note');

            note.textContent = '';
            must<HTMLElement>(content, '#add-error').textContent = '';

            const written = readFrom(form);

            try
            {
                const made = await whileSaving(content, note, () => api.connections.add(written));

                if (cancelled)
                    return;

                took(made.connections);

                draw();

                // Written down, so no longer typed: the form is empty again.
                form.reset();

                note.textContent = 'Written down.';
            }
            catch (problem)
            {
                if (!cancelled)
                    must<HTMLElement>(content, '#add-error').textContent = errorMessage(problem);
            }

        }

        async function save(id: string, form: HTMLFormElement): Promise<void> {

            const note  = must<HTMLElement>(content, `[data-note="${id}"]`);
            const error = must<HTMLElement>(content, `[data-error="${id}"]`);

            note. textContent = '';
            error.textContent = '';

            const written = readFrom(form, id);

            try
            {
                took(await whileSaving(content, note, () => api.connections.update(written)));

                if (cancelled)
                    return;

                opened.add(id);

                draw();

                // A draw leaves a form as it is typed into; the one saved goes
                // back to what it says now - the station's answer.
                form.reset();

                note.textContent = 'Saved.';
            }
            catch (problem)
            {
                if (!cancelled)
                    error.textContent = errorMessage(problem);
            }

        }

        async function remove(id: string): Promise<void> {

            const error = must<HTMLElement>(content, `[data-error="${id}"]`);
            const note  = must<HTMLElement>(content, `[data-note="${id}"]`);

            error.textContent = '';

            try
            {
                took(await whileSaving(content, note, () => api.connections.remove(id)));

                if (cancelled)
                    return;

                opened.delete(id);

                draw();
            }
            catch (problem)
            {
                if (!cancelled)
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

            /**
             * The dialog, saying what is being tried and then what came of it:
             * drawn as a whole each time, so that what it said while waiting
             * goes when the answer comes.
             */
            const drawDialog = (Steps: TemplateResult, Waiting: boolean): void => render(dialog, html`
                <h2>Testing ${entry.description || 'this connection'}</h2>
                <p class="hint"><code>${entry.url || 'no URL given'}</code></p>
                <div class="test-steps" id="test-steps">${Steps}</div>
                <div class="form-actions">
                    <button type="button" class="btn" id="test-close" ?disabled=${Waiting} @click=${dismiss}>Close</button>
                </div>
            `);

            drawDialog(html`<div class="loading">Connecting, and staying connected for two seconds ...</div>`, true);

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
            function dismiss(): void {
                dialog.close();
                dialog.remove();
            }

            // Whichever of these the browser actually delivers.
            dialog.addEventListener('close',  dismiss);
            dialog.addEventListener('cancel', dismiss);

            let result: ConnectionTest;

            try
            {
                result = await api.connections.test(entry);
            }
            catch (problem)
            {
                drawDialog(html`
                    <div class="error-box">The test could not be run: ${errorMessage(problem)}</div>
                `, false);
                must<HTMLButtonElement>(dialog, '#test-close').focus();
                return;
            }

            drawDialog(html`
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
            `, false);

            must<HTMLButtonElement>(dialog, '#test-close').focus();

        }

        /**
         * The page as the station has it now, drawn over the page as it is -
         * what is typed into its forms kept, as a draw keeps it. Reload empties
         * them itself.
         */
        async function load(): Promise<void> {

            try
            {
                const loaded = await api.connections.get();

                if (cancelled)
                    return;

                took(loaded);
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

        /**
         * Loaded anew - Reload - is what the station has, the forms too, which
         * a draw on its own would leave as typed.
         */
        async function reload(): Promise<void> {
            await load();
            if (!cancelled)
                content.querySelectorAll('form').forEach(form => form.reset());
        }

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        // Every five seconds: a connection that is lost is tried again after a
        // second or so, so this is about as late as the page can be told of it
        // without asking more often than a person could read.
        const askingAgain = setInterval(() => void askAgain(), 5_000);

        return () => { cancelled = true; clearInterval(askingAgain); release(); };

    }

};
