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
         type Operation,
         type KnownServer,
         type PinKeys,
         type ServerJudgement,
         type ServerPins }  from '@node/api/client';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module as before. What follows is what a charging
// station adds: its resources, what its configuration says beyond every
// node's, the kinds its store keeps, and its own routes.
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this charging station: the node's
 * resources, and the station's sixteen - see StationAccess.
 */
export type Resource = NodeResource
                     | 'evses' | 'rfid' | 'availability' | 'power' | 'calibration'
                     | 'display' | 'session' | 'connections' | 'v2g' | 'meters' | 'providers'
                     | 'nameplate' | 'commissioning' | 'unlock' | 'monitoring' | 'authorization';

/** What somebody signed in to this station may do: an operation on a resource, written "dns:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the station is doing right now: what every node says, and nothing beyond it. */
export type Status = NodeStatus;

/**
 * What the station is made of: every node's sections, and its own. Only the
 * shape the Configuration page relies on is named; the fields of each section
 * are rendered from whatever the station sends, and which sections there are
 * is the page's to say.
 */
export interface Configuration extends NodeConfiguration {
    station:     Record<string, unknown>;
    /** The link below the cable, as V2GLink.ToJSON() says it - or { enabled: false } where there is none. */
    v2g:         Record<string, unknown>;
    ocpp:        Record<string, unknown>[];
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

/** Where a SunSpec meter is read: a host, Modbus/TLS's port 802 and unit 1 by default. */
export interface MeterEndpoint {
    host:  string;
    port:  number;
    unit:  number;
}

/** Three values of one quantity: one for each phase, and the one for them all. */
export interface PhaseValues {
    all:  number | null;
    L1:   number | null;
    L2:   number | null;
    L3:   number | null;
}

/** What a SunSpec meter said when it was last read. */
export interface MeterReading {
    at:            string;
    manufacturer:  string;
    model:         string;
    version:       string;
    serialNumber:  string;
    /** The SunSpec meter model it was read by: 201 to 204, 211 to 214. */
    sunSpecModel?: number;
    power_W:       PhaseValues;
    current_A:     PhaseValues;
    voltage_V:     PhaseValues;
    frequency_Hz:  number | null;
    imported_Wh:   number | null;
    exported_Wh:   number | null;
}

/** A TLS identity of the store, with the SunSpec role in its certificate, as a meter is shown it. */
export interface MeterIdentity {
    id:        string;
    label:     string;
    role:      string;
    subject:   string;
    notAfter?: string;
    usable?:   boolean;
}

/**
 * How the meter of one EVSE that is to be read stands: whether it is, over
 * what, and what it measures - as GET /status/meters and each "meters" event
 * say it.
 */
export interface MeterStatus {
    evse:         number;
    label:        string;
    endpoint:     MeterEndpoint;
    /** "reading", or what is in the way: "connecting", "noIdentity", "untrusted", "refused", "unreachable", "notSunSpec", "failed" - or "notStarted" before the station is. */
    state:        string;
    since?:       string;
    error?:       string | null;
    connection?:  {
        protocol:     string;
        cipherSuite:  string;
        meter:        CertificateSummary | null;
        identity:     MeterIdentity;
        judged:       string | null;
        /** How the meter describes itself: where its registers begin, the models it has, and the one it is read by. */
        sunSpec?:     { base: number; models: { id: number; address: number; length: number }[]; meter: number };
    } | null;
    reading?:     MeterReading | null;
    /** Where the latest session at its EVSE stands with it, where it signs sessions. */
    signed?:      { id: string; state: SignedSessionState; verification: string | null } | null;
}

/** Where a charging session stands with the meter that signs it. */
export type SignedSessionState = 'starting' | 'running' | 'stopping' | 'signed' | 'unsigned';

/** A charging session as the meter of its EVSE signs it, as GET /meters/sessions says it. */
export interface SignedSession {
    id:                   string;
    evse:                 number;
    startedAt:            string;
    state:                SignedSessionState;
    identificationType?:  string;
    identification?:      string;
    evseId?:              string;
    tariffText?:          string;
    stoppedAt?:           string;
    /** Why it is not signed, or not yet. */
    error?:               string;
    startValue?:          number;
    stopValue?:           number;
    energy_kWh?:          number;
    /** What ChargyCore said of the document and the key: "ValidSignature", or what is wrong. */
    verification?:        string;
    ocmf?:                string;
    publicKey?:           string;
}

/** The signed sessions, the newest first. */
export interface SignedSessions {
    timestamp:  string;
    sessions:   SignedSession[];
}

/** Every meter that is to be read, with the time at the station where it was asked. */
export interface MeterStates {
    timestamp?:  string;
    meters:      MeterStatus[];
}

/** How the meter of one EVSE is configured to be read, and what it was last believed with. */
export interface MeterLink {
    evse:        number;
    label:       string;
    /** Whether it is read at all. */
    read:        boolean;
    host:        string | null;
    port:        number | null;
    unit:        number | null;
    /** How the node's judgements name it: "10.0.0.5:802". */
    server:      string | null;
    /** The ids of the identities shown to it to read and to write; null for the least that may read, and for writing nothing. */
    identities:  { read: string | null; write: string | null };
    /** The tariff its sessions are signed with, OCMF "TT"; null for none. */
    tariffText:  string | null;
    /** The EVSE its sessions are signed at, OCMF "CI"; null for none. */
    evseId:      string | null;
    heldTo:      ServerPins | null;
    judgement:   ServerJudgement | null;
    known:       KnownServer | null;
}

/** The meters page's: each EVSE's meter, and the identities a meter can be shown. */
export interface MetersConfiguration {
    meters:      MeterLink[];
    identities:  MeterIdentity[];
    /** What a TLS root of the store is for, where it vouches for meters alone. */
    usage:       string;
    defaults:    { port: number; unit: number };
    running:     boolean;
    file:        string;
}

/** A change to how one EVSE's meter is read; a blank host reads it nowhere. */
export interface MeterUpdate extends PinKeys {
    evse:          number;
    host:          string;
    port:          number | null;
    unit:          number | null;
    identities:    { read: string | null; write: string | null };
    tariffText:    string | null;
    evseId:        string | null;
    pinsAsShown?:  PinKeys;
}

/** One Modbus request, as the Modbus client asks it. */
export interface ModbusAsk {
    evse:      number;
    /** 3 and 4 read, 6 and 16 write. */
    function:  number;
    address:   number;
    count?:    number;
    value?:    number;
    values?:   number[];
}

/** What came back, down to the bytes. */
export interface ModbusAnswer {
    at:          string;
    endpoint:    MeterEndpoint;
    function:    number;
    name:        string;
    operation:   'read' | 'write';
    address:     number;
    count:       number;
    /** The PDU sent, in hexadecimal. */
    request:     string;
    /** "answered", "exception", or what was in the way: "noIdentity", "untrusted", "refused", "failed". */
    outcome:     string;
    identity?:   MeterIdentity;
    tls?:        { protocol: string; cipherSuite: string; meter: CertificateSummary | null; judged: string | null };
    /** The PDU that came back, in hexadecimal. */
    response?:   string;
    exception?:  { code: number; name: string };
    registers?:  { address: number; value: number }[];
    error?:      string;
    ms?:         number;
}

/** Whether an EVSE is free, charging, held or out of service. */
export type EVSEState = 'available' | 'occupied' | 'reserved' | 'inoperative';

/** A certificate as somebody checks one. */
export interface CertificateSummary {
    subject:    string;
    issuer:     string;
    notBefore:  string;
    notAfter:   string;
    sha256:     string;
}

/** What the V2G link below one EVSE is doing right now. */
export interface V2GLiveStatus {
    /** Whether the link it is to have is up. */
    up:            boolean;
    interface?:    string | null;
    endpoint?:     string | null;
    tls?:          boolean;
    /** The station's certificate, which the endpoint shows where it speaks TLS. */
    certificate?:  CertificateSummary | null;
    sdp?:          boolean;
    slac?:         boolean;
    evseId?:       string;
    mode?:         string;
    /** The last time a vehicle asked SDP where the endpoint is, and what it was told. */
    lastSDP?:      {
        at:            string;
        from:          string;
        version:       string;
        security:      string;
        transport:     string;
        accepted:      boolean;
        rejectReason:  string | null;
        answer:        { address: string; port: number; security: string } | null;
    } | null;
    /** The vehicle on the endpoint now. */
    connection?:   {
        since:     string;
        from:      string | null;
        /** "ISO 15118-2" or "ISO 15118-20", once the handshake agreed on one. */
        protocol:  string | null;
        mode:      string | null;
        tls:       { protocol: string; cipherSuite: string | null; library: string; vehicle: CertificateSummary | null } | null;
    } | null;
    /** How the last session ended. */
    lastSession?:  { endedAt: string; protocol: string; seconds: number; paused: boolean } | null;
}

/** How one EVSE stands right now, as the EVSEs page shows it. */
export interface EVSEStatus {
    id:                 number;
    /** Its physical reference, or its id where it has none. */
    label:              string;
    physicalReference:  string | null;
    status:             EVSEState;
    operative:          boolean;
    /** Charging now, and out of service once that ends. */
    closing:            boolean;
    maxPower_kW:        number;
    connectors:         Connector[];
    session:            { method: string; startedAt: string; provider: { name: string; logo: string | null } | null } | null;
    reservation:        { until: string; minutesLeft: number } | null;
    /** What is going on below its cable; null where it is to have no V2G link. */
    v2g:                V2GLiveStatus | null;
    /** What is written down about its meter, and whether it is read - what it measures is a "meters" event's. */
    meter:              { type: string | null; serialNumber: string | null; read: boolean } | null;
}

/**
 * Every EVSE of this station, as GET /status/evses answers - with the time at
 * the station - and as each "evses" event on the stream says it, without.
 */
export interface EVSEStates {
    timestamp?:  string;
    evses:       EVSEStatus[];
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

/** What one EVSE of this station offers a vehicle below its charging cable. */
export interface V2GLinkConfiguration {
    /** The EVSE it is below, by its id. */
    evse:            number;
    /** Its physical reference, or its id where it has none. */
    label:           string;
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
    /** Whether the outlet behind the socket is AC or DC. */
    mode:            'ac' | 'dc';
    /** What came up, or null where nothing did. */
    link:            V2GLinkStatus | null;
}

/** What each EVSE of this station offers a vehicle below its charging cable. */
export interface V2GConfiguration {
    /**
     * Whether a V2G server certificate was passed on the command line. Not
     * settable from the page - a certificate is a file and a password - but it
     * is what decides whether an endpoint speaks TLS. The station's, and so the
     * same below every cable.
     */
    certificate:     boolean;
    /** The transports this station knows, for the picker. */
    slacTransports:  string[];
    /** The same, for the bus below a megawatt coupler. */
    t1sTransports:   string[];
    /** The group the emulation uses when none is named, for the placeholder. */
    t1sDefaultBus:   string;
    /** Whether the station has been started; nothing comes up before that. */
    running:         boolean;
    /** One for each EVSE, whether it is to have a link or not. */
    links:           V2GLinkConfiguration[];
    file:            string;
}

/** What a PUT to the V2G configuration may carry: the EVSE, and what is to change of its link. */
export interface V2GUpdate {
    /** The EVSE whose link this changes. */
    evse:        number;
    enabled?:    boolean;
    mode?:       'ac' | 'dc';
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
    /** Whether the picture walks against burn-in. Off unless it was switched on. */
    keepMoving:        boolean;
    /** The display's port as the file says it, or null for the default. */
    port:              number | null;
    /** Where the display is now, or null for a station started without one. */
    portInUse:         number | null;
    /** What --kiosk-port said at this start, which a start puts it on whatever the file says. */
    portGivenAtStart:  number | null;
    defaultPort:       number;
    /** The display's address now, or null without one. */
    url:               string | null;
    /** How long a port the display moved away from still tells screens where it went. */
    handoverSeconds:   number;
    file:       string;
}

/**
 * What a PUT to the display configuration carries: the whole section, because
 * it replaces the whole section. A null is the default - no hours, a picture
 * standing still, the port a start would give it.
 */
export interface DisplayUpdate {
    dimFrom:     string | null;
    dimUntil:    string | null;
    dimTo:       number | null;
    keepMoving:  true | null;
    port:        number | null;
}

/** What a PUT to the power configuration carries; null takes the limit away. */
export interface PowerUpdate {
    uplinkPowerLimit_kW:  number | null;
}

/**
 * Where one OCPP node of the station stands with its CSMS: not asked yet,
 * asking, accepted, told to wait - Pending or Rejected - or not answered.
 */
export interface Registration {
    state:     'NotAsked' | 'Asking' | 'Accepted' | 'Pending' | 'Rejected' | 'Unanswered';
    /** When the CSMS last answered. */
    since:     string | null;
    /** Accepted, how often a heartbeat goes, in seconds; otherwise how long until it asks again. */
    interval:  number | null;
    /** When it asks again, where it does. */
    nextTry:   string | null;
    /** Why the last BootNotification got no answer. */
    problem:   string | null;
}

/**
 * The station's nameplate: what it tells a CSMS it is in its BootNotification,
 * as the file says it - null where the file says nothing - and as it is said,
 * the defaults filled in. Each field is held to what both OCPP 1.6 and 2.1
 * take, and to printable ASCII.
 */
export interface NameplateConfiguration {
    vendor:           string | null;
    model:            string | null;
    serialNumber:     string | null;
    firmwareVersion:  string | null;
    said:             { vendor: string; model: string; serialNumber: string | null; firmwareVersion: string };
    limits:           { vendor: number; model: number; serialNumber: number; firmwareVersion: number };
    registration:     { '1.6': Registration; '2.1': Registration };
    file:             string;
}

/** What a PUT to the nameplate carries: the whole section, a null being the default. */
export interface NameplateUpdate {
    vendor:           string | null;
    model:            string | null;
    serialNumber:     string | null;
    firmwareVersion:  string | null;
}


/**
 * Commissioning over SSH: a tool speaking OCPP 2.1 to this station on its own
 * SSH server, as an account that may run it - under the account's name, never
 * as the CSMS.
 */
export interface CommissioningConfiguration {
    /** What the file says: switched on or not. */
    enabled:       boolean;
    /** Whether the subsystem is on the SSH server now. */
    served:        boolean;
    /** Whether the node's SSH server is on at all. */
    sshServer:     boolean;
    /** Where a tool connects, as its account; null without an SSH server. */
    url:           string | null;
    /** The SSH server's host key, "ssh-ed25519 SHA256:...", for the tool to pin. */
    hostKey:       string | null;
    /** "ocpp@open.charging.cloud". */
    subsystem:     string;
    /** The OCPP versions spoken, "ocpp2.1" first. */
    subprotocols:  string[];
    /** The OCPP node a tool speaks to, "test02". */
    station:       string;
    /** What an account needs, "commissioning:run". */
    permission:    string;
    /** The tools connected now, by the names of their accounts. */
    tools:         string[];
    file:          string;
}

/** What a PUT to commissioning carries. */
export interface CommissioningUpdate {
    enabled:  boolean;
}


/** A key that signs as a role. */
export interface SignerKey {
    /** The RFC 7638 thumbprint of the key. */
    kid:         string;
    label?:      string;
    notBefore?:  string;
    notAfter?:   string;
}

/** A role of the station, and the keys that sign as it. */
export interface SignerRole {
    role:          string;
    description?:  string;
    /** What the role may do, "resource:operation". */
    permissions:   string[];
    /** The version of the role assignment that is stored; 0 for a role that never had keys. */
    version:       number;
    keys:          SignerKey[];
    lastUsed?:     string;
    /** The role assignment as it was received, with its signatures. */
    assignment?:   Record<string, unknown>;
}

/** How the station holds what it receives to its signature policy. */
export interface SignatureSettings {
    /** "Off", "Observe" or "Enforce". */
    mode:                    string;
    acceptFormat0:           boolean;
    /** Seconds. */
    maxClockSkew:            number;
    /** Seconds. */
    maxSignatureAge:         number;
    allowFirstOwnerByCSMS:   boolean;
    allowPermissionsByCSMS:  boolean;
    /** How many owners have to sign a role assignment or a policy table. */
    quorum:                  number;
}

/** What a PUT to the signature settings carries: what changes. */
export type SignatureSettingsUpdate = Partial<SignatureSettings>;

/**
 * Signed messages: the settings of the signature policy, the policy table the
 * station was given, and its roles with the keys that sign as them.
 */
export interface Signatures {
    settings:       SignatureSettings;
    /** The signatures the station verifies, "ES256". */
    algorithms:     string[];
    hasOwner:       boolean;
    /** "systemadmin". */
    ownerRole:      string;
    policyVersion:  number;
    /** The policy table as it was received, with its signatures. */
    policy?:        Record<string, unknown>;
    roles:          SignerRole[];
    /** Why the last change could not be written down. */
    notSaved?:      string;
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
    /** Over an HTTP WebSocket, or over the SSH subsystem ocpp@open.charging.cloud. */
    transport?:          'websocket' | 'ssh';
    createdAt:           string;
    authenticationId?:   string;
    certificateId?:      string;
    /** The host keys the server of an ssh:// connection may show, separated by commas; none where nothing is pinned. */
    sshHostKey?:         string;
    /** Whether an ssh:// connection that pins nothing learns the first host key shown, on probation. */
    sshTrustOnFirstUse?: 'None' | 'HostKey';
    /** What a host key that matches no pin leads to. */
    sshOnPinMismatch?:   'Refuse' | 'Record' | 'Accept';
    /** How long a learned host key waits for a signed pin set, in seconds. */
    sshProbationTime?:   number;
    /** The version of the signed pin set the pins came from. */
    sshHostKeysVersion?: number;
    /** The host key learned on the first connection, while it is on probation. */
    sshLearnedHostKey?:  string;
    sshLearnedAt?:       string;
    /**
     * Whether the server of a TLS connection is known by the key pin of its key
     * rather than by a CA: it has key pins, or learns one on first use. Only
     * for wss:// and https://.
     */
    keyPinned?:                  boolean;
    /** The key pins of the keys its server may show: 64 lowercase hexadecimal digits each, at most four. */
    serverKeyPins?:              string[];
    /** The version of the signed pin set the key pins came from. */
    serverKeyPinsVersion?:       number;
    /** Where the key pins come from: written down, learned and on probation, or learned and confirmed since. */
    serverKeyPinState?:          'Configured' | 'Learned' | 'Confirmed';
    /** Whether raw public keys (RFC 7250) are offered; this station's TLS stack cannot offer them yet. */
    rawPublicKeys?:              'Off' | 'Prefer' | 'Require';
    /** Whether a wss:// connection that pins no key learns the key pin of the first one shown, on probation. */
    trustOnFirstUse?:            'None' | 'Key';
    /** How long a learned key pin waits for a signed pin set, in seconds. */
    serverKeyPinProbationTime?:  number;
    /** The key pin learned on the first connection, while it is on probation. */
    serverKeyPinLearned?:        string;
    serverKeyPinLearnedAt?:      string;
    /**
     * What the server of a wss:// connection checked by its CA is held to
     * beside that, in the keys a time server, a name server and a meter are
     * held to something with. Never beside key pins.
     */
    pins?:                       PinKeys;
    /** The version of the signed pin set the roots came from. */
    pinsVersion?:                number;
    /** What the station made of the certificate its server showed last, where it is checked by its CA. */
    judgement?:                  ServerJudgement | null;
    /**
     * The version of HTTP its WebSocket is opened over, where it names one:
     * "1.1", "2" or "3", and that alone. Left out, it is "auto": the station
     * offers HTTP/2 and HTTP/1.1 and the server chooses, and goes over
     * HTTP/3 once the server said it is reached over it.
     */
    httpVersion?:                HTTPVersionOfAConnection;
    /** The version of HTTP the WebSocket it has now was opened over. */
    httpVersionInUse?:           '1.1' | '2' | '3';
    /**
     * How its OCPP messages are written, where it is not JSON: "cbor" offers
     * the CBOR subprotocol of its OCPP version and nothing else, "auto"
     * offers it first and takes what the back end chooses. Left out, it is "json".
     */
    encoding?:                   EncodingOfAConnection;
    /** The subprotocol the connection it has now agreed on, over a WebSocket or over ssh://: "ocpp2.1", "ocpp2.1cbor". */
    subprotocolInUse?:           string;
    /**
     * Which signatures the messages the station signs for it get: "jws",
     * "cose" or "both". Left out, the signature policy of the station decides.
     */
    signatureFormats?:           SignatureFormatsOfAConnection;
    warnings?:           string[];
}

/** The version of HTTP a ws:// or wss:// connection opens its WebSocket over. */
export type HTTPVersionOfAConnection = 'auto' | '1.1' | '2' | '3';

/** How the OCPP messages of a connection are written. */
export type EncodingOfAConnection = 'json' | 'cbor' | 'auto';

/** Which signatures a connection writes; "policy" is what the signature policy of the station says. */
export type SignatureFormatsOfAConnection = 'policy' | 'jws' | 'cose' | 'both';

/** What goes in when a connection is written down or changed. */
export interface ConnectionToSave {
    /** The version of HTTP its WebSocket is opened over; left out, the station keeps the one it has. */
    httpVersion?:        HTTPVersionOfAConnection;
    /** How its OCPP messages are written; left out, the station keeps what it says. */
    encoding?:           EncodingOfAConnection;
    /** Which signatures it writes; left out, the station keeps what it says, and "policy" is saying nothing. */
    signatureFormats?:   SignatureFormatsOfAConnection;
    id?:                 string;
    description:         string;
    url:                 string;
    connectionType:      string;
    ocppVersion:         string;
    autoConnect:  boolean;
    authenticationId:    string | null;
    certificateId:       string | null;
    /** The host keys typed, separated by commas: "" for none. */
    sshHostKey?:         string;
    sshTrustOnFirstUse?: 'None' | 'HostKey';
    sshOnPinMismatch?:   'Refuse' | 'Record' | 'Accept';
    sshProbationTime?:   number;
    /**
     * The host keys the page showed when it was drawn: where those typed are
     * still those, the station keeps the ones it has now - taken from a signed
     * pin set meanwhile, perhaps.
     */
    sshHostKeyAsShown?:  string;
    /** The key pins typed, one each: none for none. */
    serverKeyPins?:              string[];
    /** The key pins the page showed when it was drawn, as for the host keys. */
    serverKeyPinsAsShown?:       string[];
    rawPublicKeys?:              'Off' | 'Prefer' | 'Require';
    trustOnFirstUse?:            'None' | 'Key';
    serverKeyPinProbationTime?:  number;
    /** What its server is to be held to; nothing in it for nothing. */
    pins?:                       PinKeys;
    /** What the page showed it held to when it was drawn: what was learned since, or taken from a signed pin set, stays. */
    pinsAsShown?:                PinKeys;
}

/** A key a signed pin set is believed of: the point of an elliptic curve, in base64, as an OCPP signature's keyId carries it. */
export interface PinSetSigner {
    publicKey:    string;
    description:  string;
    createdAt:    string;
}

/** The key this station signs in with over SSH. */
export interface StationSshKey {
    /** Its public half, as a line of authorized_keys. */
    publicKey:    string;
    fingerprint:  string;
    path:         string;
}

/** One line of what happened while a connection was being tested. */
export interface ConnectionTestStep {
    /** Milliseconds since the test started. */
    at_ms:  number;
    level:  'info' | 'notice' | 'warning' | 'error';
    text:   string;
}

/**
 * What a connection test asks its back end in OCPP once it is connected: a
 * Heartbeat, or nothing - everything up to the handshake and no further.
 */
export type ConnectionTestAsks = 'heartbeat' | 'none';

/** How long the station waits for the answer to what a connection test asked, in seconds. */
export const connectionTestAnswerWithin = 10;

/** What came of testing one connection. */
export interface ConnectionTest {
    description:  string;
    url:          string;
    ok:           boolean;
    runtime_ms:   number;
    steps:        ConnectionTestStep[];
    /** The key pin of the key the server of a wss:// connection showed, believed or not: what it would be pinned to. */
    serverKeyPin?: string;
    /** What the test asked in OCPP once it was connected, and what came of it; absent where it did not come that far, or asked nothing. */
    ocpp?: {
        request:        string;
        sent:           unknown[];
        sentAt_ms:      number;
        answered:       boolean;
        answer?:        unknown[];
        /** Whether the answer is a CALLERROR: an answer in OCPP all the same. */
        error?:         boolean;
        roundTrip_ms?:  number;
    };
}

/**
 * Where a connection this station dialled stands.
 *
 * Three kinds of "not connected", because they ask three different things of
 * whoever is looking: lost and trying are waited out, the station comes back by
 * itself; refused needs the other end fixed; not dialled and failed need this
 * end fixed. And one that is not connected on purpose: standby, an endpoint of
 * a back end that has more than one, dialled when the one dialled now cannot be
 * reached.
 */
export type ConnectionStatus = 'connected' | 'lost' | 'trying' | 'refused' | 'notDialled' | 'failed' | 'standby';

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
    /** Null until the first ssh:// connection made it. */
    sshClientKey?:         StationSshKey | null;
    /** The keys a pin set for a connection over SSH is believed of. */
    pinSetSigners?:        PinSetSigner[];
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
        // The whole section at once, because its fields are not independent
        // and a PUT replaces all of them.
        save:  (update: DisplayUpdate) => request<DisplayConfiguration>('PUT', '/configuration/display', update)
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

