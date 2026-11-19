using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux.Views;

/// <summary>
/// Port of the Windows Shell EulaWindow: same flow, same Loc keys, same
/// language picker. Shown two ways —
///   * requireAccept: true  → the Servers tab gate. The player must accept the
///     current notice version before the master-server list is fetched.
///   * requireAccept: false → the footer "Read EULA" link, read-only.
/// Prefetched notices skip the round trip when the language already matches.
/// </summary>
public partial class EulaWindow : Window
{
    private readonly string _baseUrl;
    private readonly bool _requireAccept;
    private readonly EulaResult? _prefetched;
    private readonly MainViewModel? _vm;
    private EulaResult? _loaded;
    private string _language;
    private bool _suppressLang;
    private bool _busy;
    private bool _ready;

    /// <summary>True only when the player pressed Accept on a loaded notice.</summary>
    public bool Accepted { get; private set; }

    /// <summary>Version of the notice that was accepted (0 when not accepted).</summary>
    public int AcceptedVersion { get; private set; }

    /// <summary>Runtime-XAML-loader constructor; the launcher always uses the
    /// owning-window overload so the dialog centres on the shell.</summary>
    public EulaWindow() : this(null, null, false, null, null) { }

    public EulaWindow(
        Window? owner,
        string? baseUrl = null,
        bool requireAccept = false,
        EulaResult? prefetched = null,
        MainViewModel? vm = null)
    {
        InitializeComponent();
        if (owner is not null)
            Owner = owner;
        _requireAccept = requireAccept;
        _prefetched = prefetched;
        _vm = vm;
        _baseUrl = NoticeClient.NormalizeBaseUrl(baseUrl);
        // No VM (loader ctor) still resolves from the OS locale, like Windows.
        _language = vm?.EulaUiLanguage() ?? NoticeLanguages.ResolveUi(null, null);

        // Read-only mode has nothing to accept or decline — Close only, like
        // the Windows dialog.
        BtnAccept.IsVisible = requireAccept;
        BtnDecline.IsVisible = requireAccept;
        BtnClose.IsVisible = !requireAccept;

        _suppressLang = true;
        CmbLanguage.ItemsSource = NoticeLanguages.Picker;
        SelectLanguage(_language);
        _suppressLang = false;

        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        try
        {
            if (_prefetched is { Success: true }
                && string.Equals(_prefetched.Lang, _language, StringComparison.OrdinalIgnoreCase))
            {
                Apply(_prefetched);
                _ready = true;
                return;
            }

            await LoadLanguageAsync(_language);
        }
        finally
        {
            _ready = true;
        }
    }

    private async void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressLang || _busy)
            return;
        if (CmbLanguage.SelectedItem is not NoticeLanguage row)
            return;
        if (string.Equals(row.Code, _language, StringComparison.OrdinalIgnoreCase)
            && _loaded is { Success: true })
            return;

        await LoadLanguageAsync(row.Code);
    }

    private async Task LoadLanguageAsync(string language)
    {
        var lang = NoticeLanguages.Sanitize(language);
        _busy = true;
        CmbLanguage.IsEnabled = false;
        TxtBody.Text = Loc.Get("loading");
        TxtMeta.Text = string.Empty;
        BtnAccept.IsEnabled = false;
        try
        {
            EulaResult result;
            if (_prefetched is { Success: true }
                && string.Equals(_prefetched.Lang, lang, StringComparison.OrdinalIgnoreCase))
                result = _prefetched;
            else
                result = await NoticeClient.GetEulaAsync(_baseUrl, lang);

            Apply(result);
            if (result.Success)
            {
                _language = lang;
                _vm?.RememberEulaLanguage(lang);
            }
            else
            {
                SelectLanguage(_language);
            }
        }
        catch (Exception ex)
        {
            Apply(EulaResult.Fail(ex.Message));
            SelectLanguage(_language);
        }
        finally
        {
            _busy = false;
            CmbLanguage.IsEnabled = true;
        }
    }

    private void Apply(EulaResult result)
    {
        _loaded = result;
        if (!result.Success)
        {
            TxtTitle.Text = Loc.Get("eula_load_failed");
            TxtBody.Text = result.Error ?? Loc.Get("eula_no_text");
            TxtMeta.Text = string.Empty;
            BtnAccept.IsEnabled = false;
            return;
        }

        TxtTitle.Text = _requireAccept ? Loc.Get("eula_accept_servers") : Loc.Get("legal_notice");
        TxtBody.Text = result.Contents;
        var bits = new List<string>();
        if (result.Version > 0)
            bits.Add(Loc.Format("eula_version", result.Version));
        if (!string.IsNullOrWhiteSpace(result.Lang))
            bits.Add(result.Lang);
        TxtMeta.Text = bits.Count > 0 ? string.Join("  ·  ", bits) : string.Empty;
        TxtBody.CaretIndex = 0;
        // Accept stays dark until there is real text to accept.
        BtnAccept.IsEnabled = _requireAccept;
    }

    private void SelectLanguage(string code)
    {
        var canon = NoticeLanguages.Sanitize(code);
        _suppressLang = true;
        try
        {
            foreach (var row in NoticeLanguages.Picker)
            {
                if (string.Equals(row.Code, canon, StringComparison.Ordinal))
                {
                    CmbLanguage.SelectedItem = row;
                    return;
                }
            }
            CmbLanguage.SelectedIndex = 0;
        }
        finally
        {
            _suppressLang = false;
        }
    }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        // Nothing to accept if the notice never loaded (or came back empty).
        if (_loaded is not { Success: true } || _loaded.Version <= 0)
            return;
        Accepted = true;
        AcceptedVersion = _loaded.Version;
        Close(true);
    }

    private void OnDecline(object? sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close(false);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);

    private void OnCaptionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
