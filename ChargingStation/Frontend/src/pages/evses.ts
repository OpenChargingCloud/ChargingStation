import { api, type Connector, type EVSE, type EVSEConfiguration } from '../api/client';
import { auth } from '../auth';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, numberFrom, whileSaving } from '@node/ui';
import { unsaved } from '@node/unsaved';
import { html, nothing, render, repeat } from '@node/view';

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
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChangeHardware     = auth.can('evses', 'edit');
        const mayChangeLimits       = auth.can('power', 'edit');
        const mayChangeAvailability = auth.can('availability', 'edit');
        const mayChangeAnything     = mayChangeHardware || mayChangeLimits || mayChangeAvailability;

        let cancelled     = false;
        let configuration: EVSEConfiguration | null = null;

        /** What is on screen: the saved list until somebody changes it. */
        let draft: EVSE[] = [];

        /** Whether the draft differs from what was last saved. */
        let dirty = false;

        /**
         * What is typed into an EVSE's field for another type of cable and not
         * added yet - the field keeps it across a draw, and this is what asks
         * before leaving the page, and what carries it over a save, which
         * draws the EVSEs anew from the station's answer. Kept by the EVSE
         * rather than by its place or its number, which change when an EVSE
         * before it is removed.
         */
        const anotherType = new WeakMap<EVSE, string>();

        /** Whether another type of cable is typed somewhere and not added yet. */
        const anotherTypeTyped = (): boolean => draft.some(evse => (anotherType.get(evse) ?? '') !== '');


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

        /**
         * What a row is known by while it is drawn - an EVSE, a cable - so
         * that its fields stay its own while one before it is removed: kept
         * by the object, not by its place or its number, which change. The
         * fields hold what is typed into them; a draw does not touch it.
         */
        const keys    = new WeakMap<object, number>();
        let   nextKey = 0;

        const keyOf = (row: object): number => {
            let key = keys.get(row);
            if (key === undefined)
                keys.set(row, key = ++nextKey);
            return key;
        };

        /** What a number field shows of a number: nothing where nothing was said. */
        const shown = (value: number): string => Number.isFinite(value) ? String(value) : '';


        function draw(): void {

            if (configuration === null)
                return;

            // Into a local, because the narrowing above does not survive into
            // the callbacks of the template below.
            const current = configuration;
            const kind    = changeKind();

            render(content, html`

                ${mayChangeAnything ? nothing : html`
                    <div class="notice">${mayButNot('look at the EVSEs', 'change them')}</div>
                `}

                ${mayChangeAnything && !mayChangeHardware ? html`
                    <div class="notice">
                        ${mayButNot(mayChangeShortOfWhatIsFitted(mayChangeAvailability, mayChangeLimits), 'change what is fitted')}
                        How many EVSEs there are and what shape of plug is fitted describes hardware somebody
                        installed.
                    </div>
                `: nothing}

                <datalist id="connector-types">
                    ${current.connectorTypes.map(type => html`<option value="${type}"></option>`)}
                </datalist>

                <div class="evse-list" id="evses">
                    ${repeat(draft, keyOf, (evse, index) => html`
                        <section class="card evse" data-index="${index}">

                            <h2>
                                <i class="fa-solid fa-plug"></i> EVSE ${evse.id}
                                <button type="button" class="btn small danger" data-remove="${index}"
                                        ?disabled=${!mayChangeHardware || draft.length === 1}
                                        title=${mayChangeHardware && draft.length === 1 ? 'A charging station needs at least one EVSE.' : nothing}
                                        @click=${() => removeEVSE(evse)}>
                                    Remove
                                </button>
                            </h2>

                            <div class="form-stack">

                                <label>Maximum power of this EVSE in kW
                                    <input type="number" data-field="maxPower_kW" data-index="${index}"
                                           min="0.1" max="${current.maxPower_kW}" step="0.1"
                                           value="${shown(evse.maxPower_kW)}" ?disabled=${!mayChangeLimits}
                                           @input=${(event: Event) => typed(() => { evse.maxPower_kW = numberFrom(valueOf(event)); })} />
                                    <span class="hint">
                                        What the power stage behind its cables can deliver. Only one vehicle
                                        charges at a time, so this is not their sum - and no cable below may be
                                        set above it.
                                    </span>
                                </label>

                                <div class="connectors">

                                    <span class="k">Cables and sockets</span>

                                    <div class="connector-list">
                                        ${repeat(evse.connectors, keyOf, (connector, position) => html`
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
                                                       ?disabled=${!mayChangeHardware}
                                                       @input=${(event: Event) => typed(() => { connector.type = valueOf(event).trim(); })} />

                                                ${current.connectorTypes.includes(connector.type)
                                                      ? nothing
                                                      : html`<span class="chip custom" title="Not named by OCPP 2.1; passed on as written">custom</span>`}

                                                <input type="number" class="connector-power"
                                                       data-connector-power="${index}" data-position="${position}"
                                                       min="0.1" max="${evse.maxPower_kW}" step="0.1"
                                                       value="${shown(connector.maxPower_kW)}"
                                                       ?disabled=${!mayChangeLimits}
                                                       @input=${(event: Event) => typed(() => { connector.maxPower_kW = numberFrom(valueOf(event)); })} />
                                                <span class="unit">kW</span>

                                                <button type="button" class="btn small danger"
                                                        data-remove-connector="${index}" data-position="${position}"
                                                        ?disabled=${!mayChangeHardware || evse.connectors.length === 1}
                                                        title=${mayChangeHardware && evse.connectors.length === 1 ? 'An EVSE needs at least one connector.' : nothing}
                                                        @click=${() => removeConnector(evse, connector)}>
                                                    Remove
                                                </button>

                                            </div>
                                        `)}
                                    </div>

                                    ${mayChangeHardware
                                          ? html`
                                              <div class="add-connector">
                                                  <input type="text" data-custom="${index}" list="connector-types"
                                                         value="${anotherType.get(evse) ?? ''}"
                                                         placeholder="another type, e.g. cCCS2"
                                                         maxlength="${current.maxConnectorTypeLength}"
                                                         ?disabled=${evse.connectors.length >= current.maxConnectors}
                                                         @input=${(event: Event) => { anotherType.set(evse, valueOf(event)); }}
                                                         @keydown=${(event: KeyboardEvent) => {
                                                             // Otherwise Enter in a text field submits nothing
                                                             // and looks like the page ignored it.
                                                             if (event.key === 'Enter') {
                                                                 event.preventDefault();
                                                                 addConnector(evse, event.currentTarget as HTMLInputElement);
                                                             }
                                                         }} />
                                                  <button type="button" class="btn small" data-add-connector="${index}"
                                                          ?disabled=${evse.connectors.length >= current.maxConnectors}
                                                          @click=${(event: Event) => addConnector(evse, (event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement)}>Add</button>
                                              </div>
                                              <span class="hint">
                                                  OCPP 2.1 leaves this list open, so a plug it does not name may still be
                                                  typed here and is passed on as written. A new cable starts at the limit
                                                  of its EVSE.
                                              </span>
                                            `
                                          : nothing}

                                </div>

                                <label>Physical reference
                                    <input type="text" data-field="physicalReference" data-index="${index}"
                                           value="${evse.physicalReference ?? ''}" placeholder="what is written on the housing, e.g. A"
                                           ?disabled=${!mayChangeHardware}
                                           @input=${(event: Event) => typed(() => { evse.physicalReference = orNull(valueOf(event)); })} />
                                </label>

                                <label class="checkbox">
                                    <input type="checkbox" data-field="operative" data-index="${index}"
                                           ?checked=${evse.operative} ?disabled=${!mayChangeAvailability}
                                           @change=${(event: Event) => typed(() => { evse.operative = (event.target as HTMLInputElement).checked; })} />
                                    Operative
                                    <span class="hint">An inoperative EVSE is reported as one, and no vehicle is served by it.</span>
                                </label>

                                <label>Meter type
                                    <input type="text" data-field="meterType" data-index="${index}"
                                           value="${evse.meterType ?? ''}" placeholder="optional" ?disabled=${!mayChangeHardware}
                                           @input=${(event: Event) => typed(() => { evse.meterType = orNull(valueOf(event)); })} />
                                </label>

                                <label>Meter serial number
                                    <input type="text" data-field="meterSerialNumber" data-index="${index}"
                                           value="${evse.meterSerialNumber ?? ''}" placeholder="optional" ?disabled=${!mayChangeHardware}
                                           @input=${(event: Event) => typed(() => { evse.meterSerialNumber = orNull(valueOf(event)); })} />
                                </label>

                            </div>

                        </section>
                    `)}
                </div>

                <div class="evse-actions">
                    <button type="button" id="add" class="btn"
                            ?disabled=${!mayChangeHardware || draft.length >= current.maxEVSEs}
                            @click=${addEVSE}>
                        Add an EVSE
                    </button>
                    <button type="button" id="save" class="btn primary" ?disabled=${!(dirty && maySaveDraft())}
                            @click=${() => void save()}>Save</button>
                    <button type="button" id="revert" class="btn" ?disabled=${!dirty}
                            @click=${revert}>Discard changes</button>
                    <span id="form-note"  class="form-notice" role="status"></span>
                    <span id="form-error" class="form-error"  role="alert"></span>

                    ${dirty && kind.hardware && !mayChangeHardware
                          ? html`<span class="form-error">
                                     This changes what the station is made of, not only what it may deliver or
                                     whether it is in service - which needs a role that may change what is fitted.
                                 </span>`
                          : html`<span class="hint">
                                     Saved to ${current.file}, and in effect at once - the OCPP nodes are rebuilt from it.
                                 </span>`}
                </div>

            `);

        }

        /** What a field says now. */
        const valueOf = (event: Event): string => (event.target as HTMLInputElement).value;

        /** What a field for an optional text says: nothing, where it is empty. */
        const orNull = (text: string): string | null => text.trim() === '' ? null : text;

        /**
         * What is typed goes into the draft, and the page is drawn anew from
         * it: the buttons, the limit of the cables below an EVSE's power, the
         * chip of a type OCPP does not name. The field typed into is left as
         * it is typed - a draw does not touch what is in a field.
         *
         * What a field for power says is read with numberFrom: emptied, it is
         * NaN, which goes as null - not said, and a cable that says nothing
         * may deliver as much as its EVSE. Number() made it 0 kW, which the
         * station refused, and which a drawing anew put back into the field
         * (found by the local controller).
         */
        function typed(Change: () => void): void {
            Change();
            dirty = true;
            draw();
        }

        function touched(): void {
            dirty = true;
            draw();
        }

        /**
         * Every field back at what the page last drew into it: the draft as
         * it is now. A draw leaves what is typed in a field; after the draft
         * was put back to what the station has, the fields go with it.
         */
        function fieldsAsDrawn(): void {
            content.querySelectorAll<HTMLInputElement>('#evses input').forEach(input => {
                if (input.type === 'checkbox')
                    input.checked = input.defaultChecked;
                else
                    input.value = input.defaultValue;
            });
        }

        function addConnector(evse: EVSE, input: HTMLInputElement): void {

            const type = input.value.trim();

            if (type.length === 0 || evse.connectors.some(connector => connector.type === type))
                return;

            // At the limit of its EVSE, which is the most it could be anyway
            // and the only number available before somebody has said anything
            // about this cable.
            evse.connectors = [...evse.connectors,
                               { id: evse.connectors.length + 1, type, maxPower_kW: evse.maxPower_kW }];
            anotherType.delete(evse);

            touched();

            // Added, so no longer typed.
            input.value = '';

        }

        function removeConnector(evse: EVSE, connector: Connector): void {

            if (evse.connectors.length <= 1)
                return;

            evse.connectors.splice(evse.connectors.indexOf(connector), 1);
            renumber();
            touched();

        }

        function removeEVSE(evse: EVSE): void {

            if (draft.length <= 1)
                return;

            draft.splice(draft.indexOf(evse), 1);
            renumber();
            touched();

        }

        function addEVSE(): void {

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

        }

        function revert(): void {
            draft = clone(configuration!.evses);
            dirty = false;
            draw();
            fieldsAsDrawn();
        }

        async function save(): Promise<void> {

            const note = must<HTMLElement>(content, '#form-note');

            note.textContent = '';

            must<HTMLElement>(content, '#form-error').textContent = '';

            try
            {
                configuration = await whileSaving(content, note, () => api.evses.save(draft));

                if (cancelled)
                    return;

                // The station answers with the list as it was sent, in the
                // same order: each EVSE and cable keeps its row - and the
                // field somebody is in, its focus - and another type typed and
                // not added, which was not saved, stays with its EVSE.
                const before  = draft;

                draft         = clone(configuration.evses);
                dirty         = false;

                draft.forEach((evse, index) => {
                    const was = before[index];
                    if (was === undefined)
                        return;
                    keys.set(evse, keyOf(was));
                    evse.connectors.forEach((connector, position) => {
                        if (was.connectors[position] !== undefined)
                            keys.set(connector, keyOf(was.connectors[position]));
                    });
                    const typed = anotherType.get(was);
                    if (typed)
                        anotherType.set(evse, typed);
                });

                draw();

                // What is in the fields is what the station took, as it says it.
                fieldsAsDrawn();

                note.textContent = 'Saved, and in effect.';
            }
            catch (problem)
            {
                if (!cancelled)
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
                fieldsAsDrawn();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The EVSEs could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        // The draft is in the flag; another type typed and not added yet is in
        // no flag, and leaving the page throws it away just the same.
        const release = unsaved.heldBy(() => dirty || anotherTypeTyped());

        void load();

        return () => { cancelled = true; release(); };

    }

};


/**
 * What somebody who may not change what is fitted may do to these EVSEs, as
 * the page says it: only what they may. The station's operator may take an
 * EVSE out of service and not correct what its cable may deliver - and was told
 * it could, while the fields for it stayed disabled.
 */
export function mayChangeShortOfWhatIsFitted(Availability: boolean, Limits: boolean): string {

    if (Availability && Limits)
        return 'take these EVSEs out of service and correct what they and their cables may deliver';

    return Availability
               ? 'take these EVSEs out of service'
               : 'correct what these EVSEs and their cables may deliver';

}

/** A copy to edit, so that discarding changes has something to go back to. */
function clone(evses: EVSE[]): EVSE[] {
    return evses.map(evse => ({
        ...evse,
        connectors: evse.connectors.map((connector): Connector => ({ ...connector }))
    }));
}