    nameplate: {
        get:   ()                          => request<NameplateConfiguration>('GET', '/configuration/nameplate'),
        /**
         * The whole section at once. The station builds its OCPP nodes anew,
         * hangs up and dials again, so that a CSMS is told.
         */
        save:  (update: NameplateUpdate)   => request<NameplateConfiguration>('PUT', '/configuration/nameplate', update)
    },

    signatures: {
        get:                ()                                              => request<Signatures>('GET',  '/configuration/signatures'),
        saveSettings:       (update: SignatureSettingsUpdate)               => request<Signatures>('PUT',  '/configuration/signatures/settings', update),
        /** A signed role assignment, checked as one received over OCPP is. */
        importRole:         (assignment: Record<string, unknown>)           => request<Signatures>('POST', '/configuration/signatures/roles', assignment),
        /** A role assignment for the owner, or { jwk, label }: once, while there is no owner. */
        installFirstOwner:  (owner: Record<string, unknown>)                => request<Signatures>('POST', '/configuration/signatures/roles/firstOwner', owner),
        removeRole:         (role: { role: string, version: number })       => request<Signatures>('POST', '/configuration/signatures/roles/remove', role),
        /** A signed policy table, checked as one received over OCPP is. */
        setPolicy:          (policy: Record<string, unknown>)               => request<Signatures>('POST', '/configuration/signatures/policy', policy),
        /** Every key of every role and the policy table are forgotten. */
        reset:              ()                                              => request<Signatures>('POST', '/configuration/signatures/reset', { confirm: 'reset' })
    },

