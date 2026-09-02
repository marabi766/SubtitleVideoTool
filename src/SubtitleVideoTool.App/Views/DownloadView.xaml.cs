using System.Windows;
using System.Windows.Controls;
using SubtitleVideoTool.Core;

namespace SubtitleVideoTool.App.Views;

public partial class DownloadView : UserControl
{
    public DownloadView() => InitializeComponent();

    private DownloadViewModel? Model => DataContext as DownloadViewModel;

    private void ChooseAnonymous(object sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            model.CookieSource = CookieSource.None;
        }
    }

    private void ChooseAppSignIn(object sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            model.CookieSource = CookieSource.AppSignIn;
        }
    }

    private void ChooseBrowser(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && !model.IsBrowserCookies)
        {
            model.CookieSource = SelectedBrowser();
        }
    }

    private void BrowserChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is { IsBrowserCookies: true } model)
        {
            model.CookieSource = SelectedBrowser();
        }
    }

    private CookieSource SelectedBrowser() => BrowserChoice.SelectedIndex switch
    {
        1 => CookieSource.Edge,
        2 => CookieSource.Chrome,
        3 => CookieSource.Brave,
        4 => CookieSource.File,
        _ => CookieSource.Firefox,
    };

    private void RunPrimaryFailureAction(object sender, RoutedEventArgs e) =>
        Run(Model?.DownloadFailureCard?.PrimaryAction);

    private void RunSecondaryFailureAction(object sender, RoutedEventArgs e) =>
        Run(Model?.DownloadFailureCard?.SecondaryAction);

    /// <summary>
    /// The failure card offers one action that fixes the cause; this is where
    /// those map onto something the window can actually do.
    /// </summary>
    private void Run(FailureAction? action)
    {
        if (Model is not { } model || action is null)
        {
            return;
        }

        switch (action)
        {
            case FailureAction.SignInAndRetry:
                model.CookieSource = CookieSource.AppSignIn;
                model.SignInCommand.Execute(null);
                break;

            case FailureAction.UseBrowserCookies:
                model.CookieSource = SelectedBrowser();
                break;

            case FailureAction.ChooseAnotherFolder:
                model.BrowseFolderCommand.Execute(null);
                break;

            case FailureAction.PickAnotherQuality:
            case FailureAction.CheckAgain:
                model.CheckAgainCommand.Execute(null);
                break;

            case FailureAction.Retry:
                model.StartCommand.Execute(null);
                break;
        }
    }
}
