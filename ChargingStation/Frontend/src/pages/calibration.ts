import { api, type CalibrationCertificate, type CalibrationConfiguration } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, whileSaving } from '../ui';

/**
 * The calibration certificates this charging station runs under.
 *
 * Only the certificate is asked for. Everything shown about one - who it is
 * about, who signed it, how long it is good for - is read out of the PEM rather
 * than typed beside it, because an issuer somebody types can disagree with the
 * certificate it was typed from, and then there is no telling which of the two
 * the station means.
 *
 * Reading them needs nothing special: a certificate is a signature over a
 * public key and holds nothing that has to be kept. Putting one on this station
 * is what takes the installer role.
 */
export const calibrationPage: Page = {

    title: 'Calibration',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/calibration',
            title:     'Calibration',
            subtitle:  'The calibration certificates this charging station runs under.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => void load());

        const mayChange = auth.can('manageCalibration');

        let cancelled = false;
        let current: CalibrationConfiguration | null = null;

        /** What is on screen: the saved list until somebody changes it. */
        let draft: CalibrationCertificate[] = [];

        let dirty = false;

        /** Kept outside the template, so a redraw does not empty the form. */
        let newId          = '';
        let newDescription = '';
        let newPEM         = '';


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the
                        certificates but not change them. That needs the installer or the system
                        administrator role.
                    </div>
                `}

                <div class="certificates">

                    ${draft.length === 0
                          ? html`<div class="notice">No calibration certificates are configured.</div>`
                          : ''}

                    ${draft.map((certificate, index) => html`
                        <section class="card certificate ${certificate.expired ? 'expired' : certificate.notYetValid ? 'not-yet' : ''}">

                            <h2>
                                <i class="fa-solid fa-certificate"></i> ${certificate.id}
                                <button type="button" class="btn small danger" data-remove="${index}"
                                        ${mayChange ? '' : html`disabled`}>Remove</button>
                            </h2>

                            ${certificate.description ? html`<p class="description">${certificate.description}</p>` : ''}

                            <dl class="kv">
                                <dt>Subject</dt>        <dd class="mono">${certificate.subject}</dd>
                                <dt>Issuer</dt>         <dd class="mono">${certificate.issuer}</dd>
                                <dt>Serial number</dt>  <dd class="mono">${certificate.serialNumber}</dd>
                                <dt>Valid from</dt>     <dd>${day(certificate.notBefore)}</dd>
                                <dt>Valid until</dt>    <dd>${day(certificate.notAfter)} ${validity(certificate, configuration.limits.expiryWarningDays)}</dd>
                                <dt>SHA-256</dt>        <dd class="mono wrap">${certificate.thumbprintSHA256}</dd>
                            </dl>

                            <details>
                                <summary>The certificate itself</summary>
                                <pre class="pem">${certificate.pem}</pre>
                            </details>

                        </section>
                    `)}

                    ${mayChange ? html`
                        <section class="card wide">

                            <h2><i class="fa-solid fa-plus"></i> Add a certificate</h2>

                            <form id="add-form" class="form-stack">

                                <label>Name
                                    <input type="text" name="id" value="${newId}"
                                           maxlength="${configuration.limits.maxIdLength}"
                                           placeholder="e.g. meter-evse-1" />
                                    <span class="hint">
                                        What it is called here - letters, digits, '-', '_' and '.'. This is the
                                        name it is taken off again by.
                                    </span>
                                </label>

                                <label>Description
                                    <input type="text" name="description" value="${newDescription}"
                                           maxlength="${configuration.limits.maxDescriptionLength}"
                                           placeholder="optional" />
                                </label>

                                <label>Certificate
                                    <textarea name="pem" rows="8" class="mono"
                                              placeholder="-----BEGIN CERTIFICATE-----&#10;...&#10;-----END CERTIFICATE-----">${newPEM}</textarea>
                                    <span class="hint">
                                        In PEM. Everything else is read out of it, so there is nothing more to
                                        type.
                                    </span>
                                </label>

                                <div class="form-actions">
                                    <button type="submit" class="btn"
                                            ${draft.length >= configuration.limits.maxCertificates ? html`disabled` : ''}>
                                        Add
                                    </button>
                                    <span id="add-error" class="form-error" role="alert"></span>
                                </div>

                            </form>

                        </section>
                    ` : ''}

                </div>

                <div class="evse-actions">
                    <button type="button" id="save" class="btn primary" ${dirty && mayChange ? '' : html`disabled`}>Save</button>
                    <button type="button" id="revert" class="btn" ${dirty ? '' : html`disabled`}>Discard changes</button>
                    <span id="form-note"  class="form-notice" role="status"></span>
                    <span id="form-error" class="form-error"  role="alert"></span>
                    <span class="hint">
                        Saved to ${configuration.file}. A certificate that has already run out is kept and said
                        so about rather than refused - what this station was running under last month is worth
                        being able to show.
                    </span>
                </div>

            `);

            wire();

        }

        function wire(): void {

            const list = must<HTMLElement>(content, '.certificates');

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

                // Kept as they are typed rather than read at submit time, so
                // that a redraw - another certificate removed, say - does not
                // empty a half-written form.
                form.addEventListener('input', event => {

                    const field = event.target as HTMLInputElement | HTMLTextAreaElement;

                    if (field.name === 'id')                newId          = field.value;
                    else if (field.name === 'description')  newDescription = field.value;
                    else if (field.name === 'pem')          newPEM         = field.value;

                });

                form.addEventListener('submit', event => {

                    event.preventDefault();

                    const error = must<HTMLElement>(content, '#add-error');
                    const id    = newId.trim();
                    const pem   = newPEM.trim();

                    if (id === '' || pem === '') {
                        error.textContent = 'A certificate needs a name and its PEM.';
                        return;
                    }

                    if (draft.some(certificate => certificate.id.toLowerCase() === id.toLowerCase())) {
                        error.textContent = `There is already a certificate called '${id}'.`;
                        return;
                    }

                    // Everything but what was typed is left empty on purpose:
                    // the station reads it out of the PEM and sends the whole
                    // certificate back, which is what then goes on screen. A
                    // page that guessed at the subject here would be showing
                    // its own guess until the next reload.
                    draft.push({
                        id,
                        description:       newDescription.trim() === '' ? null : newDescription.trim(),
                        pem,
                        subject:           '(read from the certificate when it is saved)',
                        issuer:            '',
                        serialNumber:      '',
                        notBefore:         '',
                        notAfter:          '',
                        thumbprintSHA256:  '',
                        expired:           false,
                        notYetValid:       false,
                        daysLeft:          0
                    });

                    newId          = '';
                    newDescription = '';
                    newPEM         = '';
                    dirty          = true;

                    draw();

                });

            }

            must<HTMLButtonElement>(content, '#revert').addEventListener('click', () => {
                draft          = current!.certificates.map(certificate => ({ ...certificate }));
                newId          = '';
                newDescription = '';
                newPEM         = '';
                dirty          = false;
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
                current = await whileSaving(content, note, () =>
                              api.calibration.save(draft.map(certificate => ({
                                  id:           certificate.id,
                                  description:  certificate.description,
                                  pem:          certificate.pem
                              }))));

                draft = current.certificates.map(certificate => ({ ...certificate }));
                dirty = false;

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
                const loaded = await api.calibration.get();

                if (cancelled)
                    return;

                current = loaded;
                draft   = loaded.certificates.map(certificate => ({ ...certificate }));
                dirty   = false;

                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The calibration certificates could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        void load();

        return () => { cancelled = true; };

    }

};


/** A date without the time of day, which is all a certificate is read for. */
function day(timestamp: string): string {
    return timestamp === '' ? '' : timestamp.slice(0, 10);
}

/** What its validity means today, when that is worth a word. */
function validity(Certificate: CalibrationCertificate, WarningDays: number) {

    if (Certificate.notAfter === '')
        return html`<span class="chip">not saved yet</span>`;

    if (Certificate.expired)
        return html`<span class="chip bad">ran out ${-Certificate.daysLeft} day(s) ago</span>`;

    if (Certificate.notYetValid)
        return html`<span class="chip warn">not valid yet</span>`;

    if (Certificate.daysLeft <= WarningDays)
        return html`<span class="chip warn">${Certificate.daysLeft} day(s) left</span>`;

    return '';

}
