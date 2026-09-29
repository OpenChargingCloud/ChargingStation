import { afterAsking,
         nodeAPI,
         request,
         type Certificate        as NodeCertificate,
         type CertificateImport  as NodeCertificateImport,
         type CertificateStore   as NodeCertificateStore,
         type NodeConfiguration,
         type NodeMe,
         type NodeResource,
         type NodeStatus,
         type Operation }  from '@node/api/client';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module as before. What follows is what a charging
// station adds: its resources, what its configuration says beyond every
// node's, the kinds its store keeps, and its own routes.
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this charging station: the node's
 * four resources, and the station's nine - see StationAccess.
 */
export type Resource = NodeResource
                     | 'evses' | 'rfid' | 'availability' | 'power' | 'calibration'
                     | 'display' | 'session' | 'connections' | 'v2g';

/** What somebody signed in to this station may do: an operation on a resource, written "dns:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the station is doing right now: what every node says, and nothing beyond it. */
export type Status = NodeStatus;

/**
 * What the station is made of: every node's sections, and its own. Only the
 * shape the Configuration page relies on is named; the rest is rendered from
 * whatever the station sends, so that a new section on the server needs no
 * change here.
 */
export interface Configuration extends NodeConfiguration {
    station:     Record<string, unknown>;
    ocpp:        Record<string, unknown>[];
    assemblies:  Record<string, unknown>[];
}

/**
 * What a certificate is for, as the node names every kind - of which this
 * station keeps the roots and TLS's four. Roots are believed, an identity is
 * presented; a server certificate is neither, but kept to recognise a server
 * by its fingerprint.
 */
export type CertificateKind = 'v2gRoot' | 'moRoot' | 'oemRoot'
                            | 'vehicle' | 'contract' | 'oemProvisioning' | 'tariffVerification'
                            | 'tlsRoot' | 'clientRoot' | 'tlsServer' | 'tlsIdentity';

/** One certificate in the store. */
export type Certificate = NodeCertificate<CertificateKind>;

/** What an import sends. */
export type CertificateImport = NodeCertificateImport<CertificateKind>;

/** The whole store, grouped the way it is shown. */
export type CertificateStore = NodeCertificateStore<CertificateKind>;


/**
 * One cable or socket of an EVSE.
 *
 * Its shape and its limit are two different kinds of fact, and the web
 * interface treats them as two: what plug is fitted takes the hardware
 * permission, what it may deliver takes the power-limit one.
 */
export interface Connector {
    /** Which one it is, counting from 1 within its EVSE. */
    id:           number;
    /** What can be plugged into it, in OCPP 2.1's vocabulary. */
    type:         string;
    /** The most this cable may deliver; never more than its EVSE. */
    maxPower_kW:  number;
}

/** One place a vehicle can be plugged into this charging station. */
export interface EVSE {
    /** Which one it is, counting from 1 as OCPP does. */
    id:                 number;
    connectors:         Connector[];
    /** The most this EVSE can deliver, through whichever cable is in use. */
    maxPower_kW:        number;
    operative:          boolean;
    /** What is written on the housing, e.g. "A". */
    physicalReference:  string | null;
    meterType:          string | null;
    meterSerialNumber:  string | null;
}

/** The EVSEs of this station, and what may be plugged into one. */
export interface EVSEConfiguration {
    evses:                   EVSE[];
    file:                    string;
    maxEVSEs:                number;
    maxConnectors:           number;
    maxPower_kW:             number;
    maxConnectorTypeLength:  number;
    /** What the whole station may draw, for context; null when nobody has said. */
    uplinkPowerLimit_kW:     number | null;
    /**
     * The connector types OCPP 2.1 names itself, for the picker. Not a closed
     * list: anything may be typed, because a plug this station has never heard
     * of is still a plug somebody can charge from.
     */
    connectorTypes:          string[];
}


