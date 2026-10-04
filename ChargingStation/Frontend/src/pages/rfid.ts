import { api, type RFIDConfiguration, type RFIDReader } from '../api/client';
import { auth } from '../auth';
import { html as stringHTML, must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, shell } from '@node/shell';
import { errorMessage, field, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, live, nothing, render, repeat } from '@node/view';

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
            actions:   stringHTML`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        // Reload throws a draft away just as thoroughly as "Discard changes"
        // does, and from the opposite corner of the screen, so it asks first.
        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void reload();
        });

        const mayPlace  = auth.can('rfid', 'edit');
        const maySwitch = auth.can('availability', 'edit');

        let cancelled = false;
        let configuration: RFIDConfiguration | null = null;

        let draft: RFIDReader[] = [];
        let dirty = false;


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

                ${mayPlace || maySwitch ? nothing : html`
                    <div class="notice">${mayButNot('look at the readers', 'change them')}</div>
                `}

                ${maySwitch && !mayPlace ? html`
                    <div class="notice">
                        ${mayButNot('switch these readers on and off', 'say where one is installed')}
                        Which readers this station has and where they sit describes hardware somebody installed.
                    </div>
                `: nothing}

                <div class="reader-list" id="readers">

                    ${draft.length === 0
                          ? html`<div class="notice">This station has no RFID readers.</div>`
                          : nothing}

                    ${repeat(draft, reader => reader.id, (reader, index) => html`
                        <section class="card reader ${reader.enabled ? '' : 'off'}">

                            <h2>
                                <i class="fa-solid fa-id-card"></i> ${reader.id}
                                <button type="button" class="btn small danger" data-remove="${index}"
                                        ?disabled=${!mayPlace}
                                        @click=${() => remove(index)}>Remove</button>
                            </h2>

                            <div class="kv">
                                <span class="k">Kind</span>
                                <span class="v">${reader.kind}</span>
                                <span class="k">Where</span>
                                <span class="v">${where(reader)}</span>
                            </div>

                            <label class="checkbox">
                                <input type="checkbox" data-enabled="${index}"
                                       .checked=${live(reader.enabled)} ?disabled=${!maySwitch}
                                       @change=${(event: Event) => switched(index, (event.target as HTMLInputElement).checked)} />
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
                                        : nothing}

                        </section>
                    `)}

                </div>

                ${mayPlace ? html`
                    <section class="card wide">

                        <h2><i class="fa-solid fa-plus"></i> Add a reader</h2>

                        <form id="add-form" class="form-stack" @submit=${add}>

                            <label>Name
                                <input type="text" name="id" placeholder="e.g. reader-a" />
                            </label>

                            <label>Kind
                                <input type="text" name="kind" list="reader-kinds"
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
                                        <option value="${evse.id}">
                                            EVSE ${evse.id}${evse.label ? ` (${evse.label})` : ''}
                                        </option>
                                    `)}
                                </select>
                                <span class="hint">At most one reader per place, and the whole housing is a place.</span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn"
                                        ?disabled=${draft.length >= current.maxReaders}>Add</button>
                                <span id="add-error" class="form-error" role="alert"></span>
                            </div>

                        </form>

                    </section>
                ` : nothing}

                <div class="evse-actions">
                    <button type="button" id="save" class="btn primary" ?disabled=${!(dirty && maySaveDraft())}
                            @click=${() => void save()}>Save</button>
                    <button type="button" id="revert" class="btn" ?disabled=${!dirty}
                            @click=${revert}>Discard changes</button>
                    <span id="form-note"  class="form-notice" role="status"></span>
                    <span id="form-error" class="form-error"  role="alert"></span>

                    ${dirty && kind.placement && !mayPlace
                          ? html`<span class="form-error">
                                     This changes which readers this station has and where they sit, which needs
                                     a role that may say where one is installed.
                                 </span>`
                          : html`<span class="hint">Saved to ${current.file}, and in effect at once.</span>`}
                </div>

            `);

        }

        function switched(index: number, on: boolean): void {
            draft[index].enabled = on;
            dirty = true;
            draw();
        }

        function remove(index: number): void {
            draft.splice(index, 1);
            dirty = true;
            draw();
        }

        function add(event: SubmitEvent): void {

            event.preventDefault();

            const form   = event.currentTarget as HTMLFormElement;
            const error  = must<HTMLElement>(content, '#add-error');
            const id     = field(form, 'id');
            const kind   = field(form, 'kind');
            const place  = field(form, 'where');
            const evse   = place === '' ? null : Number(place);

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

            error.textContent = '';

            draft.push({ id, kind, evse, enabled: true });

            dirty = true;

            draw();

            // In the list now, so no longer typed: the form is empty again.
            form.reset();

        }

        // What is half written into the add form is discarded with the rest.
        function revert(): void {
            draft = configuration!.readers.map(reader => ({ ...reader }));
            dirty = false;
            draw();
            content.querySelector<HTMLFormElement>('#add-form')?.reset();
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

                if (cancelled)
                    return;

                draft = configuration.readers.map(reader => ({ ...reader }));
                dirty = false;

                draw();

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

        /**
         * Loaded anew - Reload - is what the station has. A reader half
         * written into the add form goes with the rest, as it does with
         * "Discard changes": Reload has asked before it came here.
         */
        async function reload(): Promise<void> {
            await load();
            if (!cancelled)
                content.querySelectorAll('form').forEach(form => form.reset());
        }

        // The list is in the flag; a reader typed into the add form and not
        // yet added is in no flag, but in the form.
        const release = unsaved.heldBy(() => dirty || anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};
