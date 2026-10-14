using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal static class PerspectiveChoices
{
    internal sealed record Choice(PerspectiveSelection? Value)
    {
        public override string ToString() => Value?.ToString() ?? "Full perspective — entire media";
    }

    public static void Populate(Forms.ListBox items, IReadOnlyList<PerspectiveSelection> available,
        PerspectiveSelection? current = null, bool led = false)
    {
        if (!led) items.Items.Add(new Choice(null));
        foreach (var perspective in available.Where(p => p.IsLed == led)) items.Items.Add(new Choice(perspective));
        if (current is not null && !available.Any(p => p.ControllerId == current.ControllerId && p.PerspectiveId == current.PerspectiveId))
            items.Items.Add(new Choice(current));
        if (items.Items.Count > 0) items.SelectedIndex = 0;
        for (var index = 0; index < items.Items.Count; index++)
            if (((Choice)items.Items[index]).Value is { } selected && selected.ControllerId == current?.ControllerId && selected.PerspectiveId == current.PerspectiveId)
                items.SelectedIndex = index;
    }
    public static PerspectiveSelection? Selected(Forms.ListBox items) => (items.SelectedItem as Choice)?.Value;
}
