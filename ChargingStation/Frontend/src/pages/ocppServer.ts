import { api, type OCPPServerConfiguration } from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render } from '@node/view';

/**
 * The OCPP server of this station: a WebSocket server a CSMS or a local
 * controller dials, where the station cannot dial them.
 *
 * OCPP as it always is, and only who opens the WebSocket is the other way
 * round. Who may come in is written down on the Connections page, as a
 * connection the other end makes; this page is the server itself - on or off,
 * and on which ports. Changed at once; whoever dialled in is let go of where
 * its server goes.
 */
export const ocppServerPage: Page = {

    title: 'OCPP server',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/ocpp-server',
            title:     'OCPP server',
            subtitle:  'A CSMS or a local controller dialling this station, rather than being dialled by it.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('connections', 'edit');

        let cancelled = false;
        let current: OCPPServerConfiguration | null = null;


        function draw(): void {

            if (current === null)
                return;

            const server  = current;
            const served  = server.servers.filter(one => one.served);
            const port    = server.servers.find(one => one.ocppVersion === 'OCPP2.1')?.port ?? null;
            const port16  = server.servers.find(one => one.ocppVersion === 'OCPP1.6')?.port ?? null;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">${mayButNot('look at the OCPP server', 'change it')}</div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-tower-broadcast"></i> The server</h2>

                        <p class="hint" id="ocpp-server-state">
                            ${served.length > 0
                                  ? html`<strong>Served</strong>: ${served.map(one => one.ocppVersion).join(' and ')} to whoever is written down as dialling in.`
                                  : html`<strong>Not served</strong>: nothing can dial this station.`}
                        </p>

                        ${served.length > 0 && !server.tls ? html`
                            <div class="error-box" id="no-tls">
                                The server speaks no TLS, as the web interface of this station speaks none: a password
                                that signs in to it crosses the network in the clear. Switch HTTPS on: the
                                "web" section of the configuration file, or <code>--https</code>.
                            </div>
                        ` : nothing}

                        ${server.servers.filter(one => one.problem !== undefined).map(one => html`
                            <div class="error-box" data-problem="${one.ocppVersion}">
                                ${one.ocppVersion} could not be served on port ${one.port}: ${one.problem}
                            </div>
                        `)}

                        <form id="ocpp-server-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void save(); }}>

                            <label class="checkbox">
                                <input type="checkbox" name="enabled" ?checked=${server.enabled} ?disabled=${!mayChange} />
                                Let a CSMS or a local controller dial this station
                            </label>

                            <div class="form-row">

                                <label>Port for OCPP 2.1 and 2.0.1
                                    <input type="number" name="port" min="1" max="65535" step="1"
                                           value="${port ?? ''}" ?disabled=${!mayChange} />
                                    <span class="hint">Left empty, OCPP 2.1 is not served.</span>
                                </label>

                                <label>Port for OCPP 1.6
                                    <input type="number" name="port16" min="1" max="65535" step="1"
                                           value="${port16 ?? ''}" ?disabled=${!mayChange} />
                                    <span class="hint">Left empty, OCPP 1.6 is not served. A port of its own: the two are two nodes.</span>
                                </label>

                            </div>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <span id="form-note"  class="form-notice" role="status"></span>
                                <span id="form-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Saved to ${server.file}, and served or not from that moment, without a restart.
                                Whoever dialled in is let go of where its server goes, and dials again.
                            </span>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-right-to-bracket"></i> Who dials in</h2>

                        <p class="hint">
                            Nothing gets in that is not written down: a connection the other end makes, on the
                            <a href="${toURL('/configuration/connections')}">Connections</a> page, under the name its URL
                            ends with and with the credentials it names. An address that signs in wrong ten times
                            within a minute is told to wait. This station forwards nothing: what comes in is for
                            this station.
                        </p>

                        <dl class="kv" id="ocpp-server-facts">
                            ${server.servers.map(one => html`
                                <dt>${one.ocppVersion}</dt>
                                <dd data-url="${one.ocppVersion}">${one.url === null
                                        ? 'no port'
                                        : html`<code>${one.url}&lt;its name&gt;</code>${one.served ? nothing : html` <span class="hint">not served</span>`}`}</dd>
                            `)}
                            <dt>TLS</dt> <dd>${server.tls ? 'with the certificate of the web interface' : 'none'}</dd>
                        </dl>

                        <div class="table-scroll">
                            <table class="records" id="ocpp-server-inbound">
                                <thead>
                                    <tr><th>Written down</th><th>Name</th><th>OCPP</th><th>Now</th></tr>
                                </thead>
                                <tbody>
                                    ${server.inbound.length === 0
                                          ? html`<tr><td colspan="4">No connection the other end makes is written down.</td></tr>`
                                          : server.inbound.map(one => html`
                                                <tr data-inbound="${one.id}">
                                                    <td>${one.description}</td>
                                                    <td><code>${one.name}</code></td>
                                                    <td>${one.ocppVersion}</td>
                                                    <td>${!one.letIn
                                                              ? 'not let in'
                                                              : server.servers.some(served => served.ocppVersion === one.ocppVersion && served.connected.includes(one.name))
                                                                    ? html`<span class="chip on">connected</span>`
                                                                    : 'waited for'}</td>
                                                </tr>
                                            `)}
                                </tbody>
                            </table>
                        </div>

                    </section>

                </div>

            `);

        }

        /**
         * The form saved, and drawn again as the station took it.
         */
        async function save(): Promise<void> {

            const form   = must<HTMLFormElement>(content, '#ocpp-server-form');
            const note   = must<HTMLElement>(content, '#form-note');
            const error  = must<HTMLElement>(content, '#form-error');

            note.textContent   = '';
            error.textContent  = '';

            // Read before the page is held still: a disabled field is left out
            // of a FormData.
            const data     = new FormData(form);
            const enabled  = data.get('enabled') !== null;

            const portOf   = (name: string): number | null => {
                const typed = String(data.get(name) ?? '').trim();
                return typed.length === 0 ? null : Number(typed);
            };

            const port     = portOf('port');
            const port16   = portOf('port16');

            try
            {
                current = await whileSaving(content, note, () => api.ocppServer.save({ enabled, port, port16 }));

                if (cancelled)
                    return;

                draw();

                form.reset();

                note.textContent = current.servers.some(one => one.served)
                                       ? 'Saved. The OCPP server is on.'
                                       : 'Saved. The OCPP server is off.';
            }
            catch (problem)
            {
                if (!cancelled)
                    error.textContent = errorMessage(problem);
            }

        }

        async function load(): Promise<void> {

            try
            {
                const loaded = await api.ocppServer.get();

                if (cancelled)
                    return;

                current = loaded;
                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The OCPP server could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        /**
         * Loaded anew - Reload - is what the station has, the form too, which a
         * draw on its own would leave as typed.
         */
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
