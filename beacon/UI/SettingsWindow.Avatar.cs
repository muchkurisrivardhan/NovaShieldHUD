using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Beacon.UI;

/// <summary>The Avatar page: Nova's look, its size, a live preview of every state and pose, and moods.</summary>
public partial class SettingsWindow
{
    private string _avatarLook = "globe";
    private string _avatarSize = "default";

    private void InitAvatarPage()
    {
        AvatarMoods.Click += (_, _) => AvatarPreview.Moods = AvatarMoods.IsChecked == true;
        // The preview wears the style picked on the Appearance page, saved or not.
        ThemePicker.SelectionChanged += (_, _) => RefreshAvatarColour();
        AccentColor.TextChanged += (_, _) => RefreshAvatarColour();
    }

    private void LoadAvatarPage()
    {
        _avatarLook = _settings.AvatarLook;
        _avatarSize = _settings.AvatarSize;
        AvatarMoods.IsChecked = _settings.AvatarMoods;
        AvatarPreview.FollowSettings(_settings);
        BuildAvatarChips();
    }

    private void RefreshAvatarColour()
    {
        if (AvatarPreview is null || ThemePicker is null) return;
        var draft = new Core.Settings
        {
            OverlayStyle = (ThemePicker.SelectedItem as ComboBoxItem)?.Tag as string ?? _settings.OverlayStyle,
            AccentColor = AccentColor.Text.Trim(),
            Theme = _settings.Theme,
        };
        try
        {
            var accent = (Color)ColorConverter.ConvertFromString(Core.Suite.PaletteFor(draft).Accent);
            AvatarPreview.Accent = accent;
            if (BrainAvatar is not null) BrainAvatar.Accent = accent;
        }
        catch (FormatException) { }
    }

    private void BuildAvatarChips()
    {
        AvatarLooks.Children.Clear();
        AvatarSizes.Children.Clear();
        AvatarStates.Children.Clear();
        AvatarMoodSamples.Children.Clear();

        var hasArt = AvatarArt.Available;
        foreach (var (key, label, sub) in new[]
                 {
                     ("character", "Character", hasArt ? "24 poses" : "add the art"),
                     ("globe", "LED globe", "dots of light"),
                     ("orb", "Orb", "a glowing sphere"),
                 })
        {
            var chip = Chip(AvatarLooks, label, sub, key == _avatarLook, () =>
            {
                _avatarLook = key;
                AvatarPreview.Look = key;
                if (BrainAvatar is not null) BrainAvatar.Look = key;
                BuildAvatarChips();
            });
            if (key == "character" && !hasArt) chip.ToolTip = "Put sheet.png (or one PNG per pose) in " + AvatarArt.UserFolder + ", then reopen Settings.";
        }
        AvatarPreview.Look = _avatarLook;

        foreach (var (key, label) in new[] { ("small", "Small"), ("default", "Default"), ("large", "Large") })
            Chip(AvatarSizes, label, null, key == _avatarSize, () => { _avatarSize = key; BuildAvatarChips(); });

        foreach (var (state, label) in new[]
                 {
                     (NovaAvatar.Activity.Idle, "Idle"), (NovaAvatar.Activity.Listening, "Listening"),
                     (NovaAvatar.Activity.Thinking, "Thinking"), (NovaAvatar.Activity.Speaking, "Speaking"),
                 })
            Chip(AvatarStates, label, null, false, () =>
            {
                AvatarPreview.State = state;
                AvatarPreview.Level = state == NovaAvatar.Activity.Listening ? 0.12 : 0;
            });

        // With the character, every pose can be tried: they're what Nova shows in its moments.
        if (_avatarLook == "character" && hasArt)
            foreach (var pose in AvatarArt.Poses)
                Chip(AvatarStates, pose, null, false, () => AvatarPreview.Flash(pose, 6));

        AvatarArtNote.Text = hasArt
            ? "The character's glow is recoloured to your Beacon style automatically."
            : "No character art yet. Put sheet.png (the 6 × 4 design sheet) or one PNG per pose in "
              + AvatarArt.UserFolder + " and reopen Settings.";

        foreach (var (mood, label) in new[]
                 {
                     (NovaAvatar.Feeling.Calm, "Calm"), (NovaAvatar.Feeling.Happy, "Happy"),
                     (NovaAvatar.Feeling.Focused, "Focused"), (NovaAvatar.Feeling.Concerned, "Concerned"),
                 })
        {
            var swatch = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(NovaAvatar.MoodColor(AvatarPreview.Accent, mood)),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = label, FontSize = 12.5 });
            var b = new Button { Style = (Style)FindResource("AiChip"), MinWidth = 0, Content = row };
            b.Click += (_, _) => { AvatarPreview.Mood = mood; AvatarPreview.Flash(mood switch
            {
                NovaAvatar.Feeling.Happy => "Happy",
                NovaAvatar.Feeling.Focused => "Idea",
                NovaAvatar.Feeling.Concerned => "Sad",
                _ => "Idle",
            }, 4); };
            AvatarMoodSamples.Children.Add(b);
        }
    }

    /// <summary>The same tile as the Brain page's options, so every AI setting looks alike.</summary>
    private Button Chip(Panel panel, string title, string? sub, bool on, Action click)
    {
        object content = title;
        if (sub is not null)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(new TextBlock { Text = sub, FontSize = 11, Opacity = 0.75 });
            content = stack;
        }
        var chip = new Button
        {
            Style = (Style)FindResource("AiChip"), Content = content, Tag = on ? "on" : null,
            MinWidth = sub is null ? 0 : 128,
        };
        chip.Click += (_, _) => click();
        panel.Children.Add(chip);
        return chip;
    }

    private void SaveAvatarPage()
    {
        _settings.AvatarLook = _avatarLook;
        _settings.AvatarSize = _avatarSize;
        _settings.AvatarMoods = AvatarMoods.IsChecked == true;
    }
}
