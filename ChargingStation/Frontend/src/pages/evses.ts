import { api, type Connector, type EVSE, type EVSEConfiguration } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, whileSaving } from '../ui';

/**
 * The EVSEs of this charging station: the places a vehicle can be plugged in.
 *
 * Edited as a whole rather than one at a time, because they are only valid
 * together - OCPP numbers them from 1 upwards without gaps, so removing the
 * third of four is a change to two of them. The page renumbers what is left
 * and sends the lot.
 *
 * Saving rebuilds the OCPP nodes from the new list, so what this page shows and
 * what a back end would be told about this station are never two different
 * things. No restart is owed.
 *
 * Three permissions meet on this page, and the difference between them is the
 * reason the whole thing is laid out the way it is. Taking an EVSE out of
 * service is a switch, and what a cable may deliver is a number that gets
 * corrected - the installer may do both. What cable is fitted at all is a claim
 * about the wall, and nothing further down can check it: a vehicle is simply
 * told what it is looking at. That takes the system administrator role. See
 * Web/UserRoles.cs.
 */
export const evsesPage: Page = {

    title: 'EVSEs',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/evses',
            title:     'EVSEs',
            subtitle:  'The places a vehicle can be plugged into this charging station.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => void load());

        const mayChangeHardware     = auth.can('changeHardware');
        const mayChangeLimits       = auth.can('changePowerLimits');
        const mayChangeAvailability = auth.can('changeAvailability');
        const mayChangeAnything     = mayChangeHardware || mayChangeLimits || mayChangeAvailability;

        let cancelled     = false;
        let configuration: EVSEConfiguration | null = null;

        /** What is on screen: the saved list until somebody changes it. */
        let draft: EVSE[] = [];

        /** Whether the draft differs from what was last saved. */
        let dirty = false;


        function renumber(): void {
            draft.forEach((evse, index) => {
                evse.id = index + 1;
                evse.connectors.forEach((connector, position) => { connector.id = position + 1; });
            });
        }

        /** What a draft amounts to; more than one of these can be true at once. */
        interface Change {
            hardware:      boolean;
            powerLimits:   boolean;
            availability:  boolean;
        }

        /**
         * What kind of change the draft would be - the same question the
         * station asks itself when the request arrives, asked here so that
         * somebody who may not do it is told before they press Save rather
         * than by a 403 afterwards.
         */
        function changeKind(): Change {

            const saved = configuration?.evses ?? [];

            // A different number of them is a different station, and there is
            // nothing to compare switch by switch: the lists do not line up.
            if (saved.length !== draft.length)
                return { hardware: true, powerLimits: false, availability: false };

            return {

                hardware: draft.some((evse, index) => {

                    const other = saved[index];

                    return evse.id                !== other.id                ||
                           evse.physicalReference !== other.physicalReference ||
                           evse.meterType         !== other.meterType         ||
                           evse.meterSerialNumber !== other.meterSerialNumber ||
                           evse.connectors.length !== other.connectors.length ||
                           evse.connectors.some((connector, position) => connector.type !== other.connectors[position].type);

                }),

                powerLimits: draft.some((evse, index) => {

                    const other = saved[index];

                    return evse.maxPower_kW !== other.maxPower_kW ||
                           evse.connectors.length !== other.connectors.length ||
                           evse.connectors.some((connector, position) => connector.maxPower_kW !== other.connectors[position].maxPower_kW);

                }),

                availability: draft.some((evse, index) => evse.operative !== saved[index].operative)

            };

        }

        /** Whether this browser may save everything the draft turned out to be. */
        function maySaveDraft(): boolean {

            const change = changeKind();

            if (!change.hardware && !change.powerLimits && !change.availability)
                return false;

            return (!change.hardware     || mayChangeHardware) &&
                   (!change.powerLimits  || mayChangeLimits)   &&
                   (!change.availability || mayChangeAvailability);

        }

        function draw(): void {

            if (configuration === null)
                return;

            // Into a local, because the narrowing above does not survive into
            // the callbacks of the template below.
            const current = configuration;
            const kind    = changeKind();

            render(content, html`

                ${mayChangeAnything ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the EVSEs
                        but not change them. Taking one out of service and correcting what a cable may deliver
                        need the installer role; changing what is fitted needs the system administrator role.
                    </div>
                `}

                ${mayChangeAnything && !mayChangeHardware ? html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may take these EVSEs
                        out of service and correct what they and their cables may deliver. How many of them
                        there are and what shape of plug is fitted describes hardware somebody installed, so
                        changing that needs the system administrator role.
                    </div>
                `: ''}

                <datalist id="connector-types">
                    ${current.connectorTypes.map(type => html`<option value="${type}"></option>`)}
                </datalist>

                <div class="evse-list" id="evses">
                    ${draft.map((evse, index) => html`
                        <section class="card evse" data-index="${index}">

                            <h2>
                                <i class="fa-solid fa-plug"></i> EVSE ${evse.id}
                                <button type="button" class="btn small danger" data-remove="${index}"
                                        ${!mayChangeHardware ? html`disabled` : draft.length === 1 ? html`disabled title="A charging station needs at least one EVSE."` : ''}>
                                    Remove
                                </button>
                            </h2>

                            <div class="form-stack">

                                <label>Maximum power of this EVSE in kW
                                    <input type="number" data-field="maxPower_kW" data-index="${index}"
                                           min="0.1" max="${current.maxPower_kW}" step="0.1"
                                           value="${evse.maxPower_kW}" ${mayChangeLimits ? '' : html`disabled`} />
                                    <span class="hint">
                                        What the power stage behind its cables can deliver. Only one vehicle
                                        charges at a time, so this is not their sum - and no cable below may be
                                        set above it.
                                    </span>
                                </label>

                                <div class="connectors">

                                    <span class="k">Cables and sockets</span>

                                    <div class="connector-list">
                                        ${evse.connectors.map((connector, position) => html`
                                            <div class="connector-row">

                                                <span class="connector-id">${connector.id}</span>

                                                <input type="text" class="connector-type"
                                                       list="connector-types"
                                                       data-connector-type="${index}" data-position="${position}"
                                                       value="${connector.type}"
                                                       maxlength="${current.maxConnectorTypeLength}"
                                                       placeholder="e.g. sType2"
                                                       title="${current.connectorTypes.includes(connector.type)
                                                                   ? 'Named by OCPP 2.1'
                                                                   : 'Not named by OCPP 2.1; passed on as written'}"
                                                       ${mayChangeHardware ? '' : html`disabled`} />

                                                ${current.connectorTypes.includes(connector.type)
                                                      ? ''
                                                      : html`<span class="chip custom" title="Not named by OCPP 2.1; passed on as written">custom</span>`}

                                                <input type="number" class="connector-power"
                                                       data-connector-power="${index}" data-position="${position}"
                                                       min="0.1" max="${evse.maxPower_kW}" step="0.1"
                                                       value="${connector.maxPower_kW}"
                                                       ${mayChangeLimits ? '' : html`disabled`} />
                                                <span class="unit">kW</span>

                                                <button type="button" class="btn small danger"
                                                        data-remove-connector="${index}" data-position="${position}"
                                                        ${!mayChangeHardware ? html`disabled`
                                                            : evse.connectors.length === 1 ? html`disabled title="An EVSE needs at least one connector."`
                                                            : ''}>
                                                    Remove
                                                </button>

                                            </div>
                                        `)}
                                    </div>

                                    ${mayChangeHardware
                                          ? html`
                                              <div class="add-connector">
                                                  <input type="text" data-custom="${index}" list="connector-types"
                                                         placeholder="another type, e.g. cCCS2"
                                                         maxlength="${current.maxConnectorTypeLength}"
                                                         ${evse.connectors.length >= current.maxConnectors ? html`disabled` : ''} />
                                                  <button type="button" class="btn small" data-add-connector="${index}"
                                                          ${evse.connectors.length >= current.maxConnectors ? html`disabled` : ''}>Add</button>
                                              </div>
                                              <span class="hint">
                                                  OCPP 2.1 leaves this list open, so a plug it does not name may still be
                                                  typed here and is passed on as written. A new cable starts at the limit
                                                  of its EVSE.
                                              </span>
                                            `
                                          : ''}

                                </div>

                                <label>Physical reference
                                    <input type="text" data-field="physicalReference" data-index="${index}"
                                           value="${evse.physicalReference ?? ''}" placeholder="what is written on the housing, e.g. A"
                                           ${mayChangeHardware ? '' : html`disabled`} />
                                </label>

                                <label class="checkbox">
                                    <input type="checkbox" data-field="operative" data-index="${index}"
                                           ${evse.operative ? html`checked` : ''} ${mayChangeAvailability ? '' : html`disabled`} />
                                    Operative
                                    <span class="hint">An inoperative EVSE is reported as one, and no vehicle is served by it.</span>
                                </label>

                                <label>Meter type
                                    <input type="text" data-field="meterType" data-index="${index}"
                                           value="${evse.meterType ?? ''}" placeholder="optional" ${mayChangeHardware ? '' : html`disabled`} />
                                </label>

                                <label>Meter serial number
                                    <input type="text" data-field="meterSerialNumber" data-index="${index}"
                                           value="${evse.meterSerialNumber ?? ''}" placeholder="optional" ${mayChangeHardware ? '' : html`disabled`} />
                                </label>

                            </div>

                        </section>
                    `)}
                </div>

                <div class="evse-actions">
                    <button type="button" id="add" class="btn"
                            ${!mayChangeHardware || draft.length >= current.maxEVSEs ? html`disabled` : ''}>
                        Add an EVSE
                    </button>
                    <button type="button" id="save" class="btn primary" ${dirty && maySaveDraft() ? '' : html`disabled`}>Save</button>
                    <button type="button" id="revert" class="btn" ${dirty ? '' : html`disabled`}>Discard changes</button>
                    <span id="form-note"  class="form-notice" role="status"></span>
                    <span id="form-error" class="form-error"  role="alert"></span>

                    ${dirty && kind.hardware && !mayChangeHardware
                          ? html`<span class="form-error">
                                     This changes what the station is made of, not only what it may deliver or
                                     whether it is in service - which needs the system administrator role.
                                 </span>`
                          : html`<span class="hint">
                                     Saved to ${current.file}, and in effect at once - the OCPP nodes are rebuilt from it.
                                 </span>`}
                </div>

            `);

            wire();

        }

        function touched(): void {
            dirty = true;
            draw();
        }

        function wire(): void {

            const list = must<HTMLElement>(content, '#evses');

            list.addEventListener('input', event => {

                const input = event.target as HTMLInputElement;

                const connectorType = input.dataset.connectorType;
                if (connectorType !== undefined) {
                    draft[Number(connectorType)].connectors[Number(input.dataset.position)].type = input.value.trim();
                    dirty = true;
                    enableActions();
                    return;
                }

                const connectorPower = input.dataset.connectorPower;
                if (connectorPower !== undefined) {
                    draft[Number(connectorPower)].connectors[Number(input.dataset.position)].maxPower_kW = Number(input.value);
                    dirty = true;
                    enableActions();
                    return;
                }

                const index = Number(input.dataset.index);
                const field = input.dataset.field;

                if (!field || Number.isNaN(index))
                    return;

                const evse = draft[index];

                if (field === 'maxPower_kW')
                    evse.maxPower_kW = Number(input.value);
                else if (field === 'operative')
                    evse.operative = input.checked;
                else if (field === 'physicalReference' || field === 'meterType' || field === 'meterSerialNumber')
                    evse[field] = input.value.trim() === '' ? null : input.value;

                // No redraw here: the field somebody is typing in would lose
                // its caret. Only the buttons need to notice.
                dirty = true;
                enableActions();

            });

            list.addEventListener('click', event => {

                const target = event.target as HTMLElement;

                const add = target.closest<HTMLElement>('[data-add-connector]');
                if (add) {

                    const index = Number(add.dataset.addConnector);
                    const input = must<HTMLInputElement>(content, `input[data-custom="${index}"]`);
                    const type  = input.value.trim();
                    const evse  = draft[index];

                    if (type.length > 0 && !evse.connectors.some(connector => connector.type === type)) {
                        // At the limit of its EVSE, which is the most it could
                        // be anyway and the only number available before
                        // somebody has said anything about this cable.
                        evse.connectors = [...evse.connectors,
                                           { id: evse.connectors.length + 1, type, maxPower_kW: evse.maxPower_kW }];
                        touched();
                    }

                    return;

                }

                const removeConnector = target.closest<HTMLElement>('[data-remove-connector]');
                if (removeConnector) {

                    const evse = draft[Number(removeConnector.dataset.removeConnector)];

                    if (evse.connectors.length > 1) {
                        evse.connectors.splice(Number(removeConnector.dataset.position), 1);
                        renumber();
                        touched();
                    }

                    return;

                }

                const remove = target.closest<HTMLElement>('[data-remove]');
                if (remove && draft.length > 1) {
                    draft.splice(Number(remove.dataset.remove), 1);
                    renumber();
                    touched();
                }

            });

            list.addEventListener('keydown', event => {

                const key = event as KeyboardEvent;

                if (key.key !== 'Enter')
                    return;

                const input = (key.target as HTMLElement).closest<HTMLInputElement>('[data-custom]');

                if (input) {
                    // Otherwise Enter in a text field submits nothing and looks
                    // like the page ignored it.
                    key.preventDefault();
                    must<HTMLElement>(content, `[data-add-connector="${input.dataset.custom}"]`).click();
                }

            });

            must<HTMLButtonElement>(content, '#add').addEventListener('click', () => {

                draft.push({
                    id:                 draft.length + 1,
                    connectors:         [ { id: 1, type: 'sType2', maxPower_kW: 22 } ],
                    maxPower_kW:        22,
                    operative:          true,
                    physicalReference:  String.fromCharCode(65 + draft.length),
                    meterType:          null,
                    meterSerialNumber:  null
                });

                touched();

            });

            must<HTMLButtonElement>(content, '#revert').addEventListener('click', () => {
                draft = clone(configuration!.evses);
                dirty = false;
                draw();
            });

            must<HTMLButtonElement>(content, '#save').addEventListener('click', () => void save());

            enableActions();

        }

        function enableActions(): void {
            must<HTMLButtonElement>(content, '#save').disabled   = !dirty || !maySaveDraft();
            must<HTMLButtonElement>(content, '#revert').disabled = !dirty;
        }

        async function save(): Promise<void> {

            const note = must<HTMLElement>(content, '#form-note');

            note.textContent = '';

            must<HTMLElement>(content, '#form-error').textContent = '';

            try
            {
                configuration = await whileSaving(content, note, () => api.evses.save(draft));
                draft         = clone(configuration.evses);
                dirty         = false;

                draw();

                must<HTMLElement>(content, '#form-note').textContent = 'Saved, and in effect.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#form-error').textContent = errorMessage(problem);
            }

        }

        async function load(): Promise<void> {

            try
            {
                const loaded = await api.evses.get();

                if (cancelled)
                    return;

                configuration = loaded;
                draft         = clone(loaded.evses);
                dirty         = false;

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

        void load();

        return () => { cancelled = true; };

    }

};


/** A copy to edit, so that discarding changes has something to go back to. */
function clone(evses: EVSE[]): EVSE[] {
    return evses.map(evse => ({
        ...evse,
        connectors: evse.connectors.map((connector): Connector => ({ ...connector }))
    }));
}
