import { api, type NTSConfiguration, type NTSSyncResult, type NTSUpdate, type TimeServerTest } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, humanizeKey, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * What the NTS client allows itself when the station has not been told.
 *
 * The station's answer carries the timeout it was configured with, and null
 * where it was configured with none - and it does not repeat what the client
 * then falls back to, which is three seconds. This is only used to work out
 * how long this page waits for "Sync now", and the page allows the station
 * fifteen seconds on top of it, so being wrong here by a few seconds costs
 * nothing at all.
 */
const theClientsOwnTimeout = 3;


/**
 * Where this charging station reads the time.
 *
 * Pointing it at another server replaces the client rather than reconfiguring
 * it - the cookies and keys an NTS client holds were issued by the host it was
 * made for - but that happens inside the station and takes effect at once, so
 * nothing here waits for a restart either.
 *
 * "Sync now" does the whole exchange: the key exchange over TLS, then one
 * authenticated NTP request. It writes every step to the log rather than only
 * the outcome, because the useful answer to "why can I not reach my time
 * server" is which step it got to.
 */
export const ntsPage: Page = {

    title: 'NTS client',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/nts',
            title:     'NTS client',
            subtitle:  'Where this charging station reads the time, and how it knows the answer is real.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        // Reload throws a draft away just as thoroughly as "Discard changes"
        // does, and from the opposite corner of the screen, so it asks first.
        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayChange = auth.can('changeNetworkSettings');
        const mayTest   = auth.can('runDiagnostics');

        let cancelled = false;
        let current: NTSConfiguration | null = null;
        let syncing = false;


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const sync          = configuration.result ?? configuration.lastSync;

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the time
                        client but not change it. That needs the CPO or the system administrator role.
                    </div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-power-off"></i> Time synchronisation</h2>

                        <label class="switch">
                            <input type="checkbox" id="enabled"
                                   ${configuration.enabled ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>${configuration.enabled ? 'switched on' : 'switched off'}</span>
                        </label>

                        <p class="hint">
                            Switched off, this station asks its time server nothing at all - the
                            synchronisation below is refused rather than quietly doing nothing.
                        </p>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-clock"></i> Server for the detailed test</h2>

                        <p class="hint">
                            The host and ports the detailed test below starts from. Synchronisation
                            does not come through here - it asks the group of time servers below.
                        </p>

                        <form id="nts-form" class="form-stack">

                            <label>Host name
                                <input type="text" name="hostname" value="${configuration.server.hostname}"
                                       placeholder="ptbtime1.ptb.de" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>NTS-KE port
                                <input type="number" name="ntsKEPort" min="1" max="65535"
                                       value="${configuration.server.ntsKEPort}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>NTP port
                                <input type="number" name="ntpPort" min="1" max="65535"
                                       value="${configuration.server.ntpPort}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Timeout in seconds
                                <input type="number" name="timeoutSeconds" min="0.1" max="${configuration.limits.maxTimeout}"
                                       step="0.1" value="${configuration.settings.timeoutSeconds ?? ''}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                                <span id="form-note"  class="form-notice" role="status"></span>
                                <span id="form-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Saved to ${configuration.file}. Changing the host or a port builds a new
                                client, so the cookies of the old server are let go of along with it.
                            </span>

                        </form>

                    </section>

                    <section class="card wide">

                        <h2><i class="fa-solid fa-rotate"></i> Synchronise</h2>

                        <div class="form-actions">
                            <button type="button" id="sync" class="btn primary" ${mayTest && !syncing ? '' : html`disabled`}>
                                ${syncing ? 'Asking the server ...' : 'Sync now'}
                            </button>
                            <button type="button" id="test" class="btn" ${mayTest ? '' : html`disabled`}>
                                <i class="fa-solid fa-list-check"></i> Test in detail
                            </button>
                            <span id="sync-error" class="form-error" role="alert"></span>
                        </div>

                        <p class="hint">
                            ${mayTest
                                  ? html`
                                      A key exchange over TLS, then one authenticated NTP request. Every step
                                      goes into the log, so the Logs page shows where it got to. The clock of
                                      this station is not stepped by it - that is a different thing, with meter
                                      readings and certificates hanging off it, and not something a button does
                                      by surprise.
                                    `
                                  : html`Running a synchronisation needs the CPO, the installer or the system administrator role.`}
                        </p>

                        ${sync === null || sync === undefined ? '' : syncResult(sync)}

                    </section>

                    ${!configuration.timeSources || configuration.timeSources.length === 0 ? '' : html`
                        <section class="card">

                            <h2><i class="fa-solid fa-users"></i> Time servers</h2>

                            <div class="kv-list">
                                ${configuration.timeSources.map(source => html`
                                    <div class="kv">
                                        <span class="k">${source.hostname}${source.enabled ? '' : html` <span class="muted small">switched off</span>`}</span>
                                        <span class="v">
                                            ${source.lastExchange
                                                  ? html`${formatValue(source.cookies)} cookie(s)
                                                         <span class="muted small">${source.aeadAlgorithm ?? ''}, exchanged ${formatValue(source.lastExchange)}</span>`
                                                  : html`<span class="muted">not asked yet</span>`}
                                        </span>
                                    </div>
                                `)}
                            </div>

                            ${configuration.group
                                  ? html`<p class="hint">
                                             Group '${configuration.group.name}': at least ${configuration.group.minServers}
                                             of them must answer, and a disagreement of
                                             ${configuration.group.maxDeviationSeconds} s or more is written down.
                                         </p>`
                                  : ''}

                        </section>
                    `}

                    <section class="card">
                        <h2><i class="fa-solid fa-cookie-bite"></i> Cookies of that client</h2>
                        <div class="kv-list">
                            ${Object.entries(configuration.cookies).map(([key, value]) => html`
                                <div class="kv">
                                    <span class="k">${humanizeKey(key)}</span>
                                    <span class="v">${formatValue(value)}</span>
                                </div>
                            `)}
                        </div>
                        <p class="hint">
                            One cookie is spent per request and a new one usually comes back with the answer.
                            These stay at zero while nothing spends them: the detailed test builds a fresh
                            client each time it runs, and what a synchronisation spends is shown per server
                            in the card above.
                        </p>
                    </section>

                    <section class="card">
                        <h2><i class="fa-solid fa-scale-balanced"></i> Cookie pool policy</h2>
                        <div class="kv-list">
                            ${Object.entries(configuration.policy).map(([key, value]) => html`
                                <div class="kv">
                                    <span class="k">${humanizeKey(key)}</span>
                                    <span class="v">${formatValue(value)}</span>
                                </div>
                            `)}
                        </div>
                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-key"></i> Key exchange of that client</h2>

                        <div class="kv-list">
                            <div class="kv">
                                <span class="k">Exchanges so far</span>
                                <span class="v">${configuration.keyExchange.automatic}</span>
                            </div>
                            <div class="kv">
                                <span class="k">Offered AEAD algorithms</span>
                                <span class="v">${configuration.keyExchange.aeadAlgorithms.join(', ')}</span>
                            </div>
                            <div class="kv">
                                <span class="k">Compliant exporter context</span>
                                <span class="v">${formatValue(configuration.keyExchange.compliantExporterContext)}</span>
                            </div>
                        </div>

                        ${configuration.keyExchange.lastExchange === null
                              ? html`<p class="muted small">No key exchange has happened yet.</p>`
                              : html`
                                  <div class="kv-list">
                                      <div class="kv">
                                          <span class="k">Last exchange</span>
                                          <span class="v">${configuration.keyExchange.lastExchange.error ?? 'succeeded'}</span>
                                      </div>
                                      ${configuration.keyExchange.lastExchange.servers.length > 0
                                            ? html`
                                                <div class="kv">
                                                    <span class="k">NTP servers named</span>
                                                    <span class="v">
                                                        ${configuration.keyExchange.lastExchange.servers.map(named => html`
                                                            <span class="named-server">
                                                                <code>${named}</code>
                                                                <button type="button" class="btn small" data-test-host="${named}"
                                                                        title="Ask this server everything, on its own"
                                                                        ${mayTest ? '' : html`disabled`}>Test</button>
                                                            </span>
                                                        `)}
                                                    </span>
                                                </div>
                                                <p class="hint">
                                                    Each of these is asked in its own right: its own key exchange and
                                                    its own time request. It has to be - the keys that protect an NTS
                                                    request come out of the exchange that issued the cookies, so
                                                    cookies from one host cannot protect a request to another.
                                                </p>
                                              `
                                            : ''}
                                      ${configuration.keyExchange.lastExchange.warnings.map(warning => html`
                                          <div class="kv">
                                              <span class="k">Warning</span>
                                              <span class="v">${warning}</span>
                                          </div>
                                      `)}
                                  </div>
                              `}

                    </section>

                </div>

            `);

            wire();

        }


        /**
         * Ask one time server everything, in a dialog, line by line.
         *
         * "Sync now" answers whether it worked; this answers where it got to,
         * which is the question somebody has when it did not. The steps are
         * the ones the exchange actually has - the name, the TCP connection,
         * the TLS handshake, the key exchange, the authenticated request - and
         * each is timed, so a server that is merely slow can be told from one
         * that is refusing.
         *
         * @param host  which server, or null for the configured one.
         */
        async function testServer(host: string | null): Promise<void> {

            const dialog = document.createElement('dialog');

            dialog.className = 'test-dialog';

            render(dialog, html`
                <h2>Asking ${host ?? current?.server.hostname ?? 'the time server'}</h2>
                <div class="test-steps" id="test-steps">
                    <div class="loading">Name, key exchange, authenticated time request ...</div>
                </div>
                <div class="form-actions">
                    <button type="button" class="btn" id="test-close" disabled>Close</button>
                </div>
            `);

            document.body.appendChild(dialog);
            dialog.showModal();

            // Both halves explicitly - see the connections page for why the
            // close event cannot be relied on here.
            const dismiss = (): void => { dialog.close(); dialog.remove(); };

            dialog.addEventListener('close',  dismiss);
            dialog.addEventListener('cancel', dismiss);

            const close = must<HTMLButtonElement>(dialog, '#test-close');

            close.addEventListener('click', dismiss);

            let result: TimeServerTest;

            try
            {
                result = await api.nts.test(current?.settings.timeoutSeconds ?? theClientsOwnTimeout,
                                            host ?? undefined);
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
                          ? html`${result.host} answered. ${result.runtime_ms} ms altogether.`
                          : html`${result.host} did not answer. ${result.runtime_ms} ms altogether.`}
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

        function syncResult(sync: NTSSyncResult) {

            return html`
                <div class="query-result ${sync.ok ? 'ok' : 'bad'}">

                    <div class="kv-list">
                        <div class="kv"><span class="k">Result</span><span class="v">${sync.ok ? 'succeeded' : `failed${sync.step ? ` at the ${sync.step === 'ntske' ? 'key exchange' : 'NTP request'}` : ''}`}</span></div>
                        <div class="kv"><span class="k">Server</span><span class="v">${sync.server}</span></div>
                        <div class="kv"><span class="k">At</span><span class="v">${formatValue(sync.at)}</span></div>
                        ${sync.error      ? html`<div class="kv"><span class="k">Error</span><span class="v">${sync.error}</span></div>` : ''}
                        ${sync.runtime_ms ? html`<div class="kv"><span class="k">Took</span><span class="v">${sync.runtime_ms} ms</span></div>` : ''}
                    </div>

                    ${sync.group
                          ? html`
                              <h3>What the group concluded</h3>
                              <div class="kv-list">
                                  ${Object.entries(sync.group).map(([key, value]) => html`
                                      <div class="kv"><span class="k">${humanizeKey(key)}</span><span class="v">${formatValue(value)}</span></div>
                                  `)}
                              </div>
                            `
                          : ''}

                    ${sync.servers && sync.servers.length > 0
                          ? html`
                              <h3>What each server said</h3>
                              <div class="kv-list">
                                  ${sync.servers.map(server => html`
                                      <div class="kv">
                                          <span class="k">${server.hostname}</span>
                                          <span class="v">
                                              ${server.ok
                                                    ? html`${formatValue(server.offset_ms)} ms, round trip ${formatValue(server.roundTrip_ms)} ms
                                                           <span class="muted small">key exchange ${server.keyExchange ?? 'unknown'}</span>`
                                                    : html`<span class="muted">${server.error ?? 'no answer'}</span>`}
                                          </span>
                                      </div>
                                  `)}
                              </div>
                            `
                          : ''}

                    ${sync.ntske
                          ? html`
                              <h3>Key exchange</h3>
                              <div class="kv-list">
                                  ${Object.entries(sync.ntske).map(([key, value]) => html`
                                      <div class="kv"><span class="k">${humanizeKey(key)}</span><span class="v">${formatValue(value)}</span></div>
                                  `)}
                              </div>
                            `
                          : ''}

                    ${sync.ntp
                          ? html`
                              <h3>NTP request</h3>
                              <div class="kv-list">
                                  ${Object.entries(sync.ntp).map(([key, value]) => html`
                                      <div class="kv"><span class="k">${humanizeKey(key)}</span><span class="v">${formatValue(value)}</span></div>
                                  `)}
                              </div>
                            `
                          : ''}

                </div>
            `;

        }


        function wire(): void {

            must<HTMLInputElement>(content, '#enabled').addEventListener('change', event => {
                void save({ enabled: (event.target as HTMLInputElement).checked });
            });

            must<HTMLFormElement>(content, '#nts-form').addEventListener('submit', event => {

                event.preventDefault();

                const data     = new FormData(event.target as HTMLFormElement);
                const timeout  = String(data.get('timeoutSeconds') ?? '').trim();

                const update: NTSUpdate = {
                    hostname:   String(data.get('hostname') ?? '').trim(),
                    ntsKEPort:  Number(data.get('ntsKEPort')),
                    ntpPort:    Number(data.get('ntpPort'))
                };

                if (timeout.length > 0)
                    update.timeoutSeconds = Number(timeout);

                void save(update);

            });

            must<HTMLButtonElement>(content, '#test').addEventListener('click', () => testServer(null));

            content.addEventListener('click', event => {

                const asking = (event.target as HTMLElement).closest<HTMLButtonElement>('[data-test-host]');

                if (asking && !asking.disabled)
                    void testServer(asking.dataset.testHost ?? null);

            });

            must<HTMLButtonElement>(content, '#sync').addEventListener('click', () => void runSync());

        }


        async function save(update: NTSUpdate): Promise<void> {

            const note = must<HTMLElement>(content, '#form-note');

            note.textContent = '';

            must<HTMLElement>(content, '#form-error').textContent = '';

            try
            {
                current = await whileSaving(content, note, () => api.nts.save(update));
                draw();
                must<HTMLElement>(content, '#form-note').textContent = 'Saved, and in effect.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#form-error').textContent = errorMessage(problem);
            }

        }


        async function runSync(): Promise<void> {

            must<HTMLElement>(content, '#sync-error').textContent = '';

            syncing = true;
            draw();

            try
            {
                // The answer carries the whole configuration as well as the
                // result, because an exchange moves the cookie pool and the
                // record of the last key exchange that this page is showing.
                current = await api.nts.sync(current?.settings.timeoutSeconds ?? theClientsOwnTimeout);
            }
            catch (problem)
            {
                syncing = false;
                draw();
                must<HTMLElement>(content, '#sync-error').textContent = errorMessage(problem);
                return;
            }

            syncing = false;
            draw();

        }


        async function load(): Promise<void> {

            try
            {
                const loaded = await api.nts.get();

                if (!cancelled) {
                    current = loaded;
                    draw();
                }
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The NTS configuration could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#nts-form')));

        void load();

        return () => { cancelled = true; release(); };

    }

};
