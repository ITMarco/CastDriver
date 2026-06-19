using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Size = System.Windows.Size;

namespace CastDriver.UI;

// Attached behavior for a clipping container (a Border with ClipToBounds="True") that holds a
// single TextBlock. When the pointer hovers and the text is wider than the container, the text
// scrolls left then back so a long name can be read in full; it resets when the pointer leaves.
public static class Marquee
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(Marquee),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.MouseEnter -= OnEnter;
        fe.MouseLeave -= OnLeave;
        if ((bool)e.NewValue)
        {
            fe.MouseEnter += OnEnter;
            fe.MouseLeave += OnLeave;
        }
    }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement container) return;
        var text = FindText(container);
        if (text == null) return;

        // The TextBlock is arranged at the (clipped) container width, so ActualWidth doesn't
        // reveal the true text length — measure it unconstrained to find the real overflow.
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double overflow = text.DesiredSize.Width - container.ActualWidth;
        if (overflow <= 1) return;

        if (text.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            text.RenderTransform = tt;
        }

        var anim = new DoubleAnimation
        {
            From           = 0,
            To             = -overflow,
            Duration       = TimeSpan.FromSeconds(Math.Max(1.2, overflow / 40.0)),
            BeginTime      = TimeSpan.FromMilliseconds(350),
            AutoReverse    = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        tt.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement container &&
            FindText(container)?.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
        }
    }

    private static TextBlock? FindText(DependencyObject root)
    {
        if (root is TextBlock tb) return tb;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindText(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
