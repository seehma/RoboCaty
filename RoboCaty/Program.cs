// ABB Robotics PC SDK
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.Discovery;
using ABB.Robotics.Controllers.IOSystemDomain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using TwinCAT;
// TwinCAT
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace AdsRobotInterface
{
    class Program
    {
        // --- Configuration Defaults ---
        static string textFilePath = @"C:\temp\vars_robot.txt";
        static string amsNetId = "199.4.42.250.1.1";
        static int amsPort = 851;
        static int cycleTimeMs = 100;
        static volatile bool showDashboard = false;

        // --- Threading & Sync Globals ---
        private static bool _quitRequested = false;
        private static object _syncLock = new object();
        private static AutoResetEvent _waitHandle = new AutoResetEvent(false);

        // --- Robot Controller reference
        private static Controller? robotController = null;

        static void Main(string[] args)
        {
            if (!ParseArguments(args)) return;

            // Setup & ProcessExit
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(CurrentDomain_ProcessExit);

            if (!File.Exists(textFilePath))
            {
                ExitWithError($"ERROR: Configuration file not found:\n'{Path.GetFullPath(textFilePath)}'");
                return;
            }

            // Load Mappings
            Console.WriteLine("Reading configuration file...");
            List<SignalMapping> mappings = LoadMappings(textFilePath);
            if (mappings.Count == 0)
            {
                ExitWithError("ERROR: No valid mapping entries found.");
                return;
            }

            // Connect Robot
            Console.WriteLine("Scanning for virtual ABB Controller...");
            robotController = ConnectToVirtualController();
            if (robotController == null)
            {
                ExitWithError("ERROR: No virtual robot controller found!");
                return;
            }

            PrintStatusScreen(mappings.Count);

            // Start Worker Thread
            Thread workerThread = new Thread(() => WorkerLoop(mappings));
            workerThread.Name = "RoboCatyWorker";
            workerThread.Start();

            // Input Loop (Main Thread)
            while (!_quitRequested)
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(true);

                    if (keyInfo.Key == ConsoleKey.Q)
                    {
                        break;
                    }
                    else if (keyInfo.Key == ConsoleKey.V)
                    {
                        showDashboard = !showDashboard;
                        if (!showDashboard)
                        {
                            Console.Clear();
                            PrintStatusScreen(mappings.Count);
                        }
                        Thread.Sleep(200);

                        while (Console.KeyAvailable) Console.ReadKey(true);
                    }
                }
                Thread.Sleep(50);
            }

            Console.WriteLine("\nStopping worker thread...");
            SetQuitRequested();
            _waitHandle.WaitOne();
            Console.WriteLine("Main thread finished. Bye!");
            CleanupRobot();
        }

        // --- Reprint the Home-Screen ---
        static void PrintStatusScreen(int mappingCount)
        {
            PrintHeader();
            Console.WriteLine($"[OK] Variables loaded: {mappingCount}");

            if (robotController != null)
                Console.WriteLine($"[OK] Robot connected:  {robotController.SystemName}");
            else
                Console.WriteLine($"[--] Robot connected:  Waiting...");

            Console.WriteLine($"[OK] TwinCAT Status:   Running (Cyclic)");

            Console.WriteLine("\n---------------------------------------------------");
            Console.WriteLine(" SYSTEM RUNNING (Background Mode).");
            Console.WriteLine(" Press [V] -> Toggle Live Dashboard");
            Console.WriteLine(" Press [Q] -> Quit Program");
            Console.WriteLine("---------------------------------------------------");
        }

        // --- WORKER THREAD ---
        private static void WorkerLoop(List<SignalMapping> mappings)
        {
            using (AdsClient client = new AdsClient())
            {
                try
                {
                    client.Connect(amsNetId, amsPort);
                    if (!client.IsConnected)
                    {
                        Console.WriteLine("ERROR: Could not connect to TwinCAT.");
                        _waitHandle.Set();
                        return;
                    }

                    ISymbolLoader loader = SymbolLoaderFactory.Create(client, SymbolLoaderSettings.Default);
                    List<string> logBuffer = new List<string>();

                    bool _wasShowingDashboard = false;
                    DateTime _lastDisplayUpdate = DateTime.MinValue;
                    TimeSpan _displayInterval = TimeSpan.FromMilliseconds(500);

                    bool stop = false;
                    while (!stop)
                    {
                        lock (_syncLock) { if (_quitRequested) stop = true; }

                        if (!stop)
                        {
                            if (showDashboard)
                            {
                                // Only update display if enough time passed (500ms)
                                if (DateTime.Now - _lastDisplayUpdate > _displayInterval)
                                {
                                    logBuffer.Clear();
                                    ProcessMappings(mappings, loader, robotController, logBuffer);

                                    Console.Clear();
                                    Console.WriteLine($"--- RoboCaty LIVE DASHBOARD ({DateTime.Now:HH:mm:ss}) ---");
                                    Console.WriteLine($"Data Cycle: {cycleTimeMs}ms | Display Update: 500ms");
                                    Console.WriteLine($"{"DIR",-5} | {"ADS VARIABLE",-30} | {"VAL",-8} | {"ROBOT SIGNAL",-30}");
                                    Console.WriteLine(new string('-', 80));

                                    foreach (var log in logBuffer) Console.WriteLine(log);

                                    _wasShowingDashboard = true;
                                    _lastDisplayUpdate = DateTime.Now;
                                }
                                else
                                {
                                    ProcessMappings(mappings, loader, robotController, null);
                                }
                            }
                            else
                            {
                                // Dashboard OFF: Silent Mode
                                if (_wasShowingDashboard)
                                {
                                    _wasShowingDashboard = false;
                                }

                                ProcessMappings(mappings, loader, robotController, null);
                            }

                            Thread.Sleep(cycleTimeMs);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"CRITICAL WORKER ERROR: {ex.Message}");
                }
                finally
                {
                    _waitHandle.Set();
                }
            }
        }

        // --- Threading Helpers ---
        private static void SetQuitRequested() 
        { 
            lock (_syncLock) 
            { 
                _quitRequested = true; 
            } 
        }

        // --- EVENT: Process Exit ---
        static void CurrentDomain_ProcessExit(object? sender, EventArgs e) 
        { 
            SetQuitRequested(); 
            CleanupRobot(); 
        }

        private static void CleanupRobot()
        {
            if (robotController != null)
            {
                try 
                { 
                    robotController.Logoff(); 
                    robotController.Dispose(); 
                }
                catch { }
                finally 
                { 
                    robotController = null; 
                }
            }
        }

        // --- DATA EXCHANGE LOGIC ---
        static void ProcessMappings(List<SignalMapping> mappings, ISymbolLoader adsLoader, Controller? robotCtrl, List<string>? logBuffer)
        {
            if (robotCtrl == null) return;
            foreach (var map in mappings)
            {
                try
                {
                    if (map.Direction == "r") TransferAdsToRobot(adsLoader, robotCtrl, map.AdsPath, map.RobotSignal, map.Bits, logBuffer);
                    else if (map.Direction == "w") TransferRobotToAds(adsLoader, robotCtrl, map.AdsPath, map.RobotSignal, map.Bits, logBuffer);
                }
                catch (Exception ex)
                {
                    if (logBuffer != null) logBuffer.Add($"[ERR] {map.AdsPath}: {ex.Message}");
                }
            }
        }

        static void TransferAdsToRobot(ISymbolLoader l, Controller c, string a, string r, int b, List<string>? logs)
        {
            object? val = ReadAdsValue(l, a);
            if (val == null)
            {
                if (logs != null) logs.Add($"ADS->ROB | {a,-30} | {"ERR",-8} | {r} (Missing)");
                return;
            }

            using (Mastership.Request(c))
            {
                Signal sig = c.IOSystem.GetSignal(r);
                if (sig == null)
                {
                    if (logs != null) logs.Add($"ADS->ROB | {a,-30} | {val,-8} | {r} (Sig Missing)");
                    return;
                }

                try
                {
                    string dVal = "";

                    if (b == 1)
                    {
                        bool v = Convert.ToBoolean(val);
                        if (sig is DigitalSignal di && di.Value != (v ? 1 : 0)) di.Value = v ? 1 : 0;
                        if (logs != null) dVal = v ? "TRUE" : "FALSE";
                    }
                    else
                    {
                        double d = Convert.ToDouble(val);
                        if (sig is GroupSignal g) g.Value = (float)d; else if (sig is AnalogSignal an) an.Value = (float)d;
                        if (logs != null) dVal = d.ToString("0.##");
                    }

                    if (logs != null) logs.Add($"ADS->ROB | {a,-30} | {dVal,-8} | {r}");
                }
                catch (Exception x)
                {
                    if (logs != null) logs.Add($"[ERR] {r}: {x.Message}");
                }
            }
        }

        static void TransferRobotToAds(ISymbolLoader l, Controller c, string a, string r, int b, List<string>? logs)
        {
            Signal sig = c.IOSystem.GetSignal(r);
            if (sig == null)
            {
                if (logs != null) logs.Add($"ROB->ADS | {a,-30} | {"---",-8} | {r} (Sig Missing)");
                return;
            }

            object? wVal = null;
            string dVal = "";

            if (b == 1)
            {
                wVal = (sig.Value == 1);
                if (logs != null) dVal = (sig.Value == 1) ? "TRUE" : "FALSE";
            }
            else
            {
                double v = sig.Value;
                if (logs != null) dVal = v.ToString("0.##");
                if (b == 8) wVal = Convert.ToByte(v); else if (b == 16) wVal = Convert.ToUInt16(v); else if (b == 32) wVal = Convert.ToUInt32(v); else wVal = v;
            }

            WriteAdsValue(l, a, wVal);

            if (logs != null) logs.Add($"ROB->ADS | {a,-30} | {dVal,-8} | {r}");
        }

        static object? ReadAdsValue(ISymbolLoader l, string p) { try { ISymbol s = l.Symbols[p]; if (s is IValueSymbol v) return v.ReadValue(); } catch { } return null; }
        static void WriteAdsValue(ISymbolLoader l, string p, object v) { try { ISymbol s = l.Symbols[p]; if (s is IValueSymbol vs) vs.WriteValue(v); } catch { } }

        static List<SignalMapping> LoadMappings(string p)
        {
            var l = new List<SignalMapping>();
            foreach (var ln in File.ReadAllLines(p))
            {
                var m = Regex.Match(ln.Trim(), @"^(r|w)#\s*([^:]+):\s*([^\[]+)\[(\d+)\]");
                if (m.Success) l.Add(new SignalMapping { Direction = m.Groups[1].Value, AdsPath = m.Groups[2].Value.Trim(), RobotSignal = m.Groups[3].Value.Trim(), Bits = int.Parse(m.Groups[4].Value) });
            }
            return l;
        }

        static Controller? ConnectToVirtualController()
        {
            var s = new NetworkScanner(); s.Scan();
            foreach (ControllerInfo c in s.Controllers)
            {
                if (c.IsVirtual)
                {
                    try
                    {
#pragma warning disable CS0618
                        var ctrl = ControllerFactory.CreateFrom(c);
#pragma warning restore CS0618
                        ctrl.Logon(UserInfo.DefaultUser); return ctrl;
                    }
                    catch { }
                }
            }
            return null;
        }

        static void PrintHeader()
        {
            Console.WriteLine("###############################################");
            Console.WriteLine("#             RoboCaty v1.0.0                 #");
            Console.WriteLine("#     TwinCAT <-> ABB Robot Interface         #");
            Console.WriteLine("###############################################");
            Console.WriteLine($"NetID:   {amsNetId}");
            Console.WriteLine($"Port:    {amsPort}");
            Console.WriteLine($"File:    {textFilePath}");
            Console.WriteLine($"Cycle:   {cycleTimeMs} ms");
            Console.WriteLine("-----------------------------------------------");
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
            Console.WriteLine("\nExample: RoboCaty.exe -file \"config.txt\" -time 10");
            Console.ReadKey();
        }

        static void ExitWithError(string m) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("\n" + m); Console.ResetColor(); Console.ReadLine(); Environment.Exit(-1); }
    }

    // Data Structure for Signal Mapping
    class SignalMapping
    {
        public string Direction { get; set; } = null!;
        public string AdsPath { get; set; } = null!;
        public string RobotSignal { get; set; } = null!;
        public int Bits { get; set; }
    }
}