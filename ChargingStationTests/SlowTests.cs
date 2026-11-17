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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using NUnit.Framework;
using NUnit.Framework.Interfaces;

#endregion

[assembly: cloud.charging.open.ChargingStation.Tests.SaysWhereASlowTestSpentItsTime]

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Says where a test that took long, or failed, spent its time: what its
    /// stations said and when, by the clock on the wall, and how the machine
    /// was doing meanwhile.
    /// </summary>
    /// <remarks>
    /// Tests that take a third of a second here have taken twenty on the
    /// runners of the CI, another one in every run, and what a failed test
    /// says of its station has no times in it - a station may be on a clock
    /// its test made up. So the time is taken here, off a stopwatch, when a
    /// station says something, is made, and is taken away.
    ///
    /// And three things are watched all along, by a thread of its own that
    /// asks for nothing from the thread pool: how long something handed to the
    /// thread pool waited to be run, which is what a pool out of threads
    /// shows; how much longer than it asked that thread slept, which is what a
    /// machine standing still shows; and how long the garbage collector held
    /// everything.
    ///
    /// On the assembly, so that it is around every test: NUnit runs the
    /// BeforeTest of an action from up there before the SetUp of a test and
    /// its AfterTest after the TearDown, which are in the time a test is said
    /// to have taken.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class SaysWhereASlowTestSpentItsTimeAttribute : Attribute,
                                                                    ITestAction
    {

        public ActionTargets Targets
            => ActionTargets.Test;

        public void BeforeTest(ITest Test)
            => SlowTests.Begin(Test);

        public void AfterTest(ITest Test)
            => SlowTests.End(Test);

    }


    /// <summary>
    /// What <see cref="SaysWhereASlowTestSpentItsTimeAttribute"/> keeps of the
    /// tests that are running.
    /// </summary>
    internal static class SlowTests
    {

        #region Data

        /// <summary>
        /// From how long on a test is said to have been slow.
        /// </summary>
        public static readonly TimeSpan  Slow          = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The longest silence between two things a test is told of that is
        /// not pointed at.
        /// </summary>
        public static readonly TimeSpan  ASilence      = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How many lines of a test are written at most: the first and the last.
        /// </summary>
        public const           Int32     MaxLines      = 240;

        /// <summary>
        /// How often the machine is looked at.
        /// </summary>
        private static readonly TimeSpan Every         = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// The one clock everything here is read off.
        /// </summary>
        private static readonly Stopwatch clock        = Stopwatch.StartNew();

        private static readonly ConcurrentDictionary<String, Followed>  running  = new ();

        /// <summary>
        /// What the machine was seen doing: when, how long what was handed to
        /// the thread pool had been waiting, and how much too long the watching
        /// thread slept.
        /// </summary>
        private static readonly ConcurrentQueue<(TimeSpan At, TimeSpan PoolWait, TimeSpan Overslept)>  seen  = new ();

        private static Int32   watching;
        private static Int64   handedOverAt   = -1;

        #endregion


        #region (private class) Followed

        private sealed class Followed
        {

            public TimeSpan                                   Started    { get; } = clock.Elapsed;
            public TimeSpan                                   GCPaused   { get; } = GC.GetTotalPauseDuration();
            public ConcurrentQueue<(TimeSpan At, String What)> Told      { get; } = new ();
            public Int32                                      Stations;

        }

        #endregion


        #region Begin(Test) / End(Test)

        /// <summary>
        /// A test begins: before its SetUp.
        /// </summary>
        public static void Begin(ITest Test)
        {

            try
            {
                Watch();
                running[Test.Id] = new Followed();
            }
            catch (Exception)
            { }

        }

        /// <summary>
        /// A test is over: after its TearDown. Said of it is something where
        /// it took long or did not pass.
        /// </summary>
        public static void End(ITest Test)
        {

            try
            {

                if (!running.TryRemove(Test.Id, out var followed))
                    return;

                var ended   = clock.Elapsed;
                var took    = ended - followed.Started;
                var passed  = TestContext.CurrentContext.Result.Outcome.Status is TestStatus.Passed or TestStatus.Skipped or TestStatus.Inconclusive;

                if (took < Slow && passed)
                    return;

                TestContext.Out.WriteLine(Said(Test.Name, followed, ended, passed));

            }
            catch (Exception)
            { }

        }

        #endregion

        #region Mark(What) / Follow(Station)

        /// <summary>
        /// Something the test that is running did, to be said with its time.
        /// </summary>
        public static void Mark(String What)
        {

            try
            {
                if (running.TryGetValue(TestContext.CurrentContext.Test.ID, out var followed))
                    followed.Told.Enqueue((clock.Elapsed, What));
            }
            catch (Exception)
            { }

        }

        /// <summary>
        /// Listen to what a station of the test that is running says.
        /// </summary>
        public static void Follow(ChargingStation Station)
        {

            try
            {

                if (!running.TryGetValue(TestContext.CurrentContext.Test.ID, out var followed))
                    return;

                var number = Interlocked.Increment(ref followed.Stations);

                followed.Told.Enqueue((clock.Elapsed, $"station {number} was made"));

                // Whatever thread it says it on, and long after the test for
                // all this knows: kept with the test it was made in.
                Station.Log.OnLogged += entry => followed.Told.Enqueue((clock.Elapsed, $"station {number}: {OneLine(entry.Message)}"));

            }
            catch (Exception)
            { }

        }

        #endregion


        #region (private) Said(Name, Followed, Ended, Passed)

        private static String Said(String    Name,
                                   Followed  Followed,
                                   TimeSpan  Ended,
                                   Boolean   Passed)
        {

            var text      = new StringBuilder();
            var took      = Ended - Followed.Started;
            var machine   = seen.Where(one => one.At >= Followed.Started && one.At <= Ended).ToArray();

            var pool      = machine.Length > 0 ? machine.MaxBy(one => one.PoolWait)  : default;
            var slept     = machine.Length > 0 ? machine.MaxBy(one => one.Overslept) : default;

            ThreadPool.GetAvailableThreads(out var workers,    out var ports);
            ThreadPool.GetMaxThreads      (out var maxWorkers, out var maxPorts);
            ThreadPool.GetMinThreads      (out var minWorkers, out _);

            text.AppendLine($"[where the time went] '{Name}' {(Passed ? "took" : "did not pass, and took")} {Seconds(took)} with its SetUp and TearDown.");

            text.AppendLine($"  The machine, looked at {machine.Length} times: what was handed to the thread pool waited at most {Seconds(pool.PoolWait)} to be run (at +{Seconds(pool.At - Followed.Started)}), " +
                            $"the watching thread overslept by at most {Seconds(slept.Overslept)} (at +{Seconds(slept.At - Followed.Started)}), " +
                            $"and the garbage collector held everything for {Seconds(GC.GetTotalPauseDuration() - Followed.GCPaused)}.");

            text.AppendLine($"  Now: {ThreadPool.ThreadCount} threads in the pool ({maxWorkers - workers} busy, {maxPorts - ports} on I/O, at least {minWorkers}), " +
                            $"{ThreadPool.PendingWorkItemCount} items waiting, {Environment.ProcessorCount} processors, {Process.GetCurrentProcess().Threads.Count} threads in the process.");

            var told      = Followed.Told.ToArray();
            var last      = Followed.Started;
            var left      = told.Length > MaxLines ? told.Length - MaxLines : 0;

            for (var i = 0; i < told.Length; i++)
            {

                if (left > 0 && i == MaxLines / 2)
                {
                    text.AppendLine($"  ... {left} lines left out ...");
                    i   += left;
                    last = told[i].At;
                }

                if (told[i].At - last >= ASilence)
                    text.AppendLine($"           ... {Seconds(told[i].At - last)} of nothing ...");

                text.AppendLine($"  +{Seconds(told[i].At - Followed.Started),8}  {told[i].What}");

                last = told[i].At;

            }

            if (Ended - last >= ASilence)
                text.AppendLine($"           ... {Seconds(Ended - last)} of nothing ...");

            text.Append($"  +{Seconds(took),8}  the TearDown was over");

            return text.ToString();

        }

        private static String Seconds(TimeSpan Time)
            => Time.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " s";

        private static String OneLine(String Text)
        {

            var line = Text.ReplaceLineEndings(" ");

            return line.Length > 300
                       ? line[..300] + " ..."
                       : line;

        }

        #endregion

        #region (private) Watch()

        /// <summary>
        /// Start looking at the machine, once: on a thread of its own, so that
        /// a thread pool that is out of threads does not keep it from saying so.
        /// </summary>
        private static void Watch()
        {

            if (Interlocked.Exchange(ref watching, 1) != 0)
                return;

            new Thread(() => {

                while (true)
                {

                    try
                    {

                        var before     = clock.Elapsed;

                        Thread.Sleep(Every);

                        var now        = clock.Elapsed;
                        var overslept  = now - before - Every;

                        // One thing at a time is handed to the thread pool; as
                        // long as it has not been run, it is still waiting.
                        var handedOver = Interlocked.Read(ref handedOverAt);
                        var poolWait   = TimeSpan.Zero;

                        if (handedOver < 0)
                        {
                            Interlocked.Exchange(ref handedOverAt, now.Ticks);
                            ThreadPool.UnsafeQueueUserWorkItem(_ => Interlocked.Exchange(ref handedOverAt, -1), null);
                        }
                        else
                            poolWait = now - TimeSpan.FromTicks(handedOver);

                        seen.Enqueue((now, poolWait, overslept > TimeSpan.Zero ? overslept : TimeSpan.Zero));

                        // Twenty minutes of it, and no more.
                        while (seen.Count > 12_000)
                            seen.TryDequeue(out _);

                    }
                    catch (Exception)
                    { }

                }

            }) {
                IsBackground  = true,
                Name          = "SlowTests"
            }.Start();

        }

        #endregion

    }

}
