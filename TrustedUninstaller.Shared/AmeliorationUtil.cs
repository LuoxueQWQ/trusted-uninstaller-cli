﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using TrustedUninstaller.Shared.Actions;
using TrustedUninstaller.Shared.Parser;
using TrustedUninstaller.Shared.Tasks;
using MessageBox = System.Windows.MessageBox;

namespace TrustedUninstaller.Shared
{

    public static class AmeliorationUtil
    {
        private static readonly ConfigParser Parser = new ConfigParser();

        private static readonly HttpClient Client = new HttpClient();

        //TODO: custom.yml path or .apbx path?
        public static Playbook Playbook { set; get; }

        public static readonly List<string> ErrorDisplayList = new List<string>();

        public static int GetProgressMaximum()
        {
            return Parser.Tasks.Sum(task => task.Actions.Sum(action => action.GetProgressWeight()));
        }
        
        public static bool AddTasks(string configPath, string file)
        {
            try
            {
                //This allows for a proper detection of if any error occurred, and if so the CLI will relay an :AME-Fatal Error:
                //This is important, as we want the process to stop immediately if a YAML syntax error was detected.
                bool hadError = false;
                
                //Adds the config file to the parser's task list
                Parser.Add(Path.Combine(configPath, file));

                var currentTask = Parser.Tasks[Parser.Tasks.Count - 1];

                if (File.Exists("TasksAdded.txt"))
                {
                    var doneTasks = File.ReadAllText("TasksAdded.txt").Split(new[] { "\r\n" }, StringSplitOptions.None);

                    if (doneTasks.Contains(currentTask.Title))
                    {
                        Parser.Tasks.Remove(currentTask);
                        return true;
                    }
                }

                //Get the features of the last added task (the task that was just added from the config file)
                var features = currentTask.Features;
                
                //Each feature would reference a directory that has a YAML file, we take those directories and then run the
                //AddTasks function again, until we reach a file that doesn't reference any other YAML files, and add them
                //all to the parser's tasks list.
                if (features == null) return true;
                foreach (var feature in features)
                {
                    var subResult = AddTasks(configPath, feature);
                    
                    // We could return false here, however we want to output ALL detected YAML errors,
                    // which is why we continue here.
                    if (!subResult) hadError = true;
                }

                return hadError ? false : true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error adding tasks in {configPath + "\\" + file}:\r\n{e.Message}");
                ErrorLogger.WriteToErrorLog(e.Message, e.StackTrace, $"Error adding tasks in {configPath + "\\" + file}.");
                return false;
            }
        }
        public static async Task<int> DoActions(UninstallTask task, UninstallTaskPrivilege privilege)
        {
            try
            {
                //If the privilege is admin and the program is running as TI, do not do the action.
                if (privilege == UninstallTaskPrivilege.Admin && WinUtil.IsTrustedInstaller())
                {
                    return 0;
                }
                
                if (privilege == UninstallTaskPrivilege.TrustedInstaller && !WinUtil.IsTrustedInstaller())
                {
                    Console.WriteLine("Relaunching as Trusted Installer!");
                    
                    var mmf = MemoryMappedFile.CreateNew("ImgA", 5000000);
                    WinUtil.RelaunchAsTrustedInstaller();
                    if (NativeProcess.Process == null)
                    {
                        ErrorLogger.WriteToErrorLog($"Could not launch TrustedInstaller process. Return output was null.",
                            Environment.StackTrace, "Error while attempting to sync with TrustedInstaller process.");
                        
                        Console.WriteLine(":AME-Fatal Error: Could not launch TrustedInstaller process.");
                        Environment.Exit(-1);
                    }
                    
                    var delay = 20;
                    while (!NativeProcess.Process.HasExited)
                    {
                        if (delay > 3500)
                        {
                            NativeProcess.Process.Kill();
                            
                            ErrorLogger.WriteToErrorLog($"Could not initialize memory data exchange. Timeframe exceeded.",
                                Environment.StackTrace, "Error while attempting to sync with TrustedInstaller process.");
                            
                            Console.WriteLine(":AME-Fatal Error: Could not initialize memory data exchange.");
                            Environment.Exit(-1);
                        }

                        Task.Delay(delay).Wait();
                        // Kind of inefficient looping this, however it's likely to cause access errors otherwise
                        using var stream = mmf.CreateViewStream();
                        using BinaryReader binReader = new BinaryReader(stream);
                        {
                            var res = binReader.ReadBytes((int)stream.Length);
                            var data = Encoding.UTF8.GetString(res);

                            var end = data.IndexOf('\0');
                            if (end == 0)
                            {
                                delay += 200;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    
                    var offset = 0;
                    var read = false;
                    using (var stream = mmf.CreateViewStream())
                    {
                        while (!NativeProcess.Process.HasExited || read)
                        {
                            read = false;
                            
                            BinaryReader binReader = new BinaryReader(stream);

                            binReader.BaseStream.Seek(offset, SeekOrigin.Begin);

                            var res = binReader.ReadBytes((int)stream.Length - offset);
                            var data = Encoding.UTF8.GetString(res);

                            var end = data.IndexOf("\0");

                            var content = data.Substring(0, end);
                            offset += Encoding.UTF8.GetBytes(content).Length;

                            var output = content.Split(new [] {Environment.NewLine}, StringSplitOptions.None);
                            if (output.Length > 0) output = output.Take(output.Length - 1).ToArray();
                            
                            foreach (var line in output)
                            {
                                Console.WriteLine(line);
                                read = true;
                                // Introducing ANY delay here makes it lag behind, which isn't ideal
                                //Task.Delay(5).Wait();
                            }
                            Task.Delay(20).Wait();
                        }
                    }
                    mmf.Dispose();
                    return 0; //Only returns after TI is done
                }

                //Goes through the list of tasks that are inside the parser class,
                //and runs the task using the RunTask method
                //Check the Actions folder inside the Shared folder for reference.
                foreach (ITaskAction action in task.Actions)
                {
                    int i = 0;

                    //var actionType = action.GetType().ToString().Replace("TrustedUninstaller.Shared.Actions.", "");
                    
                    do
                    {
                        //Console.WriteLine($"Running {actionType}");
                        Console.WriteLine();
                        try
                        {
                            await action.RunTask();
                            action.ResetProgress();
                        }
                        catch (Exception e)
                        {
                            action.ResetProgress();
                            if (e.InnerException != null)
                            {
                                ErrorLogger.WriteToErrorLog(e.InnerException.Message, e.InnerException.StackTrace, e.Message);
                            }
                            else
                            {
                                ErrorLogger.WriteToErrorLog(e.Message, e.StackTrace, action.ErrorString());
                                List<string> ExceptionBreakList = new List<string>() { "System.ArgumentException", "System.SecurityException", "System.UnauthorizedAccessException", "System.UnauthorizedAccessException", "System.TimeoutException" };
                                if (ExceptionBreakList.Any(x => x.Equals(e.GetType().ToString())))
                                {
                                    i = 10;
                                    break;
                                } 
                            }
                        }
                        Console.WriteLine($"Status: {action.GetStatus()}");
                        if (i > 0) Thread.Sleep(50);
                        i++;
                    } while (action.GetStatus() != UninstallTaskStatus.Completed && i < 10);

                    if (i == 10)
                    {
                        var errorString = action.ErrorString();
                        ErrorLogger.WriteToErrorLog(errorString, Environment.StackTrace, "Action failed to complete.");
                        // AmeliorationUtil.ErrorDisplayList.Add(errorString) would NOT work here since this
                        // might be a separate process, and thus has to be forwarded via the console
                        Console.WriteLine($":AME-ERROR: {errorString}");
                        //Environment.Exit(-2);
                        Console.WriteLine($"Action completed. Weight:{action.GetProgressWeight()}");
                        continue;
                    }
                    Console.WriteLine($"Action completed. Weight:{action.GetProgressWeight()}");
                }
                Console.WriteLine("Task completed.");
                
                File.AppendAllText("TasksAdded.txt", task.Title + Environment.NewLine);
            }
            catch (Exception e)
            {
                ErrorLogger.WriteToErrorLog(e.Message, e.StackTrace,
                    "Encountered an error while doing task actions.");
            }

            return 0;
        }

        public static Task<Playbook> DeserializePlaybook(string dir)
        {
            Playbook pb;
            
            XmlSerializer serializer = new XmlSerializer(typeof(Playbook));
            using (XmlReader reader = XmlReader.Create($"{dir}\\playbook.conf"))
            {
                pb = (Playbook)(serializer.Deserialize(reader));
            }
            pb.Path = dir;

            return Task.FromResult(pb);
        }

        public static async Task<int> StartAmelioration()
        {
            //Needed after defender removal's reboot, the "current directory" will be set to System32
            //After the auto start up.
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            if (File.Exists("TasksAdded.txt") && !WinUtil.IsTrustedInstaller())
            {
                File.Delete("TasksAdded.txt");
            }

            if (Directory.Exists("Logs") && !WinUtil.IsTrustedInstaller())
            {
                if (File.Exists("Logs\\AdminOutput.txt"))
                {
                    File.Delete("Logs\\AdminOutput.txt");
                }

                if (File.Exists("Logs\\TIOutput.txt"))
                {
                    File.Delete("Logs\\TIOutput.txt");
                }

                if (File.Exists("Logs\\FileChecklist.txt"))
                {
                    File.Delete("Logs\\FileChecklist.txt");
                }
            }

            //Check if KPH is installed.
            ServiceController service = ServiceController.GetDevices()
                                            .FirstOrDefault(s => s.DisplayName == "KProcessHacker2");
            if (service == null)
            {
                //Installs KPH
                await WinUtil.RemoveProtectionAsync();
            }

            var langsFile = Path.Combine($"{Playbook.Path}\\Configuration", "langs.txt");
            //Download language packs that were selected by the user
            if (!File.Exists(langsFile))
            {
                File.Create(langsFile);
            }

            //var langsSelected = File.ReadLines(langsFile);

            //await DownloadLanguagesAsync(langsSelected);

            //Start adding tasks from the top level configuration folder.
            if (!AddTasks($"{Playbook.Path}\\Configuration", "custom.yml"))
            {
                Console.WriteLine($":AME-Fatal Error: Error adding tasks.");
                Environment.Exit(1);
            }

            if (!Parser.Tasks.Any())
            {
                Console.Error.WriteLine($"Couldn't find any tasks.");
                return -1;
            }

            //Sort the list based on the priority value.
            if (Parser.Tasks.Any(x => x.Priority != Parser.Tasks.First().Priority))
                Parser.Tasks.Sort(new TaskComparer());

            UninstallTaskPrivilege prevPriv = UninstallTaskPrivilege.Admin;
            foreach (var task in Parser.Tasks.Where(task => task.Actions.Count != 0))
            {
                try
                {
                    if (prevPriv == UninstallTaskPrivilege.TrustedInstaller && task.Privilege == UninstallTaskPrivilege.TrustedInstaller && !WinUtil.IsTrustedInstaller())
                    {
                        continue;
                    }
                    await DoActions(task, task.Privilege);
                    prevPriv = task.Privilege;
                }
                catch (Exception ex)
                {
                    ErrorLogger.WriteToErrorLog(ex.Message, ex.StackTrace, "Error during DoAction loop.");
                }
            }

            if (WinUtil.IsTrustedInstaller()) return 0;
            
            WinUtil.RegistryManager.UnhookUserHives();

            //Check how many files were successfully and unsuccessfully deleted.
            var deletedItemsCount = 0;
            var failedDeletedItemsCount = 0;

            if (File.Exists("Logs\\FileChecklist.txt"))
            {
                using (var reader = new StreamReader("Logs\\FileChecklist.txt"))
                {
                    var data = reader.ReadToEnd();
                    var listData = data.Split(new [] { Environment.NewLine }, StringSplitOptions.None).ToList();
                    deletedItemsCount = listData.FindAll(s => s == "Deleted: True").Count();
                    failedDeletedItemsCount = listData.FindAll(s => s == "Deleted: False").Count();
                }

                using (var writer = new StreamWriter("Logs\\FileChecklist.txt", true))
                {
                    writer.WriteLine($"{deletedItemsCount} files were deleted successfully. " +
                        $"{failedDeletedItemsCount} files couldn't be deleted.");
                }
            }

            Console.WriteLine($"{deletedItemsCount} files were deleted successfully. " +
                $"{failedDeletedItemsCount} files couldn't be deleted.");


            //Check if the kernel driver is installed.
            service = ServiceController.GetDevices()
                .FirstOrDefault(s => s.DisplayName == "KProcessHacker2");
            if (service != null)
            { 
                //Remove Process Hacker's kernel driver.
                await WinUtil.UninstallDriver();
            }
            File.Delete("TasksAdded.txt");
            
            Console.WriteLine();
            Console.WriteLine("Playbook finished.");
            
            return 0;
        }
        public static async Task DownloadLanguagesAsync(IEnumerable<string> langsSelected)
        {

            foreach (var lang in langsSelected)
            {

                var lowerLang = lang.ToLower();

                var arch = RuntimeInformation.OSArchitecture;
                var winVersion = Environment.OSVersion.Version.Build;

                var convertedArch = "";
                switch (arch)
                {
                    case Architecture.X64:
                        convertedArch = "amd64";
                        break;
                    case Architecture.Arm64:
                        convertedArch = "arm64";
                        break;
                    case Architecture.X86:
                        convertedArch = "x86";
                        break;
                }

                var uuidOfWindowsVersion = "";
                var uuidResponse =
                    await Client.GetAsync(
                        $"https://api.uupdump.net/listid.php?search={winVersion}%20{convertedArch}&sortByDate=1");
                switch (uuidResponse.StatusCode)
                {
                    //200 Status code
                    case HttpStatusCode.OK:
                        {
                            var result = uuidResponse.Content.ReadAsStringAsync().Result;
                            //Gets the UUID of the first build object in the response, we take the first since it's the newest.
                            uuidOfWindowsVersion = (string)(JToken.Parse(result)["response"]?["builds"]?.Children().First()
                                .Children().First().Last());
                            break;
                        }
                    //400 Status code
                    case HttpStatusCode.BadRequest:
                        {
                            var result = uuidResponse.Content.ReadAsStringAsync().Result;
                            dynamic data = JObject.Parse(result);
                            Console.WriteLine($"Bad request.\r\nError:{data["response"]["error"]}");
                            break;
                        }
                    //429 Status code
                    case (HttpStatusCode)429:
                        {
                            var result = uuidResponse.Content.ReadAsStringAsync().Result;
                            dynamic data = JObject.Parse(result);
                            Console.WriteLine($"Too many requests, try again later.\r\nError:{data["response"]["error"]}");
                            break;
                        }
                    //500 Status code
                    case HttpStatusCode.InternalServerError:
                        {
                            var result = uuidResponse.Content.ReadAsStringAsync().Result;
                            dynamic data = JObject.Parse(result);
                            Console.WriteLine($"Internal Server Error.\r\nError:{data["response"]["error"]}");
                            break;
                        }
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                var responseString =
                    await Client.GetAsync(
                        $"https://api.uupdump.net/get.php?id={uuidOfWindowsVersion}&lang={lowerLang}");
                switch (responseString.StatusCode)
                {
                    //200 Status code
                    case HttpStatusCode.OK:
                        {

                            var result = responseString.Content.ReadAsStringAsync().Result;
                            dynamic data = JObject.Parse(result);
                            //Add different urls to different packages to a list
                            var urls = new Dictionary<string, string>
                        {
                            {
                                "basic", (string) data["response"]["files"][
                                    $"microsoft-windows-languagefeatures-basic-{lowerLang}-package-{convertedArch}.cab"]
                                [
                                    "url"]
                            },
                            {
                                "hw", (string) data["response"]["files"][
                                    $"microsoft-windows-languagefeatures-handwriting-{lowerLang}-package-{convertedArch}.cab"]
                                [
                                    "url"]
                            },
                            {
                                "ocr", (string) data["response"]["files"][
                                    $"microsoft-windows-languagefeatures-ocr-{lowerLang}-package-{convertedArch}.cab"][
                                    "url"]
                            },
                            {
                                "speech", (string) data["response"]["files"][
                                    $"microsoft-windows-languagefeatures-speech-{lowerLang}-package-{convertedArch}.cab"]
                                [
                                    "url"]
                            },
                            {
                                "tts", (string) data["response"]["files"][
                                    $"microsoft-windows-languagefeatures-texttospeech-{lowerLang}-package-{convertedArch}.cab"]
                                [
                                    "url"]
                            }
                        };


                            var amePath = Path.Combine(Path.GetTempPath(), "AME\\");
                            //Create the directory if it doesn't exist.
                            var file = new FileInfo(amePath);
                            file.Directory?.Create(); //Does nothing if the directory already exists

                            //Final result being "temp\AME\Languages\file.cab"
                            var downloadPath = Path.Combine(amePath, "Languages\\");
                            file = new FileInfo(downloadPath);
                            file.Directory?.Create();
                            using (var webClient = new WebClient())
                            {
                                Console.WriteLine($"Downloading {lowerLang}.cab file, please wait..");
                                foreach (var url in urls)
                                {
                                    //Check if the file exists, if it does exist, skip it.
                                    if (File.Exists(Path.Combine(downloadPath, $"{url.Key}_{lowerLang}.cab")))
                                    {
                                        Console.WriteLine($"{url.Key}_{lowerLang} already exists, skipping.");
                                        continue;
                                    }
                                    //Output file format: featureName_languageCode.cab: speech_de-de.cab
                                    webClient.DownloadFile(url.Value, $@"{downloadPath}\{url.Key}_{lowerLang}.cab");
                                }
                            }

                            break;
                        }
                    //400 Status code
                    case HttpStatusCode.BadRequest:
                        {
                            var result = responseString.Content.ReadAsStringAsync().Result;
                            dynamic data = JObject.Parse(result);
                            Console.WriteLine($"Bad request.\r\nError:{data["response"]["error"]}");
                            break;
                        }
                    //429 Status code
                    case (HttpStatusCode)429:
                        {
                            var result = responseString.Content.ReadAsStringAsync().Result;
                            dynamic data = JObject.Parse(result);
                            Console.WriteLine($"Too many requests, try again later.\r\nError:{data["response"]["error"]}");
                            break;
                        }
                    //500 Status code
                    case HttpStatusCode.InternalServerError:
                        {
                            var result = responseString.Content.ReadAsStringAsync().Result;
                            dynamic data = JObject.Parse(result);
                            Console.WriteLine($"Internal Server Error.\r\nError:{data["response"]["error"]}");
                            break;
                        }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
        
        public static async Task<bool> SafeRunAction(ITaskAction action)
        {
            try
            {
                return await action.RunTask();
            }
            catch (Exception e)
            {
                action.ResetProgress();
                if (e.InnerException != null)
                {
                    ErrorLogger.WriteToErrorLog(e.InnerException.Message, e.InnerException.StackTrace, e.Message);
                }
                else
                {
                    ErrorLogger.WriteToErrorLog(e.Message, e.StackTrace, action.ErrorString());
                }
            }
            return false;
        }
    }
}
