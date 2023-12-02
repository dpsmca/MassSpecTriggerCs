﻿
/*
 * MassSpecTrigger: Program to copy or move RAW files from ThermoFisher mass spectrometers.
 * Intended to be used as part of an automation pipeline.
 * Will be called from Xcalibur after each RAW file is created.
 * In Xcalibur post-processing dialog, specify the complete path to the MassSpecTrigger
 * executable, followed by the %R variable to supply the path to the new RAW file. The
 * Xcalibur sequence (SLD) file should be created in the same directory as the RAW files
 * and there should only be a single SLD file per directory.
 * 
 * When all RAW files for a sequence have been produced, it will:
 * - Copy or move them to a destination folder (depending on config file)
 * - Create a trigger file (MSAComplete.txt) in the destination folder
 *
 * Argument #1: the current RAW file (%R from Xcalibur post-processing dialog)
 */

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
using System.Xml;
using System.Linq;
using Color = System.Drawing.Color;
using System.Collections.Specialized;
using System.Reflection;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoFisher.CommonCore.RawFileReader;
using CommandLine;
using CommandLine.Text;
using Pastel;

namespace MassSpecTrigger
{
    public class Options
    {
        [Option('m', "mock", Required = false, MetaValue = "\"file1.raw;file2.raw;...\"", HelpText = "Mock sequence: a semicolon-separated list of RAW files (complete path) which will stand in for the contents of an SLD file")]
        public string MockSequence { get; set; }

        [Option('l', "logfile", Required = false, MetaValue = "\"logfile\"", HelpText = "Complete path to log file")]
        public string Logfile { get; set; }

        [Option('d', "debug", Required = false, HelpText = "Enable debug output")]
        public bool Debug { get; set; }

        [Value(0, MetaName = "\"file_path.raw\"", HelpText = "RAW file (complete path)")]
        public string InputRawFile { get; set; } 
    }

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
        public static string AppName = Assembly.GetExecutingAssembly().GetName().Name;
        public static string AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(); 
        public const string LinuxEndl = "\n";
        public const string RawFilePattern = ".raw";
        public const string OutputDirKey = "Output_Directory";
        public const string RepeatRunKey = "Repeat_Run_Matches";
        public const string TokenFileKey = "Token_File";
        public const string SourceTrimKey = "Source_Trim";
        public const string SldStartsWithKey = "SLD_Starts_With";
        public const string PostBlankMatchesKey = "PostBlank_Matches";
        public const string IgnorePostBlankKey = "Ignore_PostBlank";
        public const string RemoveFilesKey = "Remove_Files";
        public const string RemoveDirectoriesKey = "Remove_Directories";
        public const string PreserveSldKey = "Preserve_SLD";
        public const string UpdateFilesKey = "Overwrite_Older";
        public const string MinRawFileSizeKey = "Min_Raw_Files_To_Move_Again";
        public const string DebugKey = "Debug";
        public const string TriggerLogFileStem = "mass_spec_trigger_log_file";
        public const string DefaultConfigFilename = "MassSpecTrigger.cfg";
        public const string TriggerLogFileExtension = "txt";
        public static string DefaultSourceTrim = "Transfer";
        public static string DefaultRepeatRun = "_RPT";
        public static string DefaultTokenFile = "MSAComplete.txt";
        public static string DefaultSldStartsWith = "Exploris";
        public static string DefaultPostBlankMatches = "PostBlank";
        public static bool DefaultIgnorePostBlank = true;
        public static bool DefaultRemoveFiles = false;
        public static bool DefaultRemoveDirectories = false;
        public static bool DefaultPreserveSld = true;
        public static bool DefaultUpdateFiles = false;
        public static bool DefaultDebugging = false;
        public const int DefaultMinRawFileSize = 100000;
        public static string SourceTrim = DefaultSourceTrim;
        public static string RepeatRun = DefaultRepeatRun;
        public static string TokenFile = DefaultTokenFile;
        public static string SldStartsWith = DefaultSldStartsWith;
        public static string PostBlankMatches = DefaultPostBlankMatches;
        public static bool IgnorePostBlank = DefaultIgnorePostBlank;
        public static bool RemoveFiles = DefaultRemoveFiles;
        public static bool RemoveDirectories = DefaultRemoveDirectories;
        public static bool PreserveSld = DefaultPreserveSld;
        public static bool UpdateFiles = DefaultUpdateFiles;
        public static bool DebugMode = DefaultDebugging;
        public static int MinRawFileSize = DefaultMinRawFileSize;
        public static StringKeyDictionary ConfigMap;