/**
 * What came up on the wire below the charging cable.
 *
 * Null while the station has not been started; and every field here may
 * disagree with what was asked for, which is the reason it is sent at all.
 */
export interface V2GLinkStatus {
    /** The interface actually chosen, which need not be the one named. */
    interface:      string | null;
    linkLocal:      string | null;
    v2gEndpoint:    string | null;
    v2gTLS:         boolean;
    /** Whether SDP is really answering, not whether it was asked to. */
    sdp:            boolean;
    /** Whether it is really also answering vehicles on this machine. */
    sdpLoopback:    boolean;
    slac:           boolean;
    slacTransport:  string | null;
    slacSessions:   number;
    evseId:         string;
    /** The 10BASE-T1S bus of a megawatt coupler, where this station coordinates one. */
    t1s:            T1SBusStatus | null;
}

/** One node on the coupler's bus: the vehicle, or a sensor in a pin. */
export interface T1SNodeStatus {
    id:             number;
    name:           string;
    /** Vehicle, TemperatureSensor, and whatever else joins. */
    role:           string;
    mac:            string;
    /** Transmit opportunities per cycle: the vehicle asks for more than a sensor. */
    weight:         number;
    lastSeen:       string;
    /** Cycles in a row this node did not answer; five and it is given up for lost. */
    missed:         number;
    frames:         number;
    yields:         number;
    /** What the pin reads, where the node is a temperature sensor. */
    temperatureC:   number | null;
    /** normal, warning, overload or lost - the station's opinion of it. */
    thermal:        string | null;
}

/** The bus below a megawatt coupler, as the station coordinating it sees it. */
export interface T1SBusStatus {
    /** What the medium is: "UDP multicast 239.151.18.1:2354" or "AF_PACKET on eth1". */
    medium:         string;
    mac:            string;
    cycle:          number;
    /** Frames that arrived outside their sender's turn: a fault, or a node that is not ours. */
    outOfTurn:      number;
    /** Nodes that asked to join in the same opportunity. */
    collisions:     number;
    thermal: {
        state:      string;
        alarm:      boolean;
        warningC:   number;
        overloadC:  number;
    };
    nodes:          T1SNodeStatus[];
}

/** What this station offers a vehicle below the charging cable. */
export interface V2GConfiguration {
    enabled:         boolean;
    /** Whether the SECC Discovery Protocol answers vehicles. */
    sdp:             boolean;
    /** Whether it also answers a vehicle running on this same machine. */
    loopback:        boolean;
    /** The powerline interface, or null to let the station pick one. */
    interface:       string | null;
    /** 0 lets the system pick a port, which is what SDP then advertises. */
    port:            number;
    evseId:          string;
    slac:            string;
    /** Which medium the coupler's bus is on: none, auto, afpacket or udp. */
    t1sTransport:    string;
    /** The group and port of the emulated medium, or null for the library's default. */
    t1sBus:          string | null;
    t1sInterface:    string | null;
    t1sName:         string | null;
    t1sCycleMs:      number;
    t1sWarningC:     number;
    t1sOverloadC:    number;
    /** The group the emulation uses when none is named, for the placeholder. */
    t1sDefaultBus:   string;
    /**
     * Whether a V2G server certificate was passed on the command line. Not
     * settable from the page - a certificate is a file and a password - but it
     * is what decides whether the endpoint speaks TLS.
     */
    certificate:     boolean;
    /** The transports this station knows, for the picker. */
    slacTransports:  string[];
    /** The same, for the bus below a megawatt coupler. */
    t1sTransports:   string[];
    /** Whether the station has been started; nothing comes up before that. */
    running:         boolean;
    link:            V2GLinkStatus | null;
    file:            string;
}

