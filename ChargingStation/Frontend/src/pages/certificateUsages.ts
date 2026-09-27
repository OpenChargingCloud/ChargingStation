import type { CertificateKind, CertificateStore } from '../api/client';

/**
 * What a certificate of a kind may be told it is for, as the certificates
 * page offers it.
 *
 * Apart from the page, because this is the part that decides what the page
 * offers - and it offered one list for every kind: the services a TLS root
 * vouches for, "dns" and "nts", were offered for a TLS identity as well, which
 * is told the listeners it is shown on, and of which a station has none.
 */


/** What a usage is called on the page: the service, in the words of the pages it is set on. */
const usageNames: Record<string, string> = {
    dns:  'name servers (DNS)',
    nts:  'time servers (NTS)'
};

export function usageName(usage: string): string {
    return usageNames[usage] ?? usage;
}


/**
 * What a certificate of this kind may be told it is for in this store - what
 * the store says of the kind, and nothing for a kind it tells nothing.
 *
 * A store that said it once for all its kinds, before every kind said its own,
 * is taken at that one word for the kinds it says have usages.
 */
export function usagesOf(store: CertificateStore, kind: CertificateKind): string[] {

    const ofKind = store.kinds[kind];

    if (ofKind?.hasUsages !== true)
        return [];

    return ofKind.usages ?? store.usages ?? [];

}


/** Whether a certificate of this kind is told what it is for in this store at all. */
export function hasUsages(store: CertificateStore, kind: CertificateKind): boolean {
    return usagesOf(store, kind).length > 0;
}
