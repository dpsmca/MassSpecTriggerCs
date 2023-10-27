namespace TestMassSpecTriggerCs;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void TestConstructDestinationPath()
    {
        var sourcePath = @"D:\Transfer\rawFiles\TestSearch001";
        var outputPath = @"Z:\Transfer";
        var stripPath = @"Transfer";
        var expected = @"Z:\Transfer\rawFiles\TestSearch001";
        Assert.AreEqual(expected, MassSpecTriggerCs.MainClass.ConstructDestinationPath(sourcePath, outputPath, stripPath));
        sourcePath = @"D:\Transfer";
        expected = @"Z:\Transfer";
        Assert.AreEqual(expected, MassSpecTriggerCs.MainClass.ConstructDestinationPath(sourcePath, outputPath, stripPath));
        sourcePath = @"D:\Transfer";
        stripPath = @"";
        expected = @"Z:\Transfer\Transfer";
        Assert.AreEqual(expected, MassSpecTriggerCs.MainClass.ConstructDestinationPath(sourcePath, outputPath, stripPath));
    }

    [Test]
    public void FailingTest()
    {
        Assert.AreEqual(5, 4);
    }
}