
// TestMassSpecTrigger.cpp : Windows only exe to test Thermo Fisher Mass Spec. triggering.
// Argument #1: the current raw data file %R from Thermo Fisher triggering.
// Writes MSAComplete.txt in folder of raw data file.
/* 
Rules:
Yes, patients do get re-processed in three scenarios:
a) Patient samples are run on a different instrument and a new folder is created with updated instrument name in the folder name

b) Patient samples are repeated either on same instrument or different instrument. 
For this, a tag of "_RPTMS" is put in the file names. If all samples are repeated, there would be a new folder with tag "_RPTMS". 
But, if only some are repeated, the new files are kept in the same folder and combined with other files for processing and
c) patient biopsy gets a fresh microdissection. This is handled the same way as scenario b with exception of using tag "_RPTLMD"

common: "_RPT*"
---
New requirements 10/2023
When Xcalibur triggers the executable that creates the MSAComplete.txt file, 
it should also move the resulting RAW files over to the NAS drive. 
This will be a network share on the Windows machine, probably mapped to a drive letter.

To avoid adding more complexity to the watcher script, we think adding a step to the MassSpecTrigger code to handle this step makes the most sense. 
A plain-text config file in the same directory as the executable will define the input and output directories, and 
the code will move everything from the input directory to the output directory, like the robocopy command below.

If the executable and config file are placed on the D: drive, this will be maintainable since we can edit the config file via the shared directory on the NAS.
(In all cases, make sure to move MSAComplete.txt file last, after all RAW files have been moved.)

formerly Robocopy command:  robocopy D:\Transfer "Z:\Transfer\" *.raw /min:<100000> /MOV /Z /S /XO /R:3 /W:2000 /MINAGE:.01
/S : Copy Subfolders.   [ok]
/R:3 : 3 Retries on failed copies. [NA]
/W:2000 : Wait time between retries IN seconds. [NA]
/MOV : MOVe files (delete from source after copying). [ok]
/Z : Copy files in restartable mode (survive network glitch) use with caution as
                     this significantly reduces copy performance due to the extra logging. [NA]
/XO : Exclude Older - if destination file already exists and is the same date or newer than the source, don�t overwrite it. [does not overwrite if exists]
/MINAGE:.01 DAYS: MINimum file AGE - exclude files newer than n days/date. [NA]
*/
// See https://aka.ms/new-console-template for more information
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoFisher.CommonCore.RawFileReader;
using System.Xml;
using System.Linq;

/*
 * Test data in subfolder test.
"D:\\Amyloid_Standards\\2023\\AmyloidStandard10_20231010_Ex1_PostBlanks_NewTrigger\\AmyloidStandard10_20231010_Ex1_27min_PostBlank2.raw",
"D:\\Amyloid_Standards\\2023\\AmyloidStandard10_20231010_Ex1_PostBlanks_NewTrigger\\AmyloidStandard10_20231010_Ex1_27min_PostBlank3.raw",
"D:\\Amyloid_Standards\\2023\\AmyloidStandard10_20231010_Ex1_PostBlanks_NewTrigger\\AmyloidStandard10_20231010_Ex1_27min_PostBlank4.raw",
"D:\\Amyloid_Standards\\2023\\AmyloidStandard10_20231010_Ex1_PostBlanks_NewTrigger\\AmyloidStandard10_20231010_Ex1_27min_PostBlank5.raw",
"D:\\Amyloid_Standards\\2023\\AmyloidStandard10_20231010_Ex1_PostBlanks_NewTrigger\\AmyloidStandard10_20231010_Ex1_27min_PostBlank6.raw",
"D:\\Amyloid_Standards\\2023\\AmyloidStandard10_20231010_Ex1_PostBlanks_NewTrigger\\AmyloidStandard10_20231010_Ex1_27min_PostBlank7.raw",
"D:\\Amyloid_Standards\\2023\\AmyloidStandard10_20231010_Ex1_PostBlanks_NewTrigger\\AmyloidStandard10_20231010_Ex1_27min_PostBlank8.raw"

"C:\\Xcalibur\\Data\\SWG_serum_100512053549.raw",
"C:\\Xcalibur\\Data\\SWG_serum_100512094915.raw"

"D:\\Data\\GFB\\matt\\08112011\\PBS_IP_IKBalpha_SMTA.raw"
*/

