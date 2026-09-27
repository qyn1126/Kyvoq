using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Kyvoq.App.Services;
using Kyvoq.App.Views;
using Kyvoq.Core.Models;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Kyvoq.App.Tests.Views;

/// <summary>应用资源及主题为进程级状态，使用独占集合避免影响其他窗口测试。</summary>
[CollectionDefinition("Message dialog UI", DisableParallelization = true)]
public sealed class MessageDialogUiCollection;

/// <summary>通过真实模态消息窗口验证键盘、所属窗口及主题行为，不启动完整应用。</summary>
[Collection("Message dialog UI")]
public sealed class MessageDialogTests
{
    /// <summary>在同一个应用调度器中验证消息窗口的完整交互及输入校验回归。</summary>
    [Fact]
    public async Task Dialogs_ShouldRespectConfirmationOwnershipThemeAndInputFocus()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources = LoadApplicationResources();
                Exception? failure = null;
                application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    try
                    {
                        var theme = new ThemeService();
                        theme.ApplyApplicationTheme(new AppSettings { Theme = AppTheme.Light, WindowMaterial = WindowMaterial.Solid });
                        var messages = new MessageDialogService(theme);
                        VerifyConfirmation(application, messages);
                        VerifyOwnership(application, messages);
                        VerifyThemesAndLongMessages(application, theme, messages);
                        VerifyInputValidation(application, theme, messages);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        application.Shutdown();
                    }
                });
                application.Run();
                if (failure is null)
                {
                    completion.TrySetResult();
                }
                else
                {
                    completion.TrySetException(failure);
                }
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    /// <summary>确认只能通过主按钮或其聚焦后的 Enter 提交，其余退出路径都取消。</summary>
    private static void VerifyConfirmation(Application application, MessageDialogService messages)
    {
        var actions = new (Action<MessageDialog> Interact, bool Expected)[]
        {
            (dialog => PressKey(dialog, Key.Enter), false),
            (dialog => PressKey(dialog, Key.Escape), false),
            (dialog => Find<Button>(dialog, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), false),
            (dialog => Find<Button>(dialog, "CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), false),
            (dialog => dialog.Close(), false),
            (dialog => Find<Button>(dialog, "ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), true),
            (dialog =>
            {
                Find<Button>(dialog, "ConfirmButton").Focus();
                PressKey(dialog, Key.Enter);
            }, true)
        };

        foreach (var (interact, expected) in actions)
        {
            var confirmed = false;
            WithNextMessage(application, dialog =>
            {
                Assert.Equal("登录新账号", dialog.Title);
                Assert.Equal("登录新账号", Find<Button>(dialog, "ConfirmButton").Content);
                Assert.True(Find<Button>(dialog, "CancelButton").IsKeyboardFocusWithin);
                Assert.InRange(dialog.ActualHeight, 1, 240);
                interact(dialog);
            }, () => confirmed = messages.Confirm(null, "登录新账号", "Steam 将退出并重新启动。",
                confirmText: "登录新账号"));
            Assert.Equal(expected, confirmed);
        }

        WithNextMessage(application, dialog =>
        {
            Assert.Equal(Visibility.Collapsed, Find<Button>(dialog, "CancelButton").Visibility);
            Assert.True(Find<Button>(dialog, "ConfirmButton").IsKeyboardFocusWithin);
            PressKey(dialog, Key.Enter);
        }, () => messages.ShowMessage(null, "Kyvoq", "配置已导入。"));
    }

    /// <summary>可见窗口及其子窗口拥有提示，未显示、隐藏、关闭的窗口使用独立提示。</summary>
    private static void VerifyOwnership(Application application, MessageDialogService messages)
    {
        var owner = new Window
        {
            Width = 300, Height = 220, Left = -10000, Top = -10000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, Style = null
        };
        try
        {
            VerifyStandalone();
            owner.Show();
            WithNextMessage(application, dialog =>
            {
                Assert.Same(owner, dialog.Owner);
                Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
                Assert.False(dialog.ShowInTaskbar);
                Assert.True(owner.IsVisible);
            }, () => messages.ShowMessage(owner, "Kyvoq", "所属窗口保持可见。"));

            var child = new Window { Owner = owner, Width = 240, Height = 160, ShowInTaskbar = false, Style = null };
            try
            {
                child.Show();
                WithNextMessage(application, dialog => Assert.Same(child, dialog.Owner),
                    () => messages.ShowMessage(owner, "Kyvoq", "提示属于最内层可见窗口。"));
            }
            finally
            {
                child.Close();
            }

            owner.Hide();
            VerifyStandalone();
        }
        finally
        {
            owner.Close();
        }

        VerifyStandalone();

        /// <summary>检查无有效所属窗口时仍能显示并从任务栏找到消息。</summary>
        void VerifyStandalone() => WithNextMessage(application, dialog =>
        {
            Assert.Null(dialog.Owner);
            Assert.True(dialog.ShowInTaskbar);
            Assert.Equal(WindowStartupLocation.CenterScreen, dialog.WindowStartupLocation);
        }, () => messages.ShowMessage(owner, "Kyvoq", "后台或退出阶段的消息。"));
    }

    /// <summary>切换主题后新窗口使用最新强调色和材质，长内容滚动时按钮仍可操作。</summary>
    private static void VerifyThemesAndLongMessages(Application application, ThemeService theme, MessageDialogService messages)
    {
        var longMessage = string.Join('\n', Enumerable.Repeat(new string('测', 120), 80));
        foreach (var mode in new[] { AppTheme.Light, AppTheme.Dark })
        {
            foreach (var material in Enum.GetValues<WindowMaterial>())
            {
                var settings = new AppSettings
                {
                    Theme = mode, WindowMaterial = material,
                    AccentMode = AccentMode.Custom, CustomAccentArgb = 0xFF336699
                };
                theme.ApplyApplicationTheme(settings);
                WithNextMessage(application, dialog =>
                {
                    dialog.UpdateLayout();
                    var primary = Find<Button>(dialog, "ConfirmButton");
                    Assert.Equal(Color.FromRgb(0x33, 0x66, 0x99), Assert.IsType<SolidColorBrush>(primary.Background).Color);
                    var presenter = Assert.IsType<ContentPresenter>(FindVisualChild<ContentPresenter>(primary));
                    var buttonText = Assert.IsType<TextBlock>(FindVisualChild<TextBlock>(presenter));
                    Assert.Equal(Colors.White,
                        Assert.IsType<SolidColorBrush>(buttonText.Foreground).Color);
                    Assert.Equal(longMessage, Find<TextBlock>(dialog, "MessageText").Text);
                    Assert.True(Find<ScrollViewer>(dialog, "MessageScrollViewer").ScrollableHeight > 0);
                    Assert.InRange(dialog.ActualHeight, 1, dialog.MaxHeight);
                    var buttonBottom = primary.TransformToAncestor(dialog).Transform(new Point(0, primary.ActualHeight));
                    Assert.InRange(buttonBottom.Y, 1, dialog.ActualHeight);
                    var background = Assert.IsType<SolidColorBrush>(Assert.IsType<Grid>(dialog.Content).Background);
                    Assert.Equal(dialog.WindowBackdropType == WindowBackdropType.None ? byte.MaxValue : byte.MinValue,
                        background.Color.A);
                    Assert.Equal(theme.IsDark(mode) ? Color.FromRgb(0xA8, 0xA9, 0xB6) : Color.FromRgb(0x6F, 0x71, 0x80),
                        Assert.IsType<SolidColorBrush>(Find<TextBlock>(dialog, "MessageText").Foreground).Color);
                }, () => messages.ShowMessage(null, "长错误提示", longMessage, MessageDialogSeverity.Error));
            }
        }
    }

    /// <summary>空输入使用新的消息框，关闭提示后焦点回到输入框并可正常提交。</summary>
    private static void VerifyInputValidation(Application application, ThemeService theme, MessageDialogService messages)
    {
        var input = new TextInputDialog("新建分组", "分组名称", string.Empty, theme, messages,
            AppTheme.Dark, WindowMaterial.Solid);
        Exception? failure = null;
        input.ContentRendered += (_, _) =>
        {
            try
            {
                var textBox = Find<System.Windows.Controls.TextBox>(input, "ValueTextBox");
                WithNextMessage(application, dialog =>
                {
                    Assert.Same(input, dialog.Owner);
                    Assert.Equal("输入内容不能为空。", Find<TextBlock>(dialog, "MessageText").Text);
                    PressKey(dialog, Key.Escape);
                }, () => PressKey(textBox, Key.Enter, Keyboard.KeyDownEvent));
                Assert.True(input.IsVisible);
                Assert.True(textBox.IsKeyboardFocusWithin);
                textBox.Text = "新分组";
                PressKey(textBox, Key.Enter, Keyboard.KeyDownEvent);
            }
            catch (Exception exception)
            {
                failure = exception;
                input.Close();
            }
        };
        var confirmed = input.ShowDialog();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.True(confirmed);
        Assert.Equal("新分组", input.Value);
    }

    /// <summary>在模态循环完成布局后检查并关闭消息，将调度器中的断言异常传回测试。</summary>
    private static void WithNextMessage(Application application, Action<MessageDialog> interact, Action show)
    {
        Exception? failure = null;
        var inspected = false;
        var inspection = application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            MessageDialog? dialog = null;
            try
            {
                dialog = Assert.Single(application.Windows.OfType<MessageDialog>(), window => window.IsVisible);
                inspected = true;
                interact(dialog);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (dialog?.IsVisible == true)
                {
                    dialog.Close();
                }
            }
        });
        try
        {
            show();
        }
        finally
        {
            inspection.Abort();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.True(inspected, "未显示预期的应用消息窗口。");
    }

    /// <summary>查找实际 XAML 控件，避免用替身代替窗口交互。</summary>
    private static T Find<T>(FrameworkElement element, string name) where T : class =>
        Assert.IsType<T>(element.FindName(name));

    /// <summary>取得控件模板实际呈现的子元素，检查最终可见的文字样式。</summary>
    private static T? FindVisualChild<T>(DependencyObject element) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    /// <summary>向控件发送路由按键事件。</summary>
    private static void PressKey(UIElement element, Key key, RoutedEvent? routedEvent = null) =>
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key)
        {
            RoutedEvent = routedEvent ?? Keyboard.PreviewKeyDownEvent
        });

    /// <summary>读取真实应用资源字典，在独立测试应用中使用而不运行生产启动逻辑。</summary>
    private static ResourceDictionary LoadApplicationResources()
    {
        _ = System.Reflection.Assembly.Load("Wpf.Ui");
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var root = document.Root!;
        var dictionary = new XElement(root.Element(presentation + "Application.Resources")!.Elements().Single());
        foreach (var attribute in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            dictionary.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                ? attribute.Value + ";assembly=Kyvoq"
                : attribute.Value);
        }

        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }
}
