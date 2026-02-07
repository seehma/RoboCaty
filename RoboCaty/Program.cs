using System.Text.RegularExpressions;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.Discovery;
using ABB.Robotics.Controllers.IOSystemDomain;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace AdsRobotInterface
{
    class Program
    {
        static string textFilePath = @"C:\temp\vars_robot.txt";
        static string amsNetId = "199.4.42.250.1.1";
        static int amsPort = 851;
        static int cycleTimeMs = 500;
        static bool showDashboard = false;

        static void Main(string[] args)
        {
            if (!ParseArguments(args)) return;

            ShowConfiguration();
            Console.WriteLine("\n--- RoboCaty: TwinCAT <-> ABB Interface ---");

            if (!File.Exists(textFilePath))
            {
                ExitWithError($"ERROR: Configuration file not found:\n'{Path.GetFullPath(textFilePath)}'");
                return;
            }

            Console.WriteLine("Reading configuration...");
            List<SignalMapping> mappings = LoadMappings(textFilePath);

            if (mappings.Count == 0)
            {
                ExitWithError("ERROR: No valid entries found in the configuration file.");
                return;
            }
            Console.WriteLine($"[OK] {mappings.Count} variables loaded.");

            Console.WriteLine("Scanning for virtual ABB controller...");
            Controller robotController = ConnectToVirtualController();
            if (robotController == null)
            {
                ExitWithError("ERROR: No virtual robot controller found!\nPlease ensure RobotStudio is running.");
                return;
            }
            Console.WriteLine($"[OK] Robot controller connected: {robotController.SystemName}");

            using (AdsClient client = new AdsClient())
            {
                try
                {
                    Console.WriteLine($"Connecting to TwinCAT {amsNetId}:{amsPort}...");
                    client.Connect(amsNetId, amsPort);

                    if (!client.IsConnected)
                    {
                        ExitWithError($"ERROR: Could not connect to TwinCAT.");
                        return;
                    }

                    ISymbolLoader loader = SymbolLoaderFactory.Create(client, SymbolLoaderSettings.Default);
                    Console.WriteLine("[OK] TwinCAT connected.");

                    Console.WriteLine("\n================================================");
                    Console.WriteLine(" Program running in background.");
                    Console.WriteLine(" [V]   -> Toggle Live Dashboard (On/Off)");
                    Console.WriteLine(" [Q]   -> Quit Program");
                    Console.WriteLine("================================================");

                    bool running = true;
                    List<string> currentLogBuffer = new List<string>();

                    while (running)
                    {
                        currentLogBuffer.Clear();

                        ProcessMappings(mappings, loader, robotController, currentLogBuffer);

                        if (showDashboard)
                        {
                            Console.Clear();
                            Console.WriteLine($"--- RoboCaty MONITORING ACTIVE ({DateTime.Now:HH:mm:ss}) ---");
                            Console.WriteLine($"Cycle time: {cycleTimeMs}ms | Mappings: {mappings.Count}\n");
                            Console.WriteLine($"{"DIRECTION",-10} | {"ADS VARIABLE",-30} | {"VALUE",-10} | {"ROBOT SIGNAL",-30}");
                            Console.WriteLine(new string('-', 90));

                            foreach (string logLine in currentLogBuffer)
                            {
                                Console.WriteLine(logLine);
                            }
                        }

                        Thread.Sleep(cycleTimeMs);

                        if (Console.KeyAvailable)
                        {
                            var keyInfo = Console.ReadKey(true);
                            if (keyInfo.Key == ConsoleKey.Q || keyInfo.Key == ConsoleKey.Escape)
                            {
                                running = false;
                                Console.WriteLine("\nStopping program...");
                            }
                            else if (keyInfo.Key == ConsoleKey.V)
                            {
                                showDashboard = !showDashboard;
                                if (!showDashboard)
                                {
                                    Console.Clear();
                                    Console.WriteLine("Live dashboard deactivated.");
                                    Console.WriteLine("Press [V] to enable, [Q] to quit.");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ExitWithError($"CRITICAL ERROR: {ex.Message}");
                }
            }

            if (robotController != null)
            {
                robotController.Logoff();
                robotController.Dispose();
            }
        }

        // ---------------------------------------------------------
        //  LOAD MAPPINGS
        // ---------------------------------------------------------
        static List<SignalMapping> LoadMappings(string filePath)
        {
            List<SignalMapping> list = new List<SignalMapping>();
            string[] lines = File.ReadAllLines(filePath);

            foreach (string line in lines)
            {
                string cleanLine = line.Trim();
                if (string.IsNullOrEmpty(cleanLine)) continue;

                var match = Regex.Match(cleanLine, @"^(r|w)#\s*([^:]+):\s*([^\[]+)\[(\d+)\]");

                if (match.Success)
                {
                    SignalMapping map = new SignalMapping
                    {
                        Direction = match.Groups[1].Value,
                        AdsPath = match.Groups[2].Value.Trim(),
                        RobotSignal = match.Groups[3].Value.Trim(),
                        Bits = int.Parse(match.Groups[4].Value)
                    };
                    list.Add(map);
                }
                else
                {
                    Console.WriteLine($"[WARNING] Invalid line ignored: {cleanLine}");
                }
            }
            return list;
        }

        // ---------------------------------------------------------
        //  PROCESS MAPPINGS
        // ---------------------------------------------------------
        static void ProcessMappings(List<SignalMapping> mappings, ISymbolLoader adsLoader, Controller robotCtrl, List<string> logBuffer)
        {
            foreach (var map in mappings)
            {
                try
                {
                    if (map.Direction == "r")
                    {
                        TransferAdsToRobot(adsLoader, robotCtrl, map.AdsPath, map.RobotSignal, map.Bits, logBuffer);
                    }
                    else if (map.Direction == "w")
                    {
                        TransferRobotToAds(adsLoader, robotCtrl, map.AdsPath, map.RobotSignal, map.Bits, logBuffer);
                    }
                }
                catch (Exception ex)
                {
                    logBuffer.Add($"[ERR] {map.AdsPath}: {ex.Message}");
                }
            }
        }

        static void TransferAdsToRobot(ISymbolLoader adsLoader, Controller robotCtrl, string adsPath, string signalName, int bits, List<string> logs)
        {
            object val = ReadAdsValue(adsLoader, adsPath);
            if (val == null)
            {
                logs.Add($"ADS->ROB   | {adsPath,-30} | {"ERR",-10} | {signalName} (ADS var missing)");
                return;
            }

            // Request Mastership to write to Robot
            using (Mastership.Request(robotCtrl))
            {
                Signal sig = robotCtrl.IOSystem.GetSignal(signalName);
                if (sig == null)
                {
                    logs.Add($"ADS->ROB   | {adsPath,-30} | {val,-10} | {signalName} (Signal missing)");
                    return;
                }

                try
                {
                    string displayVal = val.ToString();

                    if (bits == 1)
                    {
                        bool b = Convert.ToBoolean(val);
                        if (sig is DigitalSignal di)
                        {
                            // Write only if changed to save performance (optional)
                            if (di.Value != (b ? 1 : 0)) di.Value = b ? 1 : 0;
                        }
                        displayVal = b ? "TRUE" : "FALSE";
                    }
                    else
                    {
                        double d = Convert.ToDouble(val);
                        if (sig is GroupSignal g) g.Value = (float)d;
                        else if (sig is AnalogSignal a) a.Value = (float)d;
                        displayVal = d.ToString("0.##");
                    }
                    logs.Add($"ADS->ROB   | {adsPath,-30} | {displayVal,-10} | {signalName}");
                }
                catch (Exception ex) { logs.Add($"[ERR WRITE] {signalName}: {ex.Message}"); }
            }
        }

        static void TransferRobotToAds(ISymbolLoader adsLoader, Controller robotCtrl, string adsPath, string signalName, int bits, List<string> logs)
        {
            Signal sig = robotCtrl.IOSystem.GetSignal(signalName);
            if (sig == null)
            {
                logs.Add($"ROB->ADS   | {adsPath,-30} | {"---",-10} | {signalName} (Signal missing)");
                return;
            }

            object writeVal = null;
            string displayVal = "";

            if (bits == 1)
            {
                writeVal = (sig.Value == 1);
                displayVal = (sig.Value == 1) ? "TRUE" : "FALSE";
            }
            else
            {
                double v = sig.Value;
                displayVal = v.ToString("0.##");

                // Convert to specific ADS types based on bits
                if (bits == 8) writeVal = Convert.ToByte(v);
                else if (bits == 16) writeVal = Convert.ToUInt16(v);
                else if (bits == 32) writeVal = Convert.ToUInt32(v);
                else writeVal = v;
            }

            WriteAdsValue(adsLoader, adsPath, writeVal);
            logs.Add($"ROB->ADS   | {adsPath,-30} | {displayVal,-10} | {signalName}");
        }

        // ---------------------------------------------------------
        // HELPER METHODS
        // ---------------------------------------------------------
        static object ReadAdsValue(ISymbolLoader loader, string path)
        {
            try
            {
                ISymbol s = loader.Symbols[path];
                if (s is IValueSymbol v) return v.ReadValue();
            }
            catch { }
            return null;
        }

        static void WriteAdsValue(ISymbolLoader loader, string path, object value)
        {
            try
            {
                ISymbol s = loader.Symbols[path];
                if (s is IValueSymbol v) v.WriteValue(value);
            }
            catch { }
        }

        static bool ParseArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (arg == "-help" || arg == "/?") { ShowHelp(); return false; }
                if (arg == "-verbose") { showDashboard = true; continue; }
                if (i + 1 < args.Length)
                {
                    if (arg == "-netid") { amsNetId = args[i + 1]; i++; }
                    else if (arg == "-port") { int.TryParse(args[i + 1], out amsPort); i++; }
                    else if (arg == "-file") { textFilePath = args[i + 1]; i++; }
                    else if (arg == "-time") { int.TryParse(args[i + 1], out cycleTimeMs); i++; }
                }
            }
            return true;
        }

        static void ShowHelp()
        {
            Console.WriteLine("\n=== RoboCaty Help ===");
            Console.WriteLine("-netid\t\tTwinCAT NetID (Default: Local)");
            Console.WriteLine("-port\t\tADS Port (Default: 851)");
            Console.WriteLine("-file\t\tPath to mapping file");
            Console.WriteLine("-time\t\tCycle time in ms (Default: 2000)");
            Console.WriteLine("-verbose\tEnable live dashboard immediately");
            Console.WriteLine("\nExample: RoboCaty.exe -file \"config.txt\" -time 500");
            Console.ReadKey();
        }

        static void ShowConfiguration()
        {
            Console.WriteLine("--------------------------------");
            Console.WriteLine($"  NetID    : {amsNetId}");
            Console.WriteLine($"  Port     : {amsPort}");
            Console.WriteLine($"  File     : {textFilePath}");
            Console.WriteLine($"  Cycle    : {cycleTimeMs} ms");
            Console.WriteLine($"  Verbose  : {showDashboard}");
            Console.WriteLine("--------------------------------");
        }

        static void ExitWithError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n" + message);
            Console.ResetColor();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
            Environment.Exit(-1);
        }

        static Controller ConnectToVirtualController()
        {
            NetworkScanner s = new NetworkScanner();
            s.Scan();

            foreach (ControllerInfo c in s.Controllers)
            {
                if (c.IsVirtual)
                {
                    try
                    {
                        Controller ctrl = ControllerFactory.CreateFrom(c);

                        ctrl.Logon(UserInfo.DefaultUser);
                        return ctrl;
                    }
                    catch { }
                }
            }
            return null;
        }
    }

    // ---------------------------------------------------------
    // Data Structure for parsed mappings
    // ---------------------------------------------------------
    class SignalMapping
    {
        public string Direction { get; set; }
        public string AdsPath { get; set; }
        public string RobotSignal { get; set; }
        public int Bits { get; set; }
    }
}