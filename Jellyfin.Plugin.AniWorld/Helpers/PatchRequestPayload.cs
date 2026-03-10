using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AniWorld.Helpers;

/// <summary>
/// Payload model for File Transformation plugin callbacks.
/// </summary>
public class PatchRequestPayload
{
    /// <summary>Gets or sets the file contents to transform.</summary>
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