    commissioning: {
        get:   ()                              => request<CommissioningConfiguration>('GET', '/configuration/commissioning'),
        /** Switched on or off at once; off lets go of every tool connected. */
        save:  (update: CommissioningUpdate)   => request<CommissioningConfiguration>('PUT', '/configuration/commissioning', update)
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
        save:  (evses: EVSE[])  => request<EVSEConfiguration>('PUT', '/configuration/evses', { evses }),

        /**
         * How each EVSE stands right now - once, when the EVSEs page opens:
         * what changes after that comes as an "evses" event on the stream.
         */
        states: ()              => request<EVSEStates>('GET', '/status/evses')
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

    meters: {
        get:     ()                      => request<MetersConfiguration>('GET', '/configuration/meters'),
        /** Read anew where it is read another way now - which takes a connection, so it may take a moment. */
        save:    (update: MeterUpdate)   => request<MetersConfiguration>('PUT', '/configuration/meters', update, 30_000),
        states:  ()                      => request<MeterStates>('GET', '/status/meters'),
        /** On a connection of its own, as the identity for what it does. */
        ask:     (ask: ModbusAsk)        => request<ModbusAnswer>('POST', '/meters/modbus', ask, 30_000),
        /** The charging sessions the meters signed, or were to sign - with their documents. */
        sessions: ()                     => request<SignedSessions>('GET', '/meters/sessions')
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

        /** A key a pin set for a connection over SSH is believed of - the CSMS's signing anchor. */
        addPinSetSigner:     (publicKey: string, description: string) =>
                                 request<StationConnections>('POST', '/configuration/connections/pinSetSigners', { publicKey, description }),

        removePinSetSigner:  (publicKey: string) =>
                                 request<StationConnections>('POST', '/configuration/connections/pinSetSigners/remove', { publicKey }),

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
         * deadline has to cover the connection plus that - and, where the
         * back end is asked something, the time the station waits for an
         * answer that may never come.
         */
        test:    (entry: ConnectionToSave, asks: ConnectionTestAsks = 'heartbeat') =>
                     request<ConnectionTest>('POST', '/configuration/connections/test', { ...entry, ocppRequest: asks },
                                             afterAsking(asks === 'none' ? [ 2 ] : [ connectionTestAnswerWithin, 2 ]))

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
