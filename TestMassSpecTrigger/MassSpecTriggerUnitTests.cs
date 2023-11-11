using System.Collections;
using System.Collections.Specialized;
using MassSpecTrigger;
using NUnit.Framework;

namespace TestMassSpecTrigger;

public class MassSpecTriggerTests
{
    private static string tempDir;
    private static string tempConfigFileName;
    private static string tempConfigFilePath;
    private static StreamWriter logFile;

    public static string GetTemporaryDirectory(string baseDirectoryPath = "")
    {
        string tempDirectory = Path.GetTempPath();
        if (!string.IsNullOrEmpty(baseDirectoryPath) && Directory.Exists(baseDirectoryPath))
        {
            tempDirectory = Path.Combine(baseDirectoryPath, Path.GetRandomFileName());
        }
        else
        {
            tempDirectory = Path.Combine(tempDirectory, Path.GetRandomFileName());
        }
        Directory.CreateDirectory(tempDirectory);
       return tempDirectory;
    }

    public static List<string> CreateTemporaryFileTree(string baseDirectory, OrderedDictionary directoriesAndFiles)
    {
        List<string> fullFilePaths = new List<string>();
        foreach (DictionaryEntry entry in directoriesAndFiles)
        {
            var dir = (string)entry.Key;
            var dirpath = Path.Combine(baseDirectory, dir);
            if (!Directory.Exists(dirpath))
            {
                Directory.CreateDirectory(dirpath);
            }

            string[] filenames = { };
            if (entry.Value is not null)
            {
                filenames = (string[])entry.Value;
            }
            // var filenames = (string[])entry.Value;
            if (filenames == null) continue;
            foreach (var filename in filenames)
            {
                var filepath = Path.Combine(dirpath, filename);
                fullFilePaths.Add(filepath);
                using (StreamWriter outputFile = new StreamWriter(filepath))
                {
                    outputFile.WriteLine("TEST FILE CONTENTS\n");
                }
            }
        }
        return fullFilePaths;
    }

    public static bool CheckAllExist(List<string> filePaths, bool checkDirsOnly = false)
    {
        bool result = true;
        foreach (var filepath in filePaths)
        {
            if (checkDirsOnly)
            {
                var dir = Path.GetDirectoryName(filepath);
                if (!Directory.Exists(dir))
                {
                    result = false;
                    break;
                }
            }
            else
            {
                if (!File.Exists(filepath))
                {
                    result = false;
                    break;
                }
            }
        }
        return result;
    }

    public bool CheckFileSizeIs(string filePath, int size)
    public static bool CheckFileSizeIs(string filePath, int size)
    {
        var result = false;
        if (!File.Exists(filePath))
        {
            return result;
        }

        var fi = new FileInfo(filePath);
        return fi.Length == size;
    }

    public string GetLogFileName()
    public static string GetLogFileName()
    {
        return MainClass.TriggerLogFileStem + "." + MainClass.TriggerLogFileExtension;
    }

    [SetUp]
    public void Setup()
    {
        tempConfigFileName = MainClass.DefaultConfigFilename;
        string[] configLines =
        {
            "Output_Directory=\"Z:\\Transfer\"",
            "Source_Trim=\"Transfer\"",
            "SLD_Starts_With=\"Exploris\"",
            "Ignore_PostBlank=true",
            "PostBlank_Matches=\"PostBlank\"",
            "Remove_Files=false",
            "Remove_Directories=false",
            "Preserve_SLD=true",
            "Overwrite_Older=true",
            "Min_Raw_Files_To_Move_Again=100000",
            "Debug=false",
        };
        tempDir = GetTemporaryDirectory();
        Console.WriteLine($"Temp directory: \"{tempDir}\"");
        tempConfigFilePath = Path.Combine(tempDir, tempConfigFileName);
        using (StreamWriter outputFile = new StreamWriter(tempConfigFilePath))
        {
            foreach (string line in configLines)
                outputFile.WriteLine(line);
        }

        string logPath = tempDir;
        var logFileName = GetLogFileName();
        string logFolderPath = logPath;
        string logFilePath = Path.Combine(logFolderPath, logFileName);
        Console.WriteLine($"Log file: {logFilePath}");
        logFile = new StreamWriter(logFilePath, true);
        logFile.AutoFlush = true;
    }

