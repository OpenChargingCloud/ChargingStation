import { api, type CertificateSummary, type EVSEState, type EVSEStatus, type MeterStates, type MeterStatus, type V2GLiveStatus } from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import { logs } from '@node/logs/store';
import type { Page } from '@node/router';
import { reloadButton, shell } from '@node/shell';
import { rememberTab, tabFromURL, tabsView, type Tab } from '@node/tabs';
import { errorMessage, formatNumber, formatSince, formatTimestamp } from '@node/ui';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * How each EVSE of this charging station stands right now, one tab each:
 * whether it is free, charging, held or out of service, the session on it,
 * what is going on below its cable - the last vehicle that asked SDP and
 * what it was told, the vehicle connected now and over what - the
 * reservation, and its meter: what is written down about it, and what it
 * measures where it is read.
 *
 * Asked once when the page opens, and followed after that: every change the
 * station notices comes as an "evses" event on the stream the web interface
 * already follows for the log, within a second of happening. Nothing is asked
 * again and again.
 *
 * The configuration of the same EVSEs - what is fitted, what they may deliver
 * - is a page of its own, under Configuration. This one changes nothing of
 * how the station is set up, and reads what is going on: the support sees it,
 * and is not sent to a page that is not theirs.
 *
 * What it does is what is done to a station that runs, each for those who
 * may: restart it once nothing is charging or at once, have it tell its back
 * ends again how the EVSEs stand, have it register again or send a heartbeat,
 * and let go of a cable that is stuck - what a CSMS asks over OCPP with
 * Reset, TriggerMessage and UnlockConnector, at the same rights.
 */
