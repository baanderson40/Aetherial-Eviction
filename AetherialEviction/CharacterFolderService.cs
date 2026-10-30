using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AetherialEviction;

internal sealed unsafe class CharacterFolderService
{
    private readonly Configuration _configuration;
    private readonly IPluginLog _log;
    private readonly HashSet<ulong> _processedContentIds = [];

    public CharacterFolderService(Configuration configuration, IPluginLog log)
    {
        _configuration = configuration;
        _log = log;
    }

    public string Status { get; private set; } = "Waiting for character selection or login.";

    public void ScanCurrentSelection()
    {
        if (!_configuration.CharacterFolderMarkersEnabled)
            return;

        try
        {
            var lobby = AgentLobby.Instance();
            if (lobby == null || lobby->LobbyData.CharaSelectEntries.Count == 0)
                return;

            var entries = lobby->LobbyData.CharaSelectEntries;
            var created = 0;
            var existing = 0;
            var missing = 0;
            var errors = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i].Value;
                if (entry == null || entry->ContentId == 0)
                    continue;

                var world = ExcelWorldHelper.GetName(entry->HomeWorldId);
                ProcessCharacter(entry->ContentId, entry->NameString, world,
                    ref created, ref existing, ref missing, ref errors);
            }

            UpdateStatus(created, existing, missing, errors);
        }
        catch (Exception ex)
        {
            Status = "Character-list scan failed; see plugin log.";
            _log.Error(ex, "[Aetherial Eviction] Failed to scan the character-selection list.");
        }
    }

    public void ScanLoggedInCharacter()
    {
        if (!_configuration.CharacterFolderMarkersEnabled)
            return;

        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
                return;

            var character = (Character*)player.Address;
            var world = ExcelWorldHelper.GetName((uint)player.HomeWorld.RowId);
            var created = 0;
            var existing = 0;
            var missing = 0;
            var errors = 0;
            ProcessCharacter(character->ContentId, player.Name.ToString(), world,
                ref created, ref existing, ref missing, ref errors);
            UpdateStatus(created, existing, missing, errors);
        }
        catch (Exception ex)
        {
            Status = "Login marker creation failed; see plugin log.";
            _log.Error(ex, "[Aetherial Eviction] Failed to create marker for logged-in character.");
        }
    }

    private void ProcessCharacter(ulong contentId, string name, string homeWorld,
        ref int created, ref int existing, ref int missing, ref int errors)
    {
        if (contentId == 0 || _processedContentIds.Contains(contentId))
            return;

        try
        {
            var userPath = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->UserPathString;
            var characterFolder = Path.Combine(userPath, $"FFXIV_CHR{contentId:X16}");
            if (!Directory.Exists(characterFolder))
            {
                missing++;
                return;
            }

            var markerName = $"_{SanitizeFileName(name)}@{SanitizeFileName(homeWorld)}";
            var markerPath = Path.Combine(characterFolder, markerName);
            if (File.Exists(markerPath))
            {
                _processedContentIds.Add(contentId);
                existing++;
                return;
            }

            using (new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
            }

            _processedContentIds.Add(contentId);
            created++;
        }
        catch (IOException)
        {
            try
            {
                if (File.Exists(GetMarkerPath(contentId, name, homeWorld)))
                {
                    // Another event or plugin instance created the same marker first.
                    _processedContentIds.Add(contentId);
                    existing++;
                    return;
                }
            }
            catch
            {
                // Report the original file operation failure below.
            }

            errors++;
            _log.Error("[Aetherial Eviction] Could not create marker for Content ID {ContentId:X16}.", contentId);
        }
        catch (Exception ex)
        {
            errors++;
            _log.Error(ex, "[Aetherial Eviction] Failed to create character folder marker for Content ID {ContentId:X16}.", contentId);
        }
    }

    private static string GetMarkerPath(ulong contentId, string name, string homeWorld)
    {
        var userPath = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->UserPathString;
        return Path.Combine(userPath, $"FFXIV_CHR{contentId:X16}",
            $"_{SanitizeFileName(name)}@{SanitizeFileName(homeWorld)}");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = new HashSet<char>("<>:\"/\\|?*".Concat(Enumerable.Range(0, 32).Select(i => (char)i)));
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private void UpdateStatus(int created, int existing, int missing, int errors)
    {
        Status = $"Character folders: {created} marker(s) created, {existing} already present, " +
                 $"{missing} folder(s) not found, {errors} error(s).";
    }
}
