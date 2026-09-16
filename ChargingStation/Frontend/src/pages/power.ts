import { api, type PowerConfiguration } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, whileSaving } from '../ui';

/**
 * What this charging station may draw from the grid.
 *
 * One number, and a page of its own for it, because it belongs to the building
 * and not to the station: it is what the grid operator and the fuse behind the
 * meter allow, and it stays put while EVSEs are added and taken away in front
 * of it. It is also the number that is routinely wrong on the day of
 * commissioning and right a week later, which is why the installer role carries
 * it and the operator role does not.
 *
 * A station that could deliver more than its connection allows is the ordinary
 * case rather than a mistake - four 22 kW outlets on a 55 kW connection is how
 * most of them are built, and load management is what the difference is for. So
 * the two numbers are shown next to each other and neither is called wrong.
 */
export const powerPage: Page = {

    title: 'Grid connection',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/power',
            title:     'Grid connection',
            subtitle:  'What this charging station may draw, and what it could deliver.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => void load());

        const mayChange = auth.can('changePowerLimits');

        let cancelled = false;
        let current: PowerConfiguration | null = null;


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;
            const uplink        = configuration.uplinkPowerLimit_kW;
            const total         = configuration.evsesTotal_kW;

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at what this
                        station may draw but not change it. That needs the installer or the system
                        administrator role.
                    </div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-bolt"></i> Uplink power limit</h2>

                        <form id="power-form" class="form-stack">

                            <label>Maximum power in kW
                                <input type="number" name="uplinkPowerLimit_kW"
                                       min="0.1" max="${configuration.limits.maxUplinkPowerLimit_kW}" step="0.1"
                                       value="${uplink ?? ''}" placeholder="not configured"
                                       ${mayChange ? '' : html`disabled`} />
                                <span class="hint">
                                    What the connection behind the meter allows. Leave it empty to take the
                                    limit away again - this station then does not know what it may draw.
                                </span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                                <span id="form-note"  class="form-notice" role="status"></span>
                                <span id="form-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Saved to ${configuration.file}. Nothing in this station enforces it yet - there
                                is no load management here to enforce it with - so today it is a number the
                                station knows and reports.
                            </span>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-plug"></i> What the EVSEs could draw</h2>

                        <table class="records">
                            <thead>
                                <tr><th>EVSE</th><th class="right">Maximum power</th></tr>
                            </thead>
                            <tbody>
                                ${configuration.evses.map(evse => html`
                                    <tr>
                                        <td>EVSE ${evse.id}</td>
                                        <td class="right">${evse.maxPower_kW} kW</td>
                                    </tr>
                                `)}
                                <tr class="total">
                                    <td>All of them at once</td>
                                    <td class="right">${total} kW</td>
                                </tr>
                            </tbody>
                        </table>

                        <p class="hint">
                            ${uplink === null
                                  ? html`No grid connection limit is configured, so there is nothing to compare this with.`
                                  : total > uplink
                                        ? html`
                                              That is ${round(total - uplink)} kW more than the connection allows.
                                              Perfectly normal - charging everything at full power at once is what
                                              load management is there to prevent - but this station has none yet,
                                              so nothing here holds the total down.
                                          `
                                        : html`
                                              The connection covers all of them at once, with ${round(uplink - total)} kW
                                              to spare.
                                          `}
                        </p>

                        <p class="hint">
                            The limit of each cable is on the <a href="/configuration/evses">EVSEs</a> page.
                        </p>

                    </section>

                </div>

            `);

            wire();

        }

        function wire(): void {

            must<HTMLFormElement>(content, '#power-form').addEventListener('submit', event => {
                event.preventDefault();
                void save();
            });

        }

        async function save(): Promise<void> {

            const form   = must<HTMLFormElement>(content, '#power-form');
            const note   = must<HTMLElement>(content, '#form-note');

            note.textContent  = '';

            must<HTMLElement>(content, '#form-error').textContent = '';

            // An empty field is how the limit is taken away, which is a
            // different thing from a limit of nothing - so it travels as null
            // rather than as 0, which the station would refuse.
            const typed = new FormData(form).get('uplinkPowerLimit_kW')?.toString().trim() ?? '';

            try
            {
                current = await whileSaving(content, note, () =>
                              api.power.save({ uplinkPowerLimit_kW: typed === '' ? null : Number(typed) }));

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
                const loaded = await api.power.get();

                if (cancelled)
                    return;

                current = loaded;
                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The power limits could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        void load();

        return () => { cancelled = true; };

    }

};


/** Two decimals at most, and none when there are none to show. */
function round(value: number): number {
    return Math.round(value * 100) / 100;
}
