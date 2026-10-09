import qrcode from 'qrcode-generator';

// What the page decides, as opposed to what it draws. Kept apart so it can be
// asked directly - see kiosk-rules.test.ts.
import { bundleIn,
         cableLimitWorthSaying,
         columnsFor        as howManyColumns,
         dimTo,
         driftAt,
         hasMoreToSay,
         howTightlyToListCables,
         messagesToShow    as whichMessagesToShow,
         nameOfCable,
         qrCodeIsStillGood,
         whatIsHappening,
         type DisplayMessage } from './kiosk-rules';

import { config } from '@node/config';
import { must } from '@node/html';
import { html, nothing, render, repeat, unsafeHTML } from '@node/view';

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
    /**
     * Charging now, and out of service once this session ends.
     *
     * Taking an outlet out of service does not pull the plug on a car that is
     * already on it - the change takes effect when the cable comes out. Which
     * makes this the one thing the person on that cable needs to know and
     * nobody else does: it works now, and it will not be here afterwards.
     */
    closing:           boolean;
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
    outOfServiceAfter:  string;
    notKnown:           string;
    upTo:               (kW: number) => string;
    simulated:          string;
    scanToCharge:       string;
    tapACard:           string;
    holdYourCard:       string;
    card:               string;
    readerOutOfOrder:   string;
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
    checkedAgainstGroup: (answered: number, asked: number) => string;
    timeUnverified:     string;
    notClaimed:         string;
    ntsOff:             string;
    neverChecked:       string;
    timeNotYetChecked:  string;
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
    outOfServiceAfter:  'out of service after this session',
    notKnown:           'not known',
    upTo:               kW => `up to ${kW} kW`,
    simulated:          'simulated',
    scanToCharge:       'Scan to charge',
    tapACard:           'Tap a card',
    holdYourCard:       'Hold your card here',
    card:               'Card',
    readerOutOfOrder:   'Card reader out of order',
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
    checkedAgainstGroup: (answered, asked) => `checked against ${answered} of ${asked} time servers,`,
    timeUnverified:     'time not verified',
    notClaimed:         'no time authority configured',
    ntsOff:             'time checking is switched off',
    neverChecked:       'not checked yet',
    timeNotYetChecked:  'time not checked yet',
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
    outOfServiceAfter:  'nach diesem Ladevorgang außer Betrieb',
    notKnown:           'unbekannt',
    upTo:               kW => `bis zu ${kW} kW`,
    simulated:          'simuliert',
    scanToCharge:       'Zum Laden scannen',
    tapACard:           'Karte eingeben',
    holdYourCard:       'Karte hier auflegen',
    card:               'Karte',
    readerOutOfOrder:   'Kartenleser gestört',
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
    checkedAgainstGroup: (answered, asked) => `geprüft gegen ${answered} von ${asked} Zeitservern,`,
    timeUnverified:     'Zeit ungeprüft',
    notClaimed:         'keine Zeitautorität konfiguriert',
    ntsOff:             'Zeitprüfung ist abgeschaltet',
    neverChecked:       'noch nicht geprüft',
    timeNotYetChecked:  'Zeit noch nicht geprüft',
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
                      nts:        { enabled: boolean; group: string | null; server: string | null; lastServer: string | null;
                                    servers: string[] | null; minServers: number | null;
                                    asked: number | null; answered: number | null;
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
    /**
     * How dark the station would like the screen while nothing is happening,
     * or null outside its quiet hours. Whether anything is happening is this
     * page's to know - see dimTo.
     */
    dim:          number | null;
    /**
     * Whether the picture walks its ring against burn-in - see driftAt. Off
     * unless the station was told otherwise: on a panel that keeps no ghost, a
     * picture that moves is only a screen that twitches.
     */
    keepMoving:   boolean;
    /**
     * The port the display moved to, said for a while by the port it moved
     * away from; null where this is where the display is.
     */
    movedTo?:     number | null;
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
 * How often the picture moves a step, and how long a step takes.
 *
 * Slow on purpose. A step every three quarters of a minute walks the whole ring
 * in six, which is often enough that nothing stands still for long - and the
 * step itself is a glide rather than a jump. It used to be a jump: thirteen
 * pixels of the whole screen at once on a 1080p panel, which somebody standing
 * in front of it saw as the display twitching.
 *
 * The glide is a second at a time, a fifteenth of the step each - under a
 * pixel on that panel - so that it is a page laid out fifteen times a step
 * rather than sixty times a second for fifteen seconds, on hardware that is
 * often the smallest that will run a browser.
 */
const driftEvery       = 45_000;
const driftTakes       = 15_000;
const driftTakesSteps  = 15;

/**
 * How often the display asks whether it is still the page the station serves.
 *
 * Rarely: a display is built when somebody builds it, not on a schedule. Every
 * five minutes means a panel is at most five minutes behind a new one, at the
 * cost of one request for one small file.
 */
const checkForANewPageEvery   = 5 * 60 * 1000;
const checkForANewPageWithin  = 10_000;

/**
 * How long anything at all keeps the screen at full brightness.
 *
 * Somebody who has just held a card up is still standing in front of the
 * station reading what it says about their charge, and a screen that dimmed
 * while they were reading would be a screen that had understood nothing. Two
 * minutes is longer than anybody stands there and short enough that an empty
 * car park is dark again before the next car arrives.
 */
const stayAwakeFor = 2 * 60 * 1000;

/** How long going dark takes. Coming back up is immediate. */
const dimTakes = 2_000;

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
 * The page is drawn by comparing (WWCP_Node's view.ts, on lit-html): a draw
 * changes only what differs, and keeps the elements, the caret, the focus and
 * on a touch screen the keyboard that is open. It used to be drawn by
 * replacing all of it, and every redraw threw those away.
 *
 * A poll that brings back what is already on screen still draws nothing -
 * there is nothing to compare. The timestamp is left out of the comparison on
 * purpose: it is the one field that differs every single time, and comparing
 * it would make this test always say "changed".
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

        if (typeof state.movedTo === 'number')
            followTheDisplayTo(state.movedTo);

        if (state.keepMoving !== true)
            standStill();
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
                  : nothing}
            <h1>${current.station.name ?? 'Charging Station'}</h1>

            ${current.holds
                  ? html`
                      <div class="kiosk-hold">
                          <span>
                              ${words.keptFree(current.holds.count, current.holds.minutesLeft)}
                          </span>
                          ${anyCardReader()
                                ? html`<button type="button" class="kiosk-btn release" id="release-hold" @click=${releaseTheHold}>${words.cancelReservation}</button>`
                                : nothing}
                      </div>
                    `
                  : nothing}

            ${offline ? html`<span class="kiosk-offline">${words.noConnection}</span>` : nothing}

            ${clockCorner(current)}

        </header>

        ${messageBand(current.messages ?? [], 'station')}

        <main class="kiosk-evses">
            ${repeat(current.evses, evse => evse.id, evse => evseCard(evse))}
        </main>

        ${current.rfid
              ? html`
                  <footer class="kiosk-foot">
                      <button type="button" class="kiosk-rfid ${readerClass(current.rfid)}"
                              data-reader="${current.rfid.id}"
                              ?disabled=${!(current.rfid.fake && current.rfid.ready)}
                              @click=${() => holdACard(null)}>
                          <i class="kiosk-rfid-icon"></i>
                          <span>${readerWords(current.rfid, words.holdYourCard)}</span>
                      </button>
                  </footer>
                `
              : nothing}

        ${dialogFor ? cardDialog(current) : nothing}

    `);

    applyColumns();
    tick();

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

    return state !== null &&
           qrCodeIsStillGood(QRCode.expiresAt, state.timestamp, ageOfWhatIsShown());

}


/**
 * The clock, and one line under it saying what it is worth.
 *
 * The time shown is the station's, carried forward by the local clock between
 * polls - the difference between two local readings, never an absolute one, so
 * that a screen whose own clock is wrong still shows the station's time.
 *
 * The digits, and how long ago the clock was checked, are written in place by
 * the ticker - tick() - rather than drawn: the two elements are drawn empty,
 * and what is in them is the ticker's alone. A draw that wrote into them as
 * well would be writing into what the ticker had just written over, which
 * takes the page's own markers in them away.
 */
function clockCorner(Current: KioskState) {

    const clock = Current.clock;

    if (clock === undefined)
        return nothing;

    const why = clock.why === 'notClaimed'   ? words.notClaimed
              : clock.why === 'ntsOff'       ? words.ntsOff
              : clock.why === 'neverChecked' ? words.neverChecked
              : clock.why === 'stale'        ? words.stale
              : clock.why === 'offBy'        ? words.offBy(clock.nts.offset_ms ?? 0)
              : '';

    // Without the root dot. "ptbtime1.ptb.de." is the correct way to write a
    // fully qualified name and the wrong way to put one in front of somebody.
    const server = (clock.nts.lastServer ?? clock.nts.server)?.replace(/\.$/, '') ?? null;

    // A name where there is one server, and a count where there are several.
    // The station sends numbers rather than a phrase for exactly this reason:
    // it does not know which language this screen is showing.
    const against = server !== null
                        ? words.checkedAgainst(server)
                        : (clock.nts.answered !== null && clock.nts.asked !== null && clock.nts.asked > 1
                               ? words.checkedAgainstGroup(clock.nts.answered, clock.nts.asked)
                               : null);

    return html`
        <div class="kiosk-clock ${clock.legal ? 'legal' : 'unverified'}">
            <span class="kiosk-time" id="clock"></span>
            <span class="kiosk-time-note">
                ${clock.legal && clock.authority
                      ? html`${words.legalTime(clock.authority)}`
                      : clock.why === 'neverChecked'
                            // On its own: "time unverified - not checked yet"
                            // is the same sentence twice, and a line that says
                            // one thing twice reads as a line nobody wrote.
                            ? html`${words.timeNotYetChecked}`
                            : html`${words.timeUnverified}${why ? html` · ${why}` : nothing}`}
                ${against !== null && clock.nts.checkedAt !== null
                      ? html`<br />${against} <span id="clock-age"></span>`
                      : nothing}
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


/** How many columns to lay the outlets out in - see kiosk-rules. */
function columnsFor(Count: number): number {

    return howManyColumns(Count,
                          root.clientWidth  || window.innerWidth,
                          root.clientHeight || window.innerHeight);

}


/**
 * Put that number on the grid.
 *
 * Written straight onto the element rather than through a redraw, so that a
 * screen being turned or a window being dragged does not take the keyboard away
 * from somebody typing a card number - the same reason the clock ticks the way
 * it does.
 */
function applyColumns(): void {

    const grid = root.querySelector<HTMLElement>('.kiosk-evses');

    if (grid !== null && state !== null)
        grid.style.gridTemplateColumns = `repeat(${columnsFor(state.evses.length)}, minmax(0, 1fr))`;

}


/**
 * What to call a reader, and how to draw it.
 *
 * A reader this station has no driver for is configured, is wired to the
 * housing, and reads nothing - the station says so in its own log at every
 * start. The display was not saying it: it drew the same quiet "Card" label as
 * a working one, so somebody held their card against a reader that could not
 * answer and had been told to.
 *
 * Not hidden, said. The reader is a physical thing on the front of the station
 * and somebody walking up will try it whether or not this screen mentions it;
 * what a screen can do is tell them why nothing happened.
 */
function readerClass(Reader: Reader): string {
    return !Reader.ready       ? 'out'
         : Reader.fake         ? 'tappable'
         :                       '';
}

function readerWords(Reader: Reader, WhenReal: string): string {
    return !Reader.ready  ? words.readerOutOfOrder
         : Reader.fake    ? words.tapACard
         :                  WhenReal;
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
/** Which notices to show, now - see kiosk-rules. */
function messagesToShow(Messages: DisplayMessage[]): DisplayMessage[] {

    return whichMessagesToShow(Messages, cycle);

}


function messageBand(Messages: DisplayMessage[], Where: string) {

    const showing = messagesToShow(Messages);

    return showing.length === 0
               ? nothing
               : html`
                   <div class="kiosk-messages ${Where}">
                       ${showing.map(message => html`
                           <div class="kiosk-message ${message.priority === 'AlwaysFront' ? 'front' : ''}">${message.text}</div>
                       `)}
                   </div>
                 `;

}


function evseCard(EVSE: KioskEVSE) {

    // Out of contact, what an outlet is drawing is a number from the last time
    // anybody said - printed to a tenth of a kilowatt beside the word "not
    // known", which is two answers to the same question. What the cable can
    // carry is a fact about the equipment and stays true, so that is what is
    // left standing.
    const power = !offline && EVSE.status === 'occupied' && EVSE.currentPower_kW !== null
                      ? html`
                          <div class="kiosk-power">
                              <span class="now">${EVSE.currentPower_kW.toFixed(1)}</span>
                              <span class="of">/ ${EVSE.maxPower_kW} kW</span>
                              ${EVSE.powerIsSimulated ? html`<span class="kiosk-sim" title="This station has no energy meter; the figure is simulated.">${words.simulated}</span>` : nothing}
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

        <div class="kiosk-evse-info">

            <div class="kiosk-evse-head">
                <span class="kiosk-evse-label">${EVSE.label}</span>
                <span class="kiosk-status">${offline ? words.notKnown : statusWord(EVSE.status)}</span>
            </div>

            ${EVSE.closing && !offline
                  ? html`<div class="kiosk-closing">${words.outOfServiceAfter}</div>`
                  : nothing}

            ${power}

            <div class="kiosk-connectors ${howTightlyToListCables(EVSE.connectors.length)}">
                ${EVSE.connectors.map(connector => html`
                    <span class="kiosk-connector">${nameOfCable(connector.type)}${
                        cableLimitWorthSaying(connector.maxPower_kW, EVSE.maxPower_kW)
                            ? html` <span class="kw">${connector.maxPower_kW} kW</span>`
                            : nothing
                    }</span>
                `)}
            </div>

            ${messageBand(EVSE.messages ?? [], 'evse')}

            ${EVSE.reservation && !EVSE.session
                  ? html`
                      <div class="kiosk-reserved">
                          <span>${words.heldFor(EVSE.reservation.minutesLeft)}</span>
                          ${readerFor(EVSE)
                                ? html`
                                    <button type="button" class="kiosk-btn release" data-release="${EVSE.id}"
                                            @click=${() => releaseOutlet(EVSE.id)}>
                                        ${words.cancelReservation}
                                    </button>
                                  `
                                : nothing}
                      </div>
                    `
                  : nothing}

            ${EVSE.session
                  ? html`
                      <div class="kiosk-session">
                          <span class="kiosk-method">${EVSE.session.method}</span>
                          ${EVSE.session.provider
                                ? EVSE.session.provider.logo
                                      ? html`<img class="kiosk-emp-logo" src="${EVSE.session.provider.logo}" alt="${EVSE.session.provider.name}" />`
                                      : html`<span class="kiosk-emp">${EVSE.session.provider.name}</span>`
                                : nothing}
                      </div>
                    `
                  : nothing}

            ${EVSE.rfid
                  ? html`
                      <button type="button" class="kiosk-rfid small ${readerClass(EVSE.rfid)}"
                              data-reader="${EVSE.rfid.id}" data-evse="${EVSE.id}"
                              ?disabled=${!(EVSE.rfid.fake && EVSE.rfid.ready)}
                              @click=${() => holdACard(EVSE.id)}>
                          <i class="kiosk-rfid-icon"></i>
                          <span>${readerWords(EVSE.rfid, words.card)}</span>
                      </button>
                    `
                  : nothing}

        </div>

            ${EVSE.qrCode && qrCodeStillGood(EVSE.qrCode)
                  ? html`
                      <div class="kiosk-qr">
                          ${qrSVG(EVSE.qrCode.url)}
                          <span class="kiosk-qr-hint">${words.scanToCharge}</span>
                      </div>
                    `
                  : nothing}

        </section>
        </div>
    `;

}


function cardDialog(Current: KioskState) {

    const reader    = dialogFor!.reader;
    const forEVSE   = dialogFor!.evse;
    const releasing = dialogFor!.mode === 'release';

    return html`
        <div class="kiosk-modal" id="modal"
             @click=${(event: Event) => { if (event.target === event.currentTarget) close(); }}>
            <div class="kiosk-dialog" role="dialog" aria-modal="true"
                 aria-label="${releasing ? words.cancelTheHold : words.presentACard}">

                <h2>${releasing ? words.cancelTheHold : words.presentACard}</h2>

                <p class="kiosk-dialog-hint">
                    ${releasing ? html`${words.onlyTheRightCard}` : nothing}
                    ${words.testReader(reader.id)}
                </p>

                <label>
                    ${words.cardUID}
                    <input type="text" id="uid" value="${dialogUID}" placeholder="04A22B3C4D5E6F"
                           autocomplete="off" spellcheck="false"
                           @input=${(event: Event) => { dialogUID = (event.target as HTMLInputElement).value; }}
                           @keydown=${(event: KeyboardEvent) => {
                               if (event.key === 'Enter')  void present();
                               if (event.key === 'Escape') close();
                           }} />
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
                      : nothing}

                <div class="kiosk-dialog-note">${dialogNote}</div>

                <div class="kiosk-dialog-actions">
                    <button type="button" id="dialog-cancel" class="kiosk-btn" ?disabled=${sending} @click=${close}>
                        ${words.back}
                    </button>
                    <button type="button" id="dialog-ok"     class="kiosk-btn primary" ?disabled=${sending} @click=${() => void present()}>
                        ${sending ? words.sending : releasing ? words.cancelTheHold : words.present}
                    </button>
                </div>

            </div>
        </div>
    `;

}


/**
 * A card held against a reader: the one beside this outlet, or - for null -
 * the station's own, which asks which outlet the card is for.
 */
function holdACard(EVSEId: number | null): void {

    if (state === null)
        return;

    const reader = EVSEId !== null
                       ? state.evses.find(evse => evse.id === EVSEId)?.rfid
                       : state.rfid;

    if (!reader?.fake || !reader.ready)
        return;

    openTheDialog({ reader, evse: EVSEId, mode: 'present' });

}

/** The hold over the whole station let go. */
function releaseTheHold(): void {

    const reader = anyCardReader();

    if (!reader)
        return;

    // No outlet: this is the hold that names none, and the station finds the
    // one this card can speak for.
    openTheDialog({ reader, evse: null, mode: 'release' });

}

/** A held outlet let go. */
function releaseOutlet(EVSEId: number): void {

    if (state === null)
        return;

    const evse    = state.evses.find(candidate => candidate.id === EVSEId);
    const reader  = evse ? readerFor(evse) : null;

    if (!reader)
        return;

    // Always for this one outlet, even at a reader that serves the whole
    // housing: the button is on the card of the outlet being let go, so there
    // is nothing to ask.
    openTheDialog({ reader, evse: EVSEId, mode: 'release' });

}

function openTheDialog(For: NonNullable<typeof dialogFor>): void {

    dialogFor  = For;
    dialogUID  = '';
    dialogNote = '';

    draw();

    root.querySelector<HTMLInputElement>('#uid')?.focus();

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

    // Read now and held rather than read again afterwards: this card is on
    // its way to that outlet, whatever is chosen while it is.
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

    // unsafeHTML(), because createSvgTag returns markup rather than text. What went
    // into it is a URL this station generated from its own template and its
    // own secret - nothing a visitor typed reaches this function.
    //
    // The margin is the quiet zone, in the same units as the cell: four
    // modules of white all round, which is what the standard asks for and what
    // this library does when nobody tells it otherwise. It used to be told
    // otherwise - margin 0 - which left the dark modules flush with the edge
    // of the code's own box, and the only white around them was a CSS padding
    // that knows nothing about how large a module is. Measured on a 1920x1080
    // screen: 1.83 modules, with the dark card immediately outside that. A
    // code with too little white around it is one that reads on one telephone
    // and not the next, and whoever put the station up never finds out.
    const cellSize = 4;

    const svg = unsafeHTML(qr.createSvgTag({ cellSize, margin: cellSize * 4, scalable: true }));

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
const drawnQRCodes = new Map<string, ReturnType<typeof unsafeHTML>>();


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
               asked:      clock.nts.asked,
               answered:   clock.nts.answered,
               checkedAt:  clock.nts.checkedAt,
               offset:     clock.nts.offset_ms
           });

}


