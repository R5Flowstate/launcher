using System.Windows;
using System.Windows.Controls;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public partial class EulaWindow : Window
{
    private readonly string _baseUrl;
    private readonly bool _requireAccept;
    private readonly EulaResult? _prefetched;
    private readonly LauncherSettings? _settings;
    private EulaResult? _loaded;
    private string _language;
    private bool _suppressLang;
    private bool _busy;
    private bool _ready;

    public bool Accepted { get; private set; }
    public int AcceptedVersion { get; private set; }

    public EulaWindow(
        Window owner,
        string baseUrl,
        bool requireAccept = false,
        EulaResult? prefetched = null,
        LauncherSettings? settings = null)
    {
        InitializeComponent();
        Owner = owner;
        _requireAccept = requireAccept;
        _prefetched = prefetched;
        _settings = settings;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? ProductConstants.DefaultMasterServerUrl
            : baseUrl;
        _language = NoticeLanguages.ResolveUi(settings?.UiLanguage, settings?.EulaLanguage);

        BtnAccept.Visibility = requireAccept ? Visibility.Visible : Visibility.Collapsed;
        BtnDecline.Visibility = requireAccept ? Visibility.Visible : Visibility.Collapsed;
        BtnClose.Visibility = requireAccept ? Visibility.Collapsed : Visibility.Visible;
        if (requireAccept)
        {
            BtnAccept.IsDefault = true;
            BtnDecline.IsCancel = true;
        }
        else
        {
            BtnClose.IsCancel = true;
        }

        _suppressLang = true;
        CmbLanguage.ItemsSource = NoticeLanguages.Picker;
        SelectLanguage(_language);
        _suppressLang = false;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_prefetched is { Success: true }
            && string.Equals(_prefetched.Lang, _language, StringComparison.OrdinalIgnoreCase))
        {
            Apply(_prefetched);
            _ready = true;
            return;
        }

        await LoadLanguageAsync(_language).ConfigureAwait(true);
        _ready = true;
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressLang || _busy)
            return;
        if (CmbLanguage.SelectedItem is not NoticeLanguage row)
            return;
        if (string.Equals(row.Code, _language, StringComparison.OrdinalIgnoreCase)
            && _loaded is { Success: true })
            return;

        await LoadLanguageAsync(row.Code).ConfigureAwait(true);
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
                result = await MasterServerClient.GetEulaAsync(_baseUrl, lang).ConfigureAwait(true);

            Apply(result);
            if (result.Success)
            {
                _language = lang;
                RememberLanguage(lang);
            }
            else
            {
                SelectLanguage(_language);
            }
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

    private void RememberLanguage(string code)
    {
        if (_settings is null)
            return;
        var ui = NoticeLanguages.ForUi(code);
        _settings.UiLanguage = ui;
        _settings.EulaLanguage = ui;
        Loc.SetLanguage(ui);
        try { SettingsStore.Save(_settings); }
        catch
        {
            // keep the in-memory pick
        }
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (_loaded is not { Success: true } || _loaded.Version <= 0)
            return;
        Accepted = true;
        AcceptedVersion = _loaded.Version;
        DialogResult = true;
    }

    private void OnDecline(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) =>
        Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_requireAccept && !Accepted && DialogResult is null)
            DialogResult = false;
    }
}
