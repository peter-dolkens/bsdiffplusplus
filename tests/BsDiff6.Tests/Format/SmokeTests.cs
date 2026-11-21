namespace BsDiff6.Tests.Format;

public class CommonSmokeTests
{
    [Fact]
    public void PlaceholderSuffixArraySortsBanana()
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes("banana");
        int[] sa = new int[text.Length];
        SuffixArray.Build(text, sa);
        Assert.Equal(new[] { 5, 3, 1, 0, 4, 2 }, sa);
    }
}