namespace MassSpecTriggerCs
{
    // to avoid most casting
    public class StringOrderedDictionary : OrderedDictionary
    {
        public string this[string key]
        {
            get
            {
                return (string)base[key];
            }
            set
            {
                base[key] = value;
            }
        }
    }  // StringOrderedDictionary

    public static class MainClass
    {
        const string TokenFile = "MSAComplete.txt";
        const string RepeatString = "_RPT";
        const string LinuxEndl = "\n";
        const string RawFilePattern = ".raw";
        const string OutputDirKey = "Output_Directory";
        const string SourceTrimKey = "Source_Trim";
        const string RemoveFilesKey = "Remove_Files";
        const string RemoveDirectoriesKey = "Remove_Directories";
        const string UpdateFilesKey = "Overwrite_Older";
        const string MinRawFileSizeKey = "Min_Raw_Files_To_Move_Again";
        const string TriggerLogFileStem = "mass_spec_trigger_log_file";
        const string DefaultConfigFilename = "MassSpecTrigger.cfg";
        const string TriggerLogFileExtension = "txt";
        const int MinRawFileSize = 100000;
        static bool DefaultRemoveFiles = false;
        static bool DefaultRemoveDirectories = false;
        static bool DefaultUpdateFiles = false;
        static string DefaultSourceTrim = "Transfer";
        static bool RemoveFiles = DefaultRemoveFiles;
        static bool RemoveDirectories = DefaultRemoveDirectories;
        static bool UpdateFiles = DefaultUpdateFiles;
        static string SourceTrim = DefaultSourceTrim;
        static Dictionary<string, string> ConfigMap;
        // Trigger each raw file vars
        private const string RAW_FILES_ACQUIRED_BASE = "RawFilesAcquired.txt";

        public static bool ContainsCaseInsensitiveSubstring(string str, string substr)
        {
            string strLower = str.ToLower();
            string substrLower = substr.ToLower();
            return strLower.Contains(substrLower);
        }

        public static string Timestamp(string format)
        {
            DateTime now = DateTime.Now;
            return now.ToString(format);
        }

        public static string Timestamp() => Timestamp("yyyy-MM-dd HH:mm:ss");

        public static string ConstructDestinationPath(string sourceDir, string outputDir, string sourceTrimPath = "")
        {
            string sourceStr = sourceDir.Replace(Path.GetPathRoot(sourceDir), "");
            // Don't trim anything if sourceTrimPath is an empty string
            int sourceTrimPos = -1;
            if (!string.IsNullOrEmpty(sourceTrimPath)) 
            {
                sourceTrimPos = sourceStr.IndexOf(sourceTrimPath, StringComparison.OrdinalIgnoreCase);
            }
            string newOutputPath = "";
            if (sourceTrimPos == -1)
            {
                newOutputPath = Path.Combine(outputDir, sourceStr);
            }
            else if (sourceTrimPos == 0 && sourceStr == sourceTrimPath)
            {
                // If source path is identical to trim, just save directly in output directory 
                newOutputPath = outputDir;
            }
            else
            {
                // If string matching contents of sourceTrimPath is found, adjust the substring start position.
                // Add 1 to include the slash after the directory so it doesn't append as a root directory.
                int startSubstrPos = (sourceTrimPos + sourceTrimPath.Length + 1);
                string newRelativePath = sourceStr.Substring(startSubstrPos);
                newOutputPath = Path.Combine(outputDir, newRelativePath);
            }
            return newOutputPath;
        }

        public static bool PrepareOutputDirectory(string outputPath, StreamWriter logFile, int minRawFileSize, string filePattern)
        {
            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
                return true;
            }
            var matchingFiles = Directory.GetFiles(outputPath, "*" + filePattern)
                .Where(filePath => new FileInfo(filePath).Length < minRawFileSize)
                .ToList();
            if (matchingFiles.Count > 0)
            {
                logFile.WriteLine("Found existing small raw files. Deleting " + outputPath);
                Directory.Delete(outputPath, true);
            }
            return true;
        }

