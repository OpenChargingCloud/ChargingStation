import { api, type StationCertificates, type StationKey } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, field, formatTimestamp, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * The keys and certificates this station dials a back end with.
 *
 * The page is built around the one thing that is hard about certificates:
 * replacing one before it runs out, without a window in which the station
 * cannot connect. So more than one lives here at a time, the replacement is
 * brought in the day it arrives even if it only becomes valid in two days, and
 * the station switches over by itself at the moment it may.
 *
 * There is no way to upload a private key, and saying so on the page is
 * deliberate: the key is made here and never leaves, and somebody looking for
 * the button should find the reason instead.
 */
export const certificatesPage: Page = {

    title: 'Certificates',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/certificates',
            title:     'Certificates',
            subtitle:  'What this charging station says it is when it dials a back end.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        // Reload throws a half-typed request away just as thoroughly as
        // anything else, and from the opposite corner of the screen.
        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayManage = auth.can('changeNetworkSettings');

        let cancelled = false;
        let store: StationCertificates | null = null;

        /** The request that was just made, put in front of somebody straight away. */
        let justMade: { id: string; csr: string } | null = null;


        function draw(): void {

            if (store === null)
                return;

            const certificates = store;
            const chosen       = certificates.algorithms.find(one => one.id === certificates.defaultAlgorithm);

            render(content, html`

                ${mayManage ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the
                        certificates but not make or replace them. That needs the role that changes how this
                        station reaches the outside world.
                    </div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-key"></i> Make a signing request</h2>

                        <form id="create-form" class="form-stack">

                            <label>Subject
                                <input type="text" name="subject" maxlength="${certificates.maxSubjectLength}"
                                       placeholder="cs001.example.org"
                                       ${mayManage ? '' : html`disabled`} />
                                <span class="hint">
                                    What a back end recognises this station by. Plain text is read as a common
                                    name; a full distinguished name is taken as written. A station is not
                                    something anybody dials, so nothing else is asked for.
                                </span>
                            </label>

                            <label>Key
                                <select name="algorithm" id="algorithm" ${mayManage ? '' : html`disabled`}>
                                    ${certificates.algorithms.map(algorithm => html`
                                        <option value="${algorithm.id}"
                                                ${algorithm.id === certificates.defaultAlgorithm ? html`selected` : ''}>
                                            ${algorithm.name}
                                        </option>
                                    `)}
                                </select>
                                <span class="hint" id="algorithm-remark">${chosen?.remark ?? ''}</span>
                            </label>

                            <div class="notice">
                                <strong>Making a key and holding it up are two questions.</strong> Every kind here
                                can be generated and made into a signing request that a certificate authority will
                                accept. Whether this station can then load the certificate that comes back together
                                with its key depends on the runtime underneath - Ed25519, Ed448 and the
                                post-quantum kinds have no key object in .NET today. Such a certificate is kept and
                                passed over rather than refused, and this page says so beside it.
                            </div>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayManage ? '' : html`disabled`}>
                                    Make a key and a request
                                </button>
                                <span id="create-note"  class="form-notice" role="status"></span>
                                <span id="create-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                An RSA 4096 or a post-quantum key takes a few seconds to generate.
                                Written to ${certificates.directory}, the private key readable by nobody else.
                            </span>

                        </form>

                    </section>

                    ${justMade !== null ? html`
                        <section class="card">
                            <h2><i class="fa-solid fa-file-export"></i> The request, waiting to be collected</h2>
                            <p class="hint">
                                Hand this to whoever issues certificates for this station. It is not a secret -
                                it holds the public half of the key and nothing else - and it stays here until
                                the answer comes back.
                            </p>
                            <textarea id="just-made" class="mono" rows="10" readonly>${justMade.csr}</textarea>
                            <div class="form-actions">
                                <button type="button" id="copy-csr" class="btn small">Copy</button>
                                <span id="copy-note" class="form-notice" role="status"></span>
                            </div>
                        </section>
                    ` : ''}

                    <section class="card">

                        <h2><i class="fa-solid fa-file-import"></i> Bring a certificate in</h2>

                        <form id="import-form" class="form-stack">

                            <label>The certificate, and any intermediates
                                <textarea name="pem" class="mono" rows="8"
                                          placeholder="-----BEGIN CERTIFICATE-----"
                                          ${mayManage ? '' : html`disabled`}></textarea>
                                <span class="hint">
                                    PEM, this station's own certificate first. It is matched to the key whose
                                    request it answers, so nothing has to be said about which one it is for.
                                </span>
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayManage ? '' : html`disabled`}>
                                    Bring it in
                                </button>
                                <span id="import-note"  class="form-notice" role="status"></span>
                                <span id="import-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                ${certificates.canImportPrivateKeys
                                      ? ''
                                      : html`
                                            A private key cannot be brought in, only a certificate. The key is
                                            made on this station and never leaves it - one that arrived from
                                            somewhere else is one somebody else has a copy of.
                                        `}
                            </span>

                        </form>

                    </section>

                </div>

                <section class="card">

                    <h2><i class="fa-solid fa-list"></i> What is here</h2>

                    <p class="hint">
                        Newest first. By this station's clock it is
                        ${formatTimestamp(certificates.now)}, which is what the days below are counted against.
                    </p>

                    ${certificates.entries.length === 0
                          ? html`
                                <p class="hint">
                                    Nothing yet. This station can only dial a back end that does not ask it
                                    for a certificate.
                                </p>
                            `
                          : html`<div class="cards">${certificates.entries.map(entryCard)}</div>`}

                </section>

            `);

            wire();

        }

        function entryCard(entry: StationKey): HTMLFragment {

            return html`
                <div class="card ${entry.inUse ? 'in-use' : ''}">

                    <div class="key-head">
                        <strong>${entry.subject || entry.id}</strong>
                        ${entry.inUse ? html`<span class="chip on">in use</span>` : ''}
                        ${entry.certificate === undefined
                              ? html`<span class="chip">request out</span>`
                              : entry.certificate.expired
                                    ? html`<span class="chip warn">expired</span>`
                                    : entry.certificate.notYetValid
                                          ? html`<span class="chip">not yet valid</span>`
                                          : html`<span class="chip">${entry.certificate.daysLeft} days left</span>`}
                    </div>

                    <dl class="kv">
                        <dt>Key</dt>          <dd>${entry.algorithm}</dd>
                        <dt>Identification</dt> <dd><code>${entry.id}</code></dd>
                        <dt>Made</dt>         <dd>${formatTimestamp(entry.createdAt)}</dd>
                        ${entry.certificate !== undefined ? html`
                            <dt>Issued by</dt>  <dd>${entry.certificate.issuer}</dd>
                            <dt>Valid</dt>      <dd>${formatTimestamp(entry.certificate.notBefore)}
                                                    &ndash; ${formatTimestamp(entry.certificate.notAfter)}</dd>
                            <dt>Fingerprint</dt><dd><code>${entry.certificate.thumbprintSHA256}</code></dd>
                            <dt>Sent along</dt> <dd>${entry.certificate.intermediates} intermediate(s)</dd>
                        ` : ''}
                    </dl>

                    ${(entry.warnings ?? []).map(warning => html`<div class="notice">${warning}</div>`)}

                    ${entry.csr !== undefined ? html`
                        <details>
                            <summary>The signing request, waiting to be collected</summary>
                            <textarea class="mono" rows="8" readonly data-csr="${entry.id}">${entry.csr}</textarea>
                        </details>
                    ` : ''}

                    <div class="form-actions">
                        <button type="button" class="btn small" data-remove="${entry.id}"
                                ${mayManage && !entry.inUse ? '' : html`disabled`}>
                            Remove
                        </button>
                        ${entry.inUse
                              ? html`<span class="hint">
                                         The one this station dials with. Bring another in before removing it,
                                         or it comes back from its next restart unable to connect.
                                     </span>`
                              : ''}
                    </div>

                </div>
            `;

        }

        function wire(): void {

            const algorithm = content.querySelector<HTMLSelectElement>('#algorithm');

            algorithm?.addEventListener('change', () => {
                must<HTMLElement>(content, '#algorithm-remark').textContent =
                    store?.algorithms.find(one => one.id === algorithm.value)?.remark ?? '';
            });

            must<HTMLFormElement>(content, '#create-form').addEventListener('submit', event => {
                event.preventDefault();
                void create();
            });

            must<HTMLFormElement>(content, '#import-form').addEventListener('submit', event => {
                event.preventDefault();
                void bringIn();
            });

            content.querySelector('#copy-csr')?.addEventListener('click', () => {
                const box = content.querySelector<HTMLTextAreaElement>('#just-made');
                if (box === null)
                    return;
                box.select();
                void navigator.clipboard?.writeText(box.value).then(
                    () => { must<HTMLElement>(content, '#copy-note').textContent = 'Copied.'; },
                    () => { must<HTMLElement>(content, '#copy-note').textContent = 'Select it and copy it by hand.'; }
                );
            });

            content.addEventListener('click', event => {

                const button = (event.target as Element | null)?.closest<HTMLElement>('[data-remove]');

                if (button && !(button as HTMLButtonElement).disabled)
                    void remove(button.dataset.remove ?? '');

            });

        }


        async function create(): Promise<void> {

            const form  = must<HTMLFormElement>(content, '#create-form');
            const note  = must<HTMLElement>(content, '#create-note');

            note.textContent = '';
            must<HTMLElement>(content, '#create-error').textContent = '';

            // Read before the page is held still: a disabled field is left out
            // of a FormData.
            const subject   = field(form, 'subject');
            const algorithm = field(form, 'algorithm');

            try
            {
                const made = await whileSaving(content, note, () => api.certificates.create(subject, algorithm));

                justMade  = { id: made.id, csr: made.csr };
                store     = made.certificates;

                draw();

                must<HTMLElement>(content, '#create-note').textContent =
                    'Made. The request is below, waiting to be collected.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#create-error').textContent = errorMessage(problem);
            }

        }

        async function bringIn(): Promise<void> {

            const form = must<HTMLFormElement>(content, '#import-form');
            const note = must<HTMLElement>(content, '#import-note');

            note.textContent = '';
            must<HTMLElement>(content, '#import-error').textContent = '';

            const pem = field(form, 'pem', false);

            try
            {
                const taken = await whileSaving(content, note, () => api.certificates.add(pem));

                store     = taken.certificates;
                justMade  = null;

                draw();

                must<HTMLElement>(content, '#import-note').textContent =
                    taken.warnings.length > 0
                        ? `Taken in. ${taken.warnings.join(' ')}`
                        : 'Taken in.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#import-error').textContent = errorMessage(problem);
            }

        }

        async function remove(id: string): Promise<void> {

            if (id === '')
                return;

            const note = must<HTMLElement>(content, '#create-note');

            note.textContent = '';

            try
            {
                store = await whileSaving(content, note, () => api.certificates.remove(id));

                if (justMade?.id === id)
                    justMade = null;

                draw();
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#create-error').textContent = errorMessage(problem);
            }

        }

        async function load(): Promise<void> {

            try
            {
                const loaded = await api.certificates.get();

                if (cancelled)
                    return;

                store = loaded;
                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The certificates could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        // A half-typed subject or a certificate pasted but not yet brought in
        // is work like any other.
        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#create-form')) ||
                                             typedSinceDrawn(content.querySelector('#import-form')));

        void load();

        return () => { cancelled = true; release(); };

    }

};