/** What a PUT to the V2G configuration may carry; everything is optional. */
export interface V2GUpdate {
    enabled?:    boolean;
    sdp?:        boolean;
    loopback?:   boolean;
    interface?:  string | null;
    port?:       number;
    evseId?:     string;
    slac?:       string;
    t1sTransport?:   string;
    t1sBus?:         string | null;
    t1sInterface?:   string | null;
    t1sName?:        string | null;
    t1sCycleMs?:     number;
    t1sWarningC?:    number;
    t1sOverloadC?:   number;
}


/** What this station may draw from the grid, and what it could deliver. */
export interface PowerConfiguration {
    /** The most the whole station may draw; null when nobody has said. */
    uplinkPowerLimit_kW:  number | null;
    evses:                { id: number; maxPower_kW: number }[];
    /** What the EVSEs could draw together, which may legitimately be more. */
    evsesTotal_kW:        number;
    limits:               { maxUplinkPowerLimit_kW: number; maxEVSEPowerLimit_kW: number };
    file:                 string;
}

/**
 * When the screen on the front of the station is dim, and how dim.
 *
 * Both ends of the window or neither: one end is not a window. Times are
 * written the way a person writes them - "22:00" - in the station's own local
 * time, and a window that crosses midnight is the ordinary case rather than a
 * special one.
 */
export interface DisplayConfiguration {
    /** When the quiet hours begin, or null when this station keeps none. */
    dimFrom:    string | null;
    /** When they end. Earlier than dimFrom means they cross midnight. */
    dimUntil:   string | null;
    /** How bright the screen is while nothing is happening, or null for the default. */
    dimTo:      number | null;
    /** Whether it is one of them at this moment, as the station reckons it. */
    quietNow:   boolean;
    limits:     { darkestDimTo: number; defaultDimTo: number };
    file:       string;
}

/** What a PUT to the power configuration carries; null takes the limit away. */
export interface PowerUpdate {
    uplinkPowerLimit_kW:  number | null;
}


/**
 * One calibration certificate this station runs under.
 *
 * Everything below the PEM was read out of it rather than typed: an issuer
 * somebody types can disagree with the certificate it was typed from, and then
 * there is no telling which of the two the station means.
 */
export interface CalibrationCertificate {
    /** What it is called here - the name it is changed and removed by. */
    id:                 string;
    description:        string | null;
    pem:                string;
    subject:            string;
    issuer:             string;
    serialNumber:       string;
    notBefore:          string;
    notAfter:           string;
    thumbprintSHA256:   string;
    expired:            boolean;
    notYetValid:        boolean;
    /** Negative once it has run out. */
    daysLeft:           number;
}

/** The calibration certificates of this station. */
export interface CalibrationConfiguration {
    certificates:  CalibrationCertificate[];
    limits: {
        maxCertificates:       number;
        maxIdLength:           number;
        maxDescriptionLength:  number;
        maxPEMLength:          number;
        expiryWarningDays:     number;
    };
    file:          string;
}

/** One card reader this station has, and where it sits. */
export interface RFIDReader {
    id:       string;
    kind:     string;
    /** Which EVSE it belongs to, or null when it serves the whole station. */
    evse:     number | null;
    enabled:  boolean;
    /** Whether this station has a driver for this kind of reader at all. */
    hasDriver?:  boolean;
    /** Whether its cards are typed into the display rather than held against it. */
    fake?:       boolean;
}

/** The card readers of this station. */
export interface RFIDConfiguration {
    readers:     RFIDReader[];
    evses:       { id: number; label: string | null }[];
    /** The kinds this station names itself. Not a closed list. */
    kinds:       string[];
    /** The one kind whose cards are typed in. */
    fakeKind:    string;
    maxReaders:  number;
    file:        string;
}

/**
 * One set of credentials this station can prove itself with.
 *
 * The secret half is never in here. `hasSecret` is what takes its place, so a
 * page can tell "not configured yet" from "configured, and you are not being
 * shown it".
 */
