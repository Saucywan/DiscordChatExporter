using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Utils.Extensions;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Models;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.Utils;
using DiscordChatExporter.Gui.Utils.Extensions;
using Gress;
using Gress.Completable;

namespace DiscordChatExporter.Gui.ViewModels.Components;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly ViewModelManager _viewModelManager;
    private readonly SnackbarManager _snackbarManager;
    private readonly DialogManager _dialogManager;
    private readonly DebugLogService _debugLogService;
    private readonly SettingsService _settingsService;

    private readonly DisposableCollector _eventRoot = new();
    private readonly AutoResetProgressMuxer _progressMuxer;

    private DiscordClient? _discord;

    public DashboardViewModel(
        ViewModelManager viewModelManager,
        DialogManager dialogManager,
        SnackbarManager snackbarManager,
        DebugLogService debugLogService,
        SettingsService settingsService
    )
    {
        _viewModelManager = viewModelManager;
        _dialogManager = dialogManager;
        _snackbarManager = snackbarManager;
        _debugLogService = debugLogService;
        _settingsService = settingsService;

        _progressMuxer = Progress.CreateMuxer().WithAutoReset();

        _eventRoot.Add(
            Progress.WatchProperty(
                o => o.Current,
                () => OnPropertyChanged(nameof(IsProgressIndeterminate))
            )
        );

        _eventRoot.Add(
            SelectedChannels.WatchProperty(
                o => o.Count,
                () => ExportCommand.NotifyCanExecuteChanged()
            )
        );
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(PullGuildsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullChannelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsBusy { get; set; }

    public ProgressContainer<Percentage> Progress { get; } = new();

    public bool IsProgressIndeterminate => IsBusy && Progress.Current.Fraction is <= 0 or >= 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullGuildsCommand))]
    public partial string? Token { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Guild>? AvailableGuilds { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullChannelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial Guild? SelectedGuild { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ChannelConnection>? AvailableChannels { get; set; }

    public ObservableCollection<ChannelConnection> SelectedChannels { get; } = [];

    [RelayCommand]
    private void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(_settingsService.LastToken))
            Token = _settingsService.LastToken;
    }

    [RelayCommand]
    private async Task ShowSettingsAsync() =>
        await _dialogManager.ShowDialogAsync(_viewModelManager.CreateSettingsViewModel());

    [RelayCommand]
    private void ShowHelp() => Process.StartShellExecute(Program.ProjectDocumentationUrl);

    private bool CanPullGuilds() => !IsBusy && !string.IsNullOrWhiteSpace(Token);

    [RelayCommand(CanExecute = nameof(CanPullGuilds))]
    private async Task PullGuildsAsync()
    {
        IsBusy = true;
        var progress = _progressMuxer.CreateInput();

        try
        {
            await _debugLogService.LogAsync("Pulling guild list...");

            var token = Token?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(token))
                return;

            AvailableGuilds = null;
            SelectedGuild = null;
            AvailableChannels = null;
            SelectedChannels.Clear();

            _discord = new DiscordClient(token, _settingsService.RateLimitPreference);
            _settingsService.LastToken = token;

            var guilds = await _discord.GetUserGuildsAsync();

            await _debugLogService.LogAsync($"Retrieved {guilds.Count} guild(s) for the user.");

            AvailableGuilds = guilds;
            SelectedGuild = guilds.FirstOrDefault();

            await PullChannelsAsync();
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
            await _debugLogService.LogExceptionAsync(ex, "Non-fatal error while pulling guilds");
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.CreateMessageBoxViewModel(
                "Error pulling servers",
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(dialog);

            await _debugLogService.LogExceptionAsync(ex, "Fatal error while pulling guilds");
        }
        finally
        {
            progress.ReportCompletion();
            IsBusy = false;
        }
    }

    private bool CanPullChannels() => !IsBusy && _discord is not null && SelectedGuild is not null;

    [RelayCommand(CanExecute = nameof(CanPullChannels))]
    private async Task PullChannelsAsync()
    {
        IsBusy = true;
        var progress = _progressMuxer.CreateInput();

        try
        {
            if (_discord is null || SelectedGuild is null)
                return;

            await _debugLogService.LogAsync(
                $"Pulling channels for guild {SelectedGuild.Id} ({SelectedGuild.Name}) with thread inclusion mode {_settingsService.ThreadInclusionMode}."
            );

            AvailableChannels = null;
            SelectedChannels.Clear();

            var channels = new List<Channel>();

            // Regular channels
            await foreach (var channel in _discord.GetGuildChannelsAsync(SelectedGuild.Id))
                channels.Add(channel);

            var baseChannelCount = channels.Count;
            var threadCount = 0;

            // Threads
            if (_settingsService.ThreadInclusionMode != ThreadInclusionMode.None)
            {
                await foreach (
                    var thread in _discord.GetGuildThreadsAsync(
                        SelectedGuild.Id,
                        _settingsService.ThreadInclusionMode == ThreadInclusionMode.All
                    )
                )
                {
                    channels.Add(thread);
                    threadCount++;
                }
            }

            // Build a hierarchy of channels
            var channelTree = ChannelConnection.BuildTree(
                channels
                .OrderByDescending(c => c.IsDirect ? c.LastMessageId : null)
                .ThenBy(c => c.Position)
                .ToArray()
            );

            await _debugLogService.LogAsync(
                $"Loaded {baseChannelCount} base channel(s), {threadCount} thread(s), and {channels.Count(c => c.Kind == ChannelKind.GuildForum)} forum parent(s) for guild {SelectedGuild.Id} ({SelectedGuild.Name})."
            );

            AvailableChannels = channelTree;
            SelectedChannels.Clear();
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
            await _debugLogService.LogExceptionAsync(ex, "Non-fatal error while pulling channels");
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.CreateMessageBoxViewModel(
                "Error pulling channels",
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(dialog);

            await _debugLogService.LogExceptionAsync(ex, "Fatal error while pulling channels");
        }
        finally
        {
            progress.ReportCompletion();
            IsBusy = false;
        }
    }

    private bool CanExport() =>
        !IsBusy && _discord is not null && SelectedGuild is not null && SelectedChannels.Any();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        IsBusy = true;

        try
        {
            if (_discord is null || SelectedGuild is null || !SelectedChannels.Any())
                return;

            var dialog = _viewModelManager.CreateExportSetupViewModel(
                SelectedGuild,
                SelectedChannels.Select(c => c.Channel).ToArray()
            );

            if (await _dialogManager.ShowDialogAsync(dialog) != true)
                return;

            var exporter = new ChannelExporter(_discord);

            var channels = dialog.Channels!.ToList();

            await _debugLogService.LogAsync(
                $"Export requested for guild {dialog.Guild!.Id} ({dialog.Guild!.Name}) with {channels.Count} selected channel(s). After={dialog.After?.ToString("O") ?? "<none>"}, Before={dialog.Before?.ToString("O") ?? "<none>"}."
            );

            var forumChannels = channels
                .Where(channel => channel.Kind == ChannelKind.GuildForum)
                .ToArray();

            if (forumChannels.Any())
            {
                await _debugLogService.LogAsync(
                    $"Forum parents selected for export: {string.Join(", ", forumChannels.Select(f => $"{f.Name} ({f.Id})"))}"
                );

                var fetchedThreads = new List<Channel>();

                await foreach (
                    var thread in _discord.GetChannelThreadsAsync(
                        forumChannels,
                        true,
                        dialog.After?.Pipe(Snowflake.FromDate),
                        dialog.Before?.Pipe(Snowflake.FromDate)
                    )
                )
                {
                    fetchedThreads.Add(thread);
                }

                await _debugLogService.LogAsync(
                    $"Fetched {fetchedThreads.Count} thread(s) across {forumChannels.Length} forum channel(s): {string.Join("; ", fetchedThreads.Select(t => $"{t.Name} ({t.Id}) parent {t.Parent?.Id}"))}"
                );

                channels.AddRange(fetchedThreads);
                channels.RemoveAll(channel => channel.Kind == ChannelKind.GuildForum);
            }

            var channelProgressPairs = channels
                .Select(c => new { Channel = c, Progress = _progressMuxer.CreateInput() })
                .ToArray();

            var successfulExportCount = 0;

            await Parallel.ForEachAsync(
                channelProgressPairs,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, _settingsService.ParallelLimit),
                },
                async (pair, cancellationToken) =>
                {
                    var channel = pair.Channel;
                    var progress = pair.Progress;

                    try
                    {
                        await _debugLogService.LogAsync(
                            $"Starting export for channel {channel.Name} ({channel.Id}) of kind {channel.Kind}."
                        );

                        var request = new ExportRequest(
                            dialog.Guild!,
                            channel,
                            dialog.OutputPath!,
                            dialog.AssetsDirPath,
                            dialog.SelectedFormat,
                            dialog.After?.Pipe(Snowflake.FromDate),
                            dialog.Before?.Pipe(Snowflake.FromDate),
                            dialog.PartitionLimit,
                            dialog.MessageFilter,
                            dialog.ShouldFormatMarkdown,
                            dialog.ShouldDownloadAssets,
                            dialog.ShouldReuseAssets,
                            _settingsService.Locale,
                            _settingsService.IsUtcNormalizationEnabled
                        );

                        await exporter.ExportChannelAsync(request, progress, cancellationToken);

                        Interlocked.Increment(ref successfulExportCount);

                        await _debugLogService.LogAsync(
                            $"Completed export for channel {channel.Name} ({channel.Id})."
                        );
                    }
                    catch (ChannelEmptyException ex)
                    {
                        _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                        await _debugLogService.LogExceptionAsync(
                            ex,
                            $"Channel {channel.Id} was empty during export"
                        );
                    }
                    catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                    {
                        _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                        await _debugLogService.LogExceptionAsync(
                            ex,
                            $"Non-fatal export error for channel {channel.Id}"
                        );
                    }
                    finally
                    {
                        progress.ReportCompletion();
                    }
                }
            );

            // Notify of the overall completion
            if (successfulExportCount > 0)
            {
                _snackbarManager.Notify(
                    $"Successfully exported {successfulExportCount} channel(s)"
                );
            }
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.CreateMessageBoxViewModel(
                "Error exporting channel(s)",
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(dialog);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDiscord() => Process.StartShellExecute("https://discord.com/app");

    [RelayCommand]
    private void OpenDiscordDeveloperPortal() =>
        Process.StartShellExecute("https://discord.com/developers/applications");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _eventRoot.Dispose();
        }

        base.Dispose(disposing);
    }
}
