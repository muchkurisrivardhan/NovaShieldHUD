using System.IO;
using System.Net.Http;
using Beacon.LocalAi;

namespace Beacon.UI;

/// <summary>The Prompt Ring page: which free local model to use, and its download.</summary>
public partial class SettingsWindow
{
    private sealed record LocalModelChoice(LocalModel Model)
    {
        public override string ToString() => $"{Model.Label}  —  {Model.SizeLabel}";
    }

    private CancellationTokenSource? _localDownloadCts;

    private void InitLocalAiPage()
    {
        LocalModelPicker.ItemsSource = LocalModels.All.Select(m => new LocalModelChoice(m)).ToList();
        LocalModelPicker.SelectionChanged += (_, _) => RefreshLocalModel();
        LocalModelDownload.Click += async (_, _) => await DownloadLocalModelAsync();
        LocalModelDelete.Click += (_, _) =>
        {
            if (LocalModelPicker.SelectedItem is not LocalModelChoice choice) return;
            try { LocalModels.Delete(choice.Model); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LocalModelStatus.Text = "Couldn't delete it (in use?). Try again in a few minutes.";
                return;
            }
            RefreshLocalModel();
        };
    }

    private void LoadLocalAiPage()
    {
        var current = LocalModels.Find(_settings.LocalModel);
        LocalModelPicker.SelectedItem = (LocalModelPicker.ItemsSource as IEnumerable<LocalModelChoice>)
            ?.FirstOrDefault(c => c.Model.Id == current.Id);
        RefreshLocalModel();
    }

    private void RefreshLocalModel()
    {
        if (LocalModelPicker.SelectedItem is not LocalModelChoice choice) return;
        var model = choice.Model;
        LocalModelNote.Text = $"{model.Note} Licence: {model.Licence}.";
        var downloaded = LocalModels.IsDownloaded(model);
        if (_localDownloadCts is null)
            LocalModelStatus.Text = downloaded ? "Downloaded and ready." : $"Not downloaded ({model.SizeLabel}).";
        LocalModelDownload.IsEnabled = !downloaded && _localDownloadCts is null;
        LocalModelDelete.IsEnabled = downloaded && _localDownloadCts is null;
    }

    private async Task DownloadLocalModelAsync()
    {
        if (LocalModelPicker.SelectedItem is not LocalModelChoice choice || _localDownloadCts is not null) return;
        var model = choice.Model;
        _localDownloadCts = new CancellationTokenSource();
        LocalModelDownload.IsEnabled = LocalModelDelete.IsEnabled = false;
        var progress = new Progress<double>(p => LocalModelStatus.Text = $"Downloading… {p:P0} of {model.SizeLabel}");
        try
        {
            await LocalModels.DownloadAsync(model, progress, _localDownloadCts.Token);
            LocalModelStatus.Text = "Downloaded and ready.";
            _settings.LocalModel = model.Id;   // the one just fetched is the one to use
            _settings.Save();
        }
        catch (OperationCanceledException)
        {
            LocalModelStatus.Text = "Download cancelled.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            LocalModelStatus.Text = "Download failed: " + e.Message;
        }
        finally
        {
            _localDownloadCts = null;
            RefreshLocalModel();
        }
    }
}
