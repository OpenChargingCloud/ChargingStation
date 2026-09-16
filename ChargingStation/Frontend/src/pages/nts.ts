import { api, type NTSConfiguration, type NTSSyncResult, type NTSUpdate } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, humanizeKey } from '../ui';

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

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => void load());

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

                        <h2><i class="fa-solid fa-clock"></i> Server</h2>

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

                    <section class="card">
                        <h2><i class="fa-solid fa-cookie-bite"></i> Cookies</h2>
                        <div class="kv-list">
                            ${Object.entries(configuration.cookies).map(([key, value]) => html`
                                <div class="kv">
                                    <span class="k">${humanizeKey(key)}</span>
                                    <span class="v">${formatValue(value)}</span>
                                </div>
                            `)}
                        </div>
                        <p class="hint">One cookie is spent per request and a new one usually comes back with the answer.</p>
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

                        <h2><i class="fa-solid fa-key"></i> Key exchange</h2>

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
                                                    <span class="v">${configuration.keyExchange.lastExchange.servers.join(', ')}</span>
                                                </div>
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

            must<HTMLButtonElement>(content, '#sync').addEventListener('click', () => void runSync());

        }


        async function save(update: NTSUpdate): Promise<void> {

            const note  = must<HTMLElement>(content, '#form-note');
            const error = must<HTMLElement>(content, '#form-error');

            note.textContent  = '';
            error.textContent = '';

            try
            {
                current = await api.nts.save(update);
                draw();
                must<HTMLElement>(content, '#form-note').textContent = 'Saved, and in effect.';
            }
            catch (problem)
            {
                error.textContent = errorMessage(problem);
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

        void load();

        return () => { cancelled = true; };

    }

};
