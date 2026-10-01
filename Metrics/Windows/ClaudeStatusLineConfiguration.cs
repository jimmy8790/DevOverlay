using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;

namespace DevOverlay.Metrics.Windows;

public enum ClaudeStatusLineConfigurationResult { Configured, ExistingCustomStatusLine, Error }

/// <summary>Explicitly configures Claude Code's supported statusLine hook; an existing non-DevOverlay hook is never replaced.</summary>
public sealed class ClaudeStatusLineConfiguration(string? settingsPath = null)
{
    public string SettingsPath { get; } = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    public ClaudeStatusLineConfigurationResult Configure(string executablePath)
    {
        try
        {
            JsonObject root;
            if (File.Exists(SettingsPath))
            {
                root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? throw new JsonException();
                if (root["statusLine"] is not null && !IsDevOverlayHook(root["statusLine"])) return ClaudeStatusLineConfigurationResult.ExistingCustomStatusLine;
            }
            else root = new JsonObject();
            root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = $"\"{executablePath}\" {ClaudeStatusLineBridge.Argument}", ["padding"] = 0 };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, SettingsPath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return ClaudeStatusLineConfigurationResult.Configured;
        }
        catch (IOException) { return ClaudeStatusLineConfigurationResult.Error; }
        catch (UnauthorizedAccessException) { return ClaudeStatusLineConfigurationResult.Error; }
        catch (JsonException) { return ClaudeStatusLineConfigurationResult.Error; }
    }
    private static bool IsDevOverlayHook(JsonNode? node) => node?["command"]?.GetValue<string>()?.Contains(ClaudeStatusLineBridge.Argument, StringComparison.OrdinalIgnoreCase) == true;
}
