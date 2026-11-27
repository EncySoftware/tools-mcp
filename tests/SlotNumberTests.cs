using ToolsMcp;
using Xunit;

public class SlotNumberTests
{
    [Theory]
    [InlineData("T71", 71)] [InlineData("#71", 71)] [InlineData("T#71", 71)] [InlineData(" 12 ", 12)]
    [InlineData("Т71", 71)] [InlineData("т#7", 7)]
    public void ARecordedSlotReadsWithALatinOrCyrillicTAndAHash(string recorded, int number) =>
        Assert.Equal(number, AuditOutput.SlotNumber(recorded));

    [Theory] [InlineData("A1")] [InlineData("")] [InlineData(null)] [InlineData("T")]
    public void AnythingElseIsNoSlotNumber(string? recorded) => Assert.Null(AuditOutput.SlotNumber(recorded));
}
