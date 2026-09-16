import { api, type RFIDConfiguration, type RFIDReader } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, whileSaving } from '../ui';
import { unsaved } from '../unsaved';

/**
 * The card readers this charging station has, and where they sit.
 *
 * A station may have one reader for the whole housing or one per EVSE, and
 * which of the two it is changes what the display has to do: a reader that
 * belongs to the station has to ask which outlet the card is for, a reader
 * beside an outlet does not. So the placement is configured here rather than
 * discovered.
 *
 * The same two permissions as the EVSEs, for the same reason: where a reader
 * sits is a statement about the installation and needs the system administrator
 * role; switching one off is operation and does not.
 */
export const rfidPage: Page = {

    title: 'RFID',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/rfid',
            title:     'RFID',
            subtitle:  'The card readers this charging station has, and where they sit.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        // Reload throws a draft away just as thoroughly as "Discard changes"
        // does, and from the opposite corner of the screen, so it asks first.
        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayPlace  = auth.can('changeHardware');
        const maySwitch = auth.can('changeAvailability');

        let cancelled = false;
        let configuration: RFIDConfiguration | null = null;

        let draft: RFIDReader[] = [];
        let dirty = false;

        /** Kept outside the template, so a redraw does not empty the form. */
        let newId    = '';
        let newKind  = '';
        let newWhere = '';


        /** What the draft amounts to - the same comparison the station makes. */
        function changeKind(): { placement: boolean; availability: boolean } {

            const saved = configuration?.readers ?? [];

            if (saved.length !== draft.length)
                return { placement: true, availability: false };

            return {
                placement:     draft.some((reader, index) => reader.id   !== saved[index].id   ||
                                                             reader.kind !== saved[index].kind ||
                                                             reader.evse !== saved[index].evse),
                availability:  draft.some((reader, index) => reader.enabled !== saved[index].enabled)
            };

        }

        function maySaveDraft(): boolean {

            const change = changeKind();

            if (!change.placement && !change.availability)
                return false;

            return (!change.placement    || mayPlace) &&
                   (!change.availability || maySwitch);

        }

        function where(Reader: RFIDReader): string {

            if (Reader.evse === null)
                return 'the whole station';

            const evse = configuration?.evses.find(candidate => candidate.id === Reader.evse);

            return `EVSE ${Reader.evse}${evse?.label ? ` (${evse.label})` : ''}`;

        }

        function draw(): void {

            if (configuration === null)
                return;

            const current = configuration;
            const kind    = changeKind();

            render(content, html`

                ${mayPlace || maySwitch ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the readers
                        but not change them. Switching one off needs the CPO role; saying where one is bolted
                        needs the system administrator role.
                    </div>
                `}

                ${maySwitch && !mayPlace ? html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may switch these readers
                        on and off. Which readers this station has and where they sit describes hardware somebody
                        installed, so changing that needs the system administrator role.
                    </div>
                `: ''}

                <div class="reader-list" id="readers">

                    ${draft.length === 0
                          ? html`<div class="notice">This station has no RFID readers.</div>`
                          : ''}

                    ${draft.map((reader, index) => html`
                        <section class="card reader ${reader.enabled ? '' : 'off'}">

                            <h2>
                                <i class="fa-solid fa-id-card"></i> ${reader.id}
                                <button type="button" class="btn small danger" data-remove="${index}"
                                        ${mayPlace ? '' : html`disabled`}>Remove</button>
                            </h2>

                            <div class="kv">
                                <span class="k">Kind</span>
                                <span class="v">${reader.kind}</span>
                                <span class="k">Where</span>
                                <span class="v">${where(reader)}</span>
                            </div>

                            <label class="checkbox">
                                <input type="checkbox" data-enabled="${index}"
                                       ${reader.enabled ? html`checked` : ''} ${maySwitch ? '' : html`disabled`} />
                                Switched on
                                <span class="hint">A reader that is switched off is not read and is not shown on the display.</span>
                            </label>

                            ${reader.fake
                                  ? html`<div class="notice small">
                                             Its cards are typed into the display, by anybody standing in front of it.
                                             For testing, and not for a station in a car park.
                                         </div>`
                                  : reader.hasDriver === false
                                        ? html`<div class="notice small">
                                                   This station has no driver for this kind of reader. It is configured,
                                                   it is shown, and it will read nothing.
                                               </div>`
                                        : ''}

                        </section>
                    `)}

                </div>

                ${mayPlace ? html`
                    <section class="card wide">

                        <h2><i class="fa-solid fa-plus"></i> Add a reader</h2>

                        <form id="add-form" class="form-stack">

                            <label>Name
                                <input type="text" name="id" value="${newId}" placeholder="e.g. reader-a" />
                            </label>

                            <label>Kind
                                <input type="text" name="kind" list="reader-kinds" value="${newKind}"
                                       placeholder="e.g. ${current.fakeKind}" />
                                <datalist id="reader-kinds">
                                    ${current.kinds.map(candidate => html`<option value="${candidate}"></option>`)}
                                </datalist>
                                <span class="hint">
                                    Not a closed list: a reader this station has no driver for is still a reader
                                    somebody bolted on, and it can be written down.
                                </span>
                            </label>

                            <label>Where
                                <select name="where">
                                    <option value="">the whole station</option>
                                    ${current.evses.map(evse => html`
                                        <option value="${evse.id}" ${String(evse.id) === newWhere ? html`selected` : ''}>
                                            EVSE ${evse.id}${evse.label ? ` (${evse.label})` : ''}
                                        </option>
                                    `)}
                                </select>
                                <span class="hint">At most one reader per place, and the whole housing is a place.</span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn"
                                        ${draft.length >= current.maxReaders ? html`disabled` : ''}>Add</button>
                                <span id="add-error" class="form-error" role="alert"></span>
                            </div>

                        </form>

                    </section>
                ` : ''}

                <div class="evse-actions">
                    <button type="button" id="save" class="btn primary" ${dirty && maySaveDraft() ? '' : html`disabled`}>Save</button>
                    <button type="button" id="revert" class="btn" ${dirty ? '' : html`disabled`}>Discard changes</button>
                    <span id="form-note"  class="form-notice" role="status"></span>
                    <span id="form-error" class="form-error"  role="alert"></span>

                    ${dirty && kind.placement && !mayPlace
                          ? html`<span class="form-error">
                                     This changes which readers this station has and where they sit, which needs
                                     the system administrator role.
                                 </span>`
                          : html`<span class="hint">Saved to ${current.file}, and in effect at once.</span>`}
                </div>

            `);

            wire();

        }

        function wire(): void {

            const list = must<HTMLElement>(content, '#readers');

            list.addEventListener('change', event => {

                const input = event.target as HTMLInputElement;

                if (input.dataset.enabled !== undefined) {
                    draft[Number(input.dataset.enabled)].enabled = input.checked;
                    dirty = true;
                    draw();
                }

            });

            list.addEventListener('click', event => {

                const remove = (event.target as HTMLElement).closest<HTMLElement>('[data-remove]');

                if (remove) {
                    draft.splice(Number(remove.dataset.remove), 1);
                    dirty = true;
                    draw();
                }

            });

            const form = content.querySelector<HTMLFormElement>('#add-form');

            if (form) {

                form.addEventListener('input', event => {

                    const field = event.target as HTMLInputElement;

                    if (field.name === 'id')         newId    = field.value;
                    else if (field.name === 'kind')  newKind  = field.value;

                });

                form.addEventListener('change', event => {
                    const field = event.target as HTMLSelectElement;
                    if (field.name === 'where')  newWhere = field.value;
                });

                form.addEventListener('submit', event => {

                    event.preventDefault();

                    const error = must<HTMLElement>(content, '#add-error');
                    const id    = newId.trim();
                    const kind  = newKind.trim();
                    const evse  = newWhere === '' ? null : Number(newWhere);

                    if (id === '' || kind === '') {
                        error.textContent = 'A reader needs a name and a kind.';
                        return;
                    }

                    if (draft.some(reader => reader.id.toLowerCase() === id.toLowerCase())) {
                        error.textContent = `There is already a reader called '${id}'.`;
                        return;
                    }

                    if (draft.some(reader => reader.evse === evse)) {
                        error.textContent = evse === null
                                                ? 'This station already has a reader for the whole housing.'
                                                : `EVSE ${evse} already has a reader.`;
                        return;
                    }

                    draft.push({ id, kind, evse, enabled: true });

                    newId    = '';
                    newKind  = '';
                    newWhere = '';
                    dirty    = true;

                    draw();

                });

            }

            must<HTMLButtonElement>(content, '#revert').addEventListener('click', () => {
                draft    = configuration!.readers.map(reader => ({ ...reader }));
                newId    = '';
                newKind  = '';
                newWhere = '';
                dirty    = false;
                draw();
            });

            must<HTMLButtonElement>(content, '#save').addEventListener('click', () => void save());

        }

        async function save(): Promise<void> {

            const note = must<HTMLElement>(content, '#form-note');

            note.textContent = '';

            must<HTMLElement>(content, '#form-error').textContent = '';

            try
            {
                configuration = await whileSaving(content, note, () =>
                                    api.rfid.save(draft.map(reader => ({
                                        id:       reader.id,
                                        kind:     reader.kind,
                                        evse:     reader.evse,
                                        enabled:  reader.enabled
                                    }))));

                draft = configuration.readers.map(reader => ({ ...reader }));
                dirty = false;

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
                const loaded = await api.rfid.get();

                if (cancelled)
                    return;

                configuration = loaded;
                draft         = loaded.readers.map(reader => ({ ...reader }));
                dirty         = false;

                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The card readers could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => dirty);

        void load();

        return () => { cancelled = true; release(); };

    }

};
