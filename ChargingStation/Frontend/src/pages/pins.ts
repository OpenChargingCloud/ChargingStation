import type { ServerHeldTo, ServerPinKeys } from '../api/client';

/**
 * What the entry of a server says it is held to - a time server's, or a name
 * server's reached over TLS or HTTPS - as the pages send it back.
 *
 * Apart from either page, because both send their whole list of servers every
 * time and show none of this: a page that rebuilds an entry only from what it
 * shows makes a server held to nothing, and nobody would have touched it. So
 * both carry it back, and by one rule - a rule that differed between the two
 * would be a server held to something on one page and let go of on the other.
 */


/**
 * What a server is held to, in the keys its entry writes it under, made of
 * what the station says it is held to: one pin of a kind as one, several as a
 * list, and a mismatch or something to learn only where it is not the usual -
 * the way the station writes it into the file.
 */
export function pinsOf(heldTo: ServerHeldTo | null | undefined): ServerPinKeys {

    const pins: ServerPinKeys = {};

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


/** What an entry says its server is held to, and nothing else of it. */
export function pinsIn(entry: ServerPinKeys): ServerPinKeys {

    const pins: ServerPinKeys = {};

    if (entry.certificateFingerprint  !== undefined)  pins.certificateFingerprint   = entry.certificateFingerprint;
    if (entry.certificateFingerprints !== undefined)  pins.certificateFingerprints  = [ ...entry.certificateFingerprints ];
    if (entry.rootFingerprint         !== undefined)  pins.rootFingerprint          = entry.rootFingerprint;
    if (entry.rootFingerprints        !== undefined)  pins.rootFingerprints         = [ ...entry.rootFingerprints ];
    if (entry.onMismatch              !== undefined)  pins.onMismatch               = entry.onMismatch;
    if (entry.trustOnFirstUse         !== undefined)  pins.trustOnFirstUse          = entry.trustOnFirstUse;

    return pins;

}


/**
 * What an entry keeps of its pins once it names another server: none of the
 * fingerprints, which were the old server's - but what it was to learn on
 * first use, and what a mismatch comes to then, which is how the entry treats
 * whichever server it names. It learns the new one's.
 */
export function learnedOnly(pins: ServerPinKeys): ServerPinKeys {

    if (pins.trustOnFirstUse === undefined || pins.trustOnFirstUse === 'none')
        return {};

    const learned: ServerPinKeys = { trustOnFirstUse: pins.trustOnFirstUse };

    if (pins.onMismatch !== undefined)
        learned.onMismatch = pins.onMismatch;

    return learned;

}
