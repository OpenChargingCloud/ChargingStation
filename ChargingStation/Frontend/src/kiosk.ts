import qrcode from 'qrcode-generator';

import { config } from './config';
import { html, must, raw, render } from './html';

import './styles/kiosk.scss';

/**
 * The display on the front of the charging station.
 *
 * Its own entry point and its own page, because it is served by its own server
 * on its own port - see KioskHTTPAPI.cs. One bundle for both would put the
 * sign-in form and every configuration page into the file a screen in a car
 * park downloads, which is exactly what the two ports are there to avoid.
 *
 * There is no router, no session and no sign-in here: one page, polled, drawn
 * again. A display recovers from a dropped connection by asking again, which
 * is the one property it must have - by the time somebody walks up to the
 * screen it has to be right, and nobody is there to reload it.
 */

/** One cable of an outlet. */
interface Connector {
    id:           number;
    type:         string;
    maxPower_kW:  number;
}

/** Whose customer is charging. */
interface Provider {
    name:  string;
    logo:  string | null;
}

/** A card reader, and whether its cards are typed in. */
interface Reader {
    id:     string;
    kind:   string;
    fake:   boolean;
    ready:  boolean;
}

/** One outlet, as the display shows it. */
interface KioskEVSE {
    id:                number;
    label:             string;
    status:            'available' | 'reserved' | 'occupied' | 'inoperative';
    maxPower_kW:       number;
    currentPower_kW:   number | null;
    /** Always true while something is charging: this station has no meter. */
    powerIsSimulated:  boolean;
    connectors:        Connector[];
    session:           { method: string; startedAt: string; provider: Provider | null } | null;
    /** Set while an OCPP ReserveNow is holding this outlet. */
    reservation:       { until: string; minutesLeft: number } | null;
    qrCode:            { url: string; expiresAt: string } | null;
    rfid:              Reader | null;
}

/** Everything on the display, in one answer. */
interface KioskState {
    station:      { name: string | null; logo: string | null };
    timestamp:    string;
    evses:        KioskEVSE[];
    /**
     * Outlets held without saying which. A reservation that names no EVSE is a
     * promise that one will be free rather than a claim on any particular one,
     * so it belongs over the whole station and not beside an outlet.
     */
    holds:        { count: number; minutesLeft: number } | null;
    rfid:         Reader | null;
    webPayments:  boolean;
}


/** How often the display asks again. */
const pollEvery = 2000;

/** How long a failed poll is tolerated before the screen says so. */
const staleAfter = 15000;

const root = must<HTMLElement>(document, '#kiosk');

let state:      KioskState | null = null;
let lastAnswer  = 0;
let offline     = false;

/**
 * What the card dialog is open for, or null when it is closed.
 *
 * 'present' is holding a card against a reader - start or stop. 'release' is
 * letting a held outlet go. Both are the same gesture and the same proof: at a
 * screen with no sign-in, the card is the only thing anybody can show.
 */
let dialogFor: { reader: Reader; evse: number | null; mode: 'present' | 'release' } | null = null;
let dialogUID  = '';
let dialogNote = '';


async function poll(): Promise<void> {

    try
    {
        const response = await fetch(`${config.apiBase}/kiosk`, { headers: { 'Accept': 'application/json' } });

        if (!response.ok)
            throw new Error(`${response.status}`);

        state       = await response.json() as KioskState;
        lastAnswer  = Date.now();
        offline     = false;
    }
    catch
    {
        // Not drawn as an error straight away: a display that flashes a warning
        // every time one request is lost is a display people stop reading.
        offline = state !== null && Date.now() - lastAnswer > staleAfter;
    }

    draw();

}


