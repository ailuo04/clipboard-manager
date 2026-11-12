using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using ClipboardManager;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using Xunit;

namespace ClipboardManager.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsOverlayWpfCollection : ICollectionFixture<SettingsOverlayWpfFixture>
{
    public const string Name = "Settings overlay WPF";
}

public sealed class SettingsOverlayWpfFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SettingsOverlayWpfFixture()
    {
        _thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources = ClipboardManager.Views.SharedResources.Create();
                ThemeManager.Apply(AppTheme.Light);
                _dispatcherReady.TrySetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
                _stopped.TrySetResult();
            }
            catch (Exception exception)
            {
                _dispatcherReady.TrySetException(exception);
                _stopped.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "SettingsOverlayWpfFixture"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = _dispatcherReady.Task.GetAwaiter().GetResult();
    }

    private Dispatcher Dispatcher { get; }

    public void Run(Action action) => Dispatcher.Invoke(action);

    public void Dispose()
    {
        Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        _stopped.Task.GetAwaiter().GetResult();
        _thread.Join();
    }
}

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class SettingsOverlayTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public void SettingsViewKeepsFooterFixedAndUsesOverlayDimensions()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", "SettingsView.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement root = XDocument.Load(path).Root!;
        XElement scrollViewer = root.Elements(presentation + "ScrollViewer").Single();
        XElement footer = root.Elements(presentation + "Border")
            .Single(element => (string?)element.Attribute("Grid.Row") == "2");

        // The page has to fit the 300-wide widget, so it must not ask for a minimum width.
        Assert.Null(root.Attribute("MinWidth"));
        Assert.Equal("1", (string?)scrollViewer.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)footer.Attribute("Grid.Row"));
        Assert.Contains("SaveButton", root.Descendants().Attributes(x + "Name").Select(attribute => attribute.Value));
    }

    [Fact]
    public void SettingsViewRootExposesAccessiblePageName()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", "SettingsView.xaml");
        XElement root = XDocument.Load(path).Root!;

        Assert.Equal("设置", (string?)root.Attribute("AutomationProperties.Name"));

        wpf.Run(() =>
        {
            var view = new ClipboardManager.Views.SettingsView();
            Assert.Equal("设置", System.Windows.Automation.AutomationProperties.GetName(view));
        });
    }

    [Fact]
    public void CancelButtonCompletesSettingsWithFalse()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            SettingsOverlay overlay = CreateOverlay(new AppSettings(), showUpdates: null);
            bool? result = true;
            overlay.CloseRequest = value =>
            {
                result = value;
                overlay.OnDismissed(value);
            };

            Click(((ClipboardManager.Views.SettingsView)overlay.Content!).CancelButton);

            Assert.False(result);
            Assert.Equal(AppTheme.Light, ThemeManager.Preference);
        });
    }
    [Fact]
    public void InvalidNumericInputShowsValidationAndDoesNotCompleteSave()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            SettingsOverlay overlay = CreateOverlay(new AppSettings(), showUpdates: null);
            bool? result = null;
            overlay.CloseRequest = value => result = value;
            var view = (ClipboardManager.Views.SettingsView)overlay.Content!;
            var storage = view.MaxStorageBox;
            storage.Text = "4097";

            Click(view.SaveButton);

            Assert.Null(result);
            Assert.Contains("64–4096", view.ValidationText.Text, StringComparison.Ordinal);
            Assert.True(storage.IsFocused);
            Assert.Equal(512, ReadSettings(overlay).MaxStorageMb);
        });
    }

    [Fact]
    public void CancelAndEscapeDismissalRestoreTheOpenedTheme()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Dark);
            SettingsOverlay canceled = CreateOverlay(new AppSettings(), showUpdates: null);
            bool? cancelResult = null;
            canceled.CloseRequest = value =>
            {
                cancelResult = value;
                canceled.OnDismissed(value);
            };
            var canceledView = (ClipboardManager.Views.SettingsView)canceled.Content!;
            canceledView.ThemeLightRadio.IsChecked = true;
            Assert.Equal(AppTheme.Light, ThemeManager.Preference);
            Click(canceledView.CancelButton);

            Assert.False(cancelResult);
            Assert.Equal(AppTheme.Dark, ThemeManager.Preference);

            ThemeManager.Apply(AppTheme.Light);
            SettingsOverlay escaped = CreateOverlay(new AppSettings(), showUpdates: null);
            var owner = new Window { Width = 340, Height = 500 };
            Grid hostRoot = CreateOverlayRoot();
            owner.Content = hostRoot;
            using var host = new WindowOverlayHost(owner, hostRoot);
            var pending = host.ShowAsync(escaped, OverlaySize.Settings);
            ((ClipboardManager.Views.SettingsView)escaped.Content!).ThemeDarkRadio.IsChecked = true;
            Assert.Equal(AppTheme.Dark, ThemeManager.Preference);

            using var keySource = new HwndSource(new HwndSourceParameters("SettingsEscapeTest")
            {
                Width = 1,
                Height = 1,
                WindowStyle = 0
            });
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            ((Grid)hostRoot.FindName("OverlayLayer")).RaiseEvent(escape);
            Assert.True(escape.Handled);

            Assert.Null(pending.GetAwaiter().GetResult());
            Assert.Equal(AppTheme.Light, ThemeManager.Preference);
            owner.Content = null;
        });
    }

    [Fact]
    public void SuccessfulSaveReturnsCloneAndDoesNotRollBackPreviewedTheme()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var original = new AppSettings();
            SettingsOverlay overlay = CreateOverlay(original, showUpdates: null);
            bool? result = null;
            overlay.CloseRequest = value => result = value;
            var view = (ClipboardManager.Views.SettingsView)overlay.Content!;
            view.MaxRecordsBox.Text = "600";
            view.MaxStorageBox.Text = "768";
            view.AlwaysOnTopCheckBox.IsChecked = true;
            view.AutoCheckUpdatesCheckBox.IsChecked = false;
            view.MinimizeToTrayCheckBox.IsChecked = true;
            view.ThemeDarkRadio.IsChecked = true;

            Click(view.SaveButton);

            Assert.True(result);
            Assert.NotSame(original, ReadSettings(overlay));
            Assert.Equal(500, original.MaxRecords);
            Assert.Equal(600, ReadSettings(overlay).MaxRecords);
            Assert.Equal(768, ReadSettings(overlay).MaxStorageMb);
            Assert.True(ReadSettings(overlay).AlwaysOnTop);
            Assert.False(ReadSettings(overlay).AutoCheckUpdates);
            Assert.True(ReadSettings(overlay).MinimizeToTray);
            Assert.Equal(AppTheme.Dark, ReadSelectedTheme(overlay));
            overlay.OnDismissed(true);
            Assert.Equal(AppTheme.Dark, ThemeManager.Preference);
        });
    }

    [Fact]
    public void UpdatesButtonInvokesTheProvidedWindowRoute()
    {
        wpf.Run(() =>
        {
            bool invoked = false;
            SettingsOverlay overlay = CreateOverlay(new AppSettings(), () => { invoked = true; return Task.CompletedTask; });
            var updateButton = ((ClipboardManager.Views.SettingsView)overlay.Content!).OpenUpdatesButton;

            Assert.True(updateButton.IsEnabled);
            Click(updateButton);

            Assert.True(invoked);
        });
    }

    private static SettingsOverlay CreateOverlay(AppSettings settings, Func<Task>? showUpdates)
    {
        return new SettingsOverlay(settings, Path.GetTempPath(), showUpdates);
    }

    private static AppSettings ReadSettings(SettingsOverlay overlay) => overlay.Settings;

    private static AppTheme ReadSelectedTheme(SettingsOverlay overlay) => overlay.SelectedTheme;

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static Grid CreateOverlayRoot()
    {
        var root = new Grid();
        NameScope.SetNameScope(root, new NameScope());
        var main = new Grid();
        var content = new ContentControl();
        var panel = new Border { Child = content };
        var layer = new Grid { Visibility = Visibility.Collapsed };
        layer.Children.Add(panel);
        root.Children.Add(main);
        root.Children.Add(layer);
        root.RegisterName("MainSurface", main);
        root.RegisterName("OverlayLayer", layer);
        root.RegisterName("OverlayPanel", panel);
        root.RegisterName("OverlayContent", content);
        return root;
    }
}