export interface StationLogin {
    id:                  string;
    /** What somebody wrote down that it is for, e.g. "CSMS login". */
    description:         string;
    kind:                'basic' | 'totp';
    /** The name the other end knows this station by. */
    login:               string;
    createdAt:           string;
    hasSecret:           boolean;
    /** Whether the one-time password is bound to the TLS session. TOTP only. */
    tlsChannelBinding?:  boolean;
    validitySeconds?:    number;
    length?:             number;
    alphabet?:           string;
    hashAlgorithm?:      string;
}

/** What goes in when credentials are written down or changed. */
export interface LoginToSave {
    id?:                 string;
    description:         string;
    kind:                'basic' | 'totp';
    login:               string;
    /** Empty means "keep whatever is already there". */
    secret:              string;
    validitySeconds?:    number;
    length?:             number;
    alphabet?:           string;
    hashAlgorithm?:      string;
    tlsChannelBinding?:  boolean;
}

/** One place this station dials. */
export interface StationConnection {
    id:                  string;
    description:         string;
    url:                 string;
    connectionType:      string;
    /** Which OCPP is spoken here, and so which of this station's two nodes dials. */
    ocppVersion:         string;
    autoConnect:  boolean;
    /** Whether the URL makes a TLS connection, which decides what the rest can mean. */
    secure:              boolean;
    createdAt:           string;
    authenticationId?:   string;
    certificateId?:      string;
    warnings?:           string[];
}

/** What goes in when a connection is written down or changed. */
export interface ConnectionToSave {
    id?:                 string;
    description:         string;
    url:                 string;
    connectionType:      string;
    ocppVersion:         string;
    autoConnect:  boolean;
    authenticationId:    string | null;
    certificateId:       string | null;
}

/** One line of what happened while a connection was being tested. */
export interface ConnectionTestStep {
    /** Milliseconds since the test started. */
    at_ms:  number;
    level:  'info' | 'notice' | 'warning' | 'error';
    text:   string;
}

/** What came of testing one connection. */
export interface ConnectionTest {
    description:  string;
    url:          string;
    ok:           boolean;
    runtime_ms:   number;
    steps:        ConnectionTestStep[];
}

/**
 * Where a connection this station dialled stands.
 *
 * Three kinds of "not connected", because they ask three different things of
 * whoever is looking: lost and trying are waited out, the station comes back by
 * itself; refused needs the other end fixed; not dialled and failed need this
 * end fixed.
 */
export type ConnectionStatus = 'connected' | 'lost' | 'trying' | 'refused' | 'notDialled' | 'failed';

/** What became of one connection this station dialled, and since when. */
export interface ConnectionState {
    status:          ConnectionStatus;
    since:           string;
    /** The sentence the station logged when it came to stand there. */
    said:            string;
    /** What was dialled - which is what is written down now only until something is changed. */
    description:     string;
    url:             string;
    ocppVersion:     string;
    /** While it is tried again: which attempt comes next, and when. */
    attempt?:        number;
    nextAttemptAt?:  string;
}

/** Where every connection this station dialled stands, and what time it is there. */
export interface ConnectionStates {
    /** The station's own clock, which is what "since" and "next" are counted from. */
    timestamp:  string;
    states:     Record<string, ConnectionState>;
}

/** A client certificate, as far as a connection is concerned. */
export interface ConnectionCertificate {
    id:                  string;
    subject:             string;
    algorithm:           string;
    hasCertificate:      boolean;
    canBeHeldUp:         boolean;
}

/**
 * Everything the two pages work from.
 *
 * One shape for both, from one handler on the station: the Connections page
 * needs the credentials in order to offer them, and the Authentication page
 * needs the connections in order to say which ones a removal would break.
 */