    [TearDown]
    public void TearDown()
    {
        if (!(logFile is null))
        {
            logFile.Close();
        }
        if (!string.IsNullOrEmpty(tempConfigFilePath) && File.Exists(tempConfigFilePath))
        {
            File.Delete(tempConfigFilePath);
        }

        if (string.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir)) return;
        Directory.Delete(tempDir, true);
    }

    [Test]
    public void TestConstructDestinationPath()
    {
        var sourcePath = @"D:\Transfer\rawFiles\TestSearch001";
        var outputPath = @"Z:\Transfer";
        var stripPath = @"Transfer";
        var expected = @"Z:\Transfer\rawFiles\TestSearch001";
        var result = MainClass.ConstructDestinationPath(sourcePath, outputPath, stripPath);
        Assert.That(result, Is.EqualTo(expected));

        sourcePath = @"D:\Transfer";
        expected = @"Z:\Transfer";
        result = MainClass.ConstructDestinationPath(sourcePath, outputPath, stripPath);
        Assert.That(result, Is.EqualTo(expected));

        sourcePath = @"D:\Transfer";
        stripPath = @"";
        expected = @"Z:\Transfer\Transfer";
        result = MainClass.ConstructDestinationPath(sourcePath, outputPath, stripPath);
        Assert.That(result, Is.EqualTo(expected));

        sourcePath = @"C:\RawFiles\TestSearch001";
        stripPath = @"Transfer";
        expected = @"Z:\Transfer\RawFiles\TestSearch001";
        result = MainClass.ConstructDestinationPath(sourcePath, outputPath, stripPath);
        Assert.That(result, Is.EqualTo(expected));

        result = MainClass.ConstructDestinationPath(sourcePath, outputPath, "");
        Assert.That(result, Is.EqualTo(expected));

    }

    [Test]
    public void TestTimestamp()
    {
        var ts = MainClass.Timestamp();
        var expected = 19;
        var result = ts.Length;
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void TestStringOrderedDictionary()
    {
        var newDict = new StringOrderedDictionary();
        var expected = "10";
        newDict.Add("int_val", 10);
        var result = newDict["int_val"];
        Assert.That(result, Is.EqualTo(expected));

        var expected2 = Boolean.FalseString;
        newDict.Add("bool_val", false);
        var result2 = newDict["bool_val"];
        Assert.That(result2, Is.EqualTo(expected2));
    }

    [Test]
    public void TestStringKeyDictionary()
    {
        var newDict = new StringKeyDictionary();
        var expected = 10;
        newDict.Add("int_val", expected);
        var result = newDict["int_val"];
        Assert.That(result, Is.EqualTo(expected));
        
        var expected2 = false;
        newDict.Add("bool_val", expected2);
        var result2 = newDict["bool_val"];
        Assert.That(result2, Is.EqualTo(expected2));

        var expected3 = true;
        newDict.Add("bool_val2", expected3);
        var result3 = newDict["bool_val2"];
        Assert.That(result3, Is.EqualTo(expected3));
    }

    [Test]
    public void TestReadConfigFile()
    {
        var configDict = MainClass.ReadAndParseConfigFile(tempConfigFilePath);
        var expected = @"Z:\Transfer";
        var key = MainClass.OutputDirKey;
        var result = configDict[key];
        Assert.That(result, Is.EqualTo(expected));

        var expected2 = false;
        key = "Remove_Files"; 
        result = configDict[key];
        Assert.That(result, Is.EqualTo(expected2));
    }

    [Test]
    public void TestContainsCaseInsensitiveSubstring()
    {
        var testValue = "A STRING WITH ALL CAPS";
        var substring = "with";
        var result = MainClass.ContainsCaseInsensitiveSubstring(testValue, substring);
        Assert.IsTrue(result);

        testValue = "a string with all lowercase";
        result = MainClass.ContainsCaseInsensitiveSubstring(testValue, substring);
        Assert.IsTrue(result);
        
        testValue = "A string With MIXED case";
        result = MainClass.ContainsCaseInsensitiveSubstring(testValue, substring);
        Assert.IsTrue(result);

        result = MainClass.ContainsCaseInsensitiveSubstring(testValue, "");
        Assert.IsTrue(result);

        result = MainClass.ContainsCaseInsensitiveSubstring(testValue, "asdfasdf");
        Assert.IsFalse(result);
    }

    [Test]
    public void TestRecursiveRemoveFiles()
    {
        var newTempDir = GetTemporaryDirectory(tempDir);
        const string dir1 = "test001";
        const string dir2 = "test001_subdir";
        const string dir3 = "test002";
        var tree = new OrderedDictionary();
        tree.Add(dir1, new string[] { "testfile001_1.txt", "testfiles001_2.txt" });
        tree.Add(dir2, new string[] { "testfile001_sub1.txt", "testfiles001_sub2.txt" });
        tree.Add(dir3, new string[] { "testfiles002_1.txt", "testfiles002_2.txt", "testfiles002_3.txt" });
        var fullFilePaths = CreateTemporaryFileTree(newTempDir, tree);

        Assert.IsTrue(Directory.Exists(newTempDir));
        
        Assert.IsTrue(CheckAllExist(fullFilePaths, true));
        Assert.IsTrue(CheckAllExist(fullFilePaths));
        var expectedSize = 21;
        foreach (var filepath in fullFilePaths)
        {
            Assert.IsTrue(CheckFileSizeIs(filepath, expectedSize));
        }
        
        /* Files and directories have been created correctly. Now test the method. */
        
        /* Remove nothing, everything should still exist and have the same size */
        MainClass.RecursiveRemoveFiles(newTempDir, false, false);
        Assert.IsTrue(CheckAllExist(fullFilePaths, true));
        Assert.IsTrue(CheckAllExist(fullFilePaths));
        foreach (var filepath in fullFilePaths)
        {
            Assert.IsTrue(CheckFileSizeIs(filepath, expectedSize));
        }
        
        /* "Remove directories" without "remove files" should also do nothing */
        MainClass.RecursiveRemoveFiles(newTempDir, false, true);
        Assert.IsTrue(CheckAllExist(fullFilePaths, true));
        Assert.IsTrue(CheckAllExist(fullFilePaths));
        foreach (var filepath in fullFilePaths)
        {
            Assert.IsTrue(CheckFileSizeIs(filepath, expectedSize));
        }
        
        /* Remove files only, only directories should still exist */
        MainClass.RecursiveRemoveFiles(newTempDir, true, false);
        Assert.IsFalse(CheckAllExist(fullFilePaths));
        Assert.IsTrue(CheckAllExist(fullFilePaths, true));
        
        /* Remove directories too */
        MainClass.RecursiveRemoveFiles(newTempDir, true, true);
        Assert.IsFalse(CheckAllExist(fullFilePaths));
        Assert.IsFalse(CheckAllExist(fullFilePaths, true));
        
        /* Check base directory itself */
        Assert.IsFalse(Directory.Exists(newTempDir));

    }
}