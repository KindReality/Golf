using Forms = System.Windows.Forms;

namespace ExperienceX;

internal enum ConfigurationSubmenu { None, AddScreen, Background }

internal static class ConfigurationSubmenuChoices
{
    public static string Text(ConfigurationSubmenu menu) => menu switch
    {
        ConfigurationSubmenu.AddScreen => "V: video   M: mat   G: visualization   L: LEDs   Esc: cancel",
        ConfigurationSubmenu.Background => "B: black   W: white   Esc: cancel",
        _ => ""
    };
    public static bool Accepts(ConfigurationSubmenu menu, Forms.Keys key) => key == Forms.Keys.Escape || menu switch
    {
        ConfigurationSubmenu.AddScreen => key is Forms.Keys.V or Forms.Keys.M or Forms.Keys.G or Forms.Keys.L,
        ConfigurationSubmenu.Background => key is Forms.Keys.B or Forms.Keys.W,
        _ => false
    };
}
