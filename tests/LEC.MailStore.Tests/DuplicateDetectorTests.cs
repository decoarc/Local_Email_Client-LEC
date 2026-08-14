// tests/LEC.MailStore.Tests/DuplicateDetectorTests.cs
using Xunit;

namespace LEC.MailStore.Tests;

public class DuplicateDetectorTests
{
    private static MessageHeader Header(int uid, string sender, string to, string subject, DateTime? date) =>
        new()
        {
            Id = new MessageRef(1, uid),
            SenderAddress = sender,
            To = to,
            Subject = subject,
            Date = date,
        };

    [Fact]
    public void LeavesTheOnlyMessageInAGroupNotMarkedAsDuplicate()
    {
        var only = Header(1, "a@x.com", "b@x.com", "Assunto", new DateTime(2026, 1, 1, 10, 0, 0));

        DuplicateDetector.MarkDuplicates(new[] { only });

        Assert.False(only.IsDuplicate);
    }

    [Fact]
    public void MarksTheSecondOfTwoIdenticalMessagesAsDuplicateAndKeepsTheFirstArrivalUnmarked()
    {
        // Duas cópias de exatamente o mesmo e-mail (mesma Data, já que Date é parte da chave de
        // igualdade) -- só a ordem de chegada em "headers" distingue quem chegou primeiro.
        var date = new DateTime(2026, 1, 1, 10, 0, 0);
        var firstArrival = Header(1, "a@x.com", "b@x.com", "Assunto", date);
        var secondArrival = Header(2, "a@x.com", "b@x.com", "Assunto", date);

        DuplicateDetector.MarkDuplicates(new[] { firstArrival, secondArrival });

        Assert.False(firstArrival.IsDuplicate);
        Assert.True(secondArrival.IsDuplicate);
    }

    [Fact]
    public void MarksOnlyTwoOfThreeIdenticalMessagesAsDuplicate()
    {
        var date = new DateTime(2026, 1, 1, 10, 0, 0);
        var firstArrival = Header(1, "a@x.com", "b@x.com", "Assunto", date);
        var secondArrival = Header(2, "a@x.com", "b@x.com", "Assunto", date);
        var thirdArrival = Header(3, "a@x.com", "b@x.com", "Assunto", date);

        DuplicateDetector.MarkDuplicates(new[] { firstArrival, secondArrival, thirdArrival });

        Assert.False(firstArrival.IsDuplicate);
        Assert.True(secondArrival.IsDuplicate);
        Assert.True(thirdArrival.IsDuplicate);
    }

    [Theory]
    [InlineData("b@x.com", "Assunto diferente", "Assunto")]
    [InlineData("c@x.com", "Assunto", "Assunto")] // destinatário diferente
    public void DoesNotMarkMessagesAsDuplicateWhenAnyCriterionDiffers(string to, string subjectA, string subjectB)
    {
        var date = new DateTime(2026, 1, 1, 10, 0, 0);
        var first = Header(1, "a@x.com", "b@x.com", subjectA, date);
        var second = Header(2, "a@x.com", to, subjectB, date);

        DuplicateDetector.MarkDuplicates(new[] { first, second });

        Assert.False(first.IsDuplicate);
        Assert.False(second.IsDuplicate);
    }

    [Fact]
    public void DoesNotMarkMessagesAsDuplicateWhenDatesDifferEvenBySeconds()
    {
        var first = Header(1, "a@x.com", "b@x.com", "Assunto", new DateTime(2026, 1, 1, 10, 0, 0));
        var second = Header(2, "a@x.com", "b@x.com", "Assunto", new DateTime(2026, 1, 1, 10, 0, 1));

        DuplicateDetector.MarkDuplicates(new[] { first, second });

        Assert.False(first.IsDuplicate);
        Assert.False(second.IsDuplicate);
    }

    [Fact]
    public void MatchesIgnoringCaseAndSurroundingWhitespace()
    {
        var date = new DateTime(2026, 1, 1, 10, 0, 0);
        var first = Header(1, "A@X.com", "B@X.com ", " Assunto", date);
        var second = Header(2, " a@x.com", "b@x.com", "assunto ", date);

        DuplicateDetector.MarkDuplicates(new[] { first, second });

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
    }
}
