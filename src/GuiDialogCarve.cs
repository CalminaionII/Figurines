using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Figurines;

/// <summary>
/// "Figurine Carving" - Calm's layout of 2026-10-08: a search box, filter tabs (All, Vanilla, then
/// one per mod that adds animals), a page of cards each showing the wooden figurine and its name,
/// page arrows, and underneath the selected one large with its POSE buttons (standing and one more)
/// and a "Select design" button. Every creature is its own card: search, tabs and paging keep any
/// number of modded animals manageable. The pick goes to the server as a block entity packet; it is
/// checked there.
///
/// The window is rebuilt on every filter / page / selection change; the search text is put back and
/// the search box takes focus again (it is the first focusable element), so typing is not broken.
/// </summary>
public sealed class GuiDialogCarve : GuiDialogGeneric
{
    private const int Cols = 4, Rows = 3, PerPage = Cols * Rows;
    private const double CardW = 205, CardH = 100, Gap = 6, IconSize = 68;

    private readonly BlockPos pos;
    private readonly List<(CreatureDefinition def, string name, ItemStack stack)> all = [];
    /// <summary>The filter tabs after All / Vanilla: one per other mod that adds creatures - (domain, the mod's name).</summary>
    private readonly List<(string domain, string name)> modTabs;
    private string search = "";
    private int filter, page;
    private string selected, selectedPose;
    private bool composing;

    public override string ToggleKeyCombinationCode => null;

    public GuiDialogCarve(ICoreClientAPI capi, BlockPos pos) : base(Lang.Get("figurines:carve-title"), capi)
    {
        this.pos = pos.Copy();
        foreach (var def in capi.ModLoader.GetModSystem<FigurinesModSystem>().Creatures)
        {
            var pose = def.CarvablePoses.FirstOrDefault();
            if (pose == null) continue;
            var stack = Wooden(def, pose.Code);
            if (stack != null) all.Add((def, Capitalise(ItemFigurine.CreatureName(def.Code)), stack));
        }
        // The game's first, by family then name; then other mods' the same way.
        all = all.OrderBy(c => IsModded(c.def) ? 1 : 0).ThenBy(c => c.def.GroupCode).ThenBy(c => c.def.Code == c.def.GroupCode ? "" : c.name).ToList();
        // Each mod its own tab (Calm, 2026-10-08), named after the mod, in name order.
        modTabs = all.Where(c => IsModded(c.def)).Select(c => new AssetLocation(c.def.Entity).Domain).Distinct()
            .Select(d => (d, capi.ModLoader.GetMod(d)?.Info?.Name ?? Capitalise(d))).OrderBy(t => t.Item2).ToList();
        Select(all.FirstOrDefault().def);
        Compose();
    }

    private ItemStack Wooden(CreatureDefinition def, string pose) =>
        ItemFigurine.Make(capi.World, new FigurineData { Creature = def.Code, Pose = pose, Material = "wood" });

    private void Select(CreatureDefinition def)
    {
        selected = def?.Code;
        selectedPose = def?.CarvablePoses.FirstOrDefault()?.Code;
    }

    private static bool IsModded(CreatureDefinition def) => new AssetLocation(def.Entity).Domain != "game";