export const evseStatusPage: Page = {

    title: 'EVSEs',

    render({ root, url }) {

        const content = shell(root, {
            active:    '/evses',
            title:     'EVSEs',
            subtitle:  'How each EVSE of this charging station stands right now.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        let cancelled              = false;
        let evses:  EVSEStatus[] | null = null;
        let shown:  string | null  = null;

        /** How each meter that is read stands, by its EVSE - from their own event, which comes every second. */
        const meters    = new Map<number, MeterStatus>();
        const mayMeters = auth.can('meters', 'read');

        /** Whether the page about how the EVSEs are set up is theirs to be sent to. */
        const mayConfigure = auth.can('evses', 'read');

        /** What somebody may have the station do, each a right of its own. */
        const mayRestartIdle  = auth.can('reset',           'run');
        const mayRestartNow   = auth.can('reset-immediate', 'run');
        const mayReport       = auth.can('state',           'run');
        const maySend         = auth.can('node',            'run');
        const mayUnlock       = auth.can('unlock',          'run');

        /** What came of the last thing the station was told to do, and whether one is on its way. */
        let said: { ok: boolean; text: string } | null = null;
        let busy = false;

        /** Have the station do something, and say what came of it. */
        async function act(What: () => Promise<string>): Promise<void> {

            if (busy)
                return;

            busy = true;
            said = null;
            draw();

            try
            {
                said = { ok: true, text: await What() };
            }
            catch (problem)
            {
                said = { ok: false, text: errorMessage(problem) };
            }

            busy = false;
            draw();

        }

        const restart = (AtOnce: boolean) => {

            if (AtOnce && !confirm('Whatever is charging is ended, and the station restarts. Go on?'))
                return;

            void act(async () => {

                const answer = await api.station.restart(AtOnce);

                return answer.restart === 'onceIdle'
                           ? `The station restarts its OCPP side once the ${answer.sessions} session(s) charging now have ended, and starts no new session until then.`
                           : answer.ended > 0
                                 ? `${answer.ended} session(s) were ended, and the station restarts its OCPP side now.`
                                 : 'Nothing is charging: the station restarts its OCPP side now.';

            });

        };

        const report = () => void act(async () =>
            `How the EVSEs stand goes to ${(await api.station.reportStatus()).backEnds} back end(s).`);

        const send = (Message: 'BootNotification' | 'Heartbeat') => void act(async () =>
            `A ${Message} goes to ${(await api.station.send(Message)).backEnds} back end(s).`);

        const unlock = (EVSE: EVSEStatus) => void act(async () => {
            await api.station.unlock(EVSE.id);
            return `The cable at EVSE ${EVSE.label} was let go of.`;
        });

        /** The station as a whole, told to do something: only what this person may. */
        function stationCard(): TemplateResult | typeof nothing {

            if (!mayRestartIdle && !mayRestartNow && !mayReport && !maySend)
                return said === null ? nothing : saidView();

            return html`
                <div class="cards">
                    <section class="card" id="station-actions">

                        <h2><i class="fa-solid fa-charging-station"></i> This station</h2>

                        <div class="button-row">
                            ${mayReport      ? html`<button type="button" class="btn small" id="report-status"  ?disabled=${busy} @click=${report}>Report the EVSEs again</button>` : nothing}
                            ${maySend        ? html`<button type="button" class="btn small" id="send-heartbeat" ?disabled=${busy} @click=${() => send('Heartbeat')}>Send a heartbeat</button>` : nothing}
                            ${maySend        ? html`<button type="button" class="btn small" id="send-boot"      ?disabled=${busy} @click=${() => send('BootNotification')}>Register again</button>` : nothing}
                            ${mayRestartIdle ? html`<button type="button" class="btn small" id="restart-idle"   ?disabled=${busy} @click=${() => restart(false)}>Restart once idle</button>` : nothing}
                            ${mayRestartNow  ? html`<button type="button" class="btn small danger" id="restart-now" ?disabled=${busy} @click=${() => restart(true)}>Restart now</button>` : nothing}
                        </div>

                        ${saidView()}

                        <p class="muted small">
                            What a CSMS asks over OCPP with TriggerMessage and Reset. A restart builds the OCPP side
                            of the station anew and dials again; once idle, it waits for what is charging and no new
                            session starts until then.
                        </p>

                    </section>
                </div>
            `;

        }

        function saidView(): TemplateResult | typeof nothing {
            return said === null
                       ? nothing
                       : html`<p class="${said.ok ? 'notice small' : 'form-error'}" id="station-said">${said.text}</p>`;
        }


        function tabsOf(List: readonly EVSEStatus[]): Tab[] {
            return List.map(evse => ({ id: tabIdOf(evse), label: `EVSE ${evse.label}`, icon: 'fa-plug' }));
        }

        function show(id: string): void {
            shown = id;
            rememberTab(tabsOf(evses ?? []), id);
            draw();
        }

        function draw(): void {

            if (cancelled || evses === null)
                return;

            if (evses.length === 0) {
                render(content, html`
                    <div class="cards">
                        <section class="card">
                            <h2><i class="fa-solid fa-plug"></i> No EVSEs</h2>
                            <p class="muted small">
                                This station has no EVSEs.${mayConfigure ? html` They are added on the
                                <a href="${toURL('/configuration/evses')}">EVSEs</a> page under Configuration.` : nothing}
                            </p>
                        </section>
                    </div>
                `);
                return;
            }

            const tabs = tabsOf(evses);

            // The tab asked for, as long as there still is one by that name:
            // an EVSE removed while the page was open takes its tab with it.
            if (shown === null || !tabs.some(tab => tab.id === shown))
                shown = tabFromURL(tabs, shown === null ? url : new URL(location.href));

            render(content, html`

                ${stationCard()}

                ${tabsView(tabs, shown, show, 'The EVSEs of this station')}

                ${repeat(evses, evse => evse.id, evse => html`
                    <div id="panel-${tabIdOf(evse)}" role="tabpanel" aria-labelledby="tab-${tabIdOf(evse)}" ?hidden=${shown !== tabIdOf(evse)}>
                        ${evseView(evse, meters.get(evse.id) ?? null, mayMeters, mayConfigure, mayUnlock ? { busy, unlock } : null)}
                    </div>
                `)}

            `);

        }

        async function load(): Promise<void> {

            try
            {
                const [ loaded, read ] = await Promise.all([
                                             api.evses.states(),
                                             mayMeters ? api.meters.states().catch(() => null) : Promise.resolve(null)
                                         ]);

                if (cancelled)
                    return;

                evses = loaded.evses;

                if (read !== null)
                    took(read);

                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The EVSEs could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        // Every change the station notices, as it says it. What the page
        // already had is replaced as a whole: an EVSE is only ever said whole.
        const unfollow = logs.onEvent<{ evses: EVSEStatus[] }>('evses', data => {
            if (cancelled || evses === null)
                return;
            evses = data.evses;
            draw();
        });

        // What the meters measure, on an event of their own: news every
        // second, which the EVSEs' own event does not carry.
        function took(States: MeterStates): void {
            meters.clear();
            for (const state of States.meters)
                meters.set(state.evse, state);
        }

        const unfollowMeters = logs.onEvent<MeterStates>('meters', data => {
            if (cancelled)
                return;
            took(data);
            draw();
        });

        void load();

        return () => { cancelled = true; unfollow(); unfollowMeters(); };

    }

};


/** The id of an EVSE's tab, in the address and in the ids of its button and panel. */
function tabIdOf(EVSE: EVSEStatus): string {
    return `evse-${EVSE.id}`;
}


/** Whether it is free, charging, held or out of service, in words. */
function stateInWords(State: EVSEState): string {
    switch (State)
    {
        case 'available':    return 'Available';
        case 'occupied':     return 'Charging';
        case 'reserved':     return 'Reserved';
        case 'inoperative':  return 'Out of service';
    }
}


/**
 * What is going on below the cable: the link, the last SDP question and its
 * answer, the vehicle connected now and over what, and how the last session
 * ended.
 */
function v2gCard(V2G: V2GLiveStatus | null): TemplateResult {

    if (V2G === null)
        return html`
            <section class="card">
                <h2><i class="fa-solid fa-car-side"></i> ISO 15118</h2>
                <p class="muted small">
                    This EVSE offers no ISO 15118 below its cable. It is switched on on the
                    <a href="${toURL('/configuration/v2g')}">V2G</a> page.
                </p>
            </section>
        `;

    if (!V2G.up)
        return html`
            <section class="card">
                <h2><i class="fa-solid fa-car-side"></i> ISO 15118</h2>
                <p class="form-error">
                    It is switched on, but nothing came up below this cable - the
                    <a href="${toURL('/logs')}">log</a> under the tag <code>15118</code> says why.
                </p>
            </section>
        `;

    const sdp  = V2G.lastSDP     ?? null;
    const now  = V2G.connection  ?? null;
    const last = V2G.lastSession ?? null;

    return html`
        <section class="card">

            <h2><i class="fa-solid fa-car-side"></i> ISO 15118</h2>

            <div class="kv-list">
                <div class="kv"><span class="k">Endpoint</span><span class="v"><code>${V2G.endpoint ?? 'not listening'}</code>, ${V2G.tls ? 'TLS' : 'plain TCP'}</span></div>
                <div class="kv"><span class="k">Interface</span><span class="v">${V2G.interface ?? 'none'}</span></div>
                <div class="kv"><span class="k">SDP</span><span class="v">${V2G.sdp ? 'answering' : 'not running'}</span></div>
                <div class="kv"><span class="k">SLAC</span><span class="v">${V2G.slac ? 'listening' : 'not running'}</span></div>
                <div class="kv"><span class="k">EVSE identification</span><span class="v"><code>${V2G.evseId ?? '-'}</code></span></div>
                <div class="kv"><span class="k">Outlet</span><span class="v">${V2G.mode ?? '-'}</span></div>
            </div>

            ${V2G.tls && V2G.certificate ? certificateView('The certificate it shows', V2G.certificate) : nothing}

            <h3>The last SDP request</h3>

            ${sdp === null
                  ? html`<p class="muted small">No vehicle has asked SDP where this endpoint is since the link came up.</p>`
                  : html`
                      <div class="kv-list">
                          <div class="kv"><span class="k">At</span><span class="v">${formatTimestamp(sdp.at)}</span></div>
                          <div class="kv"><span class="k">From</span><span class="v"><code>${sdp.from}</code></span></div>
                          <div class="kv"><span class="k">Asked for</span><span class="v">${sdp.security}, ${sdp.transport}</span></div>
                          ${sdp.accepted
                                ? sdp.answer === null
                                      ? html`<div class="kv"><span class="k">Answered</span><span class="v">not yet</span></div>`
                                      : html`<div class="kv"><span class="k">Answered</span><span class="v"><code>[${sdp.answer.address}]:${sdp.answer.port}</code>, ${sdp.answer.security}</span></div>`
                                : html`<div class="kv"><span class="k">Refused</span><span class="v">${sdp.rejectReason ?? 'without a reason'}</span></div>`}
                      </div>
                    `}

            <h3>The vehicle connected now</h3>

            ${now === null
                  ? html`<p class="muted small">No vehicle is connected to the endpoint.</p>`
                  : html`
                      <div class="kv-list">
                          <div class="kv"><span class="k">Since</span><span class="v">${formatTimestamp(now.since)} (${formatSince(now.since)})</span></div>
                          <div class="kv"><span class="k">From</span><span class="v"><code>${now.from ?? '-'}</code></span></div>
                          <div class="kv"><span class="k">Protocol</span><span class="v">${now.protocol === null ? 'not agreed yet' : html`${now.protocol}, ${now.mode}`}</span></div>
                          <div class="kv"><span class="k">Security</span><span class="v">${now.tls === null
                                                                                            ? 'plain TCP'
                                                                                            : html`${now.tls.protocol}${now.tls.cipherSuite ? html`, <code>${now.tls.cipherSuite}</code>` : nothing} (${now.tls.library} TLS)`}</span></div>
                      </div>
                      ${now.tls === null
                            ? nothing
                            : now.tls.vehicle === null
                                  ? html`<p class="muted small">The vehicle presented no certificate of its own.</p>`
                                  : certificateView('The vehicle\'s certificate', now.tls.vehicle)}
                    `}

            ${last === null
                  ? nothing
                  : html`
                      <h3>The last session</h3>
                      <div class="kv-list">
                          <div class="kv"><span class="k">Ended</span><span class="v">${formatTimestamp(last.endedAt)}</span></div>
                          <div class="kv"><span class="k">Protocol</span><span class="v">${last.protocol}, ${formatNumber(last.seconds, 1)} s${last.paused ? ', paused' : ''}</span></div>
                      </div>
                    `}

            <p class="muted small">
                Every request, answer and session is also in the <a href="${toURL('/logs')}">log</a>
                under the tag <code>15118</code>. What this EVSE offers is set on the
                <a href="${toURL('/configuration/v2g')}">V2G</a> page.
            </p>

        </section>
    `;

}


/** A certificate, as somebody checks one. */
function certificateView(Heading: string, Certificate: CertificateSummary): TemplateResult {

    return html`
        <h3>${Heading}</h3>
        <div class="kv-list">
            <div class="kv"><span class="k">Subject</span><span class="v"><code>${Certificate.subject}</code></span></div>
            <div class="kv"><span class="k">Issuer</span><span class="v"><code>${Certificate.issuer}</code></span></div>
            <div class="kv"><span class="k">Valid</span><span class="v">${formatTimestamp(Certificate.notBefore)} to ${formatTimestamp(Certificate.notAfter)}</span></div>
            <div class="kv"><span class="k">SHA-256</span><span class="v"><code>${Certificate.sha256}</code></span></div>
        </div>
    `;

}


/** Letting go of a cable, for those who may: whether something is on its way, and what to call. */
interface Unlocking {
    busy:    boolean;
    unlock:  (EVSE: EVSEStatus) => void;
}

/** One EVSE, drawn in the panel of its tab. */
function evseView(EVSE:           EVSEStatus,
                  Meter:          MeterStatus | null,
                  MayReadMeters:  boolean,
                  MayConfigure:   boolean,
                  Unlocking:      Unlocking | null = null): TemplateResult {

    return html`
        <div class="cards">

            <section class="card">

                <h2><i class="fa-solid fa-plug"></i> EVSE ${EVSE.label}</h2>

                <div class="kv-list">
                    <div class="kv"><span class="k">State</span><span class="v evse-state ${EVSE.status}">${stateInWords(EVSE.status)}</span></div>
                    ${EVSE.closing
                          ? html`<div class="kv"><span class="k">Afterwards</span><span class="v">Out of service once this session ends</span></div>`
                          : nothing}
                    <div class="kv"><span class="k">In service</span><span class="v">${EVSE.operative ? 'yes' : 'no'}</span></div>
                    <div class="kv"><span class="k">Most power</span><span class="v">${formatNumber(EVSE.maxPower_kW, 1)} kW</span></div>
                    <div class="kv"><span class="k">EVSE id</span><span class="v">${EVSE.id}</span></div>
                    <div class="kv"><span class="k">Physical reference</span><span class="v">${EVSE.physicalReference ?? '-'}</span></div>
                </div>

                <div class="table-scroll">
                    <table class="records">
                        <thead>
                            <tr><th>Connector</th><th>Type</th><th>Most power</th></tr>
                        </thead>
                        <tbody>
                            ${repeat(EVSE.connectors, connector => connector.id, connector => html`
                                <tr>
                                    <td>${connector.id}</td>
                                    <td><code>${connector.type}</code></td>
                                    <td>${formatNumber(connector.maxPower_kW, 1)} kW</td>
                                </tr>
                            `)}
                        </tbody>
                    </table>
                </div>

                ${Unlocking === null ? nothing : html`
                    <div class="button-row">
                        <button type="button" class="btn small" data-unlock="${EVSE.id}"
                                ?disabled=${Unlocking.busy || EVSE.session !== null}
                                title="${EVSE.session !== null ? 'Somebody is charging here: the session is stopped first.' : 'Let go of a cable that is stuck.'}"
                                @click=${() => Unlocking.unlock(EVSE)}>Unlock the cable</button>
                    </div>
                `}

                ${MayConfigure ? html`
                    <p class="muted small">
                        What is fitted and what it may deliver is set on the
                        <a href="${toURL('/configuration/evses')}">EVSEs</a> page under Configuration.
                    </p>
                ` : nothing}

            </section>

            <section class="card">

                <h2><i class="fa-solid fa-bolt"></i> Session</h2>

                ${EVSE.session === null
                      ? html`<p class="muted small">Nothing is charging here.</p>`
                      : html`
                          <div class="kv-list">
                              <div class="kv"><span class="k">Started by</span><span class="v">${EVSE.session.method}</span></div>
                              <div class="kv"><span class="k">Since</span><span class="v">${formatTimestamp(EVSE.session.startedAt)} (${formatSince(EVSE.session.startedAt)})</span></div>
                              <div class="kv"><span class="k">Customer of</span><span class="v">${EVSE.session.provider?.name ?? '-'}</span></div>
                          </div>
                        `}

            </section>

            ${v2gCard(EVSE.v2g ?? null)}

            <section class="card">

                <h2><i class="fa-solid fa-clock"></i> Reservation</h2>

                ${EVSE.reservation === null
                      ? html`<p class="muted small">Not held for anybody.</p>`
                      : html`
                          <div class="kv-list">
                              <div class="kv"><span class="k">Held until</span><span class="v">${formatTimestamp(EVSE.reservation.until)}</span></div>
                              <div class="kv"><span class="k">Left</span><span class="v">${EVSE.reservation.minutesLeft} min</span></div>
                          </div>
                        `}

            </section>

            ${meterCard(EVSE.meter, Meter, MayReadMeters)}

        </div>
    `;

}


/** What a meter's state is called on the page. */
const meterStates: Record<string, string> = {
    connecting:   'connecting',
    reading:      'read',
    noIdentity:   'nothing to show it',
    untrusted:    'not believed',
    refused:      'refused',
    unreachable:  'not reachable',
    notSunSpec:   'no SunSpec meter',
    failed:       'failed'
};


/**
 * The meter of an EVSE: what is written down about it, and - where it is read
 * - whether it is, over what, and what it measures, phase by phase: the last
 * from the "meters" event, where whoever looks may read the meters.
 */
function meterCard(Meter:          EVSEStatus['meter'],
                   State:          MeterStatus | null,
                   MayReadMeters:  boolean): TemplateResult {

    const read    = Meter?.read ? State : null;
    const reading = read?.reading ?? null;
    const phases  = reading === null
                        ? []
                        : [ [ 'L1', 'L1' ], [ 'L2', 'L2' ], [ 'L3', 'L3' ], [ 'all', 'Total' ] ] as const;

    const shown   = (Value: number | null, Decimals: number, Unit: string) =>
        Value === null ? '-' : `${formatNumber(Value, Decimals)} ${Unit}`;

    return html`
        <section class="card">

            <h2><i class="fa-solid fa-gauge"></i> Energy meter</h2>

            ${Meter === null
                  ? html`<p class="muted small">Nothing is written down about a meter on this EVSE.</p>`
                  : html`
                      <div class="kv-list">
                          <div class="kv"><span class="k">Type</span><span class="v">${Meter.type ?? '-'}</span></div>
                          <div class="kv"><span class="k">Serial number</span><span class="v">${Meter.serialNumber ?? '-'}</span></div>
                          ${read === null
                                ? nothing
                                : html`
                                    <div class="kv"><span class="k">Read at</span><span class="v"><code>${read.endpoint.host}:${read.endpoint.port}</code>, unit ${read.endpoint.unit}</span></div>
                                    <div class="kv"><span class="k">State</span><span class="v meter-state">${meterStates[read.state] ?? read.state}${read.since ? html`, since ${formatTimestamp(read.since)}` : nothing}</span></div>
                                  `}
                      </div>
                    `}

            ${read?.error ? html`<p class="form-error meter-error">${read.error}</p>` : nothing}

            ${read?.connection
                  ? html`
                      <h3>Over what</h3>
                      <div class="kv-list">
                          <div class="kv"><span class="k">Security</span><span class="v">${read.connection.protocol}, <code>${read.connection.cipherSuite}</code></span></div>
                          <div class="kv"><span class="k">Shown to it</span><span class="v">${read.connection.identity.label}, as <code>${read.connection.identity.role}</code></span></div>
                      </div>
                      ${read.connection.meter ? certificateView('The meter\'s certificate', read.connection.meter) : nothing}
                    `
                  : nothing}

            ${reading === null
                  ? nothing
                  : html`
                      <h3>What it measures</h3>
                      <div class="table-scroll">
                          <table class="records meter-phases">
                              <thead><tr><th>Phase</th><th>Voltage</th><th>Current</th><th>Power</th></tr></thead>
                              <tbody>
                                  ${phases.map(([ phase, label ]) => html`
                                      <tr>
                                          <td>${label}</td>
                                          <td>${shown(reading.voltage_V[phase], 1, 'V')}</td>
                                          <td>${shown(reading.current_A[phase], 2, 'A')}</td>
                                          <td>${shown(reading.power_W[phase],   0, 'W')}</td>
                                      </tr>
                                  `)}
                              </tbody>
                          </table>
                      </div>
                      <div class="kv-list">
                          <div class="kv"><span class="k">Frequency</span><span class="v">${shown(reading.frequency_Hz, 2, 'Hz')}</span></div>
                          <div class="kv"><span class="k">Imported</span><span class="v">${shown(reading.imported_Wh, 0, 'Wh')}</span></div>
                          <div class="kv"><span class="k">Exported</span><span class="v">${shown(reading.exported_Wh, 0, 'Wh')}</span></div>
                          <div class="kv"><span class="k">The meter</span><span class="v">${reading.manufacturer} ${reading.model}, version ${reading.version}, serial number ${reading.serialNumber}${reading.sunSpecModel ? `, SunSpec model ${reading.sunSpecModel}` : ''}</span></div>
                          <div class="kv"><span class="k">Read</span><span class="v">${formatTimestamp(reading.at)}</span></div>
                      </div>
                    `}

            <p class="muted small">
                ${!Meter?.read
                      ? html`What is written down about it. Where it is read over Modbus/TLS is set on the
                             <a href="${toURL('/configuration/meters')}">Meters</a> page under Configuration.`
                      : MayReadMeters
                            ? html`Read once a second over Modbus/TLS - how is set on the
                                   <a href="${toURL('/configuration/meters')}">Meters</a> page; every change of how
                                   that goes is in the <a href="${toURL('/logs')}">log</a> under the tag <code>meter</code>.`
                            : html`Read over Modbus/TLS; what it measures is for those who may read the meters.`}
            </p>

        </section>
    `;

}
