using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace Figurines;

/// <summary>
/// A placed figurine blank: remembers what the player chose to carve. The choice arrives from the
/// client's chooser window as a block entity packet and is checked here against the definitions,
/// so a client can only ask for a creature and pose that may actually be carved.
/// </summary>
public sealed class BEFigurineBlank : BlockEntity
{
    public const int ChoosePacket = 1001;

    public string Creature { get; private set; }
    public string Pose { get; private set; }

    public bool HasChoice => !string.IsNullOrEmpty(Creature);

    public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
    {
        base.OnReceivedClientPacket(fromPlayer, packetid, data);
        if (packetid != ChoosePacket || data == null) return;
        if (!Api.World.Claims.TryAccess(fromPlayer, Pos, EnumBlockAccessFlags.Use)) return;

        var parts = Encoding.UTF8.GetString(data).Split('|');
        if (parts.Length != 2) return;
        var def = Api.ModLoader.GetModSystem<FigurinesModSystem>().Creature(parts[0]);
        var pose = def?.PoseByCode(parts[1]);
        if (pose == null || !pose.Carvable) return;

        Creature = def.Code;
        Pose = pose.Code;
        MarkDirty(true);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (Creature != null) tree.SetString("creature", Creature);
        if (Pose != null) tree.SetString("pose", Pose);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Creature = tree.GetString("creature");
        Pose = tree.GetString("pose");
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        dsc.AppendLine(HasChoice
            ? Lang.Get("figurines:blank-carving", ItemFigurine.CreatureName(Creature), Lang.Get("figurines:pose-" + Pose))
            : Lang.Get("figurines:blank-choose"));
    }
}
