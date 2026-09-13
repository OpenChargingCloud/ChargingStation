import { api, type DNSConfiguration, type DNSSettings } from '../api/client';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, humanizeKey } from '../ui';

/**
 * How this charging station resolves names.
 *
 * Two halves, and the page says which is which: what the DNS client was handed
 * when it was made - its servers above all - cannot be changed without making
 * another one, so it is shown and not offered. What the client exposes as a
 * setting is a form, and saving it takes effect on the next query.
 */
export const dnsPage: Page = {

    title: 'DNS client',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/dns',
            title:     'DNS client',
            subtitle:  'How this charging station resolves names.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => void load());

        let cancelled = false;

        function draw(configuration: DNSConfiguration): void {

            render(content, html`

                <div class="cards">

                    <section class="card">
                        <h2><i class="fa-solid fa-server"></i> Name servers</h2>
                        ${configuration.servers.length === 0
                              ? html`<p class="muted small">This client has no name server configured.</p>`
                              : html`
                                  <div class="kv-list">
                                      ${configuration.servers.map(server => html`
                                          <div class="nested-item">
                                              <div class="kv">
                                                  <span class="k">Address</span>
                                                  <span class="v">${server.address ?? server.domainName ?? '-'}:${server.port}</span>
                                              </div>
                                              <div class="kv">
                                                  <span class="k">Transport</span>
                                                  <span class="v">${server.transport}</span>
                                              </div>
                                              ${server.queryTimeout
                                                    ? html`<div class="kv"><span class="k">Query timeout</span><span class="v">${server.queryTimeout}</span></div>`
                                                    : ''}
                                          </div>
                                      `)}
                                  </div>
                              `}
                        <p class="hint">
                            The servers are handed to the client when it is made and cannot be
                            exchanged while the station runs.
                        </p>
                    </section>

                    <section class="card">
                        <h2><i class="fa-solid fa-sliders"></i> Settings</h2>

                        <form id="dns-form" class="form-stack">

                            <label class="checkbox">
                                <input type="checkbox" name="useCache" ${configuration.settings.useCache ? html`checked` : ''} />
                                Use the cache
                                <span class="hint">Answer from what was already asked, for as long as its time to live says.</span>
                            </label>

                            <label class="checkbox">
                                <input type="checkbox" name="dnssecOK" ${configuration.settings.dnssecOK ? html`checked` : ''} />
                                DNSSEC OK
                                <span class="hint">Ask the server for the signatures, by setting the DO bit.</span>
                            </label>

                            <label class="checkbox">
                                <input type="checkbox" name="followCNAMEs" ${configuration.settings.followCNAMEs ? html`checked` : ''} />
                                Follow CNAMEs
                            </label>

                            <label>Recursion desired
                                <select name="recursionDesired">
                                    <option value=""      ${configuration.settings.recursionDesired === null  ? html`selected` : ''}>leave it to the server</option>
                                    <option value="true"  ${configuration.settings.recursionDesired === true  ? html`selected` : ''}>yes</option>
                                    <option value="false" ${configuration.settings.recursionDesired === false ? html`selected` : ''}>no</option>
                                </select>
                            </label>

                            <label>Maximum CNAME follows
                                <input type="number" name="maxCNAMEFollows" min="0" max="255" value="${configuration.settings.maxCNAMEFollows}" />
                            </label>

                            <label>Maximum retries
                                <input type="number" name="maxRetries" min="0" max="255" value="${configuration.settings.maxRetries}" />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary">Save</button>
                                <span id="form-note"  class="form-notice" role="status"></span>
                                <span id="form-error" class="form-error"  role="alert"></span>
                            </div>

                        </form>
                    </section>

                    <section class="card">
                        <h2><i class="fa-solid fa-circle-info"></i> Queries</h2>
                        <div class="kv-list">
                            ${(['queryTimeout', 'udpPayloadSize', 'ednsOptions', 'clientSubnet'] as const).map(key => html`
                                <div class="kv">
                                    <span class="k">${humanizeKey(key)}</span>
                                    <span class="v">${formatValue(configuration[key])}</span>
                                </div>
                            `)}
                        </div>
                    </section>

                    <section class="card">
                        <h2><i class="fa-solid fa-box-archive"></i> Cache</h2>
                        <div class="kv-list">
                            ${Object.entries(configuration.cache).map(([key, value]) => html`
                                <div class="kv">
                                    <span class="k">${humanizeKey(key)}</span>
                                    <span class="v">${formatValue(value)}</span>
                                </div>
                            `)}
                        </div>
                    </section>

                </div>

            `);

            const form   = must<HTMLFormElement>(content, '#dns-form');
            const note   = must<HTMLElement>(content, '#form-note');
            const error  = must<HTMLElement>(content, '#form-error');
            const button = must<HTMLButtonElement>(form, 'button[type="submit"]');

            form.addEventListener('submit', event => {

                event.preventDefault();

                note.textContent   = '';
                error.textContent  = '';
                button.disabled    = true;

                const data = new FormData(form);
                const tri  = String(data.get('recursionDesired') ?? '');

                const settings: Partial<DNSSettings> = {
                    useCache:          data.get('useCache')     !== null,
                    dnssecOK:          data.get('dnssecOK')     !== null,
                    followCNAMEs:      data.get('followCNAMEs') !== null,
                    recursionDesired:  tri === '' ? null : tri === 'true',
                    maxCNAMEFollows:   Number(data.get('maxCNAMEFollows')),
                    maxRetries:        Number(data.get('maxRetries'))
                };

                void (async () => {
                    try
                    {
                        // The answer is the whole configuration as it now
                        // stands, so the page shows what the station took
                        // rather than what the form sent.
                        draw(await api.dns.save(settings));
                        must<HTMLElement>(content, '#form-note').textContent = 'Saved.';
                    }
                    catch (problem)
                    {
                        error.textContent = errorMessage(problem);
                        button.disabled   = false;
                    }
                })();

            });

        }

        async function load(): Promise<void> {

            try
            {
                const configuration = await api.dns.get();

                if (!cancelled)
                    draw(configuration);
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The DNS configuration could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        void load();

        return () => { cancelled = true; };

    }

};
