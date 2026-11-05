import { api, type NameplateConfiguration, type NameplateUpdate, type Registration } from '../api/client';
import { auth } from '../auth';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, formatSince, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, type TemplateResult } from '@node/view';

/**
 * The station's nameplate: what it tells a CSMS it is - who made it, which
 * model, its serial number and its firmware - and where each of its OCPP nodes
 * stands with its CSMS.
 *
 * A CSMS learns these only from a BootNotification, so a change builds the
 * OCPP nodes anew: they hang up, dial again and say what the station is now.
 * Each field is held to what both OCPP 1.6 and 2.1 take, and to printable
 * ASCII, which is all OCPP 1.6 takes.
 *
 * The system administrator's page: it says what the equipment is, a claim
 * nothing further down can check, and a CSMS believes it.
 */
export const nameplatePage: Page = {

    title: 'Nameplate',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/nameplate',
            title:     'Nameplate',
            subtitle:  'What this station tells a CSMS it is, and whether the CSMS accepted it.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('nameplate', 'edit');

        let cancelled = false;
        let current: NameplateConfiguration | null = null;


        function draw(): void {

            if (current === null)
                return;

            const nameplate = current;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">${mayButNot('look at the nameplate', 'change it')}</div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-tag"></i> Nameplate</h2>

                        <p class="hint">
                            What the BootNotification says: ${nameplate.said.vendor} ${nameplate.said.model},
                            firmware ${nameplate.said.firmwareVersion},
                            ${nameplate.said.serialNumber === null
                                  ? html`no serial number`
                                  : html`serial number ${nameplate.said.serialNumber}`}.
                        </p>

                        <form id="nameplate-form" class="form-stack" @submit=${(event: SubmitEvent) => { event.preventDefault(); void save(); }}>

                            <label>Vendor
                                <input type="text" name="vendor" maxlength="${nameplate.limits.vendor}"
                                       value="${nameplate.vendor ?? ''}" placeholder="GraphDefined"
                                       ?disabled=${!mayChange} />
                                <span class="hint">Who made this station. At most ${nameplate.limits.vendor} characters, as OCPP 1.6 takes.</span>
                            </label>

                            <label>Model
                                <input type="text" name="model" maxlength="${nameplate.limits.model}"
                                       value="${nameplate.model ?? ''}" placeholder="ChargingStationCLI"
                                       ?disabled=${!mayChange} />
                                <span class="hint">At most ${nameplate.limits.model} characters.</span>
                            </label>

                            <label>Serial number
                                <input type="text" name="serialNumber" maxlength="${nameplate.limits.serialNumber}"
                                       value="${nameplate.serialNumber ?? ''}"
                                       ?disabled=${!mayChange} />
                                <span class="hint">Empty is none. At most ${nameplate.limits.serialNumber} characters.</span>
                            </label>

                            <label>Firmware
                                <input type="text" name="firmwareVersion" maxlength="${nameplate.limits.firmwareVersion}"
                                       value="${nameplate.firmwareVersion ?? ''}"
                                       placeholder="${nameplate.firmwareVersion === null ? nameplate.said.firmwareVersion : ''}"
                                       ?disabled=${!mayChange} />
                                <span class="hint">Empty is the version and the commit this station was built from.</span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                                <span id="form-note"  class="form-notice" role="status"></span>
                                <span id="form-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Printable ASCII only - it is all OCPP 1.6 takes. Saved to ${nameplate.file}; the
                                station then hangs up, dials again and registers with what it says now.
                            </span>

                        </form>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-handshake"></i> Registration</h2>

                        <p class="hint">
                            Until a CSMS accepts the BootNotification, the station tells it nothing else: what
                            it has to say waits, and it sends no heartbeat.
                        </p>

                        <div class="table-scroll">
                            <table class="records" id="registration">
                                <thead>
                                    <tr><th>OCPP</th><th>Stands</th><th>Since</th></tr>
                                </thead>
                                <tbody>
                                    ${([ '1.6', '2.1' ] as const).map(version => html`
                                        <tr>
                                            <td>${version}</td>
                                            <td>${stands(nameplate.registration[version])}</td>
                                            <td>${nameplate.registration[version].since === null
                                                      ? '-'
                                                      : formatSince(nameplate.registration[version].since!)}</td>
                                        </tr>
                                    `)}
                                </tbody>
                            </table>
                        </div>

                    </section>

                </div>

            `);

        }

        /**
         * How a node stands, in words.
         */
        function stands(Registration: Registration): TemplateResult {

            const seconds = Registration.interval === null ? '' : `${Math.round(Registration.interval)} s`;

            switch (Registration.state)
            {
                case 'NotAsked':    return html`Not registered: no connection was up yet.`;
                case 'Asking':      return html`Asking ...`;
                case 'Accepted':    return html`<strong>Accepted</strong>, a heartbeat every ${seconds}.`;
                case 'Pending':     return html`Pending - asks again in ${seconds}, and says nothing else until then.`;
                case 'Rejected':    return html`<strong>Rejected</strong> - asks again in ${seconds}, and says nothing else until then.`;
                case 'Unanswered':  return html`No answer (${Registration.problem ?? 'none'}) - asks again in ${seconds}, or when a connection comes up.`;
            }

        }

        /**
         * The form saved: the whole section, a blank field the default, and
         * drawn again as the station took it.
         */
        async function save(): Promise<void> {

            const form   = must<HTMLFormElement>(content, '#nameplate-form');
            const note   = must<HTMLElement>(content, '#form-note');
            const error  = must<HTMLElement>(content, '#form-error');

            note.textContent   = '';
            error.textContent  = '';

            // Read before the page is held still: a disabled field is left out
            // of a FormData.
            const typed  = new FormData(form);
            const text   = (name: string) => typed.get(name)?.toString().trim() ?? '';
            const or     = (name: string) => text(name) === '' ? null : text(name);

            const update: NameplateUpdate = {
                vendor:           or('vendor'),
                model:            or('model'),
                serialNumber:     or('serialNumber'),
                firmwareVersion:  or('firmwareVersion')
            };

            try
            {
                current = await whileSaving(content, note, () => api.nameplate.save(update));

                if (cancelled)
                    return;

                draw();

                // A draw leaves a form as it is typed into; the one saved goes
                // back to what it says now - the station's answer.
                form.reset();

                note.textContent = 'Saved. The station dials again and registers with it.';
            }
            catch (problem)
            {
                if (!cancelled)
                    error.textContent = errorMessage(problem);
            }

        }

        async function load(): Promise<void> {

            try
            {
                const loaded = await api.nameplate.get();

                if (cancelled)
                    return;

                current = loaded;
                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The nameplate could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        /**
         * Loaded anew - Reload - is what the station has, the form too, which a
         * draw on its own would leave as typed.
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
