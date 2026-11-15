using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public static class MenuTabBar
{
    private static readonly Color Gold = new("#f3deb0");

    public static void Draw(Control canvas, IReadOnlyDictionary<string, string> assets,
        Func<string, Texture2D?> texture, string currentPage, Action<string>? show)
    {
        for (var index = 0; index < MenuTabs.All.Length; index += 1)
        {
            var tab = MenuTabs.All[index];
            var x = MenuTabs.FirstX + index * MenuTabs.Stride;
            var page = tab.Page;

            var button = PanelGfx.Hit(x, MenuTabs.Y, MenuTabs.Width, MenuTabs.Height,
                () => show?.Invoke(page), canvas);

            PanelGfx.Image(texture, assets.GetValueOrDefault(MenuTabs.AssetKey(page)),
                MenuTabs.ImageX, MenuTabs.ImageY, MenuTabs.ImageSize, MenuTabs.ImageSize, button);

            PanelGfx.Centered(tab.Label, MenuTabs.LabelX - MenuTabs.LabelWidth / 2f, MenuTabs.LabelY,
                MenuTabs.LabelWidth, MenuTabs.LabelSize, Gold, button);

            if (page != currentPage)
                button.Modulate = new Color(1f, 1f, 1f, MenuTabs.InactiveAlpha);
        }
    }
}