function draw(): void {

    if (state === null) {
        render(root, html`<div class="kiosk-loading">...</div>`);
        return;
    }

    const current = state;

    render(root, html`

        <header class="kiosk-head">
            ${current.station.logo
                  ? html`<img class="kiosk-logo" src="${current.station.logo}" alt="${current.station.name ?? ''}" />`
                  : ''}
            <h1>${current.station.name ?? 'Charging Station'}</h1>

            ${current.holds
                  ? html`
                      <div class="kiosk-hold">
                          <span>
                              ${current.holds.count === 1
                                    ? html`One outlet is being kept free for somebody on their way`
                                    : html`${current.holds.count} outlets are being kept free for somebody on their way`}
                              - ${current.holds.minutesLeft} more minute(s).
                          </span>
                          ${anyCardReader()
                                ? html`<button type="button" class="kiosk-btn release" id="release-hold">Cancel</button>`
                                : ''}
                      </div>
                    `
                  : ''}

            ${offline ? html`<span class="kiosk-offline">no connection to the station</span>` : ''}
        </header>

        <main class="kiosk-evses kiosk-count-${Math.min(current.evses.length, 6)}">
            ${current.evses.map(evse => evseCard(evse))}
        </main>

        ${current.rfid
              ? html`
                  <footer class="kiosk-foot">
                      <button type="button" class="kiosk-rfid ${current.rfid.fake ? 'tappable' : ''}"
                              data-reader="${current.rfid.id}"
                              ${current.rfid.fake ? '' : html`disabled`}>
                          <i class="kiosk-rfid-icon"></i>
                          <span>${current.rfid.fake ? 'Tap a card' : 'Hold your card here'}</span>
                      </button>
                  </footer>
                `
              : ''}

        ${dialogFor ? cardDialog(current) : ''}

    `);

    wire();

}


/**
 * Any reader on this station a card could be typed into.
 *
 * For the hold over the whole station, which belongs to no outlet and so has no
 * outlet's reader: whichever reader can read a card will do, because the card
 * is the proof and the reader is only the way in.
 */
function anyCardReader(): Reader | null {
    return state?.rfid?.fake ? state.rfid
         : state?.evses.map(evse => evse.rfid).find(reader => reader?.fake) ?? null;
}


/** The reader a card for this outlet would be held against, when there is one. */
function readerFor(EVSE: KioskEVSE): Reader | null {
    return EVSE.rfid?.fake ? EVSE.rfid
         : state?.rfid?.fake ? state.rfid
         : null;
}


function evseCard(EVSE: KioskEVSE) {

    const power = EVSE.status === 'occupied' && EVSE.currentPower_kW !== null
                      ? html`
                          <div class="kiosk-power">
                              <span class="now">${EVSE.currentPower_kW.toFixed(1)}</span>
                              <span class="of">/ ${EVSE.maxPower_kW} kW</span>
                              ${EVSE.powerIsSimulated ? html`<span class="kiosk-sim" title="This station has no energy meter; the figure is simulated.">simulated</span>` : ''}
                          </div>
                        `
                      : html`<div class="kiosk-power"><span class="of">up to ${EVSE.maxPower_kW} kW</span></div>`;

    return html`
        <section class="kiosk-evse ${EVSE.status}">

            <div class="kiosk-evse-head">
                <span class="kiosk-evse-label">${EVSE.label}</span>
                <span class="kiosk-status">${statusWord(EVSE.status)}</span>
            </div>

            ${power}

            <div class="kiosk-connectors">
                ${EVSE.connectors.map(connector => html`
                    <span class="kiosk-connector">${connector.type} <span class="kw">${connector.maxPower_kW} kW</span></span>
                `)}
            </div>

            ${EVSE.reservation && !EVSE.session
                  ? html`
                      <div class="kiosk-reserved">
                          <span>Held for ${EVSE.reservation.minutesLeft} more minute(s) - hold the right card against the reader.</span>
                          ${readerFor(EVSE)
                                ? html`
                                    <button type="button" class="kiosk-btn release" data-release="${EVSE.id}">
                                        Cancel reservation
                                    </button>
                                  `
                                : ''}
                      </div>
                    `
                  : ''}

            ${EVSE.session
                  ? html`
                      <div class="kiosk-session">
                          <span class="kiosk-method">${EVSE.session.method}</span>
                          ${EVSE.session.provider
                                ? EVSE.session.provider.logo
                                      ? html`<img class="kiosk-emp-logo" src="${EVSE.session.provider.logo}" alt="${EVSE.session.provider.name}" />`
                                      : html`<span class="kiosk-emp">${EVSE.session.provider.name}</span>`
                                : ''}
                      </div>
                    `
                  : ''}

            ${EVSE.qrCode
                  ? html`
                      <div class="kiosk-qr">
                          ${qrSVG(EVSE.qrCode.url)}
                          <span class="kiosk-qr-hint">Scan to charge</span>
                      </div>
                    `
                  : ''}

            ${EVSE.rfid
                  ? html`
                      <button type="button" class="kiosk-rfid small ${EVSE.rfid.fake ? 'tappable' : ''}"
                              data-reader="${EVSE.rfid.id}" data-evse="${EVSE.id}"
                              ${EVSE.rfid.fake ? '' : html`disabled`}>
                          <i class="kiosk-rfid-icon"></i>
                          <span>${EVSE.rfid.fake ? 'Tap a card' : 'Card'}</span>
                      </button>
                    `
                  : ''}

        </section>
    `;

}


