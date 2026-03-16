using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Auth.KeyAuth;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using HostProgram = Azure.Cosmos.LightEmulator.Host.Program;

namespace Azure.Cosmos.LightEmulator.Cli;

public static class Program
{
    private const string DefaultConsistency = "Session";
    private const string StateFileName = "emulator-instance.json";
    private const string PidFileName = "emulator.pid";
    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var rootCommand = BuildRootCommand();
            return await rootCommand
                .Parse(args, new CommandLineConfiguration(rootCommand))
                .InvokeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("Azure Cosmos DB emulator CLI");

        var startCommand = new Command("start", "Starts the emulator host.");
        var portOption = CreateIntOption("--port", 8081, "Port for the NoSQL endpoint.");
        var mongoPortOption = CreateIntOption("--mongo-port", 10255, "Port for the MongoDB endpoint.");
        var dataDirOption = new Option<string?>("--data-dir") { Description = "Data directory for emulator files." };
        var keyOption = new Option<string?>("--key") { Description = "Master key to use for authentication." };
        var enableEntraOption = new Option<bool>("--enable-entra") { Description = "Enable Entra ID authentication support." };
        var consistencyOption = new Option<string>("--consistency")
        {
            Description = "Default consistency level.",
            DefaultValueFactory = _ => DefaultConsistency
        };
        var verboseOption = new Option<bool>("--verbose") { Description = "Enable verbose logging." };
        var backgroundOption = new Option<bool>("--background") { Description = "Run the emulator in the background." };
        var runHostInternalOption = new Option<bool>("--run-host-internal")
        {
            Hidden = true,
            Description = "Internal option used to bootstrap the host process."
        };

        startCommand.Add(portOption);
        startCommand.Add(mongoPortOption);
        startCommand.Add(dataDirOption);
        startCommand.Add(keyOption);
        startCommand.Add(enableEntraOption);
        startCommand.Add(consistencyOption);
        startCommand.Add(verboseOption);
        startCommand.Add(backgroundOption);
        startCommand.Add(runHostInternalOption);
        startCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var startOptions = new StartOptions(
                parseResult.GetValue(portOption),
                parseResult.GetValue(mongoPortOption),
                parseResult.GetValue(dataDirOption),
                parseResult.GetValue(keyOption),
                parseResult.GetValue(enableEntraOption),
                parseResult.GetValue(consistencyOption) ?? DefaultConsistency,
                parseResult.GetValue(verboseOption),
                parseResult.GetValue(backgroundOption));

