import { api, type DisplayConfiguration } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * The screen on the front of the station, at night.
 *
 * A display in a car park runs at full brightness through the night at nobody:
 * electricity spent, light thrown where a neighbour may not want it, and wear
 * on the panel. Which hours are quiet is a fact about the site and not about
 * charging stations - a motorway service area has none, a courtyard between
 * flats has them from ten - so the station is told rather than guessing, and
 * one nobody has told does not dim. A screen that went dark on its own would be
 * read as a fault.
 *
 * The operator's page, at the same permission as taking an outlet out of
 * general use: a statement about how the station presents itself to the people
 * standing at it, and not about what the equipment is or may deliver.
 */
export const displayPage: Page = {

    title: 'Display',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/display',
            title:     'Display',
            subtitle:  'The screen on the front of the station, and the hours it keeps.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        // Reload throws a draft away just as thoroughly as "Discard changes"
        // does, and from the opposite corner of the screen, so it asks first.
        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayChange = auth.can('changeAvailability');

        let cancelled = false;
        let current: DisplayConfiguration | null = null;


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const dims          = configuration.dimFrom !== null && configuration.dimUntil !== null;
            const percent       = Math.round((configuration.dimTo ?? configuration.limits.defaultDimTo) * 100);

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the
                        display's hours but not change them. That needs the operator role or better.
                    </div>
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

                        <form id="display-form" class="form-stack">

                            <label>Dim from
                                <input type="time" name="dimFrom" value="${configuration.dimFrom ?? ''}"
                                       ${mayChange ? '' : html`disabled`} />
                                <span class="hint">In this station's own local time.</span>
                            </label>

                            <label>Until
                                <input type="time" name="dimUntil" value="${configuration.dimUntil ?? ''}"
                                       ${mayChange ? '' : html`disabled`} />
                                <span class="hint">
                                    Earlier than the start is the ordinary case: the hours cross midnight.
                                </span>
                            </label>

                            <label>Brightness while dim
                                <input type="number" name="dimTo" step="1"
                                       min="${Math.round(configuration.limits.darkestDimTo * 100)}" max="100"
                                       value="${configuration.dimTo === null ? '' : percent}"
                                       placeholder="${Math.round(configuration.limits.defaultDimTo * 100)}"
                                       ${mayChange ? '' : html`disabled`} />
                                <span class="hint">
                                    Per cent of full. Never below
                                    ${Math.round(configuration.limits.darkestDimTo * 100)} %: a dark display is one
                                    nobody can tell from a broken one, and the person it turns away is the one
                                    arriving at two in the morning.
                                </span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                                <button type="button" id="no-quiet-hours" class="btn"
                                        ${mayChange && dims ? '' : html`disabled`}>Keep no quiet hours</button>
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

                </div>

            `);

            wire();

        }

        function wire(): void {

            must<HTMLFormElement>(content, '#display-form').addEventListener('submit', event => {
                event.preventDefault();
                void save();
            });

            must<HTMLButtonElement>(content, '#no-quiet-hours').addEventListener('click', () => void save(true));

        }

        async function save(TurnOff = false): Promise<void> {

            const form   = must<HTMLFormElement>(content, '#display-form');
            const note   = must<HTMLElement>(content, '#form-note');
            const error  = must<HTMLElement>(content, '#form-error');

            note.textContent   = '';
            error.textContent  = '';

            // Read before the page is held still: a disabled field is left out
            // of a FormData, so the order of these two matters.
            const typed  = new FormData(form);
            const text   = (name: string) => typed.get(name)?.toString().trim() ?? '';
            const level  = text('dimTo');

            // An empty section is how dimming is turned off, and it is the same
            // thing whether the button said so or both times were cleared.
            const update = TurnOff
                               ? {}
                               : {
                                     dimFrom:   text('dimFrom')  === '' ? null : text('dimFrom'),
                                     dimUntil:  text('dimUntil') === '' ? null : text('dimUntil'),
                                     dimTo:     level === '' ? null : Number(level) / 100
                                 };

            try
            {
                current = await whileSaving(content, note, () => api.display.save(update));

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

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#display-form')));

        void load();

        return () => { cancelled = true; release(); };

    }

};
