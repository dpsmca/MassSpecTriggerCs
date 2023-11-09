
// MassSpecTrigger: Windows only exe to process RAW files from ThermoFisher mass spectrometers.
// Argument #1: the current raw data file %R from Xcalibur "post-processing" dialog.
// When all RAW files for a sequence have been produced, it will:
// - Copy or move them to a destination folder
// - Writes MSAComplete.txt in the destination folder
// This currently relies on a single SLD file existing in the source folder.
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
    public class StringKeyDictionary : OrderedDictionary, IEnumerable<KeyValuePair<string, object>>
    {
        public new object this[string key]
        {
            get => base[key];
            set
            {
                ValidateValueType(value);
                base[key] = value;
            }
        }

        public void Add(string key, object value)
        {
            ValidateValueType(value);
            base.Add(key, value);
        }

        public new void Insert(int index, string key, object value)
        {
            ValidateValueType(value);
            base.Insert(index, key, value);
        }

        private void ValidateValueType(object value)
        {
            if (value is not int && value is not bool && value is not string)
            {
                throw new ArgumentException("Value must be of type int, bool, or string.", nameof(value));
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public new IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (DictionaryEntry entry in (IEnumerable)base.GetEnumerator())
            {
                if (entry.Key is string key)
                {
                    yield return new KeyValuePair<string, object>(key, entry.Value);
                }
                else
                {
                    throw new InvalidOperationException("Invalid key or value type detected.");
                }
            }
        }

        public bool TryGetValue(string key, out string outputValue)
        {
            if (this.Contains(key))
            {
                outputValue = (string)this[key];
                return true;
            }
            else
            {
                outputValue = "";
                return false;
            }
        }
        
        public object TryGetValue(string key, out int outputValue)
        {
            if (this.Contains(key))
            {
                outputValue = (int)this[key];
                return true;
            }
            else
            {
                outputValue = 0;
                return false;
            }
        }
    
        public object TryGetValue(string key, out bool outputValue)
        {
            if (this.Contains(key))
            {
                outputValue = (bool)this[key];
                return true;
            }
            else
            {
                outputValue = false;
                return false;
            }
        }

        public object GetValueOrDefault(string key, object defaultValue)
        {
            if (this.Contains(key))
            {
                return this[key];
            }
            else
            {
                return defaultValue;
            }
        }

        public string GetValueOrDefault(string key, string defaultValue)
        {
            return (string)GetValueOrDefault(key, (object)defaultValue);
        }

        public int GetValueOrDefault(string key, int defaultValue)
        {
            return (int)GetValueOrDefault(key, (object)defaultValue);
        }
    
        public bool GetValueOrDefault(string key, bool defaultValue)
        {
            return (bool)GetValueOrDefault(key, (object)defaultValue);
        }
    

        // ... Add/override other necessary methods and properties as needed ...
    }  // StringKeyDictionary

    public class StringOrderedDictionary : OrderedDictionary
    {
        public string this[string key]
        {
            get => base[key].ToString();
            set => base[key] = (string)value;
        }
    }  // StringOrderedDictionary

    public static class MainClass
    {
        public const string TokenFile = "MSAComplete.txt";
        public const string RepeatString = "_RPT";
        public const string LinuxEndl = "\n";
        public const string RawFilePattern = ".raw";
        public const string OutputDirKey = "Output_Directory";
        public const string SourceTrimKey = "Source_Trim";
        public const string RemoveFilesKey = "Remove_Files";
        public const string RemoveDirectoriesKey = "Remove_Directories";
        public const string UpdateFilesKey = "Overwrite_Older";
        public const string MinRawFileSizeKey = "Min_Raw_Files_To_Move_Again";
        public const string TriggerLogFileStem = "mass_spec_trigger_log_file";
        public const string DefaultConfigFilename = "MassSpecTrigger.cfg";
        public const string TriggerLogFileExtension = "txt";
        public static string DefaultSourceTrim = "Transfer";
        public static bool DefaultRemoveFiles = false;
        public static bool DefaultRemoveDirectories = false;
        public static bool DefaultUpdateFiles = false;
        public const int DefaultMinRawFileSize = 100000;
        public static string SourceTrim = DefaultSourceTrim;
        public static bool RemoveFiles = DefaultRemoveFiles;
        public static bool RemoveDirectories = DefaultRemoveDirectories;
        public static bool UpdateFiles = DefaultUpdateFiles;
        public static int MinRawFileSize = DefaultMinRawFileSize;
        public static StringKeyDictionary ConfigMap;
        // Trigger each raw file vars
        private const string RAW_FILES_ACQUIRED_BASE = "RawFilesAcquired.txt";

        public static string ShowDictionary(StringOrderedDictionary dict)
        {
            string res = "";
            if (dict is null)
            {
                res = "(null)";
            }
            else
            {
                foreach (DictionaryEntry de in dict)
                {
                    string val = (string)de.Value;
                    if (string.IsNullOrEmpty(val))
                    {
                        val = "\"\"";
                    }
                    res += $"{de.Key} = {val}";
                    res += "\n";
                }
            }
            return res;
        }

        public static string ShowDictionary(StringKeyDictionary dict)
        {
            string res = "";
            if (dict is null)
            {
                res = "(null)";
            }
            else
            {
                foreach (string key in dict.Keys)
                {
                    object val = dict[key];
                    res += $"{key} = {val}\n";
                }
            }

            return res;
        }

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

        public static StringOrderedDictionary ReadConfigFile(string execPath, StreamWriter logFile)
        {
            var configMap = new StringOrderedDictionary();
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

            Console.WriteLine($"ConfigMap:\n{ShowDictionary(configMap)}\n\n");
            return configMap;
        }
        
        public static StringKeyDictionary ReadAndParseConfigFile(string execPath, StreamWriter logFile)
        {
            var configStringMap = ReadConfigFile(execPath, logFile); 
            var configMap = new StringKeyDictionary();
            foreach (DictionaryEntry de in configStringMap)
            {
                bool isNumber = int.TryParse((string)de.Value, out int intVal);
                if (isNumber)
                {
                    configMap.Add(de.Key, intVal);
                    continue;
                }
                bool isBoolean = bool.TryParse((string)de.Value, out bool boolVal);
                if(isBoolean) {
                    configMap.Add(de.Key, boolVal);
                    continue;
                }
                configMap.Add(de.Key, (string)de.Value);
            }
            Console.WriteLine($"ConfigMap:\n{ShowDictionary(configMap)}\n\n");
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

        public static void RecursiveRemoveFiles(string sourceDir, bool removeFiles = false, bool removeDirectories = false, StreamWriter logFile = null)
        {
            var sourceDirectory = new DirectoryInfo(sourceDir);
            var options = new EnumerationOptions();
            options.RecurseSubdirectories = true;
            if (!(logFile is null))
            {
                if (removeFiles && removeDirectories)
                {
                    logFile.WriteLine($"Removing all files and directories from: {sourceDir}");
                } else if (removeFiles)
                {
                    logFile.WriteLine($"Removing only files from: {sourceDir}");
                }
                else
                {
                    logFile.WriteLine($"\"{RemoveFilesKey}\" and \"{RemoveDirectoriesKey}\" are false, not removing anything from: {sourceDir}");
                }
            }
            if (removeFiles)
            {
                foreach (var file in sourceDirectory.GetFiles("*", options))
                {
                    /* Uncomment for detailed logging of each file removed */
                    // if (!(logFile is null))
                    // {
                    //     logFile.WriteLine($"Removing file: {file.FullName}");
                    // }
                    file.Delete();
                }
                if (removeDirectories)
                {
                    foreach (var dir in sourceDirectory.GetDirectories("*", SearchOption.AllDirectories))
                    {
                        /* Uncomment for detailed logging of each directory removed */
                        // if (!(logFile is null))
                        // {
                        //     logFile.WriteLine($"Removing directory: {dir.FullName}");
                        // }
                        dir.Delete();
                    }

                    sourceDirectory.Delete();
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
                ConfigMap = ReadAndParseConfigFile(logPath, logFile);
                SourceTrim = ConfigMap.TryGetValue(SourceTrimKey, out string sourceTrimPath) ? sourceTrimPath : DefaultSourceTrim;
                RemoveFiles = ConfigMap.GetValueOrDefault(RemoveFilesKey, DefaultRemoveFiles);
                RemoveDirectories = ConfigMap.GetValueOrDefault(RemoveDirectoriesKey, DefaultRemoveDirectories);
                UpdateFiles = ConfigMap.GetValueOrDefault(UpdateFilesKey, DefaultUpdateFiles);
                MinRawFileSize = ConfigMap.GetValueOrDefault(MinRawFileSizeKey, DefaultMinRawFileSize);
                if (!ConfigMap.TryGetValue(OutputDirKey, out string outputPath))
                {
                    logFile.WriteLine("Missing key: " + OutputDirKey + " in MassSpecTrigger configuration file. Exiting.");
                    Environment.Exit(1);
                }
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
                    if (!PrepareOutputDirectory(destinationPath, logFile, MinRawFileSize, RawFilePattern))
                    {
                        logFile.WriteLine("Could not prepare destination: \"" + destinationPath + "\". Check this directory. Exiting.");
                        Environment.Exit(1);
                    }
                    logFile.WriteLine("Copying directory: \"" + folderPath + "\" => \"" + destinationPath + "\"");
                    CopyDirectory(folderPath, destinationPath, UpdateFiles);
                    RecursiveRemoveFiles(folderPath, RemoveFiles, RemoveDirectories, logFile);
                    // if (RemoveFiles)
                    // {
                    //     
                    //     logFile.WriteLine("Removing source directory: \"" + folderPath + "\"");
                    //     Directory.Delete(folderPath, true);
                    // }
                    // else
                    // {
                    //     logFile.WriteLine("Config value '" + RemoveFilesKey + "' is not true, not deleting source directory");
                    // }
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

