using Microsoft.Diagnostics.Tools.RuntimeClient;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Binding;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.Trace.CommandLine.Commands
{
    internal static class StreamCommandHandler
    {
        delegate Task<int> StreamDelegate(
            CancellationToken ct, 
            IConsole console, 
            int processId, 
            uint buffersize, 
            string providers, 
            string profile, 
            IEnumerable<string> manifests,
            TimeSpan duration);

        /// <summary>
        /// Stream a diagnostic trace from a currently running process.
        /// </summary>
        /// <param name="ct">The cancellation token</param>
        /// <param name="console"></param>
        /// <param name="processId">The process to collect the trace from.</param>
        /// <param name="buffersize">Sets the size of the in-memory circular buffer in megabytes.</param>
        /// <param name="providers">A list of EventPipe providers to be enabled. This is in the form 'Provider[,Provider]', where Provider is in the form: 'KnownProviderName[:Flags[:Level][:KeyValueArgs]]', and KeyValueArgs is in the form: '[key1=value1][;key2=value2]'</param>
        /// <param name="profile">A named pre-defined set of provider configurations that allows common tracing scenarios to be specified succinctly.</param>
        /// <returns></returns>
        private static async Task<int> Stream(
            CancellationToken ct, 
            IConsole console, 
            int processId, 
            uint buffersize, 
            string providers, 
            string profile, 
            IEnumerable<string> manifests,
            TimeSpan duration)
        {
            try
            {
                Debug.Assert(profile != null);
                if (processId <= 0)
                {
                    Console.Error.WriteLine("Process ID should not be negative.");
                    return ErrorCodes.ArgumentError;
                }

                var selectedProfile = ListProfilesCommandHandler.DotNETRuntimeProfiles
                    .FirstOrDefault(p => p.Name.Equals(profile, StringComparison.OrdinalIgnoreCase));
                if (selectedProfile == null)
                {
                    Console.Error.WriteLine($"Invalid profile name: {profile}");
                    return ErrorCodes.ArgumentError;
                }

                var providerCollection = Extensions.ToProviders(providers);
                var profileProviders = new List<Provider>();

                // If user defined a different key/level on the same provider via --providers option that was specified via --profile option,
                // --providers option takes precedence. Go through the list of providers specified and only add it if it wasn't specified
                // via --providers options.
                if (selectedProfile.Providers != null)
                {
                    foreach (Provider selectedProfileProvider in selectedProfile.Providers)
                    {
                        bool shouldAdd = true;

                        foreach (Provider providerCollectionProvider in providerCollection)
                        {
                            if (providerCollectionProvider.Name.Equals(selectedProfileProvider.Name))
                            {
                                shouldAdd = false;
                                break;
                            }
                        }

                        if (shouldAdd)
                        {
                            profileProviders.Add(selectedProfileProvider);
                        }
                    }
                }

                providerCollection.AddRange(profileProviders);

                if (providerCollection.Count <= 0)
                {
                    Console.Error.WriteLine("No providers were specified to start a trace.");
                    return ErrorCodes.ArgumentError;
                }

                PrintProviders(providerCollection);

                var process = Process.GetProcessById(processId);
                var configuration = new SessionConfiguration(
                    circularBufferSizeMB: buffersize,
                    format: EventPipeSerializationFormat.NetTrace,
                    providers: providerCollection);

                var shouldExit = new ManualResetEvent(false);
                var shouldStopAfterDuration = duration != default(TimeSpan);
                var failed = false;
                var terminated = false;
                System.Timers.Timer durationTimer = null;

                ct.Register(() => shouldExit.Set());

                ulong sessionId = 0;
                using (Stream stream = EventPipeClient.CollectTracing(processId, configuration, out sessionId))
                {
                    if (sessionId == 0)
                    {
                        Console.Error.WriteLine("Unable to create session.");
                        return ErrorCodes.SessionCreationError;
                    }

                    if (shouldStopAfterDuration)
                    {
                        durationTimer = new System.Timers.Timer(duration.TotalMilliseconds);
                        durationTimer.Elapsed += (s, e) => shouldExit.Set();
                        durationTimer.AutoReset = false;
                    }

                    var collectingTask = new Task(() =>
                    {
                        try
                        {
                            var stopwatch = new Stopwatch();
                            durationTimer?.Start();
                            stopwatch.Start();

                            EventPipeEventSource eventSource = new EventPipeEventSource(stream);
                            eventSource.Dynamic.All += Dynamic_All;
                            foreach (string manifest in manifests)
                            {
                                eventSource.Dynamic.AddDynamicProvider(new Tracing.Parsers.ProviderManifest(manifest));
                            }
                            eventSource.Process();
                        }
                        catch (Exception ex)
                        {
                            failed = true;
                            Console.Error.WriteLine($"[ERROR] {ex.ToString()}");
                        }
                        finally
                        {
                            terminated = true;
                            shouldExit.Set();
                        }
                    });
                    collectingTask.Start();

                    Console.Out.WriteLine("Press <Enter> or <Ctrl+C> to exit...");

                    do
                    {
                        while (!Console.KeyAvailable && !shouldExit.WaitOne(250)) { }
                    } while (!shouldExit.WaitOne(0) && Console.ReadKey(true).Key != ConsoleKey.Enter);

                    if (!terminated)
                    {
                        durationTimer?.Stop();
                        EventPipeClient.StopTracing(processId, sessionId);
                    }
                    await collectingTask;
                }

                Console.Out.WriteLine();
                Console.Out.WriteLine("Trace completed.");

                return failed ? ErrorCodes.TracingError : 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex.ToString()}");
                return ErrorCodes.UnknownError;
            }
        }

        private static void Dynamic_All(TraceEvent obj)
        {
            if (obj.EventName != "EventWriteString" && !obj.EventName.Contains("Rundown"))
            {
                Console.Out.WriteLine($"{obj.TimeStampRelativeMSec.ToString("0000000")} {obj.EventName} {obj.ID}: {obj.FormattedMessage}");
            }
        }

        [Conditional("DEBUG")]
        private static void PrintProviders(IReadOnlyList<Provider> providers)
        {
            Console.Out.WriteLine("Enabling the following providers");
            foreach (var provider in providers)
                Console.Out.WriteLine($"\t{provider.ToString()}");
        }

        public static Command StreamCommand() =>
            new Command(
                name: "stream",
                description: "Streams a diagnostic trace from a currently running process",
                symbols: new Option[] {
                    CommonOptions.ProcessIdOption(),
                    CircularBufferOption(),
                    ProvidersOption(),
                    ProfileOption(),
                    ManifestOption(),
                    DurationOption()
                },
                handler: HandlerDescriptor.FromDelegate((StreamDelegate)Stream).GetCommandHandler());

        private static uint DefaultCircularBufferSizeInMB => 256;

        private static Option CircularBufferOption() =>
            new Option(
                alias: "--buffersize",
                description: $"Sets the size of the in-memory circular buffer in megabytes. Default {DefaultCircularBufferSizeInMB} MB.",
                argument: new Argument<uint>(defaultValue: DefaultCircularBufferSizeInMB) { Name = "size" },
                isHidden: false);

        private static Option ProvidersOption() =>
            new Option(
                alias: "--providers",
                description: @"A list of EventPipe providers to be enabled. This is in the form 'Provider[,Provider]', where Provider is in the form: 'KnownProviderName[:Flags[:Level][:KeyValueArgs]]', and KeyValueArgs is in the form: '[key1=value1][;key2=value2]'",
                argument: new Argument<string>(defaultValue: "") { Name = "list-of-comma-separated-providers" }, // TODO: Can we specify an actual type?
                isHidden: false);

        private static Option ProfileOption() =>
            new Option(
                alias: "--profile",
                description: @"A named pre-defined set of provider configurations that allows common tracing scenarios to be specified succinctly.",
                argument: new Argument<string>(defaultValue: "runtime-basic") { Name = "profile-name" },
                isHidden: false);

        private static Option ManifestOption() =>
            new Option(
                aliases: new[] { "--manifests", "-m" },
                description: "The manifest files to process.",
                argument: new Argument<string[]>() { Name = "manifest-file" },
                isHidden: false);

        private static Option DurationOption() =>
            new Option(
                alias: "--duration",
                description: @"When specified, will trace for the given timespan and then automatically stop the trace. Provided in the form of dd:hh:mm:ss.",
                argument: new Argument<TimeSpan>(defaultValue: default(TimeSpan)) { Name = "duration-timespan" },
                isHidden: true);
    }
}
