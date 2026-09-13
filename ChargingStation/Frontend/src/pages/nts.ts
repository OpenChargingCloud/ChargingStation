import { api, type NTSConfiguration } from '../api/client';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, humanizeKey } from '../ui';

/**
 * Where this charging station gets the time from.
 *
 * A charging station is measured by its clock, so this page is mostly about
 * whether the clock is still being fed: the cookie pool is what lets the next
 * NTP request be authenticated, and a pool running dry is the first sign that
 * the key exchange has stopped working. Only the timeout can be changed - the
 * server, its ports and the pool policy are handed to the client when it is
 * made.
 */
export const ntsPage: Page = {

    title: 'NTS client',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/nts',
            title:     'NTS client',
            subtitle:  'Where this charging station gets the time from.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => void load());

        let cancelled = false;

        function draw(configuration: NTSConfiguration): void {

            const cookies = configuration.cookies;

            // Empty is worse than low, and both are worth seeing before
            // somebody goes looking for why a timestamp is wrong.
            const state   = cookies.isEmpty ? { label: 'empty', level: 'error' }
                          : cookies.isLow   ? { label: 'low',   level: 'warning' }
                          : cookies.isFull  ? { label: 'full',  level: 'ok' }
                          :                   { label: 'ok',    level: 'ok' };

            render(content, html`

                <div class="cards">

                    <section class="card">
                        <h2><i class="fa-solid fa-clock"></i> Server</h2>
                        <div class="kv-list">
                            ${Object.entries(configuration.server).map(([key, value]) => html`
                                <div class="kv">
                                    <span class="k">${humanizeKey(key)}</span>
                                    <span class="v">${formatValue(value)}</span>
                                </div>
                            `)}
                        </div>
                    </section>

                    <section class="card">
                        <h2><i class="fa-solid fa-sliders"></i> Settings</h2>

                        <form id="nts-form" class="form-stack">

                            <label>Timeout in seconds
                                <input type="number" name="timeoutSeconds" min="1" max="3600" step="0.5"
                                       value="${configuration.settings.timeoutSeconds ?? ''}" />
                                <span class="hint">
                                    How long a request waits for an answer. Leave it empty to wait
                                    without one.
                                </span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary">Save</button>
                                <span id="form-note"  class="form-notice" role="status"></span>
                                <span id="form-error" class="form-error"  role="alert"></span>
                            </div>

                        </form>

                        <p class="hint">
                            The server, its ports and the cookie pool policy are handed to the
                            client when it is made and cannot be changed while the station runs.
                        </p>
                    </section>

                    <section class="card">
                        <h2>
                            <i class="fa-solid fa-cookie-bite"></i> Cookie pool
                            <span class="chip level ${state.level === 'ok' ? 'notice' : state.level}">${state.label}</span>
                        </h2>
                        <div class="kv-list">
                            <div class="kv">
                                <span class="k">Available</span>
                                <span class="v">${cookies.available} of ${cookies.maxPoolSize}, low below ${cookies.lowWatermark}</span>
                            </div>
                            ${(['seeded', 'received', 'consumed', 'dropped'] as const).map(key => html`
                                <div class="kv">
                                    <span class="k">${humanizeKey(key)}</span>
                                    <span class="v">${formatValue(cookies[key])}</span>
                                </div>
                            `)}
                        </div>
                    </section>

                    <section class="card">
                        <h2><i class="fa-solid fa-scale-balanced"></i> Pool policy</h2>
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
                                <span class="k">Automatic exchanges</span>
                                <span class="v">${configuration.keyExchange.automatic}</span>
                            </div>
                            <div class="kv">
                                <span class="k">AEAD algorithms</span>
                                <span class="v">${formatValue(configuration.keyExchange.aeadAlgorithms)}</span>
                            </div>
                            <div class="kv">
                                <span class="k">Compliant exporter context</span>
                                <span class="v">${formatValue(configuration.keyExchange.compliantExporterContext)}</span>
                            </div>
                            ${configuration.keyExchange.lastExchange === null
                                  ? html`<p class="hint">No key exchange has happened yet.</p>`
                                  : html`
                                      <div class="kv">
                                          <span class="k">Last exchange</span>
                                          <span class="v">
                                              ${configuration.keyExchange.lastExchange.error ?? 'no error'}
                                          </span>
                                      </div>
                                      ${configuration.keyExchange.lastExchange.warnings.length > 0
                                            ? html`
                                                <div class="kv">
                                                    <span class="k">Warnings</span>
                                                    <span class="v">${formatValue(configuration.keyExchange.lastExchange.warnings)}</span>
                                                </div>
                                              `
                                            : ''}
                                      ${configuration.keyExchange.lastExchange.servers.length > 0
                                            ? html`
                                                <div class="kv">
                                                    <span class="k">NTP servers named</span>
                                                    <span class="v">${formatValue(configuration.keyExchange.lastExchange.servers)}</span>
                                                </div>
                                              `
                                            : ''}
                                  `}
                        </div>
                    </section>

                </div>

            `);

            const form   = must<HTMLFormElement>(content, '#nts-form');
            const error  = must<HTMLElement>(content, '#form-error');
            const button = must<HTMLButtonElement>(form, 'button[type="submit"]');

            form.addEventListener('submit', event => {

                event.preventDefault();

                error.textContent  = '';
                button.disabled    = true;

                const typed = String(new FormData(form).get('timeoutSeconds') ?? '').trim();

                void (async () => {
                    try
                    {
                        draw(await api.nts.save({
                            timeoutSeconds: typed === '' ? null : Number(typed)
                        }));
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
                const configuration = await api.nts.get();

                if (!cancelled)
                    draw(configuration);
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