    private List<(CreatureDefinition def, string name, ItemStack stack)> Shown() => all.Where(c =>
        (filter == 0 || (filter == 1 ? !IsModded(c.def) : new AssetLocation(c.def.Entity).Domain == modTabs[filter - 2].domain))
        && (search.Length == 0 || c.name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.def.Code.Contains(search, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    private void Compose()
    {
        composing = true;
        var shown = Shown();
        int pages = Math.Max(1, (int)Math.Ceiling(shown.Count / (double)PerPage));
        page = GameMath.Clamp(page, 0, pages - 1);
        double width = Cols * CardW + (Cols - 1) * Gap;

        var bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        var c = capi.Gui.CreateCompo("figurines-carve", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar(DialogTitle, () => TryClose())
            .BeginChildElements(bg);

        // Search, then the filter tabs.
        c.AddTextInput(ElementBounds.Fixed(0, 35, width, 30), OnSearch, CairoFont.WhiteSmallText(), "search");
        var names = new[] { Lang.Get("figurines:carve-tab-all"), Lang.Get("figurines:carve-tab-vanilla") }.Concat(modTabs.Select(m => m.name));
        var tabs = names.Select((t, i) => new GuiTab { Name = t, DataInt = i, Active = i == filter }).ToArray();
        c.AddHorizontalTabs(tabs, ElementBounds.Fixed(0, 75, width, 25), i => Later(() => { filter = i; page = 0; }), CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor), "filters");

        // The cards.
        double top = 110;
        var onPage = shown.Skip(page * PerPage).Take(PerPage).ToList();
        if (onPage.Count == 0)
            c.AddStaticText(Lang.Get("figurines:carve-nothing"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, top + 10, width, 30));
        for (int i = 0; i < onPage.Count; i++)
        {
            var card = onPage[i];
            double x = i % Cols * (CardW + Gap), y = top + i / Cols * (CardH + Gap);
            var def = card.def;
            c.AddInset(ElementBounds.Fixed(x, y, CardW, CardH), 2, def.Code == selected ? 0.95f : 0.6f);
            c.AddRichtext([new ItemstackTextComponent(capi, card.stack, IconSize, 0, EnumFloat.Left, _ => Later(() => Select(def)))],
                ElementBounds.Fixed(x + (CardW - IconSize) / 2, y + 2, IconSize + 4, IconSize + 2));
            c.AddSmallButton(card.name, () => { Later(() => Select(def)); return true; }, ElementBounds.Fixed(x + 4, y + CardH - 26, CardW - 8, 22));
        }

        // Page arrows.
        double pager = top + Rows * (CardH + Gap) + 4;
        c.AddSmallButton("<", () => { Later(() => page--); return true; }, ElementBounds.Fixed(width / 2 - 90, pager, 30, 24));
        c.AddStaticText(Lang.Get("figurines:carve-page", page + 1, pages), CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(width / 2 - 55, pager + 3, 110, 24));
        c.AddSmallButton(">", () => { Later(() => page++); return true; }, ElementBounds.Fixed(width / 2 + 60, pager, 30, 24));

        // The selected one, large, in the chosen pose; its pose buttons; the button that confirms it.
        double details = pager + 40;
        var sel = all.FirstOrDefault(x => x.def.Code == selected);
        c.AddInset(ElementBounds.Fixed(0, details, 110, 110), 3, 0.7f);
        if (sel.def != null)
        {
            var preview = Wooden(sel.def, selectedPose) ?? sel.stack;
            c.AddRichtext([new ItemstackTextComponent(capi, preview, 100)], ElementBounds.Fixed(5, details + 5, 104, 104));
            c.AddStaticText(Lang.Get("figurines:carve-selected", sel.name), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(125, details + 2, width - 125, 30));
            c.AddStaticText(Lang.Get("figurines:carve-material"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(125, details + 32, width - 125, 24));
            // The pose, between arrows like the page control (Calm, 2026-10-08).
            var poses = sel.def.CarvablePoses.Select(p => p.Code).ToList();
            int at = Math.Max(0, poses.IndexOf(selectedPose));
            c.AddSmallButton("<", () => { Later(() => selectedPose = poses[(at + poses.Count - 1) % poses.Count]); return true; }, ElementBounds.Fixed(125, details + 58, 30, 24));
            c.AddStaticText(Lang.Get("figurines:pose-" + poses[at]), CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(160, details + 61, 120, 24));
            c.AddSmallButton(">", () => { Later(() => selectedPose = poses[(at + 1) % poses.Count]); return true; }, ElementBounds.Fixed(285, details + 58, 30, 24));
            c.AddSmallButton(Lang.Get("figurines:carve-select"), OnConfirm, ElementBounds.Fixed(width - 150, details + 86, 150, 28));
        }

        SingleComposer = c.EndChildElements().Compose();
        var input = SingleComposer.GetTextInput("search");
        input.SetPlaceHolderText(Lang.Get("figurines:carve-search"));
        input.SetValue(search);
        SingleComposer.GetHorizontalTabs("filters").SetValue(filter, false);
        composing = false;
    }

    private void OnSearch(string text)
    {
        if (composing || text == search) return;
        search = text ?? "";
        Later(() => page = 0);
    }

    /// <summary>Change something, then rebuild - after the current click or keypress has finished, never inside the old window's handler.</summary>
    private void Later(Action change)
    {
        capi.Event.EnqueueMainThreadTask(() =>
        {
            change();
            if (IsOpened()) Compose();
        }, "figurines-carve");
    }

    private bool OnConfirm()
    {
        if (selected == null || selectedPose == null) return true;
        capi.Network.SendBlockEntityPacket(pos, BEFigurineBlank.ChoosePacket, Encoding.UTF8.GetBytes(selected + "|" + selectedPose));
        TryClose();
        return true;
    }

    private static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
