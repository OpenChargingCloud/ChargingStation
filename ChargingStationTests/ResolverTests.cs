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

using System.Reflection;

using NUnit.Framework;
using NUnit.Framework.Internal;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Whether the name resolvers these tests hand to their stations are let
    /// go of once the test they were handed out in is over.
    /// </summary>
    /// <remarks>
    /// A station leaves a DNS client it was handed to whoever made it, and
    /// the cache of one tidies itself up on a timer that ticks until the
    /// client is let go of. These tests made one for nearly every station and
    /// let go of none.
    ///
    /// Nothing a client says in public tells whether it was let go of, so
    /// these tests ask the timer of its cache, as the kit does
    /// (<see cref="NodeConformanceTests.TidiesUpStill"/>), and play the end
    /// of a test themselves: the end of their own would come too late to look.
    /// </remarks>
    [TestFixture]
    public class ResolverTests
    {

        #region (private static) TheAction()

        /// <summary>
        /// What lets go of the resolvers after every test.
        /// </summary>
        private static LetGoOfTheResolversAttribute TheAction()
        {

            var action = typeof(TestStations).Assembly.GetCustomAttribute<LetGoOfTheResolversAttribute>();

            Assert.That(action, Is.Not.Null, "Nothing on this assembly lets go of the resolvers once a test is over.");
            Assert.That(action!.Targets.HasFlag(ActionTargets.Test), Is.True, "What lets go of the resolvers is not around every test.");

            return action;

        }

        #endregion


        #region AResolverIsLetGoOfOnceTheTestItWasHandedOutInIsOver()

        /// <summary>
        /// A resolver ticks while its test runs and while another test ends,
        /// and is let go of once its own test is over.
        /// </summary>
        [Test]
        public void AResolverIsLetGoOfOnceTheTestItWasHandedOutInIsOver()
        {

            var action    = TheAction();
            var resolver  = TestStations.Resolver();
            var thisTest  = TestExecutionContext.CurrentContext.CurrentTest;
            var another   = thisTest.Parent!.Tests.First(test => test.Id != thisTest.Id);

            Assert.That(NodeConformanceTests.TidiesUpStill(resolver), Is.True,  "A resolver was let go of while its test was still running.");

            action.AfterTest(another);

            Assert.That(NodeConformanceTests.TidiesUpStill(resolver), Is.True,  "A resolver was let go of once another test was over.");

            action.AfterTest(thisTest);

            Assert.That(NodeConformanceTests.TidiesUpStill(resolver), Is.False, "A resolver was not let go of once the test it was handed out in was over.");

        }

        #endregion

        #region AResolverIsLetGoOfOnceASuiteItWasHandedOutInIsOver()

        /// <summary>
        /// A resolver handed out in a test is let go of once a suite around
        /// that test is over - as one handed out in a OneTimeSetUp is.
        /// </summary>
        [Test]
        public void AResolverIsLetGoOfOnceASuiteItWasHandedOutInIsOver()
        {

            var action    = TheAction();
            var resolver  = TestStations.Resolver();
            var fixture   = TestExecutionContext.CurrentContext.CurrentTest.Parent!;

            action.AfterTest(fixture);

            Assert.That(NodeConformanceTests.TidiesUpStill(resolver), Is.False, "A resolver was not let go of once a suite around the test it was handed out in was over.");

        }

        #endregion

    }

}
