import { api, type EVSE, type EVSEConfiguration } from '../api/client';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage } from '../ui';

/**
 * The EVSEs of this charging station: the places a vehicle can be plugged in.
 *
 * Edited as a whole rather than one at a time, because they are only valid
 * together - OCPP numbers them from 1 upwards without gaps, so removing the
 * third of four is a change to two of them. The page renumbers what is left
 * and sends the lot.
 *
 * Saving writes the file. The OCPP nodes were told how many EVSEs they have
 * when they were built, so a saved change reaches them at the next start, and
 * the page says so instead of letting somebody believe otherwise.
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

        let cancelled     = false;
        let configuration: EVSEConfiguration | null = null;

        /** What is on screen: the saved list until somebody changes it. */
        let draft: EVSE[] = [];

        /** Whether the draft differs from what was last saved. */
        let dirty = false;


        function renumber(): void {
            draft.forEach((evse, index) => { evse.id = index + 1; });
        }

        function draw(): void {

            if (configuration === null)
                return;

            // Into a local, because the narrowing above does not survive into
            // the callbacks of the template below.
            const current = configuration;
            const types   = current.connectorTypes;

            render(content, html`

                ${current.restartRequired && !dirty
                      ? html`
                          <div class="notice">
                              The saved EVSEs differ from the ones this station is running with.
                              The OCPP nodes are built at the start, so restart the station to
                              make them match.
                          </div>
                        `
                      : ''}

                <div class="evse-list" id="evses">
                    ${draft.map((evse, index) => html`
                        <section class="card evse" data-index="${index}">

                            <h2>
                                <i class="fa-solid fa-plug"></i> EVSE ${evse.id}
                                <button type="button" class="btn small danger" data-remove="${index}"
                                        ${draft.length === 1 ? html`disabled title="A charging station needs at least one EVSE."` : ''}>
                                    Remove
                                </button>
                            </h2>

                            <div class="form-stack">

                                <label>Maximum power in kW
                                    <input type="number" data-field="maxPower_kW" data-index="${index}"
                                           min="0.1" max="${current.maxPower_kW}" step="0.1"
                                           value="${evse.maxPower_kW}" />
                                </label>

                                <label>Physical reference
                                    <input type="text" data-field="physicalReference" data-index="${index}"
                                           value="${evse.physicalReference ?? ''}" placeholder="what is written on the housing, e.g. A" />
                                </label>

                                <label class="checkbox">
                                    <input type="checkbox" data-field="operative" data-index="${index}"
                                           ${evse.operative ? html`checked` : ''} />
                                    Operative
                                    <span class="hint">An inoperative EVSE is reported as one, and no vehicle is served by it.</span>
                                </label>

                                <label>Meter type
                                    <input type="text" data-field="meterType" data-index="${index}"
                                           value="${evse.meterType ?? ''}" placeholder="optional" />
                                </label>

                                <label>Meter serial number
                                    <input type="text" data-field="meterSerialNumber" data-index="${index}"
                                           value="${evse.meterSerialNumber ?? ''}" placeholder="optional" />
                                </label>

                                <div class="connector-types">
                                    <span class="k">Connector types</span>
                                    <div class="chips">
                                        ${types.map(type => html`
                                            <button type="button"
                                                    class="chip tag-button ${evse.connectorTypes.includes(type) ? 'on' : ''}"
                                                    data-connector="${type}" data-index="${index}"
                                                    aria-pressed="${evse.connectorTypes.includes(type)}">${type}</button>
                                        `)}
                                    </div>
                                    ${evse.connectorTypes.length === 0
                                          ? html`<span class="form-error">Pick at least one.</span>`
                                          : ''}
                                </div>

                            </div>

                        </section>
                    `)}
                </div>

                <div class="evse-actions">
                    <button type="button" id="add" class="btn"
                            ${draft.length >= current.maxEVSEs ? html`disabled` : ''}>
                        Add an EVSE
                    </button>
                    <button type="button" id="save" class="btn primary" ${dirty ? '' : html`disabled`}>Save</button>
                    <button type="button" id="revert" class="btn" ${dirty ? '' : html`disabled`}>Discard changes</button>
                    <span id="form-note"  class="form-notice" role="status"></span>
                    <span id="form-error" class="form-error"  role="alert"></span>
                    <span class="hint">Saved to ${current.file}</span>
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

                const connector = target.closest<HTMLElement>('[data-connector]');
                if (connector) {

                    const index = Number(connector.dataset.index);
                    const type  = connector.dataset.connector!;
                    const evse  = draft[index];

                    evse.connectorTypes = evse.connectorTypes.includes(type)
                                              ? evse.connectorTypes.filter(other => other !== type)
                                              : [...evse.connectorTypes, type];

                    touched();
                    return;

                }

                const remove = target.closest<HTMLElement>('[data-remove]');
                if (remove && draft.length > 1) {
                    draft.splice(Number(remove.dataset.remove), 1);
                    renumber();
                    touched();
                }

            });

            must<HTMLButtonElement>(content, '#add').addEventListener('click', () => {

                draft.push({
                    id:                 draft.length + 1,
                    connectorTypes:     ['sType2'],
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
            must<HTMLButtonElement>(content, '#save').disabled   = !dirty;
            must<HTMLButtonElement>(content, '#revert').disabled = !dirty;
        }

        async function save(): Promise<void> {

            const note   = must<HTMLElement>(content, '#form-note');
            const error  = must<HTMLElement>(content, '#form-error');
            const button = must<HTMLButtonElement>(content, '#save');

            note.textContent   = '';
            error.textContent  = '';
            button.disabled    = true;

            try
            {
                configuration = await api.evses.save(draft);
                draft         = clone(configuration.evses);
                dirty         = false;

                draw();

                must<HTMLElement>(content, '#form-note').textContent =
                    configuration.restartRequired
                        ? 'Saved. Restart the station to run with them.'
                        : 'Saved.';
            }
            catch (problem)
            {
                error.textContent = errorMessage(problem);
                button.disabled   = false;
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
    return evses.map(evse => ({ ...evse, connectorTypes: [...evse.connectorTypes] }));
}