        public static Dictionary<string, string> ReadConfigFile(string execPath, StreamWriter logFile)
        {
            var configMap = new Dictionary<string, string>();
            var execDir = Path.GetDirectoryName(execPath);
            var initialConfigFilename = Path.GetFileNameWithoutExtension(execPath) + ".cfg";
            var defaultConfigFilename = DefaultConfigFilename;
            var altConfigPath1 = Path.Combine(execDir, initialConfigFilename);
            var altConfigPath2 = Path.Combine(execDir, defaultConfigFilename);
            var configPaths = new List<string> { initialConfigFilename, defaultConfigFilename, altConfigPath1, altConfigPath2 };
            string configPath = configPaths.FirstOrDefault(File.Exists);
            if (configPath == null)
            {
                logFile.WriteLine("Failed to open any configuration file after trying config file locations:");
                foreach (var path in configPaths)
                {
                    logFile.WriteLine("  - \"" + Path.GetFullPath(path) + "\"");
                }
                return configMap;
            }
            using (var configFile = new StreamReader(configPath))
            {
                string line;
                while ((line = configFile.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line) && line[0] != '#')
                    {
                        string[] parts = line.Split('=');
                        if (parts.Length == 2)
                        {
                            string key = parts[0].Trim();
                            string value = parts[1].Trim().Trim('\'', '\"');
                            configMap[key] = value;
                        }
                    }
                }
            }
            return configMap;
        }

        public static void CopyDirectory(string sourceDir, string destDir, bool updateFiles)
        {
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
            foreach (string filePath in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(filePath);
                string destPath = Path.Combine(destDir, fileName);
                File.Copy(filePath, destPath, updateFiles);
            }
            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string dirName = new DirectoryInfo(subDir).Name;
                string destSubDir = Path.Combine(destDir, dirName);
                CopyDirectory(subDir, destSubDir, updateFiles);
            }
        }

        public static void RecursiveRemoveFiles(string sourceDir)
        {
            var sourceDirectory = new DirectoryInfo(sourceDir);
            if (RemoveFiles == true)
            {
                foreach (var file in sourceDirectory.GetFiles())
                {
                    file.Delete();
                }
                if (RemoveDirectories == true)
                {
                    foreach (var dir in sourceDirectory.GetDirectories())
                    {
                        dir.Delete();
                    }
                }
            }

        }

        // SLD / Keep track of acquired  logic starts here
        // returns an empty dictionary on errors
        private static StringOrderedDictionary SLDReadSamples(string sldFilePath)
        {
            StringOrderedDictionary rawFilesAcquired = new StringOrderedDictionary();
            // Initialize the SLD file reader
            var sldFile = SequenceFileReaderFactory.ReadFile(sldFilePath);
            if (sldFile is null || sldFile.IsError)
            {
                Console.WriteLine($"Error opening the SLD file: {sldFilePath}, {sldFile.FileError.ErrorMessage}");
                return rawFilesAcquired;
            }
            if (!(sldFile is ISequenceFileAccess))
            {
                Console.WriteLine($"This file {sldFilePath} does not support sequence file access.");
                return rawFilesAcquired;
            }
            foreach (var sample in sldFile.Samples)
            {
                // I saw some blank-named .raw files in a FreeStyle SLD file and will skip these.
                if (string.IsNullOrEmpty(sample.RawFileName))
                {
                    continue;
                }
                // Sometimes a path sep and sometimes not
                string rawFileName = sample.Path.TrimEnd('\\').ToLower() + Path.DirectorySeparatorChar
                    + sample.RawFileName.ToLower() + ".raw";
                rawFilesAcquired[rawFileName] = "no";  // init all to unacquired
            }
            // According to docs, the SLD file is not kept open., saw no dispose
            return rawFilesAcquired;
        }  // SLDReadSamples()

        private static bool UpdateRawFilesAcquiredDict(string rawFilePath, StringOrderedDictionary rawFilesAcquired)
        {
            if (rawFilesAcquired.Contains(rawFilePath.ToLower()))
            {
                rawFilesAcquired[rawFilePath.ToLower()] = "yes";
                return true;
            }
            else
            {
                Console.WriteLine($"{rawFilePath} is not in SLD file, please check triggered raw file and SLD file.");
                return false;
            }
        }  // UpdateRawFilesAcquiredDict()

        private static bool areAllRawFilesAcquired(StringOrderedDictionary rawFilesAcquired)
        {
            foreach (DictionaryEntry entry in rawFilesAcquired)
            {
                if (string.Equals(entry.Value.ToString(), "no", StringComparison.InvariantCulture))
                {
                    return false;
                }
            }
            Console.WriteLine($"All raw files have been acquired.");
            return true;
        }

        // Assume a stripped internal file only
        private static void writeRawFilesAcquired(string rawFilesAcquiredPath, StringOrderedDictionary rawFilesAcquired)
        {
            using (StreamWriter writer = new StreamWriter(rawFilesAcquiredPath))
            {
                foreach (DictionaryEntry entry in rawFilesAcquired)
                {
                    string line = $"{entry.Key}={entry.Value}";
                    writer.WriteLine(line);
                }
            }
        } // writeRawFilesAcquired()

        // Assume a stripped internal file only
        private static StringOrderedDictionary readRawFilesAcquired(string rawFilesAcquiredPath)
        {
            StringOrderedDictionary rawFilesAcquired = new StringOrderedDictionary();
            // Read the file and populate the OrderedDictionary
            using (StreamReader reader = new StreamReader(rawFilesAcquiredPath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split('=');
                    if (parts.Length == 2)
                    {
                        string key = parts[0];
                        string value = parts[1];
                        rawFilesAcquired.Add(key.ToLower(), value.ToLower());
                    }
                }
            }
            return rawFilesAcquired;
        }  // readRawFilesAcquired()

        private static int countRawFilesAcquired(string rawFilesAcquiredPath)
        {
            StringOrderedDictionary rawFilesAcquired = readRawFilesAcquired(rawFilesAcquiredPath);
            int total = rawFilesAcquired.Count;
            int acquired = 0;
            foreach (var value in rawFilesAcquired.Values)
            {
                var status = value.ToString();
                if (status == "yes")
                {
                    acquired++;
                }
            }
            return acquired;
        }  // readRawFilesAcquired()

        public static void Main(string[] args)
        {
            StreamWriter logFile = null;
            if (args.Length < 1)
            {
                Console.WriteLine("Please pass in the current raw data file via %R from Xcalibur.");
                Environment.Exit(1);
            }
            try
            {
                // BEG SETUP
                string rawFileName = args[0];
                // Get the name of the current program (executable)
                string logPath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string logFolderPath = Path.GetDirectoryName(logPath);
                string logFilePath = Path.Combine(logFolderPath, TriggerLogFileStem + "." + TriggerLogFileExtension);
                logFile = new StreamWriter(logFilePath, true);
                logFile.AutoFlush = true;
                logFile.WriteLine("COMMAND: " + string.Join(" ", args));
                string rawFilePath = Path.GetFullPath(rawFileName);
                if (!File.Exists(rawFilePath))
                {
                    logFile.WriteLine("Raw file: " + rawFileName + " does not exist. Exiting.");
                    Environment.Exit(1);
                }
                string folderPath = Path.GetDirectoryName(rawFilePath);
                ConfigMap = ReadConfigFile(logPath, logFile);
                if (!ConfigMap.TryGetValue(OutputDirKey, out string outputPath))
                {
                    logFile.WriteLine("Missing key: " + OutputDirKey + " in MassSpecTrigger configuration file. Exiting.");
                    Environment.Exit(1);
                }
                SourceTrim = ConfigMap.TryGetValue(SourceTrimKey, out string sourceTrimPath) ? sourceTrimPath : DefaultSourceTrim;
                bool.TryParse(ConfigMap.GetValueOrDefault(RemoveFilesKey, DefaultRemoveFiles.ToString()), out RemoveFiles);
                bool.TryParse(ConfigMap.GetValueOrDefault(RemoveDirectoriesKey, DefaultRemoveDirectories.ToString()), out RemoveDirectories);
                bool.TryParse(ConfigMap.GetValueOrDefault(UpdateFilesKey, DefaultUpdateFiles.ToString()), out UpdateFiles);
                int.TryParse(ConfigMap.GetValueOrDefault(MinRawFileSizeKey, MinRawFileSize.ToString()), out int configMinRawFileSize);
                int minRawFileSize = configMinRawFileSize > 0 ? configMinRawFileSize : MinRawFileSize;
                // END SETUP

                // BEG SLD / Check acquired raw
                string sldPath = folderPath;
                string[] sld_files = Directory.GetFiles(sldPath, "*.sld", SearchOption.TopDirectoryOnly)
                    .Where(file => file.EndsWith(".sld", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (sld_files.Length != 1)
                {
                    Console.WriteLine($"Check {sldPath} for a single SLD file.");
                    Environment.Exit(1);
                }
                string sldFile = sld_files[0];
                Console.WriteLine($"Initial work based on SLD file: Check {sldFile}.");

                string rawFilesAcquiredPath = sldPath + Path.DirectorySeparatorChar + RAW_FILES_ACQUIRED_BASE;
                // All items in the dictionary are kept ion lower case to avoid dealing with case sensitive files and strings.
                StringOrderedDictionary rawFilesAcquiredDict = new StringOrderedDictionary();
                // only need to read the SLD the first time in dir.
                if (!File.Exists(rawFilesAcquiredPath))
                {
                    rawFilesAcquiredDict = SLDReadSamples(sldFile);
                }
                else
                {
                    rawFilesAcquiredDict = readRawFilesAcquired(rawFilesAcquiredPath);
                }
                if (rawFilesAcquiredDict.Count == 0)
                {
                    Console.WriteLine($"The raw files acquired internal dictionary is empty. Check {sldFile} and {rawFilesAcquiredPath}");
                    Environment.Exit(1);
                }

                if (!UpdateRawFilesAcquiredDict(rawFileName, rawFilesAcquiredDict))
                {
                    Environment.Exit(1);
                }
                Console.WriteLine($"Updated {rawFileName} for acquisition state.");
                writeRawFilesAcquired(rawFilesAcquiredPath, rawFilesAcquiredDict);
                // END SLD / Check acquired raw

                int total = rawFilesAcquiredDict.Count;
                int acquired = countRawFilesAcquired(rawFilesAcquiredPath);
                
                // BEG WRITE MSA
                // BEG MOVE FOLDER
                if (areAllRawFilesAcquired(rawFilesAcquiredDict))
                {
                    string destinationPath = ConstructDestinationPath(folderPath, outputPath, SourceTrim);
                    logFile.WriteLine($"{acquired} / {total} raw files acquired, beginning payload activity ...");
                    if (!PrepareOutputDirectory(destinationPath, logFile, minRawFileSize, RawFilePattern))
                    {
                        logFile.WriteLine("Could not prepare destination: \"" + destinationPath + "\". Check this directory. Exiting.");
                        Environment.Exit(1);
                    }
                    logFile.WriteLine("Copying directory: \"" + folderPath + "\" => \"" + destinationPath + "\"");
                    CopyDirectory(folderPath, destinationPath, UpdateFiles);
                    if (RemoveFiles)
                    {
                        logFile.WriteLine("Removing source directory: \"" + folderPath + "\"");
                        Directory.Delete(folderPath, true);
                    }
                    else
                    {
                        logFile.WriteLine("Config value '" + RemoveFilesKey + "' is not true, not deleting source directory");
                    }
                    // END MOVE FOLDER
                    
                    // write MSAComplete.txt to The Final Destination
                    string ssRawFile = "raw_file=\"" + rawFileName + "\"";
                    string repeatRun = "false";
                    if (ContainsCaseInsensitiveSubstring(destinationPath, RepeatString) || ContainsCaseInsensitiveSubstring(rawFileName, RepeatString))
                    {
                        repeatRun = "true";
                    }
                    string ssRepeat = "repeat_run=\"" + repeatRun + "\"";
                    string msaFilePath = Path.Combine(destinationPath, TokenFile);
                    string ssDate = Timestamp();
                    using (StreamWriter msaFile = new StreamWriter(msaFilePath))
                    {
                        msaFile.WriteLine(ssDate);
                        msaFile.WriteLine(ssRawFile);
                        msaFile.WriteLine(ssRepeat);
                    }
                    logFile.WriteLine("Wrote file: " + msaFilePath);
                    Environment.Exit(0);
                }
                else
                {
                    logFile.WriteLine($"{acquired} / {total} raw files acquired, not performing payload activities yet");
                }
                // END WRITE MSA
            }  // try
            catch (Exception ex)
            {
                logFile.WriteLine("Error: " + ex.Message);
            }
            finally
            {
                logFile?.Close();
            }
        } // Main()

    }  // MainClass()
}  // ns MassSpecTriggerCs

