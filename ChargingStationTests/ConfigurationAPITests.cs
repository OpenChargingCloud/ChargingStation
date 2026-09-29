/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of ChargingStation <https://github.com/OpenChargingCloud/ChargingStation>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Net;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What this local station says it is, and what happens when somebody
    /// tells it to be something else.
    /// </summary>
    public class ConfigurationAPITests : AChargingStationTests
    {

        #region TheConfigurationNamesEverySection()

        /// <summary>
        /// The Configuration page has a card for each of these and draws in
        /// it whatever the station sends, so a section going missing is a
        /// card that quietly says nothing - or, for the two lists, a page
        /// that says it could not load the configuration.
        /// </summary>
        [Test]
        public async Task TheConfigurationNamesEverySection()
        {

            using var http = await SignedIn();

            var configuration = await GetJSON(http, "/api/v1/configuration");

            // The node's own sections - http, web, log and time - are asked
            // of every kind by the conformance suite of WWCP_Node_TestKit.
            Assert.Multiple(() => {
                Assert.That(configuration["station"],    Is.Not.Null);
                Assert.That(configuration["v2g"],        Is.Not.Null);
                Assert.That(configuration["ocpp"],       Is.TypeOf<JArray>());
                Assert.That(configuration["assemblies"], Is.TypeOf<JArray>());
            });

        }

        #endregion

        #region TheOCPPSectionDescribesBothNodes()

        /// <summary>
        /// A station speaks through two nodes, and both are reported: OCPP 1.6
        /// has connectors and no EVSEs, OCPP 2.1 has EVSEs. What a station is
        /// made of is spelled differently in each, and the page shows both
        /// rather than picking one.
        /// </summary>
        [Test]
        public async Task TheOCPPSectionDescribesBothNodes()
        {

            using var http = await SignedIn();

            var ocpp = (await GetJSON(http, "/api/v1/configuration"))["ocpp"] as JArray ?? [];

            var v16  = ocpp.FirstOrDefault(node => node.Value<String>("version") == "1.6");
            var v21  = ocpp.FirstOrDefault(node => node.Value<String>("version") == "2.1");

            Assert.Multiple(() => {

                Assert.That(ocpp.Count, Is.EqualTo(2));

                Assert.That(v16,                          Is.Not.Null, "The OCPP 1.6 node is not reported.");
                Assert.That(v16?.Value<String>("role"),   Is.EqualTo("Charge Point"));
                Assert.That(v16?["connectors"],           Is.TypeOf<JArray>());

                Assert.That(v21,                          Is.Not.Null, "The OCPP 2.1 node is not reported.");
                Assert.That(v21?.Value<String>("role"),   Is.EqualTo("Charging Station"));
                Assert.That(v21?["evses"],                Is.TypeOf<JArray>());

            });

        }

        #endregion

        #region TheOCPPNodesAgreeWithTheEVSEsTheStationHas()

        /// <summary>
        /// The nodes are built from the EVSEs and rebuilt whenever those
        /// change, so that what the station says it is made of and what a back
        /// end is told about it are never two different things.
        /// </summary>
        [Test]
        public async Task TheOCPPNodesAgreeWithTheEVSEsTheStationHas()
        {

            using var http = await SignedIn();

            var ocpp  = (await GetJSON(http, "/api/v1/configuration"))["ocpp"] as JArray ?? [];
            var v21   = ocpp.FirstOrDefault(node => node.Value<String>("version") == "2.1");
            var evses = (v21?["evses"] as JArray)?.Count ?? 0;

            Assert.Multiple(() => {

                // Said first, so that this cannot pass by both sides being
                // empty: a station has at least one outlet or it is not one.
                Assert.That(Station.EVSEs.Count, Is.GreaterThan(0));

                // One connector of OCPP 1.6 is one cable, so an EVSE with two
                // of them becomes two - which is why only the 2.1 side counts
                // EVSEs.
                Assert.That(evses, Is.EqualTo(Station.EVSEs.Count),
                            "The OCPP 2.1 node reports a different number of EVSEs than the station has.");

            });

        }

        #endregion


    }

}
