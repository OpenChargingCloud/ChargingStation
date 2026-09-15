import qrcode from 'qrcode-generator';

import { config } from './config';
import { html, HTMLFragment, must, raw, render } from './html';

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

/**
 * Something a back end asked this station to say.
 *
 * They stack, and the priority says how: AlwaysFront and InFront stay on
 * screen, everything else takes its turn. Which of them arrive here at all has
 * already been decided by the station - a message only applies in certain
 * states or at certain outlets, and it is the station that knows what it is
 * doing right now.
 */
interface DisplayMessage {
    id:        string;
    priority:  'AlwaysFront' | 'InFront' | 'NormalCycle' | string;
    text:      string;
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
    /** What to say at this outlet, most important first. */
    messages:          DisplayMessage[];
    qrCode:            { url: string; expiresAt: string } | null;
    rfid:              Reader | null;
}

/**
 * The words this page puts on the screen itself.
 *
 * Not the messages - those arrive already written, in the language the station
 * was configured with. These are the fixed words around them, and they are no
 * more use in a language nobody standing there reads than a message would be.
 *
 * English is the base and the fall-back; a language this page has never heard
 * of falls back to it rather than showing empty labels, which is the failure
 * somebody can at least work with.
 */
interface Vocabulary {
    free:               string;
    reserved:           string;
    charging:           string;
    outOfService:       string;
    notKnown:           string;
    upTo:               (kW: number) => string;
    simulated:          string;
    scanToCharge:       string;
    tapACard:           string;
    holdYourCard:       string;
    card:               string;
    noConnection:       string;
    heldFor:            (minutes: number) => string;
    keptFree:           (outlets: number, minutes: number) => string;
    cancelReservation:  string;
    presentACard:       string;
    cancelTheHold:      string;
    onlyTheRightCard:   string;
    testReader:         (id: string) => string;
    cardUID:            string;
    whichOutlet:        string;
    back:               string;
    present:            string;
    legalTime:          (authority: string) => string;
    checkedAgainst:     (server: string) => string;
    timeUnverified:     string;
    notClaimed:         string;
    ntsOff:             string;
    neverChecked:       string;
    stale:              string;
    offBy:              (milliseconds: number) => string;
    secondsAgo:         (seconds: number) => string;
    minutesAgo:         (minutes: number) => string;
    noAnswer:           string;
    sending:            string;
}

const english: Vocabulary = {
    free:               'free',
    reserved:           'reserved',
    charging:           'charging',
    outOfService:       'out of service',
    notKnown:           'not known',
    upTo:               kW => `up to ${kW} kW`,
    simulated:          'simulated',
    scanToCharge:       'Scan to charge',
    tapACard:           'Tap a card',
    holdYourCard:       'Hold your card here',
    card:               'Card',
    noConnection:       'no connection to the station',
    heldFor:            minutes => `Held for ${minutes} more minute(s) - hold the right card against the reader.`,
    keptFree:           (outlets, minutes) => outlets === 1
                                                  ? `One outlet is being kept free for somebody on their way - ${minutes} more minute(s).`
                                                  : `${outlets} outlets are being kept free for somebody on their way - ${minutes} more minute(s).`,
    cancelReservation:  'Cancel reservation',
    presentACard:       'Present a card',
    cancelTheHold:      'Cancel the reservation',
    onlyTheRightCard:   'Only the card this outlet is being held for can let it go.',
    testReader:         id => `'${id}' is a test reader, so a card is typed rather than held against it.`,
    cardUID:            'Card UID',
    whichOutlet:        'Which outlet',
    back:               'Back',
    present:            'Present',
    legalTime:          authority => `legal time · ${authority}`,
    checkedAgainst:     server => `checked against ${server},`,
    timeUnverified:     'time not verified',
    notClaimed:         'no time authority configured',
    ntsOff:             'time checking is switched off',
    neverChecked:       'not checked yet',
    stale:              'last check too long ago',
    offBy:              milliseconds => `clock is ${milliseconds > 0 ? '+' : ''}${milliseconds} ms out`,
    secondsAgo:         seconds => `${seconds} s ago`,
    minutesAgo:         minutes => `${minutes} min ago`,
    noAnswer:           'The station did not answer.',
    sending:            'One moment...'
};

