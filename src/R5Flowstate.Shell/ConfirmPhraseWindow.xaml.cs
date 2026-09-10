using System.Windows;

namespace R5Flowstate.Shell;

/// <summary>Destructive-action gate: the exact phrase must be typed before the action unlocks.</summary>
public partial class ConfirmPhraseWindow : Window
{
    private readonly string _phrase;

    private ConfirmPhraseWindow(string headline, string detail, string target, string phrase)
    {
        InitializeComponent();
        _phrase = phrase;
        TxtHeadline.Text = headline;
        TxtDetail.Text = detail;
        TxtTarget.Text = target;
        TxtPrompt.Text = Loc.Get("confirm_type");
        TxtPhraseEcho.Text = phrase;
        TxtPhraseHint.Text = Loc.Get("confirm_hint");
        Loaded += (_, _) => TxtPhrase.Focus();
    }

    public static bool Ask(Window owner, string headline, string detail, string target, string phrase)
    {
        var w = new ConfirmPhraseWindow(headline, detail, target, phrase) { Owner = owner };
        return w.ShowDialog() == true;
    }

    private void OnPhraseChanged(object sender, RoutedEventArgs e)
    {
        if (BtnConfirm is not null)
            BtnConfirm.IsEnabled = string.Equals(TxtPhrase.Text?.Trim(), _phrase, StringComparison.OrdinalIgnoreCase);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(TxtPhrase.Text?.Trim(), _phrase, StringComparison.OrdinalIgnoreCase))
            return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
