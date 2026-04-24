using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NMica.Containers;

/// <summary>
/// Mutable image-configuration document (the JSON that sits at <c>manifest.config</c>). Built by
/// cloning the base image's config and then appending to it: new <c>rootfs.diff_ids</c> entries
/// for each layer added, plus any user-overridden env / ports / entrypoint / user / workdir.
/// </summary>
/// <remarks>
/// Mirrors <c>Microsoft.NET.Build.Containers.ImageConfig</c>. Invariant we preserve from the SDK:
/// <c>rootfs.diff_ids.length == len(history where empty_layer != true)</c>. Several registries
/// (notably JFrog Artifactory) reject pushes that violate this, so <see cref="Build"/> pads the
/// history list with empty entries.
/// </remarks>
public sealed class ImageConfig
{
    private readonly JsonObject _config;
    private readonly List<string> _diffIds;
    private readonly List<JsonObject> _history;

    public ImageConfig(string baseConfigJson)
    {
        _config = JsonNode.Parse(baseConfigJson)?.AsObject()
            ?? throw new FormatException("Base image config was not a JSON object");

        _diffIds = (_config["rootfs"]?["diff_ids"] as JsonArray)
            ?.Select(n => n!.GetValue<string>()).ToList() ?? new();
        _history = (_config["history"] as JsonArray)
            ?.Select(n => (JsonObject)n!.DeepClone()).ToList() ?? new();
    }

    /// <summary>Append a layer. Pushes the uncompressed digest onto <c>rootfs.diff_ids</c>.</summary>
    public void AddLayer(Layer layer)
    {
        if (layer.Descriptor.UncompressedDigest is null)
            throw new InvalidOperationException("Layer descriptor is missing UncompressedDigest");
        _diffIds.Add(layer.Descriptor.UncompressedDigest);
        _history.Add(new JsonObject
        {
            ["created"] = DateTimeOffset.UtcNow.ToString("O"),
            ["created_by"] = "NMica: layer " + ShortDigest(layer.Descriptor.UncompressedDigest),
        });
    }

    /// <summary>Append a "meta" history row that doesn't add a new layer (e.g. ENV/LABEL changes).</summary>
    public void AddEmptyHistory(string note)
    {
        _history.Add(new JsonObject
        {
            ["created"] = DateTimeOffset.UtcNow.ToString("O"),
            ["created_by"] = note,
            ["empty_layer"] = true,
        });
    }

    public void AddLabel(string key, string value) => UpsertDictEntry("Labels", key, value);
    public void AddEnvironmentVariable(string keyValuePair) => UpsertListEntry("Env", keyValuePair);
    public void ExposePort(string portSpec) => UpsertDictEntry("ExposedPorts", portSpec, new JsonObject());
    public void SetWorkingDirectory(string wd) => SetConfigField("WorkingDir", wd);
    public void SetUser(string user) => SetConfigField("User", user);

    public void SetEntrypointAndCmd(IEnumerable<string>? entrypoint, IEnumerable<string>? cmd)
    {
        var cfg = GetOrCreateConfig();
        if (entrypoint is not null)
        {
            cfg["Entrypoint"] = new JsonArray(entrypoint.Select(s => (JsonNode)s!).ToArray());
        }
        if (cmd is not null)
        {
            cfg["Cmd"] = new JsonArray(cmd.Select(s => (JsonNode)s!).ToArray());
        }
    }

    /// <summary>
    /// Emit the finished config JSON. Rewrites <c>rootfs.diff_ids</c>, re-stamps <c>created</c>,
    /// and pads the history array so the non-empty-layer count matches <c>diff_ids.length</c>.
    /// </summary>
    public string Build()
    {
        // rootfs
        var rootfs = _config["rootfs"] as JsonObject ?? new JsonObject();
        rootfs["type"] = "layers";
        rootfs["diff_ids"] = new JsonArray(_diffIds.Select(d => (JsonNode)d!).ToArray());
        _config["rootfs"] = rootfs;

        // created
        _config["created"] = DateTimeOffset.UtcNow.ToString("O");

        // history — pad non-empty-layer entries to match diff_id count.
        var nonEmptyCount = _history.Count(h => h["empty_layer"]?.GetValue<bool>() != true);
        while (nonEmptyCount < _diffIds.Count)
        {
            _history.Add(new JsonObject { ["created"] = DateTimeOffset.UtcNow.ToString("O") });
            nonEmptyCount++;
        }
        _config["history"] = new JsonArray(_history.Select(h => (JsonNode)h.DeepClone()).ToArray());

        return _config.ToJsonString(SerializerOptions);
    }

    private JsonObject GetOrCreateConfig()
    {
        if (_config["config"] is not JsonObject cfg)
        {
            cfg = new JsonObject();
            _config["config"] = cfg;
        }
        return cfg;
    }

    private void SetConfigField(string key, string value)
    {
        GetOrCreateConfig()[key] = value;
    }

    private void UpsertDictEntry(string field, string key, JsonNode value)
    {
        var cfg = GetOrCreateConfig();
        if (cfg[field] is not JsonObject dict)
        {
            dict = new JsonObject();
            cfg[field] = dict;
        }
        dict[key] = value;
    }

    private void UpsertListEntry(string field, string value)
    {
        var cfg = GetOrCreateConfig();
        if (cfg[field] is not JsonArray list)
        {
            list = new JsonArray();
            cfg[field] = list;
        }
        list.Add(value);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private static string ShortDigest(string digest) =>
        digest.Length > 19 ? digest[..19] : digest;
}