            return await StartAsync(startOptions, parseResult.GetValue(runHostInternalOption), cancellationToken);
        });

        var stopCommand = new Command("stop", "Stops a running background emulator instance.");
        stopCommand.SetAction(async (_, cancellationToken) => await StopAsync(cancellationToken));

        var resetCommand = new Command("reset", "Wipes the emulator data directory.");
        resetCommand.SetAction(async (_, cancellationToken) => await ResetAsync(cancellationToken));

        var statusCommand = new Command("status", "Shows emulator status and connection details.");
        statusCommand.SetAction(async (_, cancellationToken) => await StatusAsync(cancellationToken));

        var exportCommand = new Command("export", "Exports emulator data as JSON.");
        var outputOption = new Option<string>("--output")
        {
            Description = "Path to the export file.",
            Required = true
        };
        exportCommand.Add(outputOption);
        exportCommand.SetAction(async (parseResult, cancellationToken) =>
            await ExportAsync(parseResult.GetValue(outputOption)!, cancellationToken));

        var importCommand = new Command("import", "Imports emulator data from JSON.");
        var inputOption = new Option<string>("--input")
        {
            Description = "Path to the import file.",
            Required = true
        };
        importCommand.Add(inputOption);
        importCommand.SetAction(async (parseResult, cancellationToken) =>
            await ImportAsync(parseResult.GetValue(inputOption)!, cancellationToken));

        rootCommand.Add(startCommand);
        rootCommand.Add(stopCommand);
        rootCommand.Add(resetCommand);
        rootCommand.Add(statusCommand);
        rootCommand.Add(exportCommand);
        rootCommand.Add(importCommand);

        return rootCommand;
    }

    private static async Task<int> StartAsync(StartOptions options, bool runHostInternal, CancellationToken cancellationToken)
    {
        var normalized = Normalize(options);
        var existingState = TryLoadCurrentState();

        if (existingState is not null && IsProcessRunning(existingState))
        {
            Console.WriteLine($"Emulator is already running (PID {existingState.ProcessId}).");
            Console.WriteLine($"Endpoint: {existingState.Endpoint}");
            return 1;
        }

        if (existingState is not null)
        {
            CleanupStateFiles(existingState);
        }

        if (normalized.Background && !runHostInternal)
        {
            return await StartBackgroundAsync(normalized, cancellationToken);
        }

        return await RunHostAsync(normalized, cancellationToken);
    }

    private static async Task<int> StartBackgroundAsync(StartOptions options, CancellationToken cancellationToken)
    {
        var startInfo = BuildSelfStartInfo(options);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("Failed to start emulator process.");
            return 1;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = TryLoadState(GetStateFilePath(options.DataDirectory!));
            if (state is not null && state.ProcessId == process.Id && await IsEndpointHealthyAsync(state, cancellationToken))
            {
                PrintConnectionInfo(state, includeStatus: true);
                return 0;
            }

            if (process.HasExited)
            {
                Console.Error.WriteLine($"Emulator exited before startup completed (exit code {process.ExitCode}).");
                return process.ExitCode == 0 ? 1 : process.ExitCode;
            }

            await Task.Delay(250, cancellationToken);
        }

        Console.Error.WriteLine("Timed out waiting for the emulator to start.");
        return 1;
    }

    private static async Task<int> RunHostAsync(StartOptions options, CancellationToken cancellationToken)
    {
        var state = EmulatorInstanceState.Create(options, Process.GetCurrentProcess().Id);
        await PersistStateAsync(state, cancellationToken);

        var app = HostProgram.BuildApplication(Array.Empty<string>(), BuildConfigurationOverrides(options));
        app.Lifetime.ApplicationStopped.Register(() => CleanupStateFiles(state));

        try
        {
            // Do not pass cancellationToken to RunAsync. The host handles Ctrl+C / SIGTERM
            // via ConsoleLifetime. Passing System.CommandLine's token creates linked
            // CancellationTokenSources inside BackgroundService.StartAsync that race with
            // the host's own shutdown path, causing ObjectDisposedException on exit.
            await app.RunAsync();
            return 0;
        }
        finally
        {
            CleanupStateFiles(state);
        }
    }

    private static async Task<int> StopAsync(CancellationToken cancellationToken)
    {
        var state = TryLoadCurrentState();
        if (state is null)
        {
            Console.WriteLine("Emulator is not running.");
            return 0;
        }

        if (!IsProcessRunning(state))
        {
            CleanupStateFiles(state);
            Console.WriteLine("Emulator is not running.");
            return 0;
        }

        var process = Process.GetProcessById(state.ProcessId);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken);
        CleanupStateFiles(state);

        Console.WriteLine($"Stopped emulator process {state.ProcessId}.");
        return 0;
    }

    private static async Task<int> ResetAsync(CancellationToken cancellationToken)
    {
        var state = TryLoadCurrentState();
        if (state is not null && IsProcessRunning(state))
        {
            var stopCode = await StopAsync(cancellationToken);
            if (stopCode != 0)
            {
                return stopCode;
            }
        }

        var dataDirectory = state?.DataDirectory ?? DefaultDataDirectory;
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }

        Directory.CreateDirectory(dataDirectory);
        CleanupStateFiles(new EmulatorInstanceState { DataDirectory = dataDirectory });

        Console.WriteLine($"Reset emulator data directory: {dataDirectory}");
        return 0;
    }

    private static async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        var state = TryLoadCurrentState();
        if (state is null)
        {
            Console.WriteLine("Emulator is not running.");
            return 0;
        }

        var running = IsProcessRunning(state) && await IsEndpointHealthyAsync(state, cancellationToken);
        if (!running)
        {
            CleanupStateFiles(state);
            Console.WriteLine("Emulator is not running.");
            return 0;
        }

        PrintConnectionInfo(state, includeStatus: true);
        return 0;
    }

    private static async Task<int> ExportAsync(string outputPath, CancellationToken cancellationToken)
    {
        var state = await RequireRunningStateAsync(cancellationToken);
        if (state is null)
        {
            return 1;
        }

        var exportDocument = new JsonObject
        {
            ["databases"] = new JsonArray()
        };

        var databasesResponse = await SendAuthenticatedAsync(state, HttpMethod.Get, "dbs", "dbs", string.Empty, null, null, cancellationToken);
        await EnsureSuccessAsync(databasesResponse, cancellationToken);
        var databasesPayload = await ReadJsonAsync(databasesResponse, cancellationToken);
        var databases = databasesPayload?["Databases"] as JsonArray ?? [];
        var databaseArray = exportDocument["databases"]!.AsArray();

        foreach (var databaseNode in databases)
        {
            if (databaseNode is not JsonObject database)
            {
                continue;
            }

            var dbId = database["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(dbId))
            {
                continue;
            }

            var databaseExport = new JsonObject
            {
                ["id"] = dbId,
                ["containers"] = new JsonArray()
            };

            var containersResponse = await SendAuthenticatedAsync(
                state,
                HttpMethod.Get,
                $"dbs/{Uri.EscapeDataString(dbId)}/colls",
                "colls",
                $"dbs/{dbId}",
                null,
                null,
                cancellationToken);
            await EnsureSuccessAsync(containersResponse, cancellationToken);
            var containersPayload = await ReadJsonAsync(containersResponse, cancellationToken);
            var containers = containersPayload?["DocumentCollections"] as JsonArray ?? [];
            var containerArray = databaseExport["containers"]!.AsArray();

            foreach (var containerNode in containers)
            {
                if (containerNode is not JsonObject container)
                {
                    continue;
                }

                var containerId = container["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(containerId))
                {
                    continue;
                }

                var containerExport = new JsonObject
                {
                    ["id"] = containerId,
                    ["partitionKey"] = container["partitionKey"]?.DeepClone(),
                    ["indexingPolicy"] = container["indexingPolicy"]?.DeepClone(),
                    ["defaultTtl"] = container["defaultTtl"]?.DeepClone(),
                    ["documents"] = new JsonArray()
                };

                var queryBody = new JsonObject
                {
                    ["query"] = "SELECT * FROM c",
                    ["parameters"] = new JsonArray()
                };
                var queryHeaders = new Dictionary<string, string>
                {
                    [CosmosHeaders.IsQuery] = "true",
                    [CosmosHeaders.EnableCrossPartition] = "true"
                };

                var documentsResponse = await SendAuthenticatedAsync(
                    state,
                    HttpMethod.Post,
                    $"dbs/{Uri.EscapeDataString(dbId)}/colls/{Uri.EscapeDataString(containerId)}/docs",
                    "docs",
                    $"dbs/{dbId}/colls/{containerId}",
                    queryBody,
                    queryHeaders,
                    cancellationToken);
                await EnsureSuccessAsync(documentsResponse, cancellationToken);
                var documentsPayload = await ReadJsonAsync(documentsResponse, cancellationToken);
                var documents = documentsPayload?["Documents"] as JsonArray ?? [];
                var documentArray = containerExport["documents"]!.AsArray();
                foreach (var document in documents)
                {
                    documentArray.Add(document?.DeepClone());
                }

                containerArray.Add(containerExport);
            }

            databaseArray.Add(databaseExport);
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        await File.WriteAllTextAsync(fullOutputPath, exportDocument.ToJsonString(StateJsonOptions), cancellationToken);
        Console.WriteLine($"Exported emulator data to {fullOutputPath}");
        return 0;
    }

    private static async Task<int> ImportAsync(string inputPath, CancellationToken cancellationToken)
    {
        var fullInputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInputPath))
        {
            Console.Error.WriteLine($"Input file was not found: {fullInputPath}");
            return 1;
        }

        var state = await RequireRunningStateAsync(cancellationToken);
        if (state is null)
        {
            return 1;
        }

        var document = JsonNode.Parse(await File.ReadAllTextAsync(fullInputPath, cancellationToken)) as JsonObject;
        var databases = document?["databases"] as JsonArray;
        if (databases is null)
        {
            Console.Error.WriteLine("Import file is missing a 'databases' array.");
            return 1;
        }

        foreach (var databaseNode in databases)
        {
            if (databaseNode is not JsonObject database)
            {
                continue;
            }

            var dbId = database["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(dbId))
            {
                continue;
            }

            await CreateIfMissingAsync(
                state,
                "dbs",
                string.Empty,
                new JsonObject { ["id"] = dbId },
                "dbs",
                cancellationToken);

            var containers = database["containers"] as JsonArray ?? [];
            foreach (var containerNode in containers)
            {
                if (containerNode is not JsonObject container)
                {
                    continue;
                }

                var containerId = container["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(containerId))
                {
                    continue;
                }

                var containerBody = new JsonObject
                {
                    ["id"] = containerId,
                    ["partitionKey"] = container["partitionKey"]?.DeepClone() ?? new JsonObject
                    {
                        ["paths"] = new JsonArray("/id"),
                        ["kind"] = "Hash",
                        ["version"] = 2
                    }
                };

                if (container["indexingPolicy"] is not null)
                {
                    containerBody["indexingPolicy"] = container["indexingPolicy"]!.DeepClone();
                }

                if (container["defaultTtl"] is not null)
                {
                    containerBody["defaultTtl"] = container["defaultTtl"]!.DeepClone();
                }

                await CreateIfMissingAsync(
                    state,
                    "colls",
                    $"dbs/{dbId}",
                    containerBody,
                    $"dbs/{Uri.EscapeDataString(dbId)}/colls",
                    cancellationToken);

                var documents = container["documents"] as JsonArray ?? [];
                foreach (var documentNode in documents)
                {
                    if (documentNode is not JsonObject importDocument)
                    {
                        continue;
                    }

                    var body = importDocument.DeepClone().AsObject();
                    body.Remove("_rid");
                    body.Remove("_self");
                    body.Remove("_etag");
                    body.Remove("_ts");
                    body.Remove("_attachments");

                    var headers = new Dictionary<string, string>
                    {
                        [CosmosHeaders.IsUpsert] = "true"
                    };

                    var response = await SendAuthenticatedAsync(
                        state,
                        HttpMethod.Post,
                        $"dbs/{Uri.EscapeDataString(dbId)}/colls/{Uri.EscapeDataString(containerId)}/docs",
                        "docs",
                        $"dbs/{dbId}/colls/{containerId}",
                        body,
                        headers,
                        cancellationToken);
                    await EnsureSuccessAsync(response, cancellationToken);
                }
            }
        }

        Console.WriteLine($"Imported emulator data from {fullInputPath}");
        return 0;
    }

    private static async Task<EmulatorInstanceState?> RequireRunningStateAsync(CancellationToken cancellationToken)
    {
        var state = TryLoadCurrentState();
        if (state is null)
        {
            Console.Error.WriteLine("Emulator is not running.");
            return null;
        }

        if (!IsProcessRunning(state) || !await IsEndpointHealthyAsync(state, cancellationToken))
        {
            CleanupStateFiles(state);
            Console.Error.WriteLine("Emulator is not running.");
            return null;
        }

        return state;
    }

    private static async Task CreateIfMissingAsync(
        EmulatorInstanceState state,
        string resourceType,
        string resourceLink,
        JsonObject body,
        string path,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedAsync(
            state,
            HttpMethod.Post,
            path,
            resourceType,
            resourceLink,
            body,
            null,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static ProcessStartInfo BuildSelfStartInfo(StartOptions options)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to determine the CLI executable path.");
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                throw new InvalidOperationException("Unable to determine the CLI assembly path.");
            }

            startInfo.FileName = processPath;
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }
        else
        {
            startInfo.FileName = processPath;
        }

        startInfo.ArgumentList.Add("start");
        startInfo.ArgumentList.Add("--run-host-internal");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(options.Port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--mongo-port");
        startInfo.ArgumentList.Add(options.MongoPort.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(options.DataDirectory!);
        startInfo.ArgumentList.Add("--consistency");
        startInfo.ArgumentList.Add(options.Consistency);

        if (!string.IsNullOrWhiteSpace(options.MasterKey))
        {
            startInfo.ArgumentList.Add("--key");
            startInfo.ArgumentList.Add(options.MasterKey);
        }

        if (options.EnableEntraId)
        {
            startInfo.ArgumentList.Add("--enable-entra");
        }

        if (options.Verbose)
        {
            startInfo.ArgumentList.Add("--verbose");
        }

        return startInfo;
    }

    private static Dictionary<string, string?> BuildConfigurationOverrides(StartOptions options) => new()
    {
        ["Emulator:Port"] = options.Port.ToString(CultureInfo.InvariantCulture),
        ["Emulator:MongoPort"] = options.MongoPort.ToString(CultureInfo.InvariantCulture),
        ["Emulator:DataDirectory"] = options.DataDirectory,
        ["Emulator:MasterKey"] = options.MasterKey,
        ["Emulator:EnableEntraId"] = options.EnableEntraId.ToString(),
        ["Emulator:ConsistencyLevel"] = options.Consistency,
        ["Emulator:Verbose"] = options.Verbose.ToString(),
        ["Emulator:EnableSsl"] = bool.FalseString,
        ["Emulator:EnableExplorer"] = bool.TrueString
    };

    private static async Task<HttpResponseMessage> SendAuthenticatedAsync(
        EmulatorInstanceState state,
        HttpMethod method,
        string path,
        string resourceType,
        string resourceLink,
        JsonNode? body,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(state);
        using var request = new HttpRequestMessage(method, path);

        var date = DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture);
        var authProvider = new MasterKeyAuthProvider(state.MasterKey);
        request.Headers.TryAddWithoutValidation(CosmosHeaders.Authorization, authProvider.GenerateAuthHeader(method.Method, resourceType, resourceLink, date));
        request.Headers.TryAddWithoutValidation("x-ms-date", date);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(CosmosHeaders.JsonContentType));

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, CosmosHeaders.JsonContentType);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). {responseText}");
    }

    private static async Task<JsonObject?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return JsonNode.Parse(text) as JsonObject;
    }

    private static HttpClient CreateHttpClient(EmulatorInstanceState state)
    {
        var handler = new HttpClientHandler();
        if (state.EnableSsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"{state.Endpoint.TrimEnd('/')}/")
        };
    }

    private static async Task<bool> IsEndpointHealthyAsync(EmulatorInstanceState state, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateHttpClient(state);
            using var response = await client.GetAsync("health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void PrintConnectionInfo(EmulatorInstanceState state, bool includeStatus)
    {
        var mongoEndpoint = $"mongodb://localhost:{state.MongoPort}";
        var connectionString = $"AccountEndpoint={state.Endpoint};AccountKey={state.MasterKey};";

        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  Azure Cosmos DB Light Emulator                                        ║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════════════════╣");
        if (includeStatus)
            Console.WriteLine($"║  Status:           {"Running (PID " + state.ProcessId + ")",-53}║");
        Console.WriteLine($"║  NoSQL Endpoint:   {state.Endpoint,-53}║");
        Console.WriteLine($"║  MongoDB Endpoint: {mongoEndpoint,-53}║");
        Console.WriteLine($"║  Consistency:      {state.ConsistencyLevel,-53}║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  Master Key:                                                          ║");
        Console.WriteLine($"║    {state.MasterKey,-70}║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  Connection String:                                                   ║");
        for (var i = 0; i < connectionString.Length; i += 70)
        {
            var chunk = connectionString.Substring(i, Math.Min(70, connectionString.Length - i));
            Console.WriteLine($"║    {chunk,-70}║");
        }
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static StartOptions Normalize(StartOptions options)
    {
        var dataDirectory = string.IsNullOrWhiteSpace(options.DataDirectory)
            ? DefaultDataDirectory
            : Path.GetFullPath(options.DataDirectory);
        var masterKey = string.IsNullOrWhiteSpace(options.MasterKey)
            ? MasterKeyAuthProvider.GenerateMasterKey()
            : options.MasterKey;

        Directory.CreateDirectory(dataDirectory);

        // Resolve available ports
        Console.WriteLine("Checking port availability...");
        var (noSqlPort, mongoPort) = PortHelper.ResolveAvailablePorts(options.Port, options.MongoPort);

        return options with
        {
            Port = noSqlPort,
            MongoPort = mongoPort,
            DataDirectory = dataDirectory,
            MasterKey = masterKey,
            Consistency = string.IsNullOrWhiteSpace(options.Consistency) ? DefaultConsistency : options.Consistency
        };
    }

    private static EmulatorInstanceState? TryLoadCurrentState()
    {
        var currentInstance = TryLoadState<CurrentInstancePointer>(CurrentInstanceFilePath);
        var candidateDirectories = new[]
        {
            currentInstance?.DataDirectory,
            DefaultDataDirectory
        };

        foreach (var dataDirectory in candidateDirectories.Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var state = TryLoadState(GetStateFilePath(dataDirectory!));
            if (state is not null)
            {
                return state;
            }
        }

        return null;
    }

    private static EmulatorInstanceState? TryLoadState(string path) => TryLoadState<EmulatorInstanceState>(path);

    private static T? TryLoadState<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), StateJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task PersistStateAsync(EmulatorInstanceState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(state.DataDirectory);
        Directory.CreateDirectory(GlobalStateDirectory);

        await File.WriteAllTextAsync(GetPidFilePath(state.DataDirectory), state.ProcessId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await File.WriteAllTextAsync(GetStateFilePath(state.DataDirectory), JsonSerializer.Serialize(state, StateJsonOptions), cancellationToken);
        await File.WriteAllTextAsync(CurrentInstanceFilePath, JsonSerializer.Serialize(new CurrentInstancePointer(state.DataDirectory), StateJsonOptions), cancellationToken);
    }

    private static void CleanupStateFiles(EmulatorInstanceState state)
    {
        if (!string.IsNullOrWhiteSpace(state.DataDirectory))
        {
            TryDelete(GetPidFilePath(state.DataDirectory));
            TryDelete(GetStateFilePath(state.DataDirectory));
        }

        var current = TryLoadState<CurrentInstancePointer>(CurrentInstanceFilePath);
        if (current is null || string.Equals(current.DataDirectory, state.DataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(CurrentInstanceFilePath);
        }
    }

    private static bool IsProcessRunning(EmulatorInstanceState state)
    {
        if (state.ProcessId <= 0)
        {
            return false;
        }

        try
        {
            var process = Process.GetProcessById(state.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            if (state.ProcessStartedAtUtc == default)
            {
                return true;
            }

            var startTime = process.StartTime.ToUniversalTime();
            return Math.Abs((startTime - state.ProcessStartedAtUtc.UtcDateTime).TotalSeconds) < 5;
        }
        catch
        {
            return false;
        }
    }

    private static Option<int> CreateIntOption(string alias, int defaultValue, string description) =>
        new(alias)
        {
            Description = description,
            DefaultValueFactory = _ => defaultValue
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string DefaultDataDirectory => Path.Combine(GlobalStateDirectory, "data");

    private static string GlobalStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CosmosEmulator");

    private static string CurrentInstanceFilePath => Path.Combine(GlobalStateDirectory, "current-instance.json");

    private static string GetPidFilePath(string dataDirectory) => Path.Combine(dataDirectory, PidFileName);

    private static string GetStateFilePath(string dataDirectory) => Path.Combine(dataDirectory, StateFileName);

    private sealed record StartOptions(
        int Port,
        int MongoPort,
        string? DataDirectory,
        string? MasterKey,
        bool EnableEntraId,
        string Consistency,
        bool Verbose,
        bool Background);

    private sealed record CurrentInstancePointer(string DataDirectory);

    private sealed record EmulatorInstanceState
    {
        public int ProcessId { get; init; }
        public DateTimeOffset ProcessStartedAtUtc { get; init; }
        public int Port { get; init; }
        public int MongoPort { get; init; }
        public string DataDirectory { get; init; } = string.Empty;
        public string MasterKey { get; init; } = string.Empty;
        public bool EnableEntraId { get; init; }
        public string ConsistencyLevel { get; init; } = DefaultConsistency;
        public bool Verbose { get; init; }
        public bool EnableSsl { get; init; }
        public string Endpoint => $"{(EnableSsl ? "https" : "http")}://localhost:{Port}";

        public static EmulatorInstanceState Create(StartOptions options, int processId) => new()
        {
            ProcessId = processId,
            ProcessStartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            Port = options.Port,
            MongoPort = options.MongoPort,
            DataDirectory = options.DataDirectory ?? DefaultDataDirectory,
            MasterKey = options.MasterKey ?? string.Empty,
            EnableEntraId = options.EnableEntraId,
            ConsistencyLevel = options.Consistency,
            Verbose = options.Verbose,
            EnableSsl = false
        };
    }
}
