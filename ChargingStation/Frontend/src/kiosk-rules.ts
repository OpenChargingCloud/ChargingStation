/**
 * The rules the display decides by.
 *
 * Everything on this page that is a decision rather than a drawing: how many
 * columns the outlets go in, which notices are shown at this moment, and
 * whether a payment code is still worth putting on a screen. They live here
 * rather than in `kiosk.ts` because they can be wrong quietly - a rule that
 * starves one notice, or an arrangement that leaves a column of air - and
 * because separated from the page they can be asked directly.
 *
 * Nothing here touches the document, reads a clock or knows what a browser is.
 * What they need is handed to them.
 *
 * See `kiosk-rules.test.ts`, which is run by `npm test`.
 */


/** One line a back end asked this station to say. */
export interface DisplayMessage {
    id:        string;
    priority:  'AlwaysFront' | 'InFront' | 'NormalCycle' | string;
    text:      string;
}


/**
 * How many notices one place on this screen shows at a time.
 *
 * A display on a charging station is there to show the outlets. Four notices
 * that all asked to stay at the front took 542 px of a 1080 px screen - half of
 * it - and left the two outlets 268 px each with no payment code on either, a
 * station that had stopped saying how to charge at it. OCPP lets a back end ask
 * for the front; on a screen this size it cannot be given to everybody at once.
 *
 * Two, then. Nothing is dropped for it - see below.
 */
export const atMostMessages = 2;


/**
 * Which of them to show, now.
 *
 * More asked for the front than there is front to give, so they take turns at
 * it: each comes round rather than the last few never being seen. And one place
 * is kept for the ordinary ones whenever there are any, so a back end cannot
 * push them off the screen by marking everything important.
 *
 * @param Messages  everything there is to say in one place.
 * @param Cycle     which turn it is, counted up for ever by the page.
 */
export function messagesToShow(Messages: DisplayMessage[],
                               Cycle:    number): DisplayMessage[] {

    const pinned   = Messages.filter(message => message.priority === 'AlwaysFront' || message.priority === 'InFront');
    const cycling  = Messages.filter(message => message.priority !== 'AlwaysFront' && message.priority !== 'InFront');

    const forPinned  = Math.max(1, atMostMessages - (cycling.length > 0 ? 1 : 0));

    const shown = pinned.length > forPinned
                      ? Array.from({ length: forPinned },
                                   (_, slot) => pinned[(Cycle + slot) % pinned.length])
                      : [...pinned];

    if (shown.length < atMostMessages && cycling.length > 0)
        shown.push(cycling[Cycle % cycling.length]);

    return shown;

}


/**
 * Whether a place has more to say than it is showing.
 *
 * Asked by comparing the two rather than by repeating the rule above: whatever
 * decides how many places there are, this stays true. Messages that asked to
 * stay at the front take turns as well when there are more of them than places,
 * and that is a turn this has to keep as much as any other.
 */
export function hasMoreToSay(Messages: DisplayMessage[],
                             Cycle:    number): boolean {

    return Messages.length > messagesToShow(Messages, Cycle).length;

}


/**
 * How many columns to lay the outlets out in.
 *
 * The stylesheet used to decide this from a minimum column width, with one
 * exception written out by hand: two outlets always side by side. Which is
 * right on a screen wider than it is tall and wrong on one turned upright -
 * two outlets on a 1080x1920 panel came out 502 px wide and 1694 tall, a
 * column of air with a letter at the top of it.
 *
 * So it is worked out from the two things that actually decide it, both of
 * which the page knows and the stylesheet cannot: how many outlets there are,
 * and the shape of the screen. Every arrangement is tried and the one whose
 * cards come out closest to square wins, with a small penalty for leaving a
 * hole in the last row - a clean three by two reads better than four and two.
 */
export function columnsFor(Count:   number,
                           Width:   number,
                           Height:  number): number {

    if (Count < 2)
        return 1;

    // Nothing to go on. All of them side by side is no worse a guess than one
    // of them, and it is what the stylesheet would have done.
    if (!(Width > 0) || !(Height > 0))
        return Count;

    let best       = 1;
    let bestScore  = Infinity;

    for (let columns = 1; columns <= Count; columns++) {

        const rows      = Math.ceil(Count / columns);
        const cardWide  = Width  / columns;
        const cardTall  = Height / rows;

        // How far from square, plus a tenth for each empty place in the grid.
        const score     = Math.max(cardWide / cardTall, cardTall / cardWide) +
                          0.1 * (columns * rows - Count);

        if (score < bestScore) {
            bestScore  = score;
            best       = columns;
        }

    }

    return best;

}