/** Whether anywhere on this screen has more to say than it is showing. */
function hasSomethingToCycle(): boolean {

    if (state === null)
        return false;

    return [state.messages ?? [], ...state.evses.map(evse => evse.messages ?? [])].
               some(messages => hasMoreToSay(messages, cycle));

}


void poll();
setInterval(() => void poll(), pollEvery);

// A screen that is turned, or a window being dragged while somebody sets the
// station up. Two outlets belong side by side on one shape and above each other
// on the other, and nothing else about the page has to change for that.
window.addEventListener('resize', applyColumns);

/**
 * The digits only, and how long ago they were checked: written straight into
 * the two elements the page draws empty for them - see clockCorner - once a
 * second, and after every draw, which may have just made them.
 */
function tick(): void {

    const digits = document.querySelector<HTMLElement>('#clock');

    if (digits !== null)
        digits.textContent = clockText();

    const age = document.querySelector<HTMLElement>('#clock-age');

    if (age !== null)
        age.textContent = ageText();

}

setInterval(tick, 1000);

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


// The picture walks its ring, a step at a time, where the station says it
// should. Written straight onto the element like the clock and the columns:
// nothing about where the picture sits is worth a redraw, and a redraw would
// take the keyboard away from whoever is typing a card number. How long a step
// takes is set from here, where it is decided - the stylesheet only glides.
let driftStep = 0;

