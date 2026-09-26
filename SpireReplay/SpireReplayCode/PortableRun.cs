using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpireReplay.SpireReplayCode;

/// <summary>Portable execution data, independent of diagnostic timestamps and session IDs.</summary>
public static class PortableRun
{
    public static byte[] Encode(RunRecording run, JsonSerializerOptions options)
    {
        var root = JsonSerializer.SerializeToNode(run, options)!.AsObject();
        root.Remove("storedAtUtc"); root.Remove("imported");
        foreach (var roomNode in root["rooms"]!.AsArray())
        {
            var room = roomNode!.AsObject();
            room["events"] = JsonSerializer.SerializeToNode(CancelledSelectionFilter.Effective(
                room["events"]!.Deserialize<List<RecordedEvent>>(options)!), options);
            CompactEvents(room["events"]!.AsArray(), false);
            if (room["battle"] is JsonObject battle)
            {
                CompactEvents(battle["events"]!.AsArray(), true);
                foreach (string field in new[] { "runId", "seed", "characterId", "gameVersion", "gameAssemblyId", "gameMode", "ascension" }) battle.Remove(field);
            }
        }
        Strip(root);
        return JsonSerializer.SerializeToUtf8Bytes(root, options);
    }
    public static void Restore(JsonObject root)
    {
        if (root["rooms"] is not JsonArray rooms) throw new InvalidDataException("复盘记录缺少房间");
        foreach (var roomNode in rooms)
        {
            if (roomNode is not JsonObject room) throw new InvalidDataException("房间数据无效");
            if (room["battle"] is not JsonObject battle) continue;
            foreach (string field in new[] { "runId", "seed", "characterId", "gameVersion", "gameAssemblyId", "gameMode", "ascension" }) battle[field] = root[field]?.DeepClone();
        }
    }
    private static void Strip(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var field in obj.ToList())
            {
                if (field.Key.ToLowerInvariant() is "atutc" or "startedatutc" or "endedatutc" or "operationid" or "eventoperationid" or
                    "eventinstanceid" or "sessionid" or "battleid" or "revisionid" or "modversion") obj.Remove(field.Key);
                else Strip(field.Value);
            }
        }
        else if (node is JsonArray array) foreach (var child in array) Strip(child);
    }
    private static void CompactEvents(JsonArray events, bool battle)
    {
        var kept = new List<JsonNode>();
        foreach (var node in events)
        {
            var entry = node!.AsObject();
            string type = entry["type"]!.GetValue<string>();
            if (type is "player_choice" or "rewards_offered" or "rewards_closed" or "reward_card_options" or "card_upgrade_started") continue;
            if (battle && type is not ("replay_action" or "cards_selected" or "combat_setup" or "undo_end_turn") && !type.StartsWith("potion_")) continue;
            if (entry["data"] is JsonObject data && data.ContainsKey("details"))
            {
                foreach (string field in new[] { "roomId", "totalFloor", "act", "roomType" }) data.Remove(field);
                if (type == "event_options" && data["details"] is JsonObject detail)
                    foreach (string field in detail.Select(p => p.Key).Where(k => k is not ("name" or "page")).ToList()) detail.Remove(field);
                if (data["details"] is JsonObject details)
                {
                    string[]? fields = type switch
                    {
                        "event_option_started" => ["eventId", "option"],
                        "event_option_finished" => ["outcome", "cardsAdded", "cardsRemoved"],
                        "rest_option_started" => ["name", "optionId", "optionIndex"],
                        "rest_option_finished" => ["success", "completed"],
                        "shop_purchase_started" => ["entryType", "item", "price"],
                        "shop_purchase_finished" => ["success", "completed", "failureReason", "cardsRemoved"],
                        "cards_selected" => ["cards"],
                        "potion_obtain_started" => ["potionId", "name"],
                        "card_upgrade_finished" => ["card", "success"],
                        _ => null
                    };
                    if (fields != null)
                        foreach (string field in details.Select(p => p.Key).Where(k => !fields.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) details.Remove(field);
                    if (type == "event_option_started" && details["option"] is JsonObject option)
                        foreach (string field in option.Select(p => p.Key).Where(k => k is not ("key" or "index" or "title")).ToList()) option.Remove(field);
                }
            }
            if (entry["state"] is JsonObject state)
            {
                if (type != "combat_setup") entry.Remove("state");
                else
                {
                    state["hand"] = new JsonArray(); state.Remove("energy"); state.Remove("stars");
                    if (state["creatures"] is JsonArray creatures)
                        foreach (var creature in creatures.OfType<JsonObject>())
                            foreach (string field in creature.Select(p => p.Key).Where(k => k is not ("combatId" or "name" or "modelId")).ToList()) creature.Remove(field);
                }
            }
            entry["sequence"] = kept.Count + 1;
            kept.Add(entry.DeepClone());
        }
        events.Clear(); foreach (var entry in kept) events.Add(entry);
    }
}
