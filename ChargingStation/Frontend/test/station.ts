/*
 * The charging station as the stand-in node every kind's page tests share
 * (WWCP_Node's test/node.ts): a stand-in for fetch, and a document of
 * happy-dom to draw a page into, framed with the station's name and icon.
 *
 * Imported first, before a page: lit-html looks for the document as it is
 * loaded, and the pages load it.
 *
 *   import { open, ... } from '../../test/station.ts';
 *   const { powerPage } = await import('./power.ts');
 */

import { standIn } from '@node/../test/node.ts';
export * from '@node/../test/node.ts';

standIn({ name: 'Charging Station', icon: 'fa-charging-station' });