root.style.setProperty('--drift-takes',  `${driftTakes}ms`);
root.style.setProperty('--drift-steps',  String(driftTakesSteps));

setInterval(() => {

    if (state?.keepMoving !== true)
        return;

    const where = driftAt(driftStep++);

    root.style.setProperty('--drift-x', String(where.x));
    root.style.setProperty('--drift-y', String(where.y));

}, driftEvery);

/**
 * Back to the middle, and the walk to begin there again when it is switched
 * on - for a station that was told to stop it, at the next poll rather than
 * at the next step.
 */
function standStill(): void {

    driftStep = 0;

    root.style.setProperty('--drift-x', '0');
    root.style.setProperty('--drift-y', '0');

}


/**
 * The display moved to another port while this screen was pointed at the old
 * one: go there.
 *
 * The old port says so for a while, at every poll, so that a screen nobody can
 * reach without a ladder follows on its own instead of showing a page nobody
 * answers for any more. Only the port changes - the host is whatever this
 * screen was pointed at, which the station cannot know better.
 */
function followTheDisplayTo(Port: number): void {

    const there = new URL(location.href);

    if (there.port === String(Port))
        return;

    there.port = String(Port);

    location.replace(there.toString());

}


/**
 * Whether this is still the page the station would serve.
 *
 * Nothing ever reloads a display. A panel that came up in March is running
 * March's page in December, whatever has been installed on the station since -
 * and the one place that would notice is the panel itself, which nobody looks
 * at until something is wrong with it.
 *
 * So it asks, every few minutes, for the page it would be given now, and
 * compares the bundle that page names with the one it is running. The name
 * carries a hash of its contents, so it differs exactly when the display has
 * been built again.
 *
 * Three things keep this from being a display that restarts itself in a loop.
 * It only ever reloads towards a page that looks like this station's display,
 * never towards a captive portal or a proxy's apology. It writes down what it
 * reloaded for and will not do it twice, which survives the reload because the
 * note does. And where that note cannot be kept - a browser with its storage
 * turned off - it does not reload at all, because once is safe and twice a
 * minute for ever is a screen nobody can use.
 */
