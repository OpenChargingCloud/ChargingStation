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
import { commissioningPage }   from './pages/commissioning';
import { authenticationPage }  from '@ocpp/pages/authentication';
import { connectionsPage }     from '@ocpp/pages/connections';
import { clientKeyAnswers, clientKeysTab, clientKeysTabId, waitingKeysSection } from '@ocpp/pages/clientKeys';
import { signerRolesPage }     from './pages/signerRoles';
import { calibrationPage }     from './pages/calibration';
import { evseStatusPage }      from './pages/evseStatus';

// What a charging station has pages for beside what every node has: the
// vehicle below the cable, what it may draw, the screen on its front, its
// EVSEs, their meters and its card readers, what it tells its back ends it is,
// a tool commissioning it over SSH, the back ends it dials and what it proves
// itself with, and what it is
// calibrated under - and, outside the
// configuration, how each of its EVSEs stands right now. The keys it makes
// for itself have no page: they are who the station is, and stand with its
// identities - as a tab of that page, and among what it presents while they
// wait for their certificate. Who is shown which
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
    // station dials its back ends with are on a tab of their own, made here
    // and never imported - which is also where a certificate uploaded without
    // its key goes: to the key that waits for it.
    certificates: {

        tabs:      clientKeysTab,
        sections:  waitingKeysSection,
        answers:   clientKeyAnswers(),

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
                one writes. The keys it dials its back ends with are on the
                <a href="${toURL(`/configuration/identities?tab=${clientKeysTabId}`)}">Client keys</a> tab,
                because they are made on this station and never imported; the ones still waiting for
                their certificate are below.
            `,
            unencrypted:  html`can take the identity this station presents in TLS.`
        }
    },

    pages: {
        '/configuration':                 configurationPage,
        '/configuration/v2g':             v2gPage,
        '/configuration/power':           powerPage,
        '/configuration/display':         displayPage,
        '/configuration/evses':           evsesPage,
        '/configuration/meters':          metersPage,
        '/configuration/rfid':            rfidPage,
        '/configuration/nameplate':       nameplatePage,
        '/configuration/commissioning':   commissioningPage,
        '/configuration/authentication':  authenticationPage,
        '/configuration/connections':     connectionsPage,
        // Where the keys were while they had a page of their own.
        '/configuration/client-keys':     movedTo(`/configuration/identities?tab=${clientKeysTabId}`, 'Identities'),
        '/configuration/signer-roles':    signerRolesPage,
        '/configuration/calibration':     calibrationPage,
        '/evses':                         evseStatusPage
    }

});
