using System.Threading;
using System.Windows.Controls;
using EQOverwatch.App.Overlays;
using Xunit;

namespace EQOverwatch.App.Tests;

public sealed class FloatingTextMergeTests
{
    [Fact]
    public void Merging_hits_swaps_the_number_without_throwing()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = "100" });
                stack.Children.Add(new TextBlock { Text = "Tankard" });
                for (int i = 2; i <= 5; i++)
                {
                    FloatingTextOverlayWindow.ReplaceFirst(stack, new TextBlock { Text = $"{i * 100} x{i}" });
                }

                Assert.Equal(2, stack.Children.Count);
                Assert.Equal("500 x5", ((TextBlock)stack.Children[0]).Text);
                Assert.Equal("Tankard", ((TextBlock)stack.Children[1]).Text);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
