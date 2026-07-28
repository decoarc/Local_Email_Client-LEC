// tests/LEC.MailStore.Tests/ScavengedMessageClassifierTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.MailStore.Tests;

public class ScavengedMessageClassifierTests
{
    [Fact]
    public void ClassifiesAnIntactParseableChainAsRecovered()
    {
        byte[] raw = Encoding.ASCII.GetBytes(
            "From: \"Ana\" <ana@example.com>\r\nSubject: Recuperada\r\nDate: Fri, 21 Oct 2005 11:11:49 -0300\r\n\r\nCorpo");
        var scavenged = new ScavengedMessage(ChainStartOffset: 100, RawBytes: raw, Truncated: false);

        var result = ScavengedMessageClassifier.Classify(scavenged);

        Assert.Equal(RecoveredMessageKind.Recovered, result.Kind);
        Assert.Equal("Recuperada", result.Subject);
        Assert.Equal("Ana", result.SenderName);
        Assert.Equal("ana@example.com", result.SenderAddress);
        Assert.NotNull(result.DateUtc);
        Assert.Equal(100, result.ChainOffset);
        Assert.Equal(raw.Length, result.ByteLength);
    }

    [Fact]
    public void ClassifiesATruncatedChainAsFragmentEvenIfHeadersParse()
    {
        byte[] raw = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Parcial\r\n\r\nCorpo");
        var scavenged = new ScavengedMessage(ChainStartOffset: 200, RawBytes: raw, Truncated: true);

        var result = ScavengedMessageClassifier.Classify(scavenged);

        Assert.Equal(RecoveredMessageKind.Fragment, result.Kind);
    }

    [Fact]
    public void ClassifiesUnparseableHeadersAsFragmentEvenIfNotTruncated()
    {
        // Genuinely not RFC5322-parseable: no header/body structure at all.
        byte[] raw = Encoding.ASCII.GetBytes("isso nao e um cabecalho de email valido, so texto solto");
        var scavenged = new ScavengedMessage(ChainStartOffset: 300, RawBytes: raw, Truncated: false);

        var result = ScavengedMessageClassifier.Classify(scavenged);

        Assert.Equal(RecoveredMessageKind.Fragment, result.Kind);
        Assert.Null(result.Subject);
    }
}
