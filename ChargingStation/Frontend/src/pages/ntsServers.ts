import type { NTSHeldTo, NTSServerEntry, NTSTimeSource } from '../api/client';

/**
 * The list of time servers as the NTS page edits it.
 *
 * Apart from the page, because this is the part that decides what the station
 * is told - and the station is told the whole list every time, so a mistake
 * here is a server deleted that nobody touched. The page around it only draws
 * and asks.
 */


/** The ports a server is asked on unless its entry says otherwise. */
export interface UsualPorts {
    ntsKE:  number;
    ntp:    number;
}


/**
 * A name as somebody reads it: without the root's dot. The station hands its
 * names back fully qualified, and "ptbtime1.ptb.de." is correct and looks like
 * a typing mistake.
 */
export function readable(hostname: string): string {
    return hostname.endsWith('.') ? hostname.slice(0, -1) : hostname;
}


/**
 * A server as the station shows it, turned back into what its configuration
 * says - with everything that is the usual left out.
 *
 * Left out rather than repeated, because the station writes back what it is
 * sent: an entry carrying the usual ports and priority 0 becomes an object in
 * the file where a bare name was, and the file stops reading the way somebody
 * would have written it.
 */
export function entryOf(source: NTSTimeSource, usual: UsualPorts): NTSServerEntry {

    const entry: NTSServerEntry = { hostname: readable(source.hostname) };

    if (source.priority  !== 0)            entry.priority   = source.priority;
    if (source.ntsKEPort !== usual.ntsKE)  entry.ntsKEPort  = source.ntsKEPort;
    if (source.ntpPort   !== usual.ntp)    entry.ntpPort    = source.ntpPort;
    if (!source.enabled)                   entry.enabled    = false;

    return { ...entry, ...pinsOf(source.heldTo) };

}


/** The part of an entry that says what its server is held to. */
type Pins = Pick<NTSServerEntry, 'certificateFingerprint' | 'certificateFingerprints' |
                                 'rootFingerprint'        | 'rootFingerprints'        |
                                 'onMismatch'             | 'trustOnFirstUse'>;

/**
 * What a server is held to, as its entry says it: one pin of a kind as one,
 * several as a list, and a mismatch or something to learn only where it is
 * not the usual - the way the station writes it into the file.
 *
 * Carried back although this page shows none of it, because the station is
 * told the whole list every time and writes back what it is sent: an entry
 * made only of what the page shows is a server held to nothing. A pin typed
 * into the file, or learned on first use, went with the next save of any
 * server in the list, and nobody had touched it.
 */
export function pinsOf(heldTo: NTSHeldTo | null | undefined): Pins {

    const pins: Pins = {};

    if (!heldTo)
        return pins;

    const certificates  = heldTo.certificates ?? (heldTo.certificate ? [ heldTo.certificate ] : []);
    const roots         = heldTo.roots        ?? (heldTo.root        ? [ heldTo.root ]        : []);

    if      (certificates.length === 1)  pins.certificateFingerprint   = certificates[0];
    else if (certificates.length  >  1)  pins.certificateFingerprints  = [ ...certificates ];

    if      (roots.length === 1)         pins.rootFingerprint          = roots[0];
    else if (roots.length  >  1)         pins.rootFingerprints         = [ ...roots ];

    if (heldTo.onMismatch      !== undefined && heldTo.onMismatch      !== 'refuse')  pins.onMismatch       = heldTo.onMismatch;
    if (heldTo.trustOnFirstUse !== undefined && heldTo.trustOnFirstUse !== 'none')    pins.trustOnFirstUse  = heldTo.trustOnFirstUse;

    return pins;

}


/**
 * The entry the dialog makes of what was typed into it, for the server that
 * was at that place before - or for a new one.
 *
 * The dialog shows nothing of what a server is held to, so what it was held
 * to is carried over: a server whose priority was changed is still the server
 * that was pinned. Given another name, it is another server, and the
 * fingerprints were the old one's - but what its entry was to learn on first
 * use, and what a mismatch comes to then, is how that entry treats whichever
 * server it names, and it learns the new one's.
 */
export function editedEntry(before:  NTSServerEntry | null,
                            typed:   NTSServerEntry): NTSServerEntry {

    if (before === null)
        return typed;

    if (readable(before.hostname).toLowerCase() === readable(typed.hostname).toLowerCase())
        return { ...typed, ...pinsIn(before) };

    if (before.trustOnFirstUse === undefined || before.trustOnFirstUse === 'none')
        return typed;

    const learned: NTSServerEntry = { ...typed, trustOnFirstUse: before.trustOnFirstUse };

    if (before.onMismatch !== undefined)
        learned.onMismatch = before.onMismatch;

    return learned;

}


/** What an entry says its server is held to, and nothing else of it. */
function pinsIn(entry: NTSServerEntry): Pins {

    const pins: Pins = {};

    if (entry.certificateFingerprint  !== undefined)  pins.certificateFingerprint   = entry.certificateFingerprint;
    if (entry.certificateFingerprints !== undefined)  pins.certificateFingerprints  = [ ...entry.certificateFingerprints ];
    if (entry.rootFingerprint         !== undefined)  pins.rootFingerprint          = entry.rootFingerprint;
    if (entry.rootFingerprints        !== undefined)  pins.rootFingerprints         = [ ...entry.rootFingerprints ];
    if (entry.onMismatch              !== undefined)  pins.onMismatch               = entry.onMismatch;
    if (entry.trustOnFirstUse         !== undefined)  pins.trustOnFirstUse          = entry.trustOnFirstUse;

    return pins;

}


/**
 * The list with one server replaced, or with one added at the end when there
 * is no place given.
 *
 * A new list rather than the old one changed: the old one is what the station
 * still has, and it is what the page has to go back to when the station says
 * no.
 */
export function withServer(list:   readonly NTSServerEntry[],
                           index:  number | null,
                           entry:  NTSServerEntry): NTSServerEntry[] {

    return index === null
               ? [...list, entry]
               : list.map((other, at) => at === index ? entry : other);

}


/** The list without the server at that place. */
export function withoutServer(list:   readonly NTSServerEntry[],
                              index:  number): NTSServerEntry[] {

    return list.filter((_, at) => at !== index);

}


/**
 * Whether another server of the list already has this name - the one at the
 * place being edited does not count, or a server could not be saved unchanged.
 *
 * Compared the way names compare: without the root's dot, and without case.
 */
export function nameTaken(list:      readonly NTSServerEntry[],
                          hostname:  string,
                          except:    number | null): boolean {

    const wanted = readable(hostname.trim()).toLowerCase();

    return list.some((other, at) => at !== except &&
                                    readable(other.hostname).toLowerCase() === wanted);

}