const german: Vocabulary = {
    free:               'frei',
    reserved:           'reserviert',
    charging:           'lädt',
    outOfService:       'außer Betrieb',
    notKnown:           'unbekannt',
    upTo:               kW => `bis zu ${kW} kW`,
    simulated:          'simuliert',
    scanToCharge:       'Zum Laden scannen',
    tapACard:           'Karte eingeben',
    holdYourCard:       'Karte hier auflegen',
    card:               'Karte',
    noConnection:       'keine Verbindung zur Ladestation',
    heldFor:            minutes => `Noch ${minutes} Minute(n) reserviert – bitte die passende Karte auflegen.`,
    keptFree:           (outlets, minutes) => outlets === 1
                                                  ? `Ein Ladepunkt wird freigehalten – noch ${minutes} Minute(n).`
                                                  : `${outlets} Ladepunkte werden freigehalten – noch ${minutes} Minute(n).`,
    cancelReservation:  'Reservierung stornieren',
    presentACard:       'Karte auflegen',
    cancelTheHold:      'Reservierung stornieren',
    onlyTheRightCard:   'Nur die Karte, für die reserviert wurde, kann die Reservierung auflösen.',
    testReader:         id => `„${id}" ist ein Testleser – die Karte wird eingetippt statt aufgelegt.`,
    cardUID:            'Karten-UID',
    whichOutlet:        'Welcher Ladepunkt',
    back:               'Zurück',
    present:            'Auflegen',
    legalTime:          authority => `gesetzliche Zeit · ${authority}`,
    checkedAgainst:     server => `geprüft gegen ${server},`,
    timeUnverified:     'Zeit ungeprüft',
    notClaimed:         'keine Zeitautorität konfiguriert',
    ntsOff:             'Zeitprüfung ist abgeschaltet',
    neverChecked:       'noch nicht geprüft',
    stale:              'letzte Prüfung zu lange her',
    offBy:              milliseconds => `Uhr weicht um ${milliseconds > 0 ? '+' : ''}${milliseconds} ms ab`,
    secondsAgo:         seconds => `vor ${seconds} s`,
    minutesAgo:         minutes => `vor ${minutes} min`,
    noAnswer:           'Die Station hat nicht geantwortet.',
    sending:            'Einen Moment...'
};

const vocabularies: Record<string, Vocabulary> = {
    en:  english,
    de:  german
};

/** The words to use, by what the station was configured to speak. */
let words = english;

/**
 * Pick the words, and tell the browser which language the page is in.
 *
 * By the language without its region: a station in Austria shows the same
 * German words as one in Germany, and a page that had to carry "de-AT" as well
 * as "de" would be carrying two copies of one language.
 */
function chooseWords(Language: string | null | undefined): void {

    const primary = (Language ?? 'en').split('-')[0].toLowerCase();

    words = vocabularies[primary] ?? english;

    // So that a screen reader, a spell checker and the browser's own hyphen
    // rules all know what they are looking at.
    document.documentElement.lang = words === english ? 'en' : primary;

}


/** Everything on the display, in one answer. */
interface KioskState {
    station:      { name: string | null; logo: string | null; language: string | null };
    timestamp:    string;
    /**
     * What time the station thinks it is, and what that is worth.
     *
     * The station decides the word "legal", not this page: whether a clock
     * carries a country's legal time is a fact about an institution and a
     * measurement, and a screen is in no position to work either of them out.
     */
    clock:        {
                      now:        string;
                      source:     string;
                      nts:        { enabled: boolean; server: string | null; lastServer: string | null;
                                    checkedAt: string | null; ageSeconds: number | null;
                                    offset_ms: number | null; everySeconds: number };
                      legal:      boolean;
                      authority:  string | null;
                      why:        string | null;
                  };
    evses:        KioskEVSE[];
    /**
     * Outlets held without saying which. A reservation that names no EVSE is a
     * promise that one will be free rather than a claim on any particular one,
     * so it belongs over the whole station and not beside an outlet.
     */
    holds:        { count: number; minutesLeft: number } | null;
    /** What to say for the whole housing, most important first. */
    messages:     DisplayMessage[];
    rfid:         Reader | null;
    webPayments:  boolean;
}


/** How often the display asks again. */
const pollEvery = 2000;

/**
 * How long each message of the normal cycle gets.
 *
 * Long enough to read a line of text twice from a few metres away while
 * walking past, which is the only speed that matters here.
 */
const cycleEvery = 7000;

/** How long a failed poll is tolerated before the screen says so. */
const staleAfter = 15000;