/**
 * Whether a payment code is still worth putting on a screen.
 *
 * A code carries about half a minute. Out of contact with the station the
 * answer stops arriving and the last one goes stale, and a dead code is worse
 * than none: somebody scans it, pays nothing, and concludes the station is
 * broken.
 *
 * How much life it has left is the difference between two of the station's own
 * timestamps, never between one of them and this machine's clock. A screen
 * bolted to a wall has whatever clock somebody left in it.
 *
 * @param ExpiresAt         when the station said the code runs out.
 * @param StationSaidAt     what the station thought the time was when it said so.
 * @param AgeOfWhatIsShown  how long ago that was, by this machine's own clock.
 */
export function qrCodeIsStillGood(ExpiresAt:         string,
                                  StationSaidAt:     string,
                                  AgeOfWhatIsShown:  number): boolean {

    const hadLeft = Date.parse(ExpiresAt) - Date.parse(StationSaidAt);

    return Number.isFinite(hadLeft) && hadLeft > AgeOfWhatIsShown;

}


/**
 * Where the picture sits at this step of its slow walk.
 *
 * A charging station's display shows the same thing for months: the operator's
 * name in the same corner, the same letter over the same outlet, the word for
 * "free" in the same place. A panel left like that keeps it - as a ghost on an
 * LCD, permanently on an OLED - and nothing about the software will bring it
 * back afterwards.
 *
 * So the whole picture walks a small ring, a step at a time, slowly enough that
 * nobody watching sees it move and far enough that no edge stands still. The
 * ring is eight places around the middle, each visited as often as the others.
 *
 * The step is a whole number of units, not a length: the page turns it into a
 * fraction of the screen, so the walk is the same size on every panel. And it
 * is applied by moving padding from one side of the display to the other, never
 * by resizing anything - what is on screen is nudged, and every card stays
 * exactly as large as it was, which is what keeps a card from crossing one of
 * the sizes at which it stops drawing its payment code.
 */
export function driftAt(Step: number): { x: number; y: number } {

    const ring = [
                     { x:  0, y: -1 },
                     { x:  1, y: -1 },
                     { x:  1, y:  0 },
                     { x:  1, y:  1 },
                     { x:  0, y:  1 },
                     { x: -1, y:  1 },
                     { x: -1, y:  0 },
                     { x: -1, y: -1 }
                 ];

    return ring[((Step % ring.length) + ring.length) % ring.length];

}


/** How many steps there are before the walk comes round again. */
export const driftSteps = 8;


/**
 * Which bundle a served page would run.
 *
 * The name carries a hash of what is in it, so it is a different name whenever
 * the display is built again - which makes it the one thing a page can compare
 * itself against to find out that the station has a newer display than the one
 * on the screen. A panel that came up in March would otherwise still be running
 * March's page in December, because nothing ever reloads it.
 *
 * Null when the page does not look like this station's display at all: a
 * captive portal, a proxy's error page, anything that is not what was asked
 * for. Reloading towards one of those would be a display that turns itself off.
 */
export function bundleIn(HTML: string): string | null {

    const match = HTML.match(/src="([^"]*\/assets\/kiosk\.[0-9a-f]+\.js)"/);

    return match ? match[1] : null;

}


/**
 * What an outlet is doing, for the purpose of noticing that it changed.
 *
 * Deliberately not everything the card shows. A station dims at night and has
 * to wake for anybody who comes near it, and the only thing it can see of
 * somebody coming near is what changes as a result: a card held up, a plug in a
 * socket, a hold placed or let go, something a back end asked to be read out.
 *
 * What is left out matters as much. The payment code turns over every half
 * minute on its own, and the power reading moves every second a car is
 * charging - a screen that woke for either would be a screen at full brightness
 * all night with a car parked at it, which is the one case where nobody is
 * looking at all.
 */
export function whatIsHappening(State: {
                                    evses:     { id:           number;
                                                 status:       string;
                                                 closing?:     boolean;
                                                 session?:     { method: string } | null;
                                                 reservation?: unknown | null;
                                                 messages?:    DisplayMessage[] }[];
                                    messages?: DisplayMessage[];
                                }): string {

    return [
               (State.messages ?? []).map(message => message.id).join(','),
               ...State.evses.map(evse => [
                                      evse.id,
                                      evse.status,
                                      evse.closing === true ? 'closing' : '',
                                      evse.session ? evse.session.method : '',
                                      evse.reservation ? 'held' : '',
                                      (evse.messages ?? []).map(message => message.id).join(',')
                                  ].join(':'))
           ].join('|');

}


/**
 * How bright the screen should be at this moment.
 *
 * The station says whether these are its quiet hours and how dark it wants the
 * screen then; the display says whether anybody is there. Null is full
 * brightness, and full brightness is what anything at all produces: a hand on
 * the glass, a card, a plug, a hold - and then a few minutes more, because
 * somebody who has just started a charge is still standing in front of it
 * reading what it says.
 *
 * @param StationSaysDimTo  what the station asked for, or null outside its quiet hours.
 * @param SinceSomethingHappened  how long ago anything last did, in milliseconds.
 * @param StaysAwakeFor  how long anything keeps it awake for.
 */
