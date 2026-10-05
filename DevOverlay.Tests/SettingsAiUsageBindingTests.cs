using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Presentation;
using DevOverlay.UI;
using Xunit;

namespace DevOverlay.Tests;

[Collection(WpfCollection.Name)]
public sealed class SettingsAiUsageBindingTests
{
    [Fact]
    public void CodexAccountLimits_IsVisibleAndIndependent_WhenAiHudIsDisabledAndCliIsMissing()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
            viewModel.UpdateCodexStatus(CodexRateLimitState.CliNotFound);
            var changes = new List<OverlaySettings>();
            var recheckRequests = 0;
            viewModel.SettingsChanged += changes.Add;
            viewModel.CodexRecheckRequested += () => recheckRequests++;
            var window = new SettingsWindow(viewModel);

            try
            {
                window.Show();
                FlushDispatcher();

                var group = Assert.IsType<GroupBox>(window.FindName("AiUsageSettingsGroup"));
                var showAi = Assert.IsType<CheckBox>(window.FindName("ShowAiCheckBox"));
                var codexLimits = Assert.IsType<CheckBox>(window.FindName("CodexAccountLimitsCheckBox"));
                var status = Assert.IsType<TextBlock>(window.FindName("CodexStatusTextBlock"));
                var recheck = Assert.IsType<Button>(window.FindName("RecheckCodexButton"));
                var copyCodexInstall = Assert.IsType<Button>(window.FindName("CopyCodexInstallCommandButton"));
                var copyClaudeInstall = Assert.IsType<Button>(window.FindName("CopyClaudeInstallCommandButton"));
                var help = Assert.IsType<TextBlock>(window.FindName("CodexSetupHelpTextBlock"));
                var guide = Assert.IsType<Expander>(window.FindName("CodexUsageGuideExpander"));
                var claudeGuide = Assert.IsType<Expander>(window.FindName("ClaudeUsageGuideExpander"));
                var scroll = Assert.IsType<ScrollViewer>(window.FindName("SettingsContentScrollViewer"));

                Assert.Equal(Visibility.Visible, group.Visibility);
                Assert.Equal(Visibility.Visible, codexLimits.Visibility);
                Assert.True(codexLimits.IsEnabled);
                Assert.False(showAi.IsChecked);
                Assert.False(codexLimits.IsChecked);
                Assert.Equal("Codex: CLI not found", status.Text);
                Assert.Contains("아래 설정 가이드", help.Text);
                Assert.False(guide.IsExpanded);
                Assert.Equal("Claude Setup & Usage", claudeGuide.Header);
                Assert.Equal("npm install -g @openai/codex@latest", copyCodexInstall.Tag);
                Assert.Equal("npm install -g @anthropic-ai/claude-code", copyClaudeInstall.Tag);
                Assert.False(claudeGuide.IsExpanded);
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
                Assert.Equal(SizeToContent.Manual, window.SizeToContent);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.Empty(changes);

                AssertBinding(showAi, nameof(SettingsViewModel.AiUsageEnabled));
                AssertBinding(codexLimits, nameof(SettingsViewModel.CodexAccountLimitsEnabled));

                codexLimits.IsChecked = true;
                FlushDispatcher();

                var saved = Assert.Single(changes);
                Assert.DoesNotContain(MetricCategory.AiUsage, saved.EnabledGroups);
                Assert.Contains(MetricId.CodexPrimaryRateLimit, saved.EnabledMetrics);
                Assert.Contains(MetricId.CodexSecondaryRateLimit, saved.EnabledMetrics);

                recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, recheckRequests);

                viewModel.UpdateCodexStatus(CodexRateLimitState.Connected);
                FlushDispatcher();
                Assert.Equal("Codex: Connected", status.Text);
                Assert.DoesNotContain("npm install", help.Text);

                var initialWidth = window.ActualWidth;
                guide.IsExpanded = true;
                FlushDispatcher();
                Assert.Equal(initialWidth, window.ActualWidth);
                var guideText = string.Join("\n", Descendants(guide).OfType<TextBlock>().Select(text => text.Text));
                Assert.Equal("Codex Setup & Usage", guide.Header);
                Assert.Contains("npm install -g @openai/codex@latest", guideText);
                Assert.Contains("codex --version", guideText);
                Assert.Contains("codex doctor", guideText);
                Assert.Contains("where.exe codex", guideText);
                Assert.Contains("1. Install", guideText);
                Assert.Contains("4. Recheck", guideText);
                Assert.Contains("0% = exhausted", guideText);
                Assert.Contains("N/A = unavailable", guideText);
                claudeGuide.IsExpanded = true;
                FlushDispatcher();
                var claudeGuideText = string.Join("\n", Descendants(claudeGuide).OfType<TextBlock>().Select(text => text.Text));
                Assert.Contains("npm install -g @anthropic-ai/claude-code", claudeGuideText);
                Assert.Contains("claude --version", claudeGuideText);
                Assert.Contains("1. Install", claudeGuideText);
                Assert.Contains("4. Refresh", claudeGuideText);
                Assert.Contains("local /usage command", claudeGuideText);
                Assert.DoesNotContain("CTX", claudeGuideText);
                Assert.Contains("0% = exhausted", claudeGuideText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SettingsUsesStableResizableFrameAndWrapsLongAiStatusWithoutGrowingWider()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
            var window = new SettingsWindow(viewModel);
            try
            {
                window.Show();
                FlushDispatcher();
                var initialWidth = window.ActualWidth;
                var codexHelp = Assert.IsType<TextBlock>(window.FindName("CodexSetupHelpTextBlock"));
                var claudeHelp = Assert.IsType<TextBlock>(window.FindName("ClaudeSetupHelpTextBlock"));
                Assert.Equal(TextWrapping.Wrap, codexHelp.TextWrapping);
                Assert.Equal(TextWrapping.Wrap, claudeHelp.TextWrapping);
                Assert.True(window.MinWidth >= 480);
                Assert.True(window.MinHeight >= 520);

                codexHelp.Text = "Codex: App Server protocol unsupported. Update Codex CLI and recheck. " + new string('x', 600);
                claudeHelp.Text = "Claude status information " + new string('y', 600);
                FlushDispatcher();
                Assert.Equal(initialWidth, window.ActualWidth);
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void AssertBinding(CheckBox checkBox, string expectedPath)
    {
        var binding = BindingOperations.GetBinding(checkBox, ToggleButton.IsCheckedProperty);
        Assert.NotNull(binding);
        Assert.Equal(expectedPath, binding.Path?.Path);
        Assert.Equal(BindingMode.TwoWay, binding.Mode);
    }

    private static void FlushDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
