import { api, type EVSEState, type EVSEStatus } from '../api/client';
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
 * the reservation, and what is written down about its meter.
 *
 * Asked once when the page opens, and followed after that: every change the
 * station notices comes as an "evses" event on the stream the web interface
 * already follows for the log, within a second of happening. Nothing is asked
 * again and again.
 *
 * The configuration of the same EVSEs - what is fitted, what they may deliver
 * - is a page of its own, under Configuration. This one changes nothing.
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
                                This station has no EVSEs. They are added on the
                                <a href="${toURL('/configuration/evses')}">EVSEs</a> page under Configuration.
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

                ${tabsView(tabs, shown, show, 'The EVSEs of this station')}

                ${repeat(evses, evse => evse.id, evse => html`
                    <div id="panel-${tabIdOf(evse)}" role="tabpanel" aria-labelledby="tab-${tabIdOf(evse)}" ?hidden=${shown !== tabIdOf(evse)}>
                        ${evseView(evse)}
                    </div>
                `)}

            `);

        }

        async function load(): Promise<void> {

            try
            {
                const loaded = await api.evses.states();

                if (cancelled)
                    return;

                evses = loaded.evses;
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

        void load();

        return () => { cancelled = true; unfollow(); };

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


/** One EVSE, drawn in the panel of its tab. */
function evseView(EVSE: EVSEStatus): TemplateResult {

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

                <p class="muted small">
                    What is fitted and what it may deliver is set on the
                    <a href="${toURL('/configuration/evses')}">EVSEs</a> page under Configuration.
                </p>

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

            <section class="card">

                <h2><i class="fa-solid fa-gauge"></i> Energy meter</h2>

                ${EVSE.meter === null
                      ? html`<p class="muted small">Nothing is written down about a meter on this EVSE.</p>`
                      : html`
                          <div class="kv-list">
                              <div class="kv"><span class="k">Type</span><span class="v">${EVSE.meter.type ?? '-'}</span></div>
                              <div class="kv"><span class="k">Serial number</span><span class="v">${EVSE.meter.serialNumber ?? '-'}</span></div>
                          </div>
                        `}

                <p class="muted small">
                    What is written down about it. This station reads no meter yet, so there are no readings here.
                </p>

            </section>

        </div>
    `;

}