export function dimTo(StationSaysDimTo:        number | null | undefined,
                      SinceSomethingHappened:  number,
                      StaysAwakeFor:           number): number | null {

    if (StationSaysDimTo === null || StationSaysDimTo === undefined)
        return null;

    if (!(SinceSomethingHappened >= StaysAwakeFor))
        return null;

    // Never off, whatever the station was configured with: a dark display is
    // one nobody can tell from a broken one, and the person it would turn away
    // is the one arriving at two in the morning.
    return Math.min(1, Math.max(darkestDim, StationSaysDimTo));

}


/** The darkest the screen will go, whatever it is asked for. */
export const darkestDim = 0.1;


/**
 * What a plug is called, to somebody holding one.
 *
 * OCPP 2.1 names them for machines - "cChaDeMo", "sType2Cable", "cGBT-DC" -
 * and that is what the station is configured with and what it passes on. It is
 * not what is printed on the plug in somebody's hand, and a display on the
 * front of a charging station is read by people, not by back ends.
 *
 * Only the ones OCPP names itself are in here. The configuration deliberately
 * lets a plug be typed in that OCPP has never heard of, and one of those is
 * passed through exactly as written: a name somebody chose is a better guess
 * than anything this table could make up.
 *
 * The names are the ones on the equipment, which are the same in every
 * language the display speaks - so this is not part of the vocabulary. "Type
 * 2" is stamped on the plug in Germany too.
 */
const cableNames = new Map<string, string>([

    // Direct current
    ['cCCS1',          'CCS 1'],
    ['cCCS2',          'CCS'],
    ['cChaDeMo',       'CHAdeMO'],
    ['cChaoJi',        'ChaoJi'],
    ['cUltraChaoJi',   'UltraChaoJi'],
    ['cG105',          'CHAdeMO'],
    ['cGBT-DC',        'GB/T'],
    ['cMCS',           'MCS'],
    ['cNACS',          'NACS'],
    ['cTesla',         'Tesla'],
    ['cType1',         'Type 1'],
    ['cType2',         'Type 2'],

    // Alternating current
    ['sType2',         'Type 2'],
    ['sType3',         'Type 3'],
    ['sCEE-7-7',       'Schuko'],
    ['sBS1361',        'BS 1363'],
    ['s309-1P-16A',    'CEE 16 A'],
    ['s309-1P-32A',    'CEE 32 A'],
    ['s309-3P-16A',    'CEE 16 A, 3-phase'],
    ['s309-3P-32A',    'CEE 32 A, 3-phase'],
    ['Other1PhMax16A', 'Other, 1-phase'],
    ['Other1PhOver16A','Other, 1-phase'],
    ['Other3Ph',       'Other, 3-phase'],

    // Neither
    ['wInductive',     'Inductive'],
    ['wResonant',      'Inductive'],
    ['Pan',            'Pantograph']

]);

/**
 * What to put on the chip for one cable.
 *
 * A type this table does not know is passed through as it stands, trimmed of
 * nothing: the station was configured with it on purpose.
 */
export function nameOfCable(Type: string): string {
    return cableNames.get(Type) ?? Type;
}

/**
 * Whether a cable's own limit is worth saying next to it.
 *
 * The card already carries what the bay can do - "up to 150 kW" - and most
 * cables on a bay can do exactly that. Repeating it on every chip says nothing
 * and costs the width that decides how many chips fit on a row, which in turn
 * decides how much of the card is left for the payment code.
 *
 * So it is said where it differs: a 22 kW socket on a 150 kW bay is the one
 * thing somebody choosing between two cables needs to know.
 */
export function cableLimitWorthSaying(CableMax_kW: number,
                                      BayMax_kW:   number): boolean {
    return !(Math.abs(CableMax_kW - BayMax_kW) < 0.05);
}


/**
 * How much room a bay's cables may take.
 *
 * Measured on a 1920x1080 screen: a bay with eight cables listed them on four
 * rows, and every row came off the payment code - 166 px, against 414 px on
 * the bay beside it with two. The code is what the card is for, and two codes
 * of that different a size standing side by side is exactly what looks broken.
 *
 * A row of chips costs about 83 px of code at that size, so the list is held
 * to two rows: the chips give way rather than the code. Nothing is dropped for
 * it - every cable is still named.
 */
export function howTightlyToListCables(Count: number): 'roomy' | 'tight' | 'tightest' {

    if (Count <= 2)
        return 'roomy';

    if (Count <= 4)
        return 'tight';

    return 'tightest';

}
