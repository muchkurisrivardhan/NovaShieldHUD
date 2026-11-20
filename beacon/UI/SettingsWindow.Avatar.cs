using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Beacon.UI.Pet;

namespace Beacon.UI;

/// <summary>The Avatar page: the 3D pet's size, behaviour and moods, with a live preview.</summary>
public partial class SettingsWindow
{
    private string _avatarSize = "default";

    private void InitAvatarPage()
    {
        AvatarMoods.Click += (_, _) => AvatarPreview.Moods = AvatarMoods.IsChecked == true;
        // The preview wears the style picked on the Appearance page, saved or not.
        ThemePicker.SelectionChanged += (_, _) => RefreshAvatarColour();
        AccentColor.TextChanged += (_, _) => RefreshAvatarColour();
        // The preview looks at the pointer, as the real one does.
        AvatarPreview.MouseMove += (_, e) => AvatarPreview.LookAt = e.GetPosition(AvatarPreview);
        AvatarPreview.MouseLeave += (_, _) => AvatarPreview.LookAt = null;
        BrainAvatar.MouseMove += (_, e) => BrainAvatar.LookAt = e.GetPosition(BrainAvatar);
        BrainAvatar.MouseLeave += (_, _) => BrainAvatar.LookAt = null;
    }

    private void LoadAvatarPage()
    {
        _avatarSize = _settings.AvatarSize;
        AvatarMoods.IsChecked = _settings.AvatarMoods;
        PetFollows.IsChecked = _settings.PetFollowsCursor;
        PushToTalkKey.SelectedItem = PushToTalkKey.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.PushToTalk)
                                     ?? PushToTalkKey.Items[0];
        AvatarPreview.FollowSettings(_settings);
        BrainAvatar.FollowSettings(_settings);
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
        AvatarSizes.Children.Clear();
        AvatarStates.Children.Clear();
        AvatarMoodSamples.Children.Clear();

        foreach (var (key, label) in new[] { ("small", "Small"), ("default", "Default"), ("large", "Large") })
            Chip(AvatarSizes, label, null, key == _avatarSize, () => { _avatarSize = key; BuildAvatarChips(); });

        foreach (var (state, label) in new[]
                 {
                     (NovaPet3D.Activity.Idle, "Idle"), (NovaPet3D.Activity.Listening, "Listening"),
                     (NovaPet3D.Activity.Thinking, "Thinking"), (NovaPet3D.Activity.Speaking, "Speaking"),
                 })
            Chip(AvatarStates, label, null, false, () =>
            {
                AvatarPreview.State = state;
                AvatarPreview.Level = state == NovaPet3D.Activity.Listening ? 0.12 : 0;
            });
        Chip(AvatarStates, "Appear", null, false, () => AvatarPreview.Appear());

        foreach (var (mood, label) in new[]
                 {
                     (NovaPet3D.Feeling.Calm, "Calm"), (NovaPet3D.Feeling.Happy, "Happy"),
                     (NovaPet3D.Feeling.Focused, "Focused"), (NovaPet3D.Feeling.Concerned, "Concerned"),
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(NovaPet3D.MoodColor(AvatarPreview.Accent, mood)),
            });
            row.Children.Add(new TextBlock { Text = label, FontSize = 12.5 });
            var b = new Button { Style = (Style)FindResource("AiChip"), MinWidth = 0, Content = row };
            b.Click += (_, _) => AvatarPreview.Mood = mood;
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
        _settings.AvatarSize = _avatarSize;
        _settings.AvatarMoods = AvatarMoods.IsChecked == true;
        _settings.PetFollowsCursor = PetFollows.IsChecked == true;
        _settings.PushToTalk = (PushToTalkKey.SelectedItem as ComboBoxItem)?.Tag as string ?? "ctrl+alt";
    }
}
