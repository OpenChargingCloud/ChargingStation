import { api, type MeterIdentity, type MeterLink, type MeterStatus, type MeterStates, type MetersConfiguration, type MeterUpdate, type ModbusAnswer, type ModbusAsk, type SignedSession } from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import { must } from '@node/html';
import { logs } from '@node/logs/store';
import { asShown, draftOf, withPins, type StoreOffers } from '@node/pages/pins';
import { certificateVerdictView, judgementView, pinsFieldset, readPinsFieldset, storeOffers } from '@node/pages/pinViews';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { rememberTab, tabFromURL, tabsView, type Tab } from '@node/tabs';
import { errorMessage, field, formatTimestamp, numberField, numberFrom, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * How the meter of each EVSE is read - one tab for each: where, whom it is
 * believed to be, and whom this station is to it to read and to write - how
 * that goes right now, and a Modbus client to ask it by hand.
 *
 * Whom it is believed to be is said as a time server's is, with the node's
 * own fields: the certificates it may show and the roots its chain may end
 * at, what showing another comes to, and what is learned the first time it
 * is believed. Whom this station is to it is a TLS identity of the
 * certificate store for reading and one for writing, because a SunSpec meter
 * decides by the role in a certificate what its peer may do.
 *
 * A meter that signs charging sessions - one with the session model of
 * OpenChargingCloud's Modbus/TLS meter - is told when a session begins and
 * ends, with who charges, the tariff and the EVSE id set here; the sessions it
 * signed are listed with what ChargyCore said of each document, and each
 * document can be taken away as an .ocmf file.
 *
 * The Modbus client asks one request on a connection of its own, as the
 * identity for what the request does - the one to read with for 3 and 4, the
 * one to write with for 6 and 16 - and shows what went over the wire. A write
 * needs "meters:run"; a read what reading this page needs.
 */
export const metersPage: Page = {

    title: 'Meters',

    render({ root, url }) {

        const content = shell(root, {
            active:    '/configuration/meters',
            title:     'Meters',
            subtitle:  'How the meter of each EVSE is read over Modbus/TLS, whom it is believed to be and whom this station is to it - and a Modbus client to ask it by hand.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('meters', 'edit');
        const mayWrite  = auth.can('meters', 'run');

        let cancelled = false;
        let current:  MetersConfiguration | null = null;
        let offers:   StoreOffers | null         = null;
        let shown:    string | null              = null;

        /** How each meter stands, by its EVSE: from GET /status/meters, then from each "meters" event. */
        const states    = new Map<number, MeterStatus>();

        /** The function chosen in each EVSE's Modbus client - which decides its fields. */
        const functions = new Map<number, number>();

        /** What each EVSE's Modbus client was answered last, or why it was not asked. */
        const answers   = new Map<number, ModbusAnswer | string>();

        /** The signed sessions, the newest first; null where they could not be read. */
        let signed:       SignedSession[] | null = null;

        /** Where the latest session of each meter stood, as the events said it - to read them again when that changes. */
        let signedSaid    = '';


        function tabsOf(Configuration: MetersConfiguration): Tab[] {
            return Configuration.meters.map(meter => ({ id: tabIdOf(meter), label: `EVSE ${meter.label}`, icon: 'fa-gauge' }));
        }

        function show(id: string): void {
            shown = id;
            if (current !== null)
                rememberTab(tabsOf(current), id);
            draw();
        }


        function draw(): void {

            if (current === null)
                return;

            const station = current;
            const tabs    = tabsOf(station);

            if (tabs.length > 0 && (shown === null || !tabs.some(tab => tab.id === shown)))
                shown = tabFromURL(tabs, shown === null ? url : new URL(location.href));

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">${mayButNot('look at how the meters are read', 'change it')}</div>
                `}

                ${tabs.length === 0
                      ? html`<div class="cards"><section class="card">
                                <h2><i class="fa-solid fa-gauge"></i> No EVSEs</h2>
                                <p class="muted small">
                                    This station has no EVSEs, so there is no meter to read. They are added on the
                                    <a href="${toURL('/configuration/evses')}">EVSEs</a> page.
                                </p>
                             </section></div>`
                      : html`
                          ${tabsView(tabs, shown!, show, 'The EVSEs of this station')}

                          ${repeat(station.meters, meter => meter.evse, meter => html`
                              <div id="panel-${tabIdOf(meter)}" role="tabpanel" aria-labelledby="tab-${tabIdOf(meter)}" ?hidden=${shown !== tabIdOf(meter)}>
                                  <div class="cards">
                                      ${linkCard(station, meter)}
                                      ${stateCard(meter)}
                                      ${signedCard(meter)}
                                      ${clientCard(station, meter)}
                                  </div>
                              </div>
                          `)}
                        `}

            `);

        }


        /** Where the meter of one EVSE is read, whom it is believed to be, and whom the station is to it. */
        function linkCard(station: MetersConfiguration, meter: MeterLink): TemplateResult {

            const identities = station.identities;

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-gauge"></i> How it is read</h2>

                    <form id="meter-form-${meter.evse}" class="form-stack meter-form" @submit=${(event: SubmitEvent) => save(event, meter)}>

                        <label>Host
                            <input type="text" name="host" value="${meter.host ?? ''}" ?disabled=${!mayChange}
                                   placeholder="where it is read - empty for nowhere" spellcheck="false" autocomplete="off" />
                            <span class="hint">
                                Its name or its address. Its certificate has to be issued for it - which is why a
                                meter is not reached at another name than the one in its certificate.
                            </span>
                        </label>

                        <label>Port
                            <input type="number" name="port" min="1" max="65535" value="${meter.port ?? ''}" ?disabled=${!mayChange}
                                   placeholder="${station.defaults.port}" />
                        </label>

                        <label>Unit
                            <input type="number" name="unit" min="0" max="255" value="${meter.unit ?? ''}" ?disabled=${!mayChange}
                                   placeholder="${station.defaults.unit}" />
                        </label>

                        <label>Shown to read
                            <select name="readIdentity" ?disabled=${!mayChange}>
                                <option value="" ?selected=${meter.identities.read === null}>the identity with the least role that may read</option>
                                ${identities.map(identity => html`
                                    <option value="${identity.id}" ?selected=${meter.identities.read === identity.id}>${identityText(identity)}</option>
                                `)}
                                ${missing(meter.identities.read, identities)}
                            </select>
                        </label>

                        <label>Shown to write
                            <select name="writeIdentity" ?disabled=${!mayChange}>
                                <option value="" ?selected=${meter.identities.write === null}>nobody - nothing is written to it</option>
                                ${identities.map(identity => html`
                                    <option value="${identity.id}" ?selected=${meter.identities.write === identity.id}>${identityText(identity)}</option>
                                `)}
                                ${missing(meter.identities.write, identities)}
                            </select>
                            <span class="hint">
                                TLS identities of the <a href="${toURL('/configuration/certificates')}">certificate store</a> whose
                                certificate carries a SunSpec role: the meter decides by it what this station may do -
                                ReadOnlySunSpec reads, GridServiceSunSpec may also write the registers that command it.
                                ${identities.length === 0 ? html`<strong>There is none yet.</strong>` : nothing}
                            </span>
                        </label>

                        <label>Tariff
                            <input type="text" name="tariffText" value="${meter.tariffText ?? ''}" ?disabled=${!mayChange}
                                   placeholder="e.g. 001;EUR;0;35;0;0 - empty for none" spellcheck="false" autocomplete="off" />
                            <span class="hint">
                                What a meter that signs charging sessions signs into each one, as OCMF <code>TT</code>: in the
                                Bonn form - <code>001;EUR;</code> start fee<code>;</code> cents per kWh<code>;</code> blocking fee
                                per minute<code>;</code> from minute - or free text.
                            </span>
                        </label>

                        <label>EVSE id
                            <input type="text" name="evseId" value="${meter.evseId ?? ''}" ?disabled=${!mayChange}
                                   placeholder="e.g. DE*GEF*E0001*1 - empty for none" spellcheck="false" autocomplete="off" />
                            <span class="hint">This EVSE as OCMF <code>CI</code> names it, signed into each session.</span>
                        </label>

                        ${pinsFieldset(draftOf(meter.heldTo), {
                              service:   station.usage,
                              what:      'meter',
                              evidence:  true,
                              shown:     meter.server === null
                                             ? null
                                             : { name:         meter.server,
                                                 certificate:  meter.judgement?.certificate ?? meter.known?.certificate ?? null,
                                                 root:         meter.judgement?.root        ?? meter.known?.root        ?? null },
                              offers
                          })}

                        <p class="muted small">
                            Beyond that, a meter's certificate has to chain to a root this machine trusts or a TLS root
                            of the store for <code>${station.usage}</code>. What is news about it goes into the
                            metrological log: the meter of an EVSE is what a charge is billed by.
                        </p>

                        ${mayChange
                              ? html`
                                  <div class="form-actions">
                                      <button type="submit" class="btn primary">Save</button>
                                      <span class="form-notice" role="status"></span>
                                      <span class="form-error"  role="alert"></span>
                                  </div>
                                  <p class="muted small">
                                      Saved to ${station.file}, and read so at once - the other EVSEs' meters stay as they are.
                                  </p>
                                `
                              : nothing}

                    </form>

                </section>
            `;

        }


        /** How the meter of one EVSE stands right now, and what its certificate was last judged. */
        function stateCard(meter: MeterLink): TemplateResult {

            const state = states.get(meter.evse) ?? null;

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-plug-circle-check"></i> How it goes</h2>

                    ${!meter.read
                          ? html`<p class="muted small">This EVSE's meter is read nowhere.</p>`
                          : state === null
                                ? html`<p class="muted small">Not said yet.</p>`
                                : html`
                                    <div class="kv-list">
                                        <div class="kv"><span class="k">State</span><span class="v meter-state">${state.state}${state.since ? html`, since ${formatTimestamp(state.since)}` : nothing}</span></div>
                                        ${state.connection
                                              ? html`
                                                  <div class="kv"><span class="k">Security</span><span class="v">${state.connection.protocol}, <code>${state.connection.cipherSuite}</code></span></div>
                                                  <div class="kv"><span class="k">Read as</span><span class="v">${identityText(state.connection.identity)}</span></div>
                                                `
                                              : nothing}
                                        ${state.reading
                                              ? html`<div class="kv"><span class="k">The meter</span><span class="v">${state.reading.manufacturer} ${state.reading.model}, serial number ${state.reading.serialNumber}</span></div>`
                                              : nothing}
                                        ${state.connection?.sunSpec
                                              ? html`<div class="kv"><span class="k">SunSpec</span><span class="v meter-sunspec">from ${state.connection.sunSpec.base}, models ${state.connection.sunSpec.models.map(model => model.id).join(', ')} - read by ${state.connection.sunSpec.meter}</span></div>`
                                              : nothing}
                                    </div>
                                    ${state.error ? html`<p class="form-error meter-error">${state.error}</p>` : nothing}
                                  `}

                    ${meter.read
                          ? html`
                              <h3>Its certificate</h3>
                              <p>${certificateVerdictView(meter.judgement, meter.known)}</p>
                              ${meter.judgement ? judgementView(meter.judgement) : nothing}
                            `
                          : nothing}

                    <p class="muted small">
                        What it measures is on the <a href="${toURL('/evses')}">EVSEs</a> page; every change of how
                        it is read is in the <a href="${toURL('/logs')}">log</a> under the tag <code>meter</code>.
                    </p>

                </section>
            `;

        }


        /** The charging sessions the meter of one EVSE signed, or was to sign. */
        function signedCard(meter: MeterLink): TemplateResult {

            const sessions = (signed ?? []).filter(session => session.evse === meter.evse);

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-file-signature"></i> Signed sessions</h2>

                    ${signed === null
                          ? html`<p class="muted small">The signed sessions could not be read.</p>`
                          : sessions.length === 0
                                ? html`<p class="muted small signed-none">
                                      No session at this EVSE was signed yet. A meter signs them when it has the session model
                                      (64900) of OpenChargingCloud's Modbus/TLS meter, and this station an identity to write to it with.
                                  </p>`
                                : html`
                                    <div class="table-scroll">
                                        <table class="records signed-sessions">
                                            <thead><tr><th>Began</th><th>Ended</th><th>Energy</th><th>Who</th><th>Signed</th><th></th></tr></thead>
                                            <tbody>
                                                ${sessions.map(session => html`
                                                    <tr data-session="${session.id}">
                                                        <td>${formatTimestamp(session.startedAt)}</td>
                                                        <td>${session.stoppedAt ? formatTimestamp(session.stoppedAt) : '-'}</td>
                                                        <td>${session.energy_kWh !== undefined ? `${session.energy_kWh} kWh` : '-'}</td>
                                                        <td>${session.identification ? `${session.identificationType ?? ''} ${session.identification}`.trim() : 'nobody'}</td>
                                                        <td class="signed-state">
                                                            ${signedChip(session)}
                                                            ${session.error ? html`<div class="muted small signed-error">${session.error}</div>` : nothing}
                                                        </td>
                                                        <td>${session.ocmf
                                                                 ? html`<button type="button" class="btn small signed-download" @click=${() => download(session)}>.ocmf</button>`
                                                                 : nothing}</td>
                                                    </tr>
                                                `)}
                                            </tbody>
                                        </table>
                                    </div>
                                  `}

                    <p class="muted small">
                        Each document is checked by ChargyCore against the key the meter handed out with it, and kept by
                        the station beside its configuration, in <code>signed-sessions</code>.
                    </p>

                </section>
            `;

        }


        /** A Modbus client for the meter of one EVSE. */
        function clientCard(station: MetersConfiguration, meter: MeterLink): TemplateResult {

            const chosen    = functions.get(meter.evse) ?? 3;
            const writes    = chosen === 6 || chosen === 16;
            const answer    = answers.get(meter.evse) ?? null;
            const asWhom    = writes ? meter.identities.write : meter.identities.read;
            const identity  = station.identities.find(candidate => candidate.id === asWhom) ?? null;

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-terminal"></i> Modbus client</h2>

                    ${!meter.read
                          ? html`<p class="muted small">There is no meter to ask until it is read somewhere.</p>`
                          : html`
                              <form id="modbus-form-${meter.evse}" class="form-stack modbus-form" @submit=${(event: SubmitEvent) => ask(event, meter)}>

                                  <label>Function
                                      <select name="function" @change=${(event: Event) => { functions.set(meter.evse, numberFrom((event.target as HTMLSelectElement).value)); draw(); }}>
                                          <option value="3"  ?selected=${chosen === 3}>03 - Read Holding Registers</option>
                                          <option value="4"  ?selected=${chosen === 4}>04 - Read Input Registers</option>
                                          <option value="6"  ?selected=${chosen === 6}>06 - Write Single Register</option>
                                          <option value="16" ?selected=${chosen === 16}>16 - Write Multiple Registers</option>
                                      </select>
                                  </label>

                                  <label>Address
                                      <input type="number" name="address" min="0" max="65535" value="40000" />
                                      <span class="hint">As it goes on the wire: a SunSpec device begins at 40000.</span>
                                  </label>

                                  ${writes
                                        ? html`
                                            <label>${chosen === 6 ? 'Value' : 'Values'}
                                                <input type="text" name="values" spellcheck="false" autocomplete="off"
                                                       placeholder="${chosen === 6 ? 'e.g. 1 or 0x0001' : 'e.g. 1, 0xCAFE'}" />
                                            </label>
                                          `
                                        : html`
                                            <label>Count
                                                <input type="number" name="count" min="1" max="125" value="2" />
                                            </label>
                                          `}

                                  <p class="muted small modbus-as">
                                      ${writes
                                            ? meter.identities.write === null
                                                  ? html`Nothing is written: no identity is set to write to this meter above.`
                                                  : html`Written as ${identity ? identityText(identity) : meter.identities.write}, on a connection of its own.`
                                            : meter.identities.read === null
                                                  ? html`Read as the identity with the least role that may read, on a connection of its own.`
                                                  : html`Read as ${identity ? identityText(identity) : meter.identities.read}, on a connection of its own.`}
                                  </p>

                                  <div class="form-actions">
                                      <button type="submit" class="btn primary" ?disabled=${writes && !mayWrite}>${writes ? 'Write' : 'Read'}</button>
                                      ${writes && !mayWrite ? html`<span class="muted small">Writing to a meter needs <code>meters:run</code>.</span>` : nothing}
                                      <span class="form-error" role="alert"></span>
                                  </div>

                              </form>

                              ${answer === null ? nothing : answerView(answer)}
                            `}

                </section>
            `;

        }


        async function save(event: SubmitEvent, meter: MeterLink): Promise<void> {

            event.preventDefault();

            const form  = event.currentTarget as HTMLFormElement;
            const note  = must<HTMLElement>(form, '.form-notice');
            const error = must<HTMLElement>(form, '.form-error');

            note.textContent  = '';
            error.textContent = '';

            const pins = readPinsFieldset(form);

            if (pins.error !== undefined) {
                error.textContent = pins.error;
                return;
            }

            // Read before whileSaving switches the form off - FormData leaves a
            // switched-off control out.
            const update = withPins<MeterUpdate>({
                               evse:         meter.evse,
                               host:         field(form, 'host'),
                               port:         numberOrNull(numberField(form, 'port')),
                               unit:         numberOrNull(numberField(form, 'unit')),
                               identities:   { read:   field(form, 'readIdentity')  || null,
                                               write:  field(form, 'writeIdentity') || null },
                               tariffText:   field(form, 'tariffText').trim() || null,
                               evseId:       field(form, 'evseId').trim()     || null,
                               pinsAsShown:  asShown(meter.heldTo)
                           }, pins.draft);

            try
            {

                current = await whileSaving(content, note, () => api.meters.save(update));

                if (cancelled)
                    return;

                draw();
                form.reset();

                note.textContent = 'Saved.';

            }
            catch (problem)
            {
                if (!cancelled)
                    error.textContent = errorMessage(problem);
            }

        }


        async function ask(event: SubmitEvent, meter: MeterLink): Promise<void> {

            event.preventDefault();

            const form   = event.currentTarget as HTMLFormElement;
            const error  = must<HTMLElement>(form, '.form-error');
            const chosen = numberField(form, 'function');

            error.textContent = '';

            const request: ModbusAsk = { evse: meter.evse, function: chosen, address: numberField(form, 'address') };

            if (chosen === 6 || chosen === 16) {

                const values = registerValues(field(form, 'values'));

                if (typeof values === 'string') {
                    error.textContent = values;
                    return;
                }

                if (chosen === 6 && values.length !== 1) {
                    error.textContent = 'Write Single Register writes one value.';
                    return;
                }

                if (chosen === 6) request.value  = values[0];
                else              request.values = values;

            }
            else
                request.count = numberField(form, 'count');

            try
            {

                const answer = await whileSaving(form, null, () => api.meters.ask(request));

                if (cancelled)
                    return;

                answers.set(meter.evse, answer);

                // What was typed was asked, and is nothing to lose any more:
                // it stays in the fields, to be asked again, as what they were
                // drawn with.
                asked(form);

                draw();

            }
            catch (problem)
            {
                if (!cancelled) {
                    answers.set(meter.evse, errorMessage(problem));
                    draw();
                }
            }

        }


        async function load(): Promise<void> {

            try
            {

                const [ loaded, kept, live, sessions ] = await Promise.all([
                                                             api.meters.get(),
                                                             storeOffers('meter'),
                                                             api.meters.states().catch(() => null),
                                                             api.meters.sessions().catch(() => null)
                                                         ]);

                if (cancelled)
                    return;

                current = loaded;
                offers  = kept;
                signed  = sessions?.sessions ?? null;

                if (live !== null)
                    took(live);

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">How the meters are read could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        async function reload(): Promise<void> {
            await load();
            if (!cancelled)
                content.querySelectorAll<HTMLFormElement>('form.meter-form').forEach(form => form.reset());
        }

        function took(States: MeterStates): void {
            states.clear();
            for (const state of States.meters)
                states.set(state.evse, state);
        }

        /** The signed sessions read again - after a meter said its latest one stands otherwise now. */
        async function reread(): Promise<void> {
            try
            {
                const sessions = await api.meters.sessions();
                if (!cancelled) {
                    signed = sessions.sessions;
                    draw();
                }
            }
            catch
            {
                // What was shown stays: the next change says so again.
            }
        }

        const unfollow = logs.onEvent<MeterStates>('meters', data => {

            took(data);

            const said = JSON.stringify(data.meters.map(meter => meter.signed ?? null));

            if (said !== signedSaid) {
                const first = signedSaid === '';
                signedSaid  = said;
                if (!first)
                    void reread();
            }

            draw();

        });

        // Every form: how a meter is read, and a request typed into a Modbus
        // client and not asked yet - one that was asked is in the fields as
        // they were drawn, see asked().
        const release  = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; unfollow(); release(); };

    }

};


/**
 * A request asked: what is in its fields is what they say from now on, so
 * that a page that was only asked something does not hold its leaving up.
 */
function asked(Form: HTMLFormElement): void {

    for (const input of Form.querySelectorAll<HTMLInputElement>('input'))
        input.defaultValue = input.value;

    for (const option of Form.querySelectorAll<HTMLOptionElement>('option'))
        option.defaultSelected = option.selected;

}


/** What came back from one request, down to the bytes. */
function answerView(Answer: ModbusAnswer | string): TemplateResult {

    if (typeof Answer === 'string')
        return html`<p class="form-error modbus-error">${Answer}</p>`;

    const tone = Answer.outcome === 'answered' ? 'ok' : Answer.outcome === 'exception' ? 'warn' : 'bad';

    return html`
        <div class="modbus-answer">

            <h3>${Answer.name} <span class="chip ${tone}">${outcomeText(Answer)}</span></h3>

            <div class="kv-list">
                ${Answer.identity ? html`<div class="kv"><span class="k">As</span><span class="v">${identityText(Answer.identity)}</span></div>` : nothing}
                ${Answer.tls ? html`<div class="kv"><span class="k">Security</span><span class="v">${Answer.tls.protocol}, <code>${Answer.tls.cipherSuite}</code></span></div>` : nothing}
                <div class="kv"><span class="k">Sent</span><span class="v"><code>${spaced(Answer.request)}</code></span></div>
                ${Answer.response ? html`<div class="kv"><span class="k">Answered</span><span class="v"><code>${spaced(Answer.response)}</code></span></div>` : nothing}
                ${Answer.ms !== undefined ? html`<div class="kv"><span class="k">Took</span><span class="v">${Answer.ms} ms</span></div>` : nothing}
            </div>

            ${Answer.error ? html`<p class="form-error">${Answer.error}</p>` : nothing}

            ${Answer.registers && Answer.registers.length > 0
                  ? html`
                      <div class="table-scroll">
                          <table class="records modbus-registers">
                              <thead><tr><th>Address</th><th>Value</th><th>Hex</th><th>Signed</th><th>Text</th></tr></thead>
                              <tbody>
                                  ${Answer.registers.map(register => html`
                                      <tr>
                                          <td>${register.address}</td>
                                          <td>${register.value}</td>
                                          <td><code>0x${register.value.toString(16).toUpperCase().padStart(4, '0')}</code></td>
                                          <td>${register.value >= 0x8000 ? register.value - 0x10000 : register.value}</td>
                                          <td><code>${textOf(register.value)}</code></td>
                                      </tr>
                                  `)}
                              </tbody>
                          </table>
                      </div>
                    `
                  : nothing}

        </div>
    `;

}


/** Where a signed session stands, as a chip: signed and verified, signed and not, or not signed. */
function signedChip(Session: SignedSession): TemplateResult {
    switch (Session.state) {
        case 'signed':    return Session.verification === 'ValidSignature'
                                     ? html`<span class="chip ok">signed, verified</span>`
                                     : html`<span class="chip bad">signed, ${Session.verification ?? 'not verified'}</span>`;
        case 'unsigned':  return html`<span class="chip bad">not signed</span>`;
        default:          return html`<span class="chip warn">${Session.state}</span>`;
    }
}


/** A session's document, as a file to take away. */
function download(Session: SignedSession): void {

    const url  = URL.createObjectURL(new Blob([ Session.ocmf ?? '' ], { type: 'text/plain' }));
    const link = document.createElement('a');

    link.href     = url;
    link.download = `${Session.id}.ocmf`;
    link.click();

    setTimeout(() => URL.revokeObjectURL(url), 1000);

}


/** What an answer came to, in words. */
function outcomeText(Answer: ModbusAnswer): string {
    switch (Answer.outcome) {
        case 'answered':    return Answer.operation === 'write' ? 'written' : 'answered';
        case 'exception':   return `refused: ${Answer.exception?.name ?? 'exception'} (${Answer.exception?.code ?? '?'})`;
        case 'noIdentity':  return 'nobody to ask as';
        case 'untrusted':   return 'not believed';
        case 'refused':     return 'handshake refused';
        default:            return Answer.outcome;
    }
}


/**
 * The register values typed into a write - separated by commas or blanks,
 * each a decimal number or a hexadecimal one after 0x - or why they are not.
 */
export function registerValues(Text: string): number[] | string {

    const words = Text.split(/[\s,;]+/).filter(word => word.length > 0);

    if (words.length === 0)
        return 'Say what is written: a value, or several separated by commas.';

    const values: number[] = [];

    for (const word of words) {

        const value = /^0x[0-9a-f]+$/i.test(word)
                          ? parseInt(word.slice(2), 16)
                          : /^\d+$/.test(word)
                                ? Number(word)
                                : NaN;

        if (Number.isNaN(value) || value > 0xFFFF)
            return `'${word}' is no register value: a whole number from 0 to 65535, or 0x0000 to 0xFFFF.`;

        values.push(value);

    }

    if (values.length > 123)
        return 'One write carries at most 123 registers.';

    return values;

}


/** An identity as a sentence names it: its label and the role in it. */
function identityText(Identity: MeterIdentity): string {
    return `${Identity.label} (${Identity.role})`;
}


/** An option for an identity the meter is configured with and the store no longer offers. */
function missing(Id: string | null, Identities: MeterIdentity[]): TemplateResult | typeof nothing {
    return Id !== null && !Identities.some(identity => identity.id === Id)
               ? html`<option value="${Id}" selected>${Id} - not in the store</option>`
               : nothing;
}


/** The two characters a register holds, where they are printable. */
function textOf(Value: number): string {
    const printable = (code: number) => code >= 0x20 && code < 0x7F ? String.fromCharCode(code) : '.';
    return printable(Value >> 8) + printable(Value & 0xFF);
}


/** Hexadecimal in pairs: "039C400002" as "03 9C 40 00 02". */
function spaced(Hex: string): string {
    return Hex.match(/.{1,2}/g)?.join(' ') ?? Hex;
}


/** A number not typed is none: the station takes its default. */
function numberOrNull(Value: number): number | null {
    return Number.isNaN(Value) ? null : Value;
}


/** The id of an EVSE's tab, in the address and in the ids of its button and panel. */
function tabIdOf(Meter: MeterLink): string {
    return `evse-${Meter.evse}`;
}