const reloadedFor = 'kiosk-reloaded-for';

async function stillTheServedPage(): Promise<void> {

    if (dialogFor !== null)
        return;

    const running = bundleIn(document.head.innerHTML);

    if (running === null)
        return;

    const giveUp = new AbortController();
    const timer  = setTimeout(() => giveUp.abort(), checkForANewPageWithin);

    try
    {
        const response = await fetch(location.pathname, {
                                   cache:   'no-store',
                                   headers: { 'Accept': 'text/html' },
                                   signal:  giveUp.signal
                               });

        if (!response.ok)
            return;

        const served = bundleIn(await response.text());

        if (served === null || served === running)
            return;

        // Kept across the reload on purpose: it is the reload itself this has
        // to remember having done.
        if (sessionStorage.getItem(reloadedFor) === served)
            return;

        sessionStorage.setItem(reloadedFor, served);

        location.reload();

    }
    catch
    {
        // Unreachable, slow, not HTML, or a browser that keeps no notes. All of
        // them mean the same thing here: leave the display alone.
    }
    finally
    {
        clearTimeout(timer);
    }

}

setInterval(() => void stillTheServedPage(), checkForANewPageEvery);


// The screen in the quiet hours.
//
// Two things decide it, and they are kept apart on purpose: the station knows
// which hours are quiet at this site, and only the display knows whether
// anybody is standing at it. It applies the brightness rather than being told
// one, because the second half of that question is asked here.