/**
 * How long the station is given to answer before the request is given up on.
 *
 * `fetch` has no deadline of its own. A station that accepts the connection and
 * then says nothing - wedged, rather than down - leaves the promise pending for
 * ever, and a page that only learns it is unreachable from a rejection never
 * learns it at all. Measured against one wedged on purpose: twenty-six requests
 * outstanding after a minute, no warning on the screen, and an outlet still
 * drawn as free while a car was charging on it. A display that lies about a
 * free bay is worse than a dark one.
 *
 * Four seconds for asking, because the answer normally takes single-digit
 * milliseconds and this has to be well under `staleAfter` for the warning to
 * appear when it says it will. Longer for doing something, because starting a
 * session or placing a hold goes out over OCPP and back.
 */
const answerWithin = 4000;
const actWithin    = 10000;

const root = must<HTMLElement>(document, '#kiosk');

let state:      KioskState | null = null;
let lastAnswer  = 0;
let offline     = false;

/**
 * What the screen is currently showing, as a string.
 *
 * The page is drawn by replacing all of it, which is fine for a screen that
 * changes when something happens and wrong for one that is rebuilt every two
 * seconds whether or not anything did: every redraw throws away the elements
 * that were there, and with them anything the browser was keeping in them -
 * the caret, the focus, and on a touch screen the keyboard that was open.
 *
 * So a poll that brings back what is already on screen draws nothing. The
 * timestamp is left out of the comparison on purpose: it is the one field that
 * differs every single time, and comparing it would make this test always say
 * "changed" and do nothing at all.
 */
let shown: string | null = null;

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

/**
 * Whether the card has already been sent and the answer is still on its way.
 *
 * Both a lock and something to look at. Nothing on screen used to change when
 * the button was pressed, so somebody who pressed it again - which is what
 * everybody does when a screen does not react - sent the card a second time,
 * and the second card stopped the session the first one had started. Seen in
 * the station's own log: started, stopped after one second, started again.
 */
let sending = false;


/**
 * Ask the station something, and give up if it does not answer.
 *
 * The deadline covers reading the body as well as opening the connection: a
 * station that sends its headers and then stops mid-answer hangs just as
 * thoroughly as one that never starts. Aborting cancels the stream, which is
 * what makes the `json()` below fail rather than wait.
 */
async function ask(Where:   string,
                   Within:  number,
                   How?:    RequestInit): Promise<{ ok: boolean; status: number; body: unknown }> {

    const giveUp = new AbortController();
    const timer  = setTimeout(() => giveUp.abort(), Within);

    try
    {
        const response = await fetch(`${config.apiBase}${Where}`, {
                                   ...How,
                                   signal:   giveUp.signal,
                                   headers:  { 'Accept': 'application/json', ...How?.headers }
                               });

        return { ok: response.ok, status: response.status, body: await response.json() };
    }
    finally
    {
        clearTimeout(timer);
    }

}


/**
 * Whether the station is being asked right now.
 *
 * One at a time. Without this, a station that is slow to answer collects a
 * second request every two seconds - and since the browser will only hold a
 * handful of connections to one host open, a station that recovers finds a
 * queue of stale questions in front of the only one that matters. A poll
 * skipped because the last one has not come back yet is no loss: the next tick
 * is two seconds away and asks the same thing.
 */
let asking = false;


async function poll(): Promise<void> {

    if (asking)
        return;

    asking = true;

    try
    {
        const answer = await ask('/kiosk', answerWithin);

        if (!answer.ok)
            throw new Error(`${answer.status}`);

        state       = answer.body as KioskState;
        lastAnswer  = Date.now();

        chooseWords(state.station.language);
        offline     = false;
    }
    catch
    {
        // Not drawn as an error straight away: a display that flashes a warning
        // every time one request is lost is a display people stop reading.
        offline = state !== null && Date.now() - lastAnswer > staleAfter;
    }
    finally
    {
        asking = false;
    }

    // Somebody is holding a card against this station. Whatever the outlets are
    // doing can wait the two seconds until they have finished typing: a modal
    // is over them and nobody is reading them, and redrawing underneath it
    // takes the keyboard away mid-word.
    if (dialogFor !== null)
        return;

    const signature = JSON.stringify({ ...state, timestamp: undefined, offline, cycle: cycleSignature(), codes: liveCodes(), clock: clockSignature() });

    if (signature === shown)
        return;

    shown = signature;

    draw();

}


