import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { toURL } from '@node/basePath';
import { html } from '@node/view';
import { movedTo, startNode } from '@node/start';

import { stationMenu } from './menu';

import { configurationPage }   from './pages/configuration';
import { v2gPage }             from './pages/v2g';
import { powerPage }           from './pages/power';
import { displayPage }         from './pages/display';
import { evsesPage }           from './pages/evses';
import { metersPage }          from './pages/meters';
import { rfidPage }            from './pages/rfid';
import { nameplatePage }       from './pages/nameplate';
import { commissioningPage }   from '@ocpp/pages/commissioning';
import { ocppServerPage }      from '@ocpp/pages/ocppServer';
import { stationLoginsPage }   from '@ocpp/pages/stationLogins';
import { clientTrustPage }     from '@ocpp/pages/clientTrust';
import { sshKeysSection }      from '@ocpp/sshKeys';
import { connectionsPage }     from '@ocpp/pages/connections';
import { ocppLogPage }         from '@ocpp/pages/ocppLog';
import { setStationsResource } from '@ocpp/settings';
import { requestTabId }        from '@node/certificateRequests';
import { signerRolesPage }     from './pages/signerRoles';
import { calibrationPage }     from './pages/calibration';
import { evseStatusPage }      from './pages/evseStatus';

// The station port, its WebSocket logins and the chains it accepts are the
// pages every node that serves OCPP has: here whoever may read or change the
// connections may read or change them - who may change where this station
// connects to may change who connects to it.
setStationsResource('connections');

// What a charging station has pages for beside what every node has: the
// vehicle below the cable, what it may draw, the screen on its front, its
// EVSEs, their meters and its card readers, what it tells its back ends it is,
// a tool commissioning it over SSH, the back ends it connects to and what it proves
// itself with, and what it is
// calibrated under - and, outside the
// configuration, how each of its EVSEs stands right now. The keys it makes
// for itself have no page: they are who the station is, and are made, wait
// for their certificate and are identities on the Client identities page - the
// node's, as its certificate store is. Who is shown which
// of them is in menu.ts. The sign-in, the log, the name
// servers, the time servers, the certificate store, the frame, "/" - the
// first page of the menu somebody may open - and following the log while
// somebody is signed in are every node's; see WWCP_Node's start.ts.
startNode({

    name:  'Charging Station',
    icon:  'fa-charging-station',

    menu:  stationMenu,

    // The certificate store in the station's words where every node's do not
    // say enough: that three of the roots are Plug & Charge's, what a vehicle,
    // a contract and an OEM's provisioning chain to, and that the keys the
    // station connects to its back ends with are identities like the others - one
    // brought with its key, or one whose key was made here.
    certificates: {

        hints: {
            believes:     html`
                Trust anchors. Every switched-on root of a kind is believed at once. A TLS root vouches for
                the time servers, the name servers and the meters it is kept for, beside the roots of the
                machine this station runs on. The V2G, Mobility Operator and OEM roots are kept for Plug &amp; Charge:
                what a vehicle's certificate, a contract and an OEM provisioning certificate chain to.
            `,
            presents:     html`
                A TLS identity, with its private key: what this station would show a server that asks for
                one - and a meter, where its certificate carries a SunSpec role: the
                <a href="${toURL('/configuration/meters')}">Meters</a> page says which one reads and which
                one writes - and a back end it connects to: the
                <a href="${toURL('/configuration/connections')}">Connections</a> page says which one each
                connection shows. An identity is brought here with its key, or its key is
                <a href="${toURL(`/configuration/client-identities?tab=${requestTabId}`)}">made on this station</a>
                and only its certificate ever travels; a key still waiting for its certificate is below.
            `,
            unencrypted:  html`can take the identity this station presents in TLS.`
        },

        // And who it is where it connects over ssh://: the keys it signs in
        // with, which are no certificates and stand among what it presents
        // all the same.
        sections: context => [ sshKeysSection(context) ],

        // The station port's server keys, on Server identities: offered there
        // before it has one.
        serverKeyListeners: [ 'ocpp' ]

    },

    pages: {

        '/ocpp-log':                          ocppLogPage,
        '/configuration':                 configurationPage,
        '/configuration/v2g':             v2gPage,
        '/configuration/power':           powerPage,
        '/configuration/display':         displayPage,
        '/configuration/evses':           evsesPage,
        '/configuration/meters':          metersPage,
        '/configuration/rfid':            rfidPage,
        '/configuration/nameplate':       nameplatePage,
        '/configuration/commissioning':   commissioningPage,
        '/configuration/connections':     connectionsPage,
        '/configuration/ocpp-server':         ocppServerPage,
        '/configuration/ocpp-server/logins':  stationLoginsPage,
        '/configuration/ocpp-server/trust':   clientTrustPage,
        // Where the credentials were while they had a page of their own:
        // they are a tab of the connections, which is where they are used.
        '/configuration/authentication':  movedTo('/configuration/connections?tab=credentials', 'Connections'),
        // Where the keys were while they had a page of their own.
        '/configuration/client-keys':     movedTo('/configuration/client-identities', 'Client identities'),
        '/configuration/signer-roles':    signerRolesPage,
        '/configuration/calibration':     calibrationPage,
        '/evses':                         evseStatusPage
    }

});