let happeningWas       = '';
let somethingHappened  = Date.now();

/**
 * Note that something did, and bring the screen back up at once.
 *
 * Immediately: whoever put their hand on the glass is looking at it now. Going
 * dark again is the slow half - see the transition in the stylesheet.
 */
function somethingIsHappening(): void {

    somethingHappened = Date.now();

    applyDim(0);

}

/** Put the brightness on the page, without redrawing any of it. */
function applyDim(TakesMilliseconds: number): void {

    const level = dimTo(state?.dim, Date.now() - somethingHappened, stayAwakeFor);

    document.body.style.setProperty('--dim-takes', `${TakesMilliseconds}ms`);
    document.body.style.setProperty('--dim', String(level ?? 1));

}

// Anything a hand does. Pointer events cover a finger on glass, a mouse and a
// pen alike; a key covers the panel that has a keyboard wired to it.
for (const event of ['pointerdown', 'keydown'] as const)
    window.addEventListener(event, somethingIsHappening, { passive: true });

// And anything the outlets do. Asked once a second rather than on every poll,
// because going dark is a thing that happens on a clock and waking is a thing
// that happens at once.
setInterval(() => {

    if (state !== null) {

        const happening = whatIsHappening(state);

        if (happening !== happeningWas) {
            happeningWas = happening;
            somethingIsHappening();
            return;
        }

    }

    applyDim(dimTakes);

}, 1000);
