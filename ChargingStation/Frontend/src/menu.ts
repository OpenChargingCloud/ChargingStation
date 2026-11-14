/*
 * The menu of a charging station: what it has pages for beside what every
 * node has, each entry shown to whoever may read what its page reads, as
 * CSHTTPAPI asks it - the two pages about dialling out both read the
 * connections, and the EVSEs as they stand now read what is going on, which
 * is apart from how the station is set up. The keys it dials with have no
 * entry: they are a tab of the identities. The sign-in, the log, the name
 * servers, the time servers and the certificate store are every node's; see
 * WWCP_Node's start.ts.
 *
 * A module of its own rather than a part of main.ts, so that who is shown
 * what can be asked without starting the web interface.
 */

import { nodeMenu } from '@node/start';
import type { MenuEntry } from '@node/shell';

export const stationMenu: MenuEntry[] = [
    nodeMenu.configuration([
        nodeMenu.dns,
        nodeMenu.nts,
        nodeMenu.ssh,
        { path: '/configuration/v2g',             label: 'V2G',              icon: 'fa-car-side',        permission: [ 'v2g:read' ]          },
        { path: '/configuration/power',           label: 'Grid connection',  icon: 'fa-bolt',            permission: [ 'power:read' ]        },
        { path: '/configuration/display',         label: 'Display',          icon: 'fa-desktop',         permission: [ 'display:read' ]      },
        { path: '/configuration/evses',           label: 'EVSEs',            icon: 'fa-plug',            permission: [ 'evses:read' ]        },
        { path: '/configuration/meters',          label: 'Meters',           icon: 'fa-gauge',           permission: [ 'meters:read' ]       },
        { path: '/configuration/rfid',            label: 'RFID',             icon: 'fa-id-card',         permission: [ 'rfid:read' ]         },
        { path: '/configuration/nameplate',       label: 'Nameplate',        icon: 'fa-tag',             permission: [ 'nameplate:read' ]    },
        { path: '/configuration/commissioning',   label: 'Commissioning',    icon: 'fa-screwdriver-wrench', permission: [ 'commissioning:read' ] },
        { path: '/configuration/authentication',  label: 'Authentication',   icon: 'fa-user-lock',       permission: [ 'connections:read' ]  },
        { path: '/configuration/connections',     label: 'Connections',      icon: 'fa-network-wired',   permission: [ 'connections:read' ]  },
        { path: '/configuration/signer-roles',    label: 'Signer roles',     icon: 'fa-user-shield',     permission: [ 'signatures:read' ]   },
        nodeMenu.certificates,
        nodeMenu.identities,
        // What the web interface shows over HTTPS, where the "web"
        // section says "tls": { "certificate": "store" }, or --https.
        nodeMenu.serverIdentities,
        { path: '/configuration/calibration',     label: 'Calibration',      icon: 'fa-scale-balanced',  permission: [ 'calibration:read' ]  }
    ]),
    // What is going on, apart from how the station is set up: the support,
    // who may see nothing else, sees it too.
    { path: '/evses', label: 'EVSEs', icon: 'fa-charging-station', permission: [ 'state:read' ] },
    nodeMenu.logs
];
