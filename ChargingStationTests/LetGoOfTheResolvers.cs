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
using NUnit.Framework.Interfaces;

#endregion

[assembly: cloud.charging.open.ChargingStation.Tests.LetGoOfTheResolvers]

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Lets go of the name resolvers <see cref="TestStations.Resolver"/> handed
    /// out in a test once that test is over, and of those handed out in a
    /// suite once that suite is over.
    /// </summary>
    /// <remarks>
    /// On the assembly, so that it is around every test of this assembly
    /// without a fixture having to ask for it. NUnit runs the AfterTest of an
    /// action from up there after the test's TearDown, so a station let go of
    /// there has stopped using its resolver by then - and a resolver handed
    /// out in a OneTimeSetUp lives until the end of the run.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class LetGoOfTheResolversAttribute : Attribute,
                                                         ITestAction
    {

        /// <summary>
        /// Every test, and the assembly's suite.
        /// </summary>
        public ActionTargets Targets
            => ActionTargets.Test | ActionTargets.Suite;

        /// <summary>
        /// Nothing before a test.
        /// </summary>
        /// <param name="Test">The test about to run.</param>
        public void BeforeTest(ITest Test)
        { }

        /// <summary>
        /// Let go of what was handed out in the test or the suite that is over.
        /// </summary>
        /// <param name="Test">The test or the suite that is over.</param>
        public void AfterTest(ITest Test)
            => TestStations.LetGoOfTheResolversOf(Test);

    }

}
