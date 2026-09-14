using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace DS4WindowsTests;

[TestClass]
public class FlickStickCalibrationLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public TestContext TestContext { get; set; }

    [DataTestMethod]
    [DataRow("DefaultTheme", 630, 96)]
    [DataRow("DefaultTheme", 630, 144)]
    [DataRow("DefaultTheme", 812, 96)]
    [DataRow("DarkTheme", 630, 96)]
    [DataRow("DarkTheme", 630, 144)]
    [DataRow("DarkTheme", 812, 96)]
    public void CalibrationTabFitsItsButtonsAndWrappedInstructionsOffscreen(
        string theme, int width, int dpi)
    {
        string renderedPath = null;
        RunSta(() =>
        {
            // Load only the real tab markup: no Application, Window,
            // BindingWindow constructor, ControlService, or device exists.
            Border host = CreateProductionTabHost(theme);
            host.Measure(new Size(width, double.PositiveInfinity));
            int height = (int)Math.Ceiling(host.DesiredSize.Height);
            Assert.IsTrue(height > 80 && height <= 320,
                $"Calibration content should remain compact at {width} DIPs: {height}.");
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            Assert.IsNull(PresentationSource.FromVisual(host),
                "Rendering must not connect to a live window or desktop surface.");

            Button[] buttons = VisualDescendants<Button>(host).ToArray();
            Assert.AreEqual(2, buttons.Length);
            foreach (Button button in buttons)
            {
                AssertInside(host, button);
                Assert.IsTrue(button.ActualWidth > 180 && button.ActualHeight >= 24,
                    $"{button.Name} needs a full action label and a usable target.");
                TextBlock label = VisualDescendants<TextBlock>(button).Single(text =>
                    text.Text == (string)button.Content);
                AssertInside(button, label);
                AssertUncroppedText(label);
            }
            Rect left = BoundsIn(host, buttons[0]);
            Rect right = BoundsIn(host, buttons[1]);
            Assert.IsTrue(left.Bottom <= right.Top,
                "The two stick choices must remain separate, visible targets.");

            TextBlock[] instructions = VisualDescendants<TextBlock>(host)
                .Where(text => text.Text.StartsWith("Bind either action", StringComparison.Ordinal) ||
                    text.Text.StartsWith("In Axis Config", StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(2, instructions.Length);
            foreach (TextBlock text in instructions)
            {
                AssertInside(host, text);
                Assert.AreEqual(TextWrapping.Wrap, text.TextWrapping);
                Assert.AreEqual(TextTrimming.None, text.TextTrimming);
                AssertUncroppedText(text);
                Assert.IsTrue(BoundsIn(host, text).Left >= Math.Max(left.Right, right.Right),
                    "Calibration guidance must not overlap either action button.");
            }

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi / 96d),
                (int)Math.Ceiling(height * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(host);
            // Match the existing artwork render tests' optional durable
            // evidence directory. Normal test runs do not write image files.
            string directory = Environment.GetEnvironmentVariable("DS4W_ARTWORK_EVIDENCE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                renderedPath = Path.Combine(directory,
                    $"flick-calibration-{theme}-{width}-{dpi}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(renderedPath)) encoder.Save(stream);
            }
        });
        if (renderedPath != null)
            TestContext.AddResultFile(renderedPath);
    }

    private static Border CreateProductionTabHost(string theme)
    {
        var document = XDocument.Load(BindingWindowPath());
        XElement productionTab = document.Descendants(Wpf + "TabItem").Single(element =>
            (string)element.Attribute(Xaml + "Name") == "flickStickCalibrationTab");
        var markup = new XElement(Wpf + "Border",
            new XAttribute(XNamespace.Xmlns + "x", Xaml.NamespaceName),
            new XElement(Wpf + "TabControl", new XElement(productionTab)));
        var host = (Border)XamlReader.Parse(markup.ToString());
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/DS4Windows;component/DS4Forms/Themes/{theme}.xaml", UriKind.Relative),
        });
        host.Background = (Brush)host.FindResource("BackgroundColor");
        TextElement.SetForeground(host, theme == "DefaultTheme" ? SystemColors.WindowTextBrush :
            (Brush)host.FindResource("ForegroundColor"));
        ((TabControl)host.Child).SelectedIndex = 0;
        return host;
    }

    private static void AssertUncroppedText(TextBlock actual)
    {
        var probe = new TextBlock
        {
            Text = actual.Text,
            FontFamily = actual.FontFamily,
            FontSize = actual.FontSize,
            FontStyle = actual.FontStyle,
            FontWeight = actual.FontWeight,
            FontStretch = actual.FontStretch,
            TextWrapping = actual.TextWrapping,
            FlowDirection = actual.FlowDirection,
            Padding = actual.Padding,
        };
        probe.Measure(new Size(actual.ActualWidth, double.PositiveInfinity));
        Assert.IsTrue(probe.DesiredSize.Height <= actual.ActualHeight + 1,
            $"Text is cropped vertically: {actual.Text}");
        if (actual.TextWrapping == TextWrapping.NoWrap)
        {
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.IsTrue(probe.DesiredSize.Width <= actual.ActualWidth + 1,
                $"Text is cropped horizontally: {actual.Text}");
        }
    }

    private static void AssertInside(FrameworkElement host, FrameworkElement child)
    {
        Assert.IsTrue(child.ActualWidth > 0 && child.ActualHeight > 0);
        Rect bounds = BoundsIn(host, child);
        Assert.IsTrue(bounds.Left >= -0.1 && bounds.Top >= -0.1 &&
            bounds.Right <= host.ActualWidth + 0.1 && bounds.Bottom <= host.ActualHeight + 0.1,
            $"{child.Name} ({bounds}) exceeds {host.RenderSize}.");
    }

    private static Rect BoundsIn(FrameworkElement host, FrameworkElement child) =>
        child.TransformToAncestor(host).TransformBounds(new Rect(child.RenderSize));

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static string BindingWindowPath([CallerFilePath] string caller = "") =>
        Path.Combine(Path.GetDirectoryName(caller)!, "..", "DS4Windows", "DS4Forms", "BindingWindow.xaml");

    private static void RunSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Calibration render timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