export interface StationConnections extends ConnectionStates {
    directory:             string;
    authentications:       StationLogin[];
    connections:           StationConnection[];
    certificates:          ConnectionCertificate[];
    connectionTypes:       string[];
    ocppVersions:          string[];
    maxDescriptionLength:  number;
    minSharedSecretLength: number;
    /** Always false, and said out loud: a secret is written here and never read back. */
    secretsAreReadable:    boolean;
    totpDefaults: {
        validitySeconds:   number;
        length:            number;
        alphabet:          string;
        hashAlgorithm:     string;
    };
}

/** One kind of key this station will make for itself. */
export interface KeyAlgorithm {
    /** How it is written in the API, e.g. "ed448". */
    id:       string;
    /** How it is written on a page, e.g. "Ed448". */
    name:     string;
    /** What somebody choosing it should know. */
    remark:   string;
}

/** One key of this station, its signing request, and its certificate if it has one. */
export interface StationKey {
    id:            string;
    algorithm:     string;
    createdAt:     string;
    subject:       string;
    /** Whether this is the one the station would hold up when it dials. */
    inUse:         boolean;
    /**
     * Whether this station can load the certificate together with its key at
     * all. False is about the platform and not about the certificate: .NET has
     * no key object for an Ed448 or an ML-DSA key today.
     */
    canBeHeldUp:   boolean;
    cannotBeHeldUp?: string;
    /** The request waiting to be collected, while there is no certificate yet. */
    csr?:          string;
    certificate?: {
        subject:           string;
        issuer:            string;
        serialNumber:      string;
        notBefore:         string;
        notAfter:          string;
        thumbprintSHA256:  string;
        /** How many were sent along between it and a root. */
        intermediates:     number;
        /** Negative once it has run out. */
        daysLeft:          number;
        expired:           boolean;
        notYetValid:       boolean;
    };
    warnings?:     string[];
}

/** The keys and certificates this station dials a back end with. */
export interface StationCertificates {
    directory:             string;
    /** What the station thinks the time is, so a page does not guess which clock the days are counted by. */
    now:                   string;
    inUseId:               string | null;
    entries:               StationKey[];
    algorithms:            KeyAlgorithm[];
    defaultAlgorithm:      string;
    maxSubjectLength:      number;
    /** Always false, and said out loud: a key that arrived from elsewhere is one somebody else has a copy of. */
    canImportPrivateKeys:  boolean;
}

/** What a certificate looks like on the way in: the rest is read out of the PEM. */
export interface CalibrationCertificateUpdate {
    id:            string;
    description?:  string | null;
    pem:           string;
}


/** The routes every node has, typed with what a charging station says its own of them are. */
const node = nodeAPI<{ me: Me; status: Status; configuration: Configuration; kind: CertificateKind; store: CertificateStore }>();

