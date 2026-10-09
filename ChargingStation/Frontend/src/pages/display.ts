import { api, type DisplayConfiguration, type DisplayUpdate } from '../api/client';
import { auth } from '../auth';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render } from '@node/view';

/**
 * The screen on the front of the station: the hours it keeps, whether its
 * picture walks, and the port it is served on.
 *
 * A display in a car park runs at full brightness through the night at nobody:
 * electricity spent, light thrown where a neighbour may not want it, and wear
 * on the panel. Which hours are quiet is a fact about the site and not about
 * charging stations - a motorway service area has none, a courtyard between
 * flats has them from ten - so the station is told rather than guessing, and
 * one nobody has told does not dim. A screen that went dark on its own would be
 * read as a fault.
 *
 * Whether the picture walks against burn-in is a fact about the panel, and off
 * unless it is switched on. The port is where the screen is pointed, and a new
 * one moves the display at once.
 *
 * The operator's page, at the same permission as taking an outlet out of
 * general use: a statement about how the station presents itself to the people
 * standing at it, and not about what the equipment is or may deliver.
 *
 * Each card saves on its own, and each save sends the whole section - a PUT
 * replaces all of it - made of what the card says and what the station has for
 * the rest.
 */
export const displayPage: Page = {

    title: 'Display',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/display',
            title:     'Display',
            subtitle:  'The screen on the front of the station: the hours it keeps, how it moves, and where it is served.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('display', 'edit');

        let cancelled = false;
        let current: DisplayConfiguration | null = null;


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const dims          = configuration.dimFrom !== null && configuration.dimUntil !== null;
            const percent       = Math.round((configuration.dimTo ?? configuration.limits.defaultDimTo) * 100);
            const startsAt      = configuration.portGivenAtStart ?? configuration.port ?? configuration.defaultPort;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">${mayButNot("look at the display's settings", 'change them')}</div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-moon"></i> Quiet hours</h2>

                        <p class="hint">
                            ${dims
                                  ? html`
                                        Between ${configuration.dimFrom} and ${configuration.dimUntil} the display
                                        drops to ${percent} % while nothing is happening
                                        ${configuration.quietNow
                                              ? html`- <strong>which is now</strong>.`
                                              : html`- which is not now.`}
                                    `
                                  : html`
                                        This station keeps no quiet hours: the display is at full brightness
                                        around the clock.
                                    `}
                        </p>

                        <form id="display-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void saveTheHours(); }}>

                            <label>Dim from
                                <input type="time" name="dimFrom" value="${configuration.dimFrom ?? ''}"
                                       ?disabled=${!mayChange} />
                                <span class="hint">In this station's own local time.</span>
                            </label>

                            <label>Until
                                <input type="time" name="dimUntil" value="${configuration.dimUntil ?? ''}"
                                       ?disabled=${!mayChange} />
                                <span class="hint">
                                    Earlier than the start is the ordinary case: the hours cross midnight.
                                </span>
                            </label>

                            <label>Brightness while dim
                                <input type="number" name="dimTo" step="1"
                                       min="${Math.round(configuration.limits.darkestDimTo * 100)}" max="100"
                                       value="${configuration.dimTo === null ? '' : percent}"
                                       placeholder="${Math.round(configuration.limits.defaultDimTo * 100)}"
                                       ?disabled=${!mayChange} />
                                <span class="hint">
                                    Per cent of full. Never below
                                    ${Math.round(configuration.limits.darkestDimTo * 100)} %: a dark display is one
                                    nobody can tell from a broken one, and the person it turns away is the one
                                    arriving at two in the morning.
                                </span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <button type="button" id="no-quiet-hours" class="btn"
                                        ?disabled=${!(mayChange && dims)}
                                        @click=${() => void saveTheHours(true)}>Keep no quiet hours</button>
                                <span id="form-note"  class="form-notice" role="status"></span>
                                <span id="form-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Saved to ${configuration.file}, and on the screen at its next poll two seconds
                                later. Nothing is restarted.
                            </span>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-hand"></i> What wakes it</h2>

                        <p class="hint">
                            The station decides whether these are quiet hours. Whether anybody is standing at
                            the display is the display's own to know, and it comes back to full brightness at
                            once for anything at all: a touch, a card held up, a plug going in, a hold placed
                            or let go, a line a back end asked to be read out. It stays awake for two minutes
                            afterwards, because somebody who has just started a charge is still reading what
                            the screen says about it.
                        </p>

                        <p class="hint">
                            It deliberately does not wake for a number moving. The payment code turns over
                            every half minute on its own and the power reading moves every second a car is
                            charging - a screen that woke for either would be at full brightness all night
                            with a car parked at it, which is the one case where nobody is looking at all.
                        </p>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-arrows-up-down-left-right"></i> Against burn-in</h2>

                        <p class="hint">
                            ${configuration.keepMoving
                                  ? html`The picture walks a small ring, a step every three quarters of a minute,
                                         each step gliding over fifteen seconds.`
                                  : html`The picture stands still.`}
                        </p>

                        <form id="walk-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void saveTheWalk(); }}>

                            <label class="checkbox">
                                <input type="checkbox" name="keepMoving"
                                       ?checked=${configuration.keepMoving}
                                       ?disabled=${!mayChange} />
                                Keep the picture moving
                                <span class="hint">
                                    For a panel that shows the same thing for months - the operator's name in
                                    the same corner, the same letter over the same outlet - and keeps it as a
                                    ghost, on an LCD for a while and on an OLED for good. The whole picture
                                    walks a ring of about a hundredth of the screen, so that no edge stands
                                    still. On a panel that keeps no ghost it is only a screen that moves, so it
                                    is off unless it is switched on here.
                                </span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <span id="walk-note"  class="form-notice" role="status"></span>
                                <span id="walk-error" class="form-error"  role="alert"></span>
                            </div>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-network-wired"></i> Where it is served</h2>

                        <p class="hint">
                            ${configuration.portInUse === null
                                  ? html`This station was started without a display (<code>--no-kiosk</code>). A port
                                         saved here is the one a start with a display gives it.`
                                  : html`The display is at <strong>${configuration.url ?? `port ${configuration.portInUse}`}</strong>
                                         - no sign-in, and nothing of the administration on it.`}
                        </p>

                        <form id="port-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void saveThePort(); }}>

                            <label>TCP port
                                <input type="number" name="port" min="1" max="65535" step="1"
                                       value="${configuration.port ?? ''}"
                                       placeholder="${configuration.portGivenAtStart ?? configuration.defaultPort}"
                                       ?disabled=${!mayChange} />
                                <span class="hint">
                                    Empty is ${configuration.portGivenAtStart === null
                                                   ? html`the default, ${configuration.defaultPort}`
                                                   : html`the port this start was given`}.
                                    A new port moves the display at once: it is listening there before anything
                                    else changes, so a port something else has is refused and the display stays
                                    where it is. A screen still on the old port is told where it went and follows
                                    on its own within ${configuration.handoverSeconds} seconds; one reached
                                    through a proxy or a forwarded port has to be pointed at the new one by hand.
                                </span>
                            </label>

                            ${configuration.portGivenAtStart === null ? nothing : html`
                                <div class="notice small">
                                    This start was given <code>--kiosk-port ${configuration.portGivenAtStart}</code>,
                                    which wins over the port here at every start. Saved here, a port still moves the
                                    display now; started again with that switch, it is back on
                                    ${configuration.portGivenAtStart}.
                                </div>
                            `}

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <span id="port-note"  class="form-notice" role="status"></span>
                                <span id="port-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Saved to ${configuration.file}. At the next start the display is on
                                ${configuration.portGivenAtStart === null
                                      ? html`port ${startsAt}`
                                      : html`the port <code>--kiosk-port</code> gives it, if it is given one, and on
                                             ${configuration.port ?? configuration.defaultPort} if not`}.
                            </span>

                        </form>

                    </section>

                </div>

            `);

        }

        /**
         * The section as the station has it, for a card that changes one part
         * of it: a PUT replaces the whole section, so what the card does not
         * say is sent as it is.
         */
        function asItIs(): DisplayUpdate {
            const configuration = current!;
            return {
                dimFrom:     configuration.dimFrom,
                dimUntil:    configuration.dimUntil,
                dimTo:       configuration.dimTo,
                keepMoving:  configuration.keepMoving ? true : null,
                port:        configuration.port
            };
        }

        /**
         * One card's form saved: read, sent as part of the whole section, and
         * drawn again as the station took it.
         *
         * @param FormId   the card's form.
         * @param NoteId   where "Saved." is said.
         * @param ErrorId  where a refusal is said.
         * @param Change   what the card says, out of what was typed into it.
         * @param Saved    what to say once it is saved, from the station before and after.
         */
        async function save(FormId:   string,
                            NoteId:   string,
                            ErrorId:  string,
                            Change:   (Typed: (Name: string) => string) => Partial<DisplayUpdate>,
                            Saved:    (Before: DisplayConfiguration, After: DisplayConfiguration) => string = () => 'Saved.'): Promise<void> {

            const form   = must<HTMLFormElement>(content, FormId);
            const note   = must<HTMLElement>(content, NoteId);
            const error  = must<HTMLElement>(content, ErrorId);

            note.textContent   = '';
            error.textContent  = '';

            // Read before the page is held still: a disabled field is left out
            // of a FormData, so the order of these two matters.
            const typed   = new FormData(form);
            const text    = (name: string) => typed.get(name)?.toString().trim() ?? '';
            const before  = current!;
            const update  = { ...asItIs(), ...Change(text) };

            try
            {
                current = await whileSaving(content, note, () => api.display.save(update));

                if (cancelled)
                    return;

                draw();

                // A draw leaves a form as it is typed into; the one saved goes
                // back to what it says now - the station's answer.
                form.reset();

                note.textContent = Saved(before, current);
            }
            catch (problem)
            {
                if (!cancelled)
                    error.textContent = errorMessage(problem);
            }

        }

        // No quiet hours is the same thing whether the button said so or both
        // times were cleared.
        const saveTheHours = (TurnOff = false) =>
            save('#display-form', '#form-note', '#form-error', text => {
                const level = text('dimTo');
                return TurnOff
                           ? { dimFrom: null, dimUntil: null, dimTo: null }
                           : {
                                 dimFrom:   text('dimFrom')  === '' ? null : text('dimFrom'),
                                 dimUntil:  text('dimUntil') === '' ? null : text('dimUntil'),
                                 dimTo:     level === '' ? null : Number(level) / 100
                             };
            });

        // Off is the default, and is sent as no opinion rather than as false.
        const saveTheWalk = () =>
            save('#walk-form', '#walk-note', '#walk-error', text => ({
                keepMoving: text('keepMoving') === 'on' ? true : null
            }));

        const saveThePort = () =>
            save('#port-form', '#port-note', '#port-error',
                 text => ({ port: text('port') === '' ? null : Number(text('port')) }),
                 (before, after) => before.portInUse !== null && after.portInUse !== before.portInUse
                                        ? `Saved - the display is on port ${after.portInUse} now. ` +
                                          `Screens still on ${before.portInUse} are sent there for ${after.handoverSeconds} s.`
                                        : 'Saved.');

        /**
         * The page as the station has it now, drawn over the page as it is -
         * what is typed into the forms kept, as a draw keeps it. Reload empties
         * them itself.
         */
        async function load(): Promise<void> {

            try
            {
                const loaded = await api.display.get();

                if (cancelled)
                    return;

                current = loaded;
                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The display settings could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        /**
         * Loaded anew - Reload - is what the station has, the forms too, which
         * a draw on its own would leave as typed.
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
