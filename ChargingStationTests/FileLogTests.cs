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

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A station's log on disk: called what a station's log files have always
    /// been called, and with the very first line of a run in it.
    /// </summary>
    /// <remarks>
    /// What a file log does - every entry, the UTC day it belongs to, nothing
    /// overwritten, a disk that fails said once - is the node's, and is tested
    /// in WWCP_Node against a node of no particular kind. What is tested here
    /// is what a station makes of it, read back from the file rather than
    /// asked of the writer.
    /// </remarks>
    [TestFixture]
    public class FileLogTests
    {

        #region AStationWritesItsVeryFirstEntryIntoTheFile()

        /// <summary>
        /// The file is attached before the station says anything at all.
        /// </summary>
        /// <remarks>
        /// The first entry a station writes is that it is starting up, and a
        /// file attached after that would begin in the middle of the story -
        /// with the one line that dates the run already missing from it.
        /// </remarks>
        [Test]
        public async Task AStationWritesItsVeryFirstEntryIntoTheFile()
        {

            var stationDirectory  = TestStations.TemporaryDirectory("file-log-station");
            var logs              = Path.Combine(stationDirectory, "logs");

            try
            {

                var station = TestStations.New(stationDirectory, TestStations.Offline, LogPath: logs);

                String[] lines;

                try
                {

                    Assert.That(station.LogPath, Is.EqualTo(Path.GetFullPath(logs)));

                    var file = Directory.GetFiles(logs, "station-*.log").Single();

                    using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

                    lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

                }
                finally
                {
                    await station.DisposeAsync();
                }

                Assert.That(lines.FirstOrDefault(), Does.Contain("[station] Charging station v").And.Contain("starting up."));

            }
            finally
            {
                TestStations.Remove(stationDirectory);
            }

        }

        #endregion

    }

}