function cardDialog(Current: KioskState) {

    const reader    = dialogFor!.reader;
    const forEVSE   = dialogFor!.evse;
    const releasing = dialogFor!.mode === 'release';

    return html`
        <div class="kiosk-modal" id="modal">
            <div class="kiosk-dialog" role="dialog" aria-modal="true"
                 aria-label="${releasing ? 'Cancel a reservation' : 'Present a card'}">

                <h2>${releasing ? 'Cancel the reservation' : 'Present a card'}</h2>

                <p class="kiosk-dialog-hint">
                    ${releasing
                          ? html`Only the card this outlet is being held for can let it go.`
                          : ''}
                    '${reader.id}' is a test reader, so a card is typed rather than held against it.
                </p>

                <label>
                    Card UID
                    <input type="text" id="uid" value="${dialogUID}" placeholder="04A22B3C4D5E6F"
                           autocomplete="off" spellcheck="false" />
                </label>

                ${forEVSE === null && !releasing
                      ? html`
                          <label>
                              Which outlet
                              <select id="which-evse">
                                  ${Current.evses.map(evse => html`
                                      <option value="${evse.id}">${evse.label}</option>
                                  `)}
                              </select>
                          </label>
                        `
                      : ''}

                <div class="kiosk-dialog-note">${dialogNote}</div>

                <div class="kiosk-dialog-actions">
                    <button type="button" id="dialog-cancel" class="kiosk-btn">Back</button>
                    <button type="button" id="dialog-ok"     class="kiosk-btn primary">
                        ${releasing ? 'Cancel the reservation' : 'Present'}
                    </button>
                </div>

            </div>
        </div>
    `;

}


