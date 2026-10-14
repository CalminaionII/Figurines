using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace Figurines;

/// <summary>
/// WHAT THE TRADERS SELL AND BUY OF OURS, ADDED FROM CODE (Calm, 2026-10-09: "need to add Compat for the figurines to work
/// with useful taders as that replaces the stock list"). These used to be JSON patches adding to vanilla's trade lists,
/// but Useful Traders patches each list with a "replace" of the whole selling and buying lists, and the game applies
/// patches in mod order (vsessentialsmod JsonPatchLoader - the order the assets come in), so whichever ran second won.
///
/// So the entries live in <c>config/figurines/trading.json</c> (any mod may ship one) and are appended here, in
/// AssetsLoaded - after JsonPatchLoader (ExecuteOrder 0.05) has applied every mod's patches, and before any trader reads its
/// list (EntityTrader loads its tradePropsFile when it is created). Works with vanilla's lists and with any mod that
/// replaces or reshapes them, as long as the file keeps its selling/buying lists. Server only - traders trade there.
/// </summary>
public static class TradeLists
{
    public static void AddEntries(ICoreAPI api)
    {
        int added = 0, files = 0;
        foreach (var source in api.Assets.GetMany("config/figurines/trading"))
        {
            JArray entries;
            try { entries = JArray.Parse(Encoding.UTF8.GetString(source.Data).TrimStart('﻿')); }
            catch (Exception e) { api.Logger.Error("[Figurines] Cannot read {0}: {1}", source.Location, e.Message); continue; }

            foreach (var entry in entries.OfType<JObject>())
            {
                if ((string)entry["file"] is not { } file) continue;
                var target = api.Assets.TryGet(new AssetLocation(file));
                if (target == null) { api.Logger.Warning("[Figurines] Trade list {0} not found - skipped", file); continue; }

                JObject list;
                try { list = JObject.Parse(Encoding.UTF8.GetString(target.Data).TrimStart('﻿')); }
                catch (Exception e) { api.Logger.Error("[Figurines] Cannot read trade list {0}: {1}", file, e.Message); continue; }

                int before = added;
                foreach (var side in new[] { "selling", "buying" })
                {
                    if (entry[side] is not JArray ours || ours.Count == 0) continue;
                    if (list[side]?["list"] is not JArray theirs)
                    {
                        api.Logger.Warning("[Figurines] Trade list {0} has no {1} list - our {1} entries skipped", file, side);
                        continue;
                    }
                    foreach (var item in ours) { theirs.Add(item.DeepClone()); added++; }
                }
                if (added == before) continue;
                target.Data = Encoding.UTF8.GetBytes(list.ToString(Formatting.None));
                files++;
            }
        }
        if (added > 0) api.Logger.Event("[Figurines] {0} trade entries added to {1} trader list(s)", added, files);
    }
}