        public static StreamWriter logFile;
        public static bool MockSequenceMode = false;

        private const string RAW_FILES_ACQUIRED_BASE = "RawFilesAcquired.txt";
        public static string SLD_FILE_PATH = "";
        public static string rawFileName = "";
        public static string logFilePath = "";
        public static List<string> mockSequence = new List<string>();

        public static Parser parser;
        public static ParserResult<Options> parserResult;

        public static void log(string message, StreamWriter logger = null)
        {
            /* Use default logFile if logger not provided */
            logger ??= logFile;
            var ts = Timestamp();
            var msg = $"[ {ts} ] {message}";
            logger?.WriteLine(msg);
            msg = msg.Pastel(Color.Cyan);
            /* Write to stderr in case we need to use output for something */
            Console.Error.WriteLine(msg);
        }

        public static void logerr(string message, StreamWriter logger = null)
        {
            /* Use default logFile if logger not provided */
            logger ??= logFile;
            var ts = Timestamp();
            var line = $"[ {ts} ] [ERROR] {message}";
            logger?.WriteLine(line);
            line = line.Pastel(Color.Red);
            Console.Error.WriteLine(line);
        }

        public static void logwarn(string message, StreamWriter logger = null)
        {
            /* Use default logFile if logger not provided */
            logger ??= logFile;
            var ts = Timestamp();
            var line = $"[ {ts} ] [WARNING] {message}";
            logger?.WriteLine(line);
            line = line.Pastel(Color.Yellow);
            Console.Error.WriteLine(line);
        }

        public static void logdbg(string message, StreamWriter logger = null)
        {
            /* Use default logFile if logger not provided */
            logger ??= logFile;
            if (DebugMode)
            {
                var ts = Timestamp();
                var line = $"[ {ts} ] [DEBUG] {message}";
                logger?.WriteLine(line);
                line = line.Pastel(Color.Magenta);
                Console.Error.WriteLine(line);
            }
        }

