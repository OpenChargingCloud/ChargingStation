import { api, type V2GConfiguration } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, field, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * What this charging station offers a vehicle on the wire below the cable.
 *
 * Two things on one page, and they are deliberately not merged: what the
 * station was told to offer, and what actually came up. A station told to
 * answer SDP on a machine with no IPv6 link-local address comes up with SDP
 * not running, and a page that showed only the setting would say everything
 * was fine. So the settings are a form and the link is a report, and where
 * they disagree the page says so in words.
 *
 * Off is the sensible default and stays it: binding UDP 15118, joining a
 * multicast group and putting a listener on a link-local address is not
 * something a charging station should do because somebody opened this page.
 */
export const v2gPage: Page = {

    title: 'V2G',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/v2g',
            title:     'V2G',
            subtitle:  'SLAC, SDP and the endpoint they lead to - what is offered below the charging cable.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayChange = auth.can('changeNetworkSettings');

        let cancelled = false;
        let current: V2GConfiguration | null = null;


        function draw(): void {

            if (current === null)
                return;

            const c    = current;
            const link = c.link;

            // What was asked for and what happened, where those differ. Only
            // worth a sentence when the station is actually running: before
            // Start() nothing is supposed to be up, and calling that a
            // disagreement would be the page inventing a fault.
            const troubles: string[] = [];

            if (c.running && c.enabled && link === null)
                troubles.push('V2G is switched on, but nothing came up below the cable at all - the log under the "15118" tag says why.');

            if (c.running && c.enabled && link !== null) {

                if (c.sdp && !link.sdp)
                    troubles.push('SDP is switched on and is not answering. It needs an interface with an IPv6 link-local address and a V2G endpoint to point a vehicle at.');

                if (!c.sdp && link.sdp)
                    troubles.push('SDP is switched off and is still answering, which should not be possible - reload this page.');

                if (c.interface !== null && link.interface !== null && c.interface !== link.interface)
                    troubles.push(`The interface asked for is "${c.interface}" and the one in use is "${link.interface}".`);

            }

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at what this
                        station offers below the cable but not change it. That needs the installer or the
                        system administrator role.
                    </div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-car-side"></i> What came up</h2>

                        ${!c.running
                              ? html`<p class="muted small">
                                        This station has not been started, so nothing is up yet. What is
                                        configured below is what it will bring up.
                                    </p>`
                              : link === null
                                    ? html`<p class="muted small">
                                              Nothing is up below the charging cable.
                                              ${c.enabled
                                                    ? html`It is switched on, so something stopped it - look for "15118" in the
                                                           <a href="/logs">log</a>.`
                                                    : html`It is switched off.`}
                                          </p>`
                                    : html`
                                        <div class="kv-list">
                                            <div class="kv">
                                                <span class="k">Interface</span>
                                                <span class="v">${link.interface ?? 'none'}</span>
                                            </div>
                                            <div class="kv">
                                                <span class="k">Link-local address</span>
                                                <span class="v"><code>${link.linkLocal ?? 'none'}</code></span>
                                            </div>
                                            <div class="kv">
                                                <span class="k">V2G endpoint</span>
                                                <span class="v"><code>${link.v2gEndpoint ?? 'none'}</code></span>
                                            </div>
                                            <div class="kv">
                                                <span class="k">Endpoint security</span>
                                                <span class="v">${link.v2gTLS ? 'TLS' : 'plain TCP'}</span>
                                            </div>
                                            <div class="kv">
                                                <span class="k">SDP</span>
                                                <span class="v">
                                                    ${link.sdp ? 'answering' : 'not running'}
                                                    ${link.sdpLoopback ? html`, this machine included` : ''}
                                                </span>
                                            </div>
                                            <div class="kv">
                                                <span class="k">SLAC</span>
                                                <span class="v">
                                                    ${link.slac ? 'listening' : 'not running'}
                                                    ${link.slacTransport === null ? '' : html` on ${link.slacTransport}`}
                                                </span>
                                            </div>
                                            <div class="kv">
                                                <span class="k">SLAC sessions</span>
                                                <span class="v">${link.slacSessions}</span>
                                            </div>
                                            <div class="kv">
                                                <span class="k">EVSE identification</span>
                                                <span class="v"><code>${link.evseId}</code></span>
                                            </div>
                                        </div>
                                    `}

                        ${troubles.map(trouble => html`<p class="form-error">${trouble}</p>`)}

                        <p class="hint">
                            Every SDP request that arrives and every answer that goes out is written to the
                            <a href="/logs">log</a> under the tags <code>15118</code> and <code>sdp</code>,
                            as is a frame that could not be read at all.
                        </p>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-sliders"></i> Settings</h2>

                        <form id="v2g-form" class="form-stack">

                            <label class="checkbox">
                                <input type="checkbox" name="enabled" ${c.enabled ? html`checked` : ''}
                                       ${mayChange ? '' : html`disabled`} />
                                Offer something below the charging cable
                                <span class="hint">
                                    Off by default, and off is the right default: this opens a raw socket and
                                    joins an IPv6 multicast group, which is not something to do on a machine
                                    that only happens to be running this binary.
                                </span>
                            </label>

                            <label class="checkbox">
                                <input type="checkbox" name="sdp" ${c.sdp ? html`checked` : ''}
                                       ${mayChange ? '' : html`disabled`} />
                                Answer SECC Discovery Protocol requests
                                <span class="hint">
                                    How a vehicle finds the V2G endpoint. Switched off, the endpoint is still
                                    there and nothing tells a vehicle where it is.
                                </span>
                            </label>

                            <label class="checkbox">
                                <input type="checkbox" name="loopback" ${c.loopback ? html`checked` : ''}
                                       ${mayChange ? '' : html`disabled`} />
                                Also answer a vehicle on this same machine
                                <span class="hint">
                                    For a bench where the vehicle is another process here. Off in the field: a
                                    station has no business answering a simulator somebody left running on its
                                    own controller. Set it on the vehicle as well - which of the two sockets
                                    decides depends on the platform.
                                </span>
                            </label>

                            <label>Powerline interface
                                <input type="text" name="interface" value="${c.interface ?? ''}"
                                       maxlength="128" placeholder="let the station pick"
                                       ${mayChange ? '' : html`disabled`} />
                                <span class="hint">
                                    Left empty, the station takes the first interface that looks like a
                                    candidate - right on a machine with one cable and a guess on a machine
                                    with six, which is why it says in the log which one it took.
                                </span>
                            </label>

                            <label>V2G endpoint port
                                <input type="number" name="port" min="0" max="65535" step="1"
                                       value="${c.port}" ${mayChange ? '' : html`disabled`} />
                                <span class="hint">
                                    0 lets the system pick a free one, which is the usual answer: there is no
                                    well-known port for this, and SDP exists precisely so that there need not
                                    be one.
                                </span>
                            </label>

                            <label>EVSE identification
                                <input type="text" name="evseId" value="${c.evseId}" maxlength="17"
                                       ${mayChange ? '' : html`disabled`} />
                                <span class="hint">
                                    What SLAC hands a vehicle. At most 17 characters, because that is what
                                    HomePlug carries - a longer one is refused here rather than quietly cut.
                                </span>
                            </label>

                            <label>SLAC transport
                                <select name="slac" ${mayChange ? '' : html`disabled`}>
                                    ${c.slacTransports.map(kind => html`
                                        <option value="${kind}" ${kind === c.slac ? html`selected` : ''}>${describe(kind)}</option>
                                    `)}
                                </select>
                                <span class="hint">
                                    "Auto" takes the real powerline interface where there is one and nothing
                                    anywhere else - never the simulated medium, because a station quietly
                                    matching vehicles over UDP would be lying about what it is.
                                </span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                                <span id="form-note"  class="form-notice" role="status"></span>
                                <span id="form-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Saved to ${c.file}, and put into effect at once${c.running && c.enabled ? html` - the
                                link goes down and comes back up, and a vehicle in the middle of a SLAC match
                                goes down with it` : ''}.
                            </span>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-lock"></i> Endpoint certificate</h2>

                        <p>
                            ${c.certificate
                                  ? html`A certificate was given to this station, so the V2G endpoint speaks TLS
                                         and SDP says so to every vehicle that asks.`
                                  : html`No certificate was given to this station, so the V2G endpoint speaks plain
                                         TCP - which ISO 15118-20 does not allow. SDP advertises that honestly
                                         rather than promising TLS a vehicle could not then complete.`}
                        </p>

                        <p class="hint">
                            Not settable here on purpose: a certificate is a file and a password, and it is
                            passed on the command line with <code>--v2g-cert</code>. The simulated SLAC medium
                            over UDP stays there too - it exists for a bench with no powerline modem, and a
                            station configured from a file is not that bench.
                        </p>

                    </section>

                </div>

            `);

            wire();

        }

        function wire(): void {

            must<HTMLFormElement>(content, '#v2g-form').addEventListener('submit', event => {
                event.preventDefault();
                void save();
            });

        }

        async function save(): Promise<void> {

            const form = must<HTMLFormElement>(content, '#v2g-form');
            const note = must<HTMLElement>(content, '#form-note');

            note.textContent = '';

            must<HTMLElement>(content, '#form-error').textContent = '';

            // Every one of them read here and not inside the call below.
            // whileSaving switches the whole form off before it runs, and a
            // disabled control is one FormData leaves out entirely - so a
            // field read in there comes back empty, which this station reads
            // as "do not change it". Measured: the page said "Saved." and the
            // file was untouched.
            const update = {
                              enabled:    checked(form, 'enabled'),
                              sdp:        checked(form, 'sdp'),
                              loopback:   checked(form, 'loopback'),
                              // An empty interface is "let the station pick",
                              // which travels as null: an absent field means
                              // "do not change this", and those are different
                              // answers.
                              interface:  field(form, 'interface') === '' ? null : field(form, 'interface'),
                              port:       field(form, 'port')      === '' ? 0    : Number(field(form, 'port')),
                              evseId:     field(form, 'evseId'),
                              slac:       field(form, 'slac')
                          };

            try
            {
                current = await whileSaving(content, note, () => api.v2g.save(update));

                draw();

                must<HTMLElement>(content, '#form-note').textContent = 'Saved.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#form-error').textContent = errorMessage(problem);
            }

        }

        async function load(): Promise<void> {

            try
            {
                const loaded = await api.v2g.get();

                if (cancelled)
                    return;

                current = loaded;
                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The V2G configuration could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#v2g-form')));

        void load();

        return () => { cancelled = true; release(); };

    }

};


/** Whether a checkbox of this form is ticked. */
function checked(form: HTMLFormElement, name: string): boolean {
    return form.querySelector<HTMLInputElement>(`[name="${name}"]`)?.checked ?? false;
}


/** A SLAC transport, written the way it would be said out loud. */
function describe(kind: string): string {
    switch (kind) {
        case 'none':      return 'None - no SLAC at all';
        case 'auto':      return 'Auto - the powerline interface where there is one';
        case 'afpacket':  return 'AF_PACKET - the powerline interface, Linux only';
        case 'udp':       return 'UDP - a simulated medium, for a bench';
        default:          return kind;
    }
}