function draw(): void {

    // Whatever is drawn now is what is on screen. Set here rather than only in
    // the poll, so that a redraw somebody caused by pressing something does not
    // leave the poll believing the screen still shows the older thing.
    shown = state === null ? null : JSON.stringify({ ...state, timestamp: undefined, offline, cycle: cycleSignature(), codes: liveCodes(), clock: clockSignature() });

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
                              ${words.keptFree(current.holds.count, current.holds.minutesLeft)}
                          </span>
                          ${anyCardReader()
                                ? html`<button type="button" class="kiosk-btn release" id="release-hold">${words.cancelReservation}</button>`
                                : ''}
                      </div>
                    `
                  : ''}

            ${offline ? html`<span class="kiosk-offline">${words.noConnection}</span>` : ''}

            ${clockCorner(current)}

        </header>

        ${messageBand(current.messages ?? [], 'station')}

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
                          <span>${current.rfid.fake ? words.tapACard : words.holdYourCard}</span>
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


/**
 * How long ago the station said what the screen is showing.
 *
 * Measured against the local clock only through the difference between two
 * readings of it - never by comparing the station's timestamp with this
 * machine's. A screen bolted to a wall in a car park has whatever clock
 * somebody left in it, and a display that decided a payment code was dead
 * because its own clock ran four minutes fast would be wrong in the direction
 * that costs a sale.
 */
function ageOfWhatIsShown(): number {
    return lastAnswer === 0 ? 0 : Date.now() - lastAnswer;
}


/**
 * Whether a payment code is still worth pointing a phone at.
 *
 * The code carries a one-time password with about thirty seconds of life. The
 * station tells us when it stops being valid and when it said so, and the
 * difference between those two is how long it had at that moment - so what is
 * left is that, minus how long ago we were told. No absolute clocks meet
 * anywhere in that sentence, which is the point.
 *
 * A code that has run out is taken off the screen rather than left there.
 * Somebody who scans a dead one pays nothing and concludes the station is
 * broken, which is worse than finding no code at all: no code is a station
 * that cannot take a payment right now, a dead code is a station that lies.
 */
function qrCodeStillGood(QRCode: { url: string; expiresAt: string }): boolean {

    if (state === null)
        return false;

    const hadLeft = Date.parse(QRCode.expiresAt) - Date.parse(state.timestamp);

    return Number.isFinite(hadLeft) && hadLeft > ageOfWhatIsShown();

}


/**
 * The clock, and one line under it saying what it is worth.
 *
 * The time shown is the station's, carried forward by the local clock between
 * polls - the difference between two local readings, never an absolute one, so
 * that a screen whose own clock is wrong still shows the station's time.
 *
 * The digits are written in place by a ticking timer rather than by redrawing
 * the page, which would throw away the caret of anybody typing a card number
 * once a second.
 */
function clockCorner(Current: KioskState) {

    const clock = Current.clock;

    if (clock === undefined)
        return '';

    const why = clock.why === 'notClaimed'   ? words.notClaimed
              : clock.why === 'ntsOff'       ? words.ntsOff
              : clock.why === 'neverChecked' ? words.neverChecked
              : clock.why === 'stale'        ? words.stale
              : clock.why === 'offBy'        ? words.offBy(clock.nts.offset_ms ?? 0)
              : '';

    // Without the root dot. "ptbtime1.ptb.de." is the correct way to write a
    // fully qualified name and the wrong way to put one in front of somebody.
    const server = (clock.nts.lastServer ?? clock.nts.server)?.replace(/\.$/, '') ?? null;

    return html`
        <div class="kiosk-clock ${clock.legal ? 'legal' : 'unverified'}">
            <span class="kiosk-time" id="clock">${clockText()}</span>
            <span class="kiosk-time-note">
                ${clock.legal && clock.authority
                      ? html`${words.legalTime(clock.authority)}`
                      : html`${words.timeUnverified}${why ? html` · ${why}` : ''}`}
                ${server !== null && clock.nts.checkedAt !== null
                      ? html`<br />${words.checkedAgainst(server)} <span id="clock-age">${ageText()}</span>`
                      : ''}
            </span>
        </div>
    `;

}


/**
 * How long ago the clock was last checked, in words.
 *
 * Worked out on the page rather than read off the answer, for the same reason
 * the time itself is: it changes every second, and a screen that redrew itself
 * because a number of seconds went up would be back to throwing away the caret
 * of anybody typing a card number.
 */
function ageText(): string {

    const checkedAt = state?.clock?.nts?.checkedAt;

    if (!checkedAt)
        return '';

    const seconds = (Date.parse(state!.clock.now) + ageOfWhatIsShown() - Date.parse(checkedAt)) / 1000;

    return !Number.isFinite(seconds) || seconds < 0
               ? ''
               : seconds < 120
                     ? words.secondsAgo(Math.round(seconds))
                     : words.minutesAgo(Math.round(seconds / 60));

}


/**
 * The station's time as a wall clock, now.
 *
 * Its clock plus however long ago it said so - measured as the difference
 * between two readings of this machine's clock, so that a screen whose own
 * clock is hours out still shows the station's time to the second.
 */
function clockText(): string {

    if (state === null)
        return '';

    const stationNow = new Date(Date.parse(state.clock?.now ?? state.timestamp) + ageOfWhatIsShown());

    return Number.isNaN(stationNow.getTime())
               ? ''
               : stationNow.toLocaleTimeString(document.documentElement.lang || 'en', { hour12: false });

}


/** The reader a card for this outlet would be held against, when there is one. */
function readerFor(EVSE: KioskEVSE): Reader | null {
    return EVSE.rfid?.fake ? EVSE.rfid
         : state?.rfid?.fake ? state.rfid
         : null;
}


/**
 * The messages to put on screen out of the ones that apply.
 *
 * Everything that asked to stay, stays. The rest take turns, one at a time,
 * and always in the same order - so a message does not vanish for a round
 * because another one arrived.
 */
function messagesToShow(Messages: DisplayMessage[]): DisplayMessage[] {

    const pinned   = Messages.filter(message => message.priority === 'AlwaysFront' || message.priority === 'InFront');
    const cycling  = Messages.filter(message => message.priority !== 'AlwaysFront' && message.priority !== 'InFront');

    return cycling.length === 0
               ? pinned
               : [...pinned, cycling[cycle % cycling.length]];

}


function messageBand(Messages: DisplayMessage[], Where: string) {

    const showing = messagesToShow(Messages);

    return showing.length === 0
               ? ''
               : html`
                   <div class="kiosk-messages ${Where}">
                       ${showing.map(message => html`
                           <div class="kiosk-message ${message.priority === 'AlwaysFront' ? 'front' : ''}">${message.text}</div>
                       `)}
                   </div>
                 `;

}


function evseCard(EVSE: KioskEVSE) {

    const power = EVSE.status === 'occupied' && EVSE.currentPower_kW !== null
                      ? html`
                          <div class="kiosk-power">
                              <span class="now">${EVSE.currentPower_kW.toFixed(1)}</span>
                              <span class="of">/ ${EVSE.maxPower_kW} kW</span>
                              ${EVSE.powerIsSimulated ? html`<span class="kiosk-sim" title="This station has no energy meter; the figure is simulated.">${words.simulated}</span>` : ''}
                          </div>
                        `
                      : html`<div class="kiosk-power"><span class="of">${words.upTo(EVSE.maxPower_kW)}</span></div>`;

    // The cell is what the grid sizes, and the card fills it. Two elements
    // rather than one because a card cannot be measured against itself: the
    // rules that make a short card compact have to live on something outside
    // it, and this is the smallest something there is.
    return html`
        <div class="kiosk-evse-cell">
        <section class="kiosk-evse ${offline ? 'unknown' : EVSE.status}">

            <div class="kiosk-evse-head">
                <span class="kiosk-evse-label">${EVSE.label}</span>
                <span class="kiosk-status">${offline ? words.notKnown : statusWord(EVSE.status)}</span>
            </div>

            ${power}

            <div class="kiosk-connectors">
                ${EVSE.connectors.map(connector => html`
                    <span class="kiosk-connector">${connector.type} <span class="kw">${connector.maxPower_kW} kW</span></span>
                `)}
            </div>

            ${messageBand(EVSE.messages ?? [], 'evse')}

            ${EVSE.reservation && !EVSE.session
                  ? html`
                      <div class="kiosk-reserved">
                          <span>${words.heldFor(EVSE.reservation.minutesLeft)}</span>
                          ${readerFor(EVSE)
                                ? html`
                                    <button type="button" class="kiosk-btn release" data-release="${EVSE.id}">
                                        ${words.cancelReservation}
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

            ${EVSE.qrCode && qrCodeStillGood(EVSE.qrCode)
                  ? html`
                      <div class="kiosk-qr">
                          ${qrSVG(EVSE.qrCode.url)}
                          <span class="kiosk-qr-hint">${words.scanToCharge}</span>
                      </div>
                    `
                  : ''}

            ${EVSE.rfid
                  ? html`
                      <button type="button" class="kiosk-rfid small ${EVSE.rfid.fake ? 'tappable' : ''}"
                              data-reader="${EVSE.rfid.id}" data-evse="${EVSE.id}"
                              ${EVSE.rfid.fake ? '' : html`disabled`}>
                          <i class="kiosk-rfid-icon"></i>
                          <span>${EVSE.rfid.fake ? words.tapACard : words.card}</span>
                      </button>
                    `
                  : ''}

        </section>
        </div>
    `;

}


function cardDialog(Current: KioskState) {

    const reader    = dialogFor!.reader;
    const forEVSE   = dialogFor!.evse;
    const releasing = dialogFor!.mode === 'release';

    return html`
        <div class="kiosk-modal" id="modal">
            <div class="kiosk-dialog" role="dialog" aria-modal="true"
                 aria-label="${releasing ? words.cancelTheHold : words.presentACard}">

                <h2>${releasing ? words.cancelTheHold : words.presentACard}</h2>

                <p class="kiosk-dialog-hint">
                    ${releasing ? html`${words.onlyTheRightCard}` : ''}
                    ${words.testReader(reader.id)}
                </p>

                <label>
                    ${words.cardUID}
                    <input type="text" id="uid" value="${dialogUID}" placeholder="04A22B3C4D5E6F"
                           autocomplete="off" spellcheck="false" />
                </label>

                ${forEVSE === null && !releasing
                      ? html`
                          <label>
                              ${words.whichOutlet}
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
                    <button type="button" id="dialog-cancel" class="kiosk-btn" ${sending ? html`disabled` : ''}>
                        ${words.back}
                    </button>
                    <button type="button" id="dialog-ok"     class="kiosk-btn primary" ${sending ? html`disabled` : ''}>
                        ${sending ? words.sending : releasing ? words.cancelTheHold : words.present}
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
    sending    = false;
    draw();
}


async function present(): Promise<void> {

    if (dialogFor === null || sending)
        return;

    const which = root.querySelector<HTMLSelectElement>('#which-evse');
    const where = dialogFor.mode === 'release' ? '/kiosk/reservation/cancel' : '/kiosk/rfid';

    // Read before the redraw below takes the elements away, and held rather
    // than read again afterwards: this card is on its way to that outlet.
    const what  = {
                      reader:  dialogFor.reader.id,
                      evse:    dialogFor.evse ?? (which ? Number(which.value) : undefined),
                      uid:     dialogUID
                  };

    sending     = true;
    dialogNote  = '';
    draw();

    try
    {
        const answer = await ask(where, actWithin, {
                                 method:   'POST',
                                 headers:  { 'Content-Type': 'application/json' },
                                 body:     JSON.stringify(what)
                             });

        const body = answer.body as { error?: string; started?: boolean };

        if (!answer.ok) {
            sending    = false;
            dialogNote = body.error ?? `${answer.status}`;
            draw();
            return;
        }

        close();
        void poll();
    }
    catch
    {
        // Whatever went wrong - no connection, no answer within the deadline, an
        // answer that was not JSON - what somebody standing here needs to know
        // is the same thing, and it is not "Failed to fetch".
        sending    = false;
        dialogNote = words.noAnswer;
        draw();
    }

}


/**
 * What a status is called on a screen somebody reads from three metres away.
 *
 * Only called while this station is being heard from. Out of contact the word
 * is not shown at all: "free" is a promise that somebody can walk up and plug
 * in, and a display that has not been told anything for half a minute is in no
 * position to make it.
 */
function statusWord(Status: KioskEVSE['status']): string {
    return Status === 'available'   ? words.free
         : Status === 'reserved'    ? words.reserved
         : Status === 'occupied'    ? words.charging
         : words.outOfService;
}


/**
 * The QR code, as an SVG built here.
 *
 * Drawn in the browser rather than fetched, because a display in a car park
 * should need nothing from anywhere: the station it is plugged into is the
 * only thing it talks to, and the code changes every few seconds.
 */
function qrSVG(URL: string) {

    const remembered = drawnQRCodes.get(URL);

    if (remembered !== undefined)
        return remembered;

    const qr = qrcode(0, 'M');

    qr.addData(URL);
    qr.make();

    // raw(), because createSvgTag returns markup rather than text. What went
    // into it is a URL this station generated from its own template and its
    // own secret - nothing a visitor typed reaches this function.
    const svg = raw(qr.createSvgTag({ cellSize: 4, margin: 0, scalable: true }));

    // One code per URL, and the URLs change every half minute or so. Kept
    // small rather than cleared on a timer: an entry costs a few hundred bytes
    // and a display that has been on for a week has seen twenty thousand of
    // them, which is worth neither the memory nor a timer to avoid it.
    if (drawnQRCodes.size >= 8)
        drawnQRCodes.clear();

    drawnQRCodes.set(URL, svg);

    return svg;

}

/**
 * Which of the cycling messages is being shown, counted up for ever.
 *
 * One counter for the whole screen rather than one per outlet: two places
 * changing their text at different moments makes a display look broken, and
 * the eye reads the whole thing as one surface.
 */
let cycle = 0;

/** The codes already drawn, so that the same URL is not encoded twice. */
const drawnQRCodes = new Map<string, HTMLFragment>();


/**
 * Which cycling message each place is showing, as a string.
 *
 * Part of what "the screen already shows this" means, so that the cycle moving
 * on is a change like any other and nothing else has to know about it. When
 * there is at most one cycling message anywhere, this never varies - and a
 * station with nothing to say goes back to not redrawing at all.
 */
function cycleSignature(): string {

    if (state === null)
        return '';

    return [state.messages ?? [], ...state.evses.map(evse => evse.messages ?? [])].
               map(messages => messagesToShow(messages).map(message => message.id).join(',')).
               join('|');

}


/**
 * Which outlets are showing a payment code, as a string.
 *
 * Part of what "the screen already shows this" means. Without it, a code that
 * ran out while the station was unreachable would stay drawn: nothing else
 * about the answer changes when the answer stops arriving.
 */
function liveCodes(): string {

    return state === null
               ? ''
               : state.evses.map(evse => evse.qrCode && qrCodeStillGood(evse.qrCode) ? '1' : '0').join('');

}


/**
 * What is worth redrawing about the clock.
 *
 * Everything except the two fields that change because time passed - the time
 * itself and the age of the last check. Those are written into their elements
 * by the ticker below; leaving them in here would redraw the whole page once
 * a second, which is exactly what this comparison exists to stop.
 */
function clockSignature(): string {

    const clock = state?.clock;

    if (clock === undefined)
        return '';

    return JSON.stringify({
               legal:      clock.legal,
               why:        clock.why,
               authority:  clock.authority,
               enabled:    clock.nts.enabled,
               server:     clock.nts.server,
               last:       clock.nts.lastServer,
               checkedAt:  clock.nts.checkedAt,
               offset:     clock.nts.offset_ms
           });

}


/** Whether anywhere on this screen has more than one message taking turns. */
function hasSomethingToCycle(): boolean {

    if (state === null)
        return false;

    return [state.messages ?? [], ...state.evses.map(evse => evse.messages ?? [])].
               some(messages => messages.filter(message => message.priority !== 'AlwaysFront' &&
                                                           message.priority !== 'InFront').length > 1);

}


void poll();
setInterval(() => void poll(), pollEvery);

// The digits only. Written straight into the element rather than through a
// redraw, because a page that rebuilt itself once a second would take the
// keyboard away from anybody typing a card number - which is the whole reason
// this display stopped redrawing itself in the first place.
setInterval(() => {

    const digits = document.querySelector<HTMLElement>('#clock');

    if (digits !== null)
        digits.textContent = clockText();

    const age = document.querySelector<HTMLElement>('#clock-age');

    if (age !== null)
        age.textContent = ageText();

}, 1000);

// The cycle turns on its own clock rather than on the poll's: how long a line
// stays readable has nothing to do with how often this station is asked what it
// is doing. It never draws over the card dialog, for the same reason the poll
// does not.
setInterval(() => {

    cycle++;

    // Only where there is actually something to take turns. One message, or
    // none, means nothing changes when the counter does - and a station with
    // nothing to say goes back to asking twice a second and drawing never.
    if (dialogFor === null && hasSomethingToCycle())
        poll();

}, cycleEvery);