        public static string StringifyDictionary(StringOrderedDictionary dict)
        {
            string output = "";
            if (dict is null)
            {
                output += "(null)\n";
            }
            else if (dict.Count == 0)
            {
                output += "(empty)\n";
            }
            else
            {
                foreach (DictionaryEntry de in dict)
                {
                    output += $"{de.Key}={de.Value}\n";
                }
            }
            return output.Trim();
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
            string sourceStr = sourceDir.Replace(Path.GetPathRoot(sourceDir) ?? string.Empty, "");
            int sourceTrimPos = -1;
            // Don't trim anything if sourceTrimPath is an empty string
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
                // If source path is identical to sourceTrimPath, just save directly in output directory 
                newOutputPath = outputDir;
            }
            else
            {
                // If string matching contents of sourceTrimPath is found, adjust the substring start position.
                // Add 1 to include the slash after the directory so it doesn't append as a root directory.
                if (sourceTrimPath != null)
                {
                    int startSubstrPos = (sourceTrimPos + sourceTrimPath.Length + 1);
                    string newRelativePath = sourceStr.Substring(startSubstrPos);
                    newOutputPath = Path.Combine(outputDir, newRelativePath);
                }
            }
            return newOutputPath;
        }

        public static bool PrepareOutputDirectory(string outputPath, int minRawFileSize, string filePattern)
        {
            if (!string.IsNullOrEmpty(outputPath))
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
                    log("Found existing small raw files, previous copy may have been interrupted. Deleting " + outputPath);
                    Directory.Delete(outputPath, true);
                }
                return true;
            }
            return false;
        }

        public static StringOrderedDictionary ReadConfigFile(string execPath)
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
                log("Failed to open any configuration file after trying config file locations:");
                foreach (var path in configPaths)
                {
                    log("  - \"" + Path.GetFullPath(path) + "\"");
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
        
        public static StringKeyDictionary ReadAndParseConfigFile(string execPath)
        {
            var configStringMap = ReadConfigFile(execPath); 
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

        public static void RecursiveRemoveFiles(string sourceDir, bool removeFiles = false, bool removeDirectories = false, bool preserveSld = true, StreamWriter logger = null)
        {
            var sourceDirectory = new DirectoryInfo(sourceDir);
            /* Use default logFile if logger not provided */
            logger ??= logFile;
            
            /* (1): Before any removal actions, log what will generally be done */
            if (removeFiles)
            {
                if (preserveSld)
                {
                    log($"Removing all non-SLD files but no subdirectories from: {sourceDir}", logger);
                }
                else if (removeDirectories)
                {
                    log($"Removing all files and directories from: {sourceDir}", logger);
                }
                else
                {
                    log($"Removing only files from: ${sourceDir}", logger);
                }
            }
            else
            {
                log($"Not removing any files or subdirectories from: {sourceDir}", logger);
            }
            
            /* (2): Perform removal actions according to provided parameters */
            if (removeFiles)
            {
                var sourceDirectoryPath = sourceDirectory.FullName;
                var filesToRemove = Directory.GetFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories);
                if (preserveSld)
                {
                    filesToRemove = filesToRemove.Where(file => !file.EndsWith(".sld", StringComparison.OrdinalIgnoreCase)).ToArray();
                }
                
                foreach (var filePath in filesToRemove)
                {
                    var file = new FileInfo(filePath);
                    logdbg($"Removing file: {file.FullName}", logger);
                    file.Delete();
                    logdbg($"Removed file: {file.FullName}", logger);
                }

                /* preserveSld overrides removing directories since we need to preserve the SLD files */
                if (removeDirectories && !preserveSld)
                {
                    foreach (var dir in sourceDirectory.GetDirectories("*", SearchOption.AllDirectories))
                    {
                        logdbg($"Removing directory: {dir.FullName}", logger);
                        dir.Delete();
                        logdbg($"Removed directory: {dir.FullName}", logger);
                    }
                    logdbg($"Removing base directory: {sourceDirectory.FullName}", logger);
                    sourceDirectory.Delete();
                    logdbg($"Removed base directory: {sourceDirectory.FullName}", logger);
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
                logerr($"Error opening the SLD file: {sldFilePath}, {sldFile?.FileError.ErrorMessage}");
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
                // string rawFileName = sample.Path.TrimEnd('\\').ToLower() + Path.DirectorySeparatorChar + sample.RawFileName.ToLower() + ".raw";
                // string rawFileName = Path.Combine(sample.Path.TrimEnd('\\').ToLower() + Path.DirectorySeparatorChar + sample.RawFileName.ToLower() + ".raw";
                string rawFileName = sample.RawFileName.ToLower() + ".raw";

                /* If we are ignoring PostBlank files, check that this RAW file is not a PostBlank */
                if (!(IgnorePostBlank && ContainsCaseInsensitiveSubstring(rawFileName, PostBlankMatches)))
                {
                    rawFilesAcquired[rawFileName] = "no";  // init all to unacquired
                }
            }
            // According to docs, the SLD file is not kept open., saw no dispose
            return rawFilesAcquired;
        }  // SLDReadSamples()

        private static StringOrderedDictionary MockSLDReadSamples(string rawFilesAcquiredPath, List<string> rawFilePaths)
        {
            StringOrderedDictionary rawFilesAcquired = readRawFilesAcquired(rawFilesAcquiredPath);
            foreach (var filePath in rawFilePaths)
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    continue;
                }

                string rawFileName = Path.GetFileName(filePath).ToLower();

                /* If we are ignoring PostBlank files, check that this RAW file is not a PostBlank */
                if (!(IgnorePostBlank && ContainsCaseInsensitiveSubstring(rawFileName, PostBlankMatches)))
                {
                    rawFilesAcquired[rawFileName] = "no"; // init all to unacquired
                }
            }
            return rawFilesAcquired;
        }

        private static bool UpdateRawFilesAcquiredDict(string rawFilePath, StringOrderedDictionary rawFilesAcquired)
        {
            var rawFileName = Path.GetFileName(rawFilePath);
            if (IgnorePostBlank && ContainsCaseInsensitiveSubstring(rawFileName, PostBlankMatches))
            {
                log($"\"{rawFileName}\" appears to be a PostBlank file and will be ignored");
                return true;
            }
            if (rawFilesAcquired.Contains(rawFileName.ToLower()))
            {
                logdbg($"Acquisition status file has record for RAW file, setting acquired status to yes");
                rawFilesAcquired[rawFileName.ToLower()] = "yes";
                return true;
            }
            logerr($"Acquisition status file error: RAW file not found in SLD file: {rawFileName} error: RAW file not found in SLD file {SLD_FILE_PATH}");
            return false;
        }  // UpdateRawFilesAcquiredDict()

        private static bool areAllRawFilesAcquired(StringOrderedDictionary rawFilesAcquired)
        {
            logdbg($"{RAW_FILES_ACQUIRED_BASE} contents:\n{StringifyDictionary(rawFilesAcquired)}");
            foreach (DictionaryEntry entry in rawFilesAcquired)
            {
                if (string.Equals(entry.Value?.ToString(), "no", StringComparison.InvariantCulture))
                {
                    return false;
                }
            }
            log($"All raw files have been acquired.");
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
            if (File.Exists(rawFilesAcquiredPath))
            {
                logdbg($"Acquisition status file exists, reading values from: {rawFilesAcquiredPath}");
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
            }
            else
            {
                logdbg($"Acquisition status file not found, will be initialized as empty: {rawFilesAcquiredPath}");
            }
            return rawFilesAcquired;
        }  // readRawFilesAcquired()

        private static int countRawFilesAcquired(string rawFilesAcquiredPath)
        {
            StringOrderedDictionary rawFilesAcquired = readRawFilesAcquired(rawFilesAcquiredPath);
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
        }  // countRawFilesAcquired()

        public static void Main(string[] args)
        {
            rawFileName = "";
            logFilePath = "";
            mockSequence = new List<string>();
            // StreamWriter logFile = null;
            parser = new CommandLine.Parser(with => with.HelpWriter = null);
            parserResult = parser.ParseArguments<Options>(args);
            parserResult
                .WithParsed<Options>(options => Run(options, args))
                .WithNotParsed(errs => DisplayHelp(parserResult, errs));
        } // Main()

        public static void DisplayHelp<T>(ParserResult<T> result, IEnumerable<Error> errors)
        {
            var help = HelpText.RenderUsageText(result);
            Console.WriteLine(help);
            var helpText = HelpText.AutoBuild(result, h =>
            {
                h.AdditionalNewLineAfterOption = false;
                h.MaximumDisplayWidth = 120;
                h.Heading = $"{AppName} v{AppVersion}".Pastel(Color.Cyan);
                h.Copyright = "Copyright 2023 Mayo Clinic";
                return HelpText.DefaultParsingErrorsHandler(result, h);
            }, e => e);
            Console.Error.WriteLine(helpText);
        } // DisplayHelp()

        public static void Run(Options options, string[] args)
        {
            if (options.Debug)
            {
                DebugMode = true;
            }

            if (!string.IsNullOrEmpty(options.Logfile))
            {
                logFilePath = options.Logfile;
            }

            if (string.IsNullOrEmpty(options.InputRawFile))
            {
                logerr("Please pass in the full path to a RAW file (using %R parameter in Xcalibur)");
                Environment.Exit(1);
            }
            else
            {
                rawFileName = options.InputRawFile;
            }

            if (!string.IsNullOrEmpty(options.MockSequence))
            {
                var str = options.MockSequence;
                var splits = str.Split(";");
                mockSequence.AddRange(splits.Select(filepath => filepath.Trim()).Where(contents => !string.IsNullOrEmpty(contents)));
                MockSequenceMode = true;
            }

            try
            {
                // BEG SETUP

                // Get the name of the current program (executable)
                var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                string logPath = currentProcess.MainModule?.FileName;
                string logFolderPath = Path.GetDirectoryName(logPath);
                if (string.IsNullOrEmpty(logFilePath))
                {
                    logFilePath = Path.Combine(logFolderPath, TriggerLogFileStem + "." + TriggerLogFileExtension);
                }
                logFile = new StreamWriter(logFilePath, true);
                logFile.AutoFlush = true;

                var exePath = currentProcess.MainModule?.FileName;
                log("");
                log("=============================================");
                log($"COMMAND: {exePath} {string.Join(" ", args)}");
                string rawFilePath = Path.GetFullPath(rawFileName);
                string rawFileBaseName = Path.GetFileName(rawFilePath);
                if (!File.Exists(rawFilePath))
                {
                    logerr($"{rawFileName} error: RAW file does not exist. Exiting.");
                    Environment.Exit(1);
                }
                string folderPath = Path.GetDirectoryName(rawFilePath);
                ConfigMap = ReadAndParseConfigFile(logPath);
                RepeatRun = ConfigMap.TryGetValue(RepeatRunKey, out string repeatRun) ? repeatRun : DefaultRepeatRun;
                TokenFile = ConfigMap.TryGetValue(TokenFileKey, out string tokenFile) ? tokenFile : DefaultTokenFile;
                SourceTrim = ConfigMap.TryGetValue(SourceTrimKey, out string sourceTrimPath) ? sourceTrimPath : DefaultSourceTrim;
                SldStartsWith = ConfigMap.TryGetValue(SldStartsWithKey, out string sldStartsWith) ? sldStartsWith : DefaultSldStartsWith;
                IgnorePostBlank = ConfigMap.GetValueOrDefault(IgnorePostBlankKey, DefaultIgnorePostBlank);
                PostBlankMatches = ConfigMap.TryGetValue(PostBlankMatchesKey, out string postBlankMatches) ? postBlankMatches : DefaultPostBlankMatches;
                RemoveFiles = ConfigMap.GetValueOrDefault(RemoveFilesKey, DefaultRemoveFiles);
                RemoveDirectories = ConfigMap.GetValueOrDefault(RemoveDirectoriesKey, DefaultRemoveDirectories);
                PreserveSld = ConfigMap.GetValueOrDefault(PreserveSldKey, DefaultPreserveSld);
                UpdateFiles = ConfigMap.GetValueOrDefault(UpdateFilesKey, DefaultUpdateFiles);
                bool tempDebug = ConfigMap.GetValueOrDefault(DebugKey, DefaultDebugging);
                DebugMode = DebugMode ? DebugMode : tempDebug;
                MinRawFileSize = ConfigMap.GetValueOrDefault(MinRawFileSizeKey, DefaultMinRawFileSize);
                if (!ConfigMap.TryGetValue(OutputDirKey, out string outputPath))
                {
                    logerr("Missing key \"" + OutputDirKey + "\" in MassSpecTrigger configuration file. Exiting.");
                    Environment.Exit(1);
                }

                if (string.IsNullOrEmpty(SldStartsWith))
                {
                    logwarn($"Config key \"{SldStartsWithKey}\" not set, using default value: \"{DefaultSldStartsWith}\"");
                    SldStartsWith = DefaultSldStartsWith;
                }

                if (IgnorePostBlank)
                {
                    log($"PostBlank files will be ignored for this sequence");
                    log($"Any RAW file in this sequence with \"{PostBlankMatches}\" in its name will be ignored");
                    logdbg($"POSTBLANK: PostBlankMatches string is \"{PostBlankMatches}\" and raw file name is {rawFileBaseName}");
                    if (ContainsCaseInsensitiveSubstring(rawFileBaseName, PostBlankMatches))
                    {
                        log($"POSTBLANK: Provided RAW file '{rawFileBaseName}' is a PostBlank and will be ignored. Exiting.");
                        Environment.Exit(0);
                    }
                }
                else
                {
                    logwarn($"PostBlank files will not be ignored. This will cause an error if the PostBlank file is saved in a different directory");
                }
                // END SETUP

                // BEG SLD / Check acquired raw
                string sldPath = folderPath;
                string searchPattern = SldStartsWith + "*";
                string sldExtension = "sld";
                string sldFile;
                string rawFilesAcquiredPath = Path.Combine(sldPath, RAW_FILES_ACQUIRED_BASE);

                // string[] sld_files = Directory.GetFiles(sldPath, searchPattern, SearchOption.TopDirectoryOnly)
                //     .Where(file => file.EndsWith(".sld", StringComparison.OrdinalIgnoreCase))
                //     .ToArray();
                // All items in the dictionary are kept in lower case to avoid dealing with case sensitive files and strings.
                StringOrderedDictionary rawFilesAcquiredDict;
                if (MockSequenceMode)
                {
                    log($"MOCK SEQUENCE MODE: mock sequence file contents are: [ {string.Join(", ", mockSequence)} ]");
                    if (!File.Exists(rawFilesAcquiredPath))
                    {
                        logdbg($"Acquisition status file does not exist, creating: '{rawFilesAcquiredPath}'");
                        rawFilesAcquiredDict = MockSLDReadSamples(rawFilesAcquiredPath, mockSequence);
                    }
                    else
                    {
                        logdbg($"Acquisition status file exists at: '{rawFilesAcquiredPath}'");
                        rawFilesAcquiredDict = readRawFilesAcquired(rawFilesAcquiredPath);
                    }

                    sldFile = Path.Combine(sldPath, "MOCK_SLD_FILE.sld");
                    SLD_FILE_PATH = sldFile;
                    logdbg($"Acquisition status file: '{rawFilesAcquiredPath}'");
                    logdbg($"Acquisition status file contents:\n{StringifyDictionary(rawFilesAcquiredDict)}'");
                }
                else
                {
                    string[] sld_files;
                    var all_sld_files = Directory.GetFiles(sldPath, searchPattern, SearchOption.TopDirectoryOnly)
                        .Where(file => file.EndsWith(".sld", StringComparison.OrdinalIgnoreCase));
                    if (IgnorePostBlank)
                    {
                        sld_files = all_sld_files.Where(file => !ContainsCaseInsensitiveSubstring(file, PostBlankMatches)).ToArray();
                    }
                    else
                    {
                        sld_files = all_sld_files.ToArray();
                    }
                    if (sld_files.Length != 1)
                    {
                        logerr($"Problem finding SLD file: directory \"{sldPath}\" contains {sld_files.Length} matching SLD files ({SldStartsWith}*.sld), directory must contain a single matching SLD file.");
                        Environment.Exit(1);
                    }
                    sldFile = sld_files[0];
                    log($"Using SLD file: {sldFile}.");
                    SLD_FILE_PATH = sldFile;
                    // only need to read the SLD the first time in dir.
                    if (!File.Exists(rawFilesAcquiredPath))
                    {
                        logdbg($"Acquisition status file does not exist, creating: '{rawFilesAcquiredPath}'");
                        rawFilesAcquiredDict = SLDReadSamples(sldFile);
                    }
                    else
                    {
                        logdbg($"Acquisition status file exists at: '{rawFilesAcquiredPath}'");
                        rawFilesAcquiredDict = readRawFilesAcquired(rawFilesAcquiredPath);
                    }
                }
                logdbg($"Acquisition status file: '{rawFilesAcquiredPath}'");
                if (rawFilesAcquiredDict.Count == 0)
                {
                    logerr($"Acquisition status internal dictionary is empty. Check {sldFile} and {rawFilesAcquiredPath}");
                    Environment.Exit(1);
                }

                if (!UpdateRawFilesAcquiredDict(rawFileName, rawFilesAcquiredDict))
                {
                    Environment.Exit(1);
                }
                log($"Updated acquisition status for RAW file {rawFileName}");
                writeRawFilesAcquired(rawFilesAcquiredPath, rawFilesAcquiredDict);
                // END SLD / Check acquired raw

                int total = rawFilesAcquiredDict.Count;
                int acquired = countRawFilesAcquired(rawFilesAcquiredPath);
                
                // BEG WRITE MSA
                // BEG MOVE FOLDER
                if (areAllRawFilesAcquired(rawFilesAcquiredDict))
                {
                    string destinationPath = ConstructDestinationPath(folderPath, outputPath, SourceTrim);
                    log($"{acquired}/{total} raw files acquired, beginning payload activity ...");
                    if (!PrepareOutputDirectory(destinationPath, MinRawFileSize, RawFilePattern))
                    {
                        logerr("Could not prepare destination: \"" + destinationPath + "\". Check this directory. Exiting.");
                        Environment.Exit(1);
                    }
                    log("Copying directory: \"" + folderPath + "\" => \"" + destinationPath + "\"");
                    CopyDirectory(folderPath, destinationPath, UpdateFiles);
                    RecursiveRemoveFiles(folderPath, RemoveFiles, RemoveDirectories, PreserveSld, logFile);
                    // if (RemoveFiles)
                    // {
                    //     
                    //     log("Removing source directory: \"" + folderPath + "\"");
                    //     Directory.Delete(folderPath, true);
                    // }
                    // else
                    // {
                    //     log("Config value '" + RemoveFilesKey + "' is not true, not deleting source directory");
                    // }
                    // END MOVE FOLDER
                    
                    // write MSAComplete.txt to The Final Destination
                    string ssRawFile = "raw_file=\"" + rawFileName + "\"";
                    string isRepeatRun = "false";
                    if (ContainsCaseInsensitiveSubstring(destinationPath, RepeatRun) || ContainsCaseInsensitiveSubstring(rawFileName, RepeatRun))
                    {
                        isRepeatRun = "true";
                    }
                    string ssRepeat = "repeat_run=\"" + isRepeatRun + "\"";
                    string msaFilePath = Path.Combine(destinationPath, TokenFile);
                    string ssDate = Timestamp();
                    using (StreamWriter msaFile = new StreamWriter(msaFilePath))
                    {
                        msaFile.WriteLine(ssDate);
                        msaFile.WriteLine(ssRawFile);
                        msaFile.WriteLine(ssRepeat);
                    }
                    log("Wrote trigger file: " + msaFilePath);
                    Environment.Exit(0);
                }
                else
                {
                    log($"{acquired}/{total} raw files acquired, not performing payload activities yet");
                }
                // END WRITE MSA
            }  // try
            catch (Exception ex)
            {
                logerr("Error: " + ex.Message);
            }
            finally
            {
                logFile?.Close();
            }

        } // Run()

    }  // MainClass()
}  // ns MassSpecTrigger

