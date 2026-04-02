using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Azure.Cosmos.LightEmulator.NoSql.Controllers;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Azure.Cosmos.LightEmulator.Host.Controllers;

public sealed class EmulatorConfigRequest
{
    [JsonPropertyName("storage")]
    public string? Storage { get; set; }

    [JsonPropertyName("dataDirectory")]
    public string? DataDirectory { get; set; }
}

public sealed class EmulatorConfigResponse
{
    [JsonPropertyName("storage")]
    public string Storage { get; set; } = "SurrealDb";

    [JsonPropertyName("dataDirectory")]
    public string DataDirectory { get; set; } = "";

    [JsonPropertyName("restartRequired")]
    public bool RestartRequired { get; set; }
}

[ApiController]
[Route("api/emulator")]
public sealed class EmulatorConfigController(
    IOptions<EmulatorOptions> emulatorOptions,
    CosmosResponseHeaderService responseHeaders) : CosmosControllerBase(responseHeaders)
{
    private static readonly string ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "emulator-config.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig(CancellationToken ct)
    {
        await SetCommonHeadersAsync(ct: ct);

        var options = emulatorOptions.Value;
        var fileConfig = ReadConfigFile();

        return Ok(new EmulatorConfigResponse
        {
            Storage = fileConfig?["Emulator"]?["Storage"]?.GetValue<string>() ?? options.Storage,
            DataDirectory = fileConfig?["Emulator"]?["DataDirectory"]?.GetValue<string>() is { Length: > 0 } dir
                ? dir
                : options.DataDirectory,
            RestartRequired = false
        });
    }

    [HttpPut("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] EmulatorConfigRequest request, CancellationToken ct)
    {
        await SetCommonHeadersAsync(ct: ct);

        var options = emulatorOptions.Value;

        // Read existing file config or create new one
        var fileConfig = ReadConfigFile() ?? new JsonObject();
        var emulatorSection = fileConfig["Emulator"]?.AsObject() ?? new JsonObject();
        fileConfig["Emulator"] = emulatorSection;

        // Update only the fields that were provided
        if (request.Storage is not null)
            emulatorSection["Storage"] = request.Storage;
        if (request.DataDirectory is not null)
            emulatorSection["DataDirectory"] = request.DataDirectory;

        // Write the file
        var json = fileConfig.ToJsonString(JsonOptions);
        await System.IO.File.WriteAllTextAsync(ConfigFilePath, json, ct);

        // Determine if a restart is needed (storage or dataDirectory changed from current runtime values)
        var restartRequired =
            (request.Storage is not null && !string.Equals(request.Storage, options.Storage, StringComparison.OrdinalIgnoreCase)) ||
            (request.DataDirectory is not null && !string.Equals(request.DataDirectory, options.DataDirectory, StringComparison.OrdinalIgnoreCase));

        return Ok(new EmulatorConfigResponse
        {
            Storage = emulatorSection["Storage"]?.GetValue<string>() ?? options.Storage,
            DataDirectory = emulatorSection["DataDirectory"]?.GetValue<string>() is { Length: > 0 } dir
                ? dir
                : options.DataDirectory,
            RestartRequired = restartRequired
        });
    }

    private static JsonObject? ReadConfigFile()
    {
        if (!System.IO.File.Exists(ConfigFilePath))
            return null;
        try
        {
            var text = System.IO.File.ReadAllText(ConfigFilePath);
            return JsonNode.Parse(text)?.AsObject();
        }
        catch
        {
            return null;
        }
    }
}
