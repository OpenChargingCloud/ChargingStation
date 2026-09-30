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

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What the station's own code says, held to what the code of every kind
    /// of node is held to - WWCP_Node_TestKit's SourceRules - as its pages are
    /// to the rules of WWCP_Node's test/pages.ts.
    /// </summary>
    [TestFixture]
    public class SourceRulesTests
    {

        #region NoTextOfTheStationPutsAnArticleBeforeAName()

        /// <summary>
        /// No text of the station puts "a" or "an" in front of a name it
        /// interpolates. Which article a name takes goes by how it is said, and
        /// a text cannot know that of a name it is handed: "A {algorithm.Name}
        /// key could not be generated" said "A ECDSA P-256 (secp256r1) key" and
        /// "A RSA 2048 key" - wrong for every one of the thirteen kinds of key
        /// the station makes (found by the local controller).
        /// </summary>
        /// <remarks>
        /// The sources are found above where the tests run by the station's
        /// project files. Named by their directories, a build with an artifacts
        /// path took the directory of its builds for them, which holds a
        /// ChargingStation and a ChargingStationTests of its own with no source
        /// in them, and the test was red there.
        /// </remarks>
        [Test]
        public void NoTextOfTheStationPutsAnArticleBeforeAName()
        {

            var repository = SourceRules.RepositoryAbove(AppContext.BaseDirectory, "ChargingStation/ChargingStation.csproj",
                                                                                   "ChargingStationTests/ChargingStationTests.csproj");

            Assert.That(SourceRules.ArticlesBeforeANameIn(Path.Combine(repository, "ChargingStation"),
                                                          Path.Combine(repository, "ChargingStationTests")),
                        Is.Empty);

        }

        #endregion

    }

}