function wire(): void {

    root.querySelectorAll<HTMLButtonElement>('[data-reader]').forEach(button => {
        button.addEventListener('click', () => {

            if (button.disabled || state === null)
                return;

            const reader = button.dataset.evse
                               ? state.evses.find(evse => evse.id === Number(button.dataset.evse))?.rfid
                               : state.rfid;

            if (!reader?.fake)
                return;

            dialogFor  = { reader, evse: button.dataset.evse ? Number(button.dataset.evse) : null, mode: 'present' };
            dialogUID  = '';
            dialogNote = '';

            draw();

            root.querySelector<HTMLInputElement>('#uid')?.focus();

        });
    });

    root.querySelector<HTMLButtonElement>('#release-hold')?.addEventListener('click', () => {

        const reader = anyCardReader();

        if (!reader)
            return;

        // No outlet: this is the hold that names none, and the station finds
        // the one this card can speak for.
        dialogFor  = { reader, evse: null, mode: 'release' };
        dialogUID  = '';
        dialogNote = '';

        draw();

        root.querySelector<HTMLInputElement>('#uid')?.focus();

    });

    root.querySelectorAll<HTMLButtonElement>('[data-release]').forEach(button => {
        button.addEventListener('click', () => {

            if (state === null)
                return;

            const evseId  = Number(button.dataset.release);
            const evse    = state.evses.find(candidate => candidate.id === evseId);
            const reader  = evse ? readerFor(evse) : null;

            if (!reader)
                return;

            // Always for this one outlet, even at a reader that serves the
            // whole housing: the button is on the card of the outlet being let
            // go, so there is nothing to ask.
            dialogFor  = { reader, evse: evseId, mode: 'release' };
            dialogUID  = '';
            dialogNote = '';

            draw();

            root.querySelector<HTMLInputElement>('#uid')?.focus();

        });
    });

    const uid = root.querySelector<HTMLInputElement>('#uid');

    if (uid) {

        // Kept outside the template, so that the next poll redrawing the page
        // does not empty the field somebody is typing into.
        uid.addEventListener('input', () => { dialogUID = uid.value; });

        uid.addEventListener('keydown', event => {
            if (event.key === 'Enter')  void present();
            if (event.key === 'Escape') close();
        });

    }

    root.querySelector<HTMLButtonElement>('#dialog-ok')?.addEventListener('click', () => void present());
    root.querySelector<HTMLButtonElement>('#dialog-cancel')?.addEventListener('click', close);

    root.querySelector<HTMLElement>('#modal')?.addEventListener('click', event => {
        if (event.target === event.currentTarget)
            close();
    });

}


function close(): void {
    dialogFor  = null;
    dialogUID  = '';
    dialogNote = '';
    draw();
}


async function present(): Promise<void> {

    if (dialogFor === null)
        return;

    const which = root.querySelector<HTMLSelectElement>('#which-evse');
    const where = dialogFor.mode === 'release' ? '/kiosk/reservation/cancel' : '/kiosk/rfid';

    try
    {
        const response = await fetch(`${config.apiBase}${where}`, {
                                   method:   'POST',
                                   headers:  { 'Content-Type': 'application/json', 'Accept': 'application/json' },
                                   body:     JSON.stringify({
                                                 reader:  dialogFor.reader.id,
                                                 evse:    dialogFor.evse ?? (which ? Number(which.value) : undefined),
                                                 uid:     dialogUID
                                             })
                               });

        const body = await response.json() as { error?: string; started?: boolean };

        if (!response.ok) {
            dialogNote = body.error ?? `${response.status}`;
            draw();
            return;
        }

        close();
        void poll();
    }
    catch (problem)
    {
        dialogNote = problem instanceof Error ? problem.message : 'The station did not answer.';
        draw();
    }

}


/** What a status is called on a screen somebody reads from three metres away. */
function statusWord(Status: KioskEVSE['status']): string {
    return Status === 'available'   ? 'free'
         : Status === 'reserved'    ? 'reserved'
         : Status === 'occupied'    ? 'charging'
         : 'out of service';
}


/**
 * The QR code, as an SVG built here.
 *
 * Drawn in the browser rather than fetched, because a display in a car park
 * should need nothing from anywhere: the station it is plugged into is the
 * only thing it talks to, and the code changes every few seconds.
 */
function qrSVG(URL: string) {

    const qr = qrcode(0, 'M');

    qr.addData(URL);
    qr.make();

    // raw(), because createSvgTag returns markup rather than text. What went
    // into it is a URL this station generated from its own template and its
    // own secret - nothing a visitor typed reaches this function.
    return raw(qr.createSvgTag({ cellSize: 4, margin: 0, scalable: true }));

}


void poll();
setInterval(() => void poll(), pollEvery);