export const api = {

    ...node,

    display: {
        get:   ()                              => request<DisplayConfiguration>('GET', '/configuration/display'),
        // The whole section at once, because its fields are not independent -
        // and an empty object is how dimming is turned off.
        save:  (update: Partial<DisplayConfiguration>) => request<DisplayConfiguration>('PUT', '/configuration/display', update)
    },

    v2g: {
        get:   ()                    => request<V2GConfiguration>('GET', '/configuration/v2g'),
        /**
         * Takes the link down and brings it up again, so it answers later than
         * the other configuration calls do - and a vehicle in the middle of a
         * SLAC match goes down with it.
         */
        save:  (update: V2GUpdate)   => request<V2GConfiguration>('PUT', '/configuration/v2g', update, 30_000)
    },

    power: {
        get:   ()                      => request<PowerConfiguration>('GET', '/configuration/power'),
        /** null takes the limit away rather than setting it to nothing. */
        save:  (update: PowerUpdate)   => request<PowerConfiguration>('PUT', '/configuration/power', update)
    },

    evses: {
        get:   ()               => request<EVSEConfiguration>('GET', '/configuration/evses'),
        /**
         * All of them at once: they are only valid together.
         *
         * Which permission this needs depends on what actually changed, and the
         * station works that out by comparing what it is sent with what it has
         * - so a 403 here can arrive for a request that a moment ago would have
         * gone through.
         */
        save:  (evses: EVSE[])  => request<EVSEConfiguration>('PUT', '/configuration/evses', { evses })
    },

    rfid: {
        get:   ()                        => request<RFIDConfiguration>('GET', '/configuration/rfid'),
        /**
         * All of them at once. Which permission this needs depends on what
         * changed - moving a reader is not the same statement as switching one
         * off - and the station works that out by comparing.
         */
        save:  (readers: RFIDReader[])   => request<RFIDConfiguration>('PUT', '/configuration/rfid', { readers })
    },

    authentications: {

        get:     ()                  => request<StationConnections>('GET', '/configuration/authentications'),

        add:     (entry: LoginToSave) =>
                     request<{ id: string; connections: StationConnections }>(
                         'POST', '/configuration/authentications', entry),

        /** A secret left empty keeps the one already there. */
        update:  (entry: LoginToSave) =>
                     request<StationConnections>('POST', '/configuration/authentications/update', entry),

        /** Refused while a connection is using it, and the refusal names it. */
        remove:  (id: string) =>
                     request<StationConnections>('POST', '/configuration/authentications/remove', { id })

    },

    connections: {

        get:     ()                       => request<StationConnections>('GET', '/configuration/connections'),

        add:     (entry: ConnectionToSave) =>
                     request<{ id: string; connections: StationConnections }>(
                         'POST', '/configuration/connections', entry),

        update:  (entry: ConnectionToSave) =>
                     request<StationConnections>('POST', '/configuration/connections/update', entry),

        remove:  (id: string) =>
                     request<StationConnections>('POST', '/configuration/connections/remove', { id }),

        /**
         * Where each connection this station dialled stands - asked again and
         * again while the page is open, and therefore only that, rather than
         * everything the page was drawn from.
         */
        states:  ()  => request<ConnectionStates>('GET', '/status/connections'),

        /**
         * Make this connection once, and say everything that happened.
         *
         * Takes the fields rather than an identification, because the page
         * offers this beside a connection being written down for the first
         * time as well as beside one that exists - and both times what
         * somebody means is "test what is on the screen". Nothing is stored.
         *
         * Slower than the other writes on purpose: the station stays connected
         * for about two seconds to see whether anything is said, so the
         * deadline has to cover the connection plus that.
         */
        test:    (entry: ConnectionToSave) =>
                     request<ConnectionTest>('POST', '/configuration/connections/test', entry,
                                             afterAsking([ 2 ]))

    },

    /** The keys this station dials its back ends with, made here and never imported. */
    clientKeys: {

        get:     ()  => request<StationCertificates>('GET', '/configuration/certificates'),

        /**
         * A new key and the signing request to be handed to whoever issues
         * certificates for this station.
         *
         * Slower than the other writes by a wide margin - an RSA 4096 or an
         * SLH-DSA key takes seconds to generate, and the deadline for a write
         * covers it.
         */
        create:  (subject: string, algorithm: string) =>
                     request<{ id: string; csr: string; certificates: StationCertificates }>(
                         'POST', '/configuration/certificates', { subject, algorithm }),

        /** The certificate that came back, and whatever intermediates came with it. */
        add:     (pem: string) =>
                     request<{ id: string; warnings: string[]; certificates: StationCertificates }>(
                         'POST', '/configuration/certificates/import', { pem }),

        /** The identification travels in the body, as it does everywhere else in this API. */
        remove:  (id: string) =>
                     request<StationCertificates>('POST', '/configuration/certificates/remove', { id })

    },

    calibration: {
        get:   ()                                             => request<CalibrationConfiguration>('GET', '/configuration/calibration'),
        /** All of them at once: what a station is certified for is one statement. */
        save:  (certificates: CalibrationCertificateUpdate[]) => request<CalibrationConfiguration>('PUT', '/configuration/calibration', { certificates })
    }

};
