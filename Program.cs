using LoggerConsoleApp.UserServiceReference;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.ServiceModel.Web;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class LogEntry
{
    public string Methode { get; set; }
    public int Code { get; set; }
    public string Message { get; set; }
    public long ExecutionTimeMs { get; set; }
    public long NetworkLatencyMs { get; set; }
    public int VirtualUsers { get; set; }
    public string Exception { get; set; }
    public string MachineName { get; set; }
    public string Protocol { get; set; }
    public int ThreadCount { get; set; }
    public int ReturnedLines { get; set; }
}

public class PeakHour
{
    public int Id { get; set; }
    public DateTime DateAnalyse { get; set; }
    public int HeurePointe { get; set; }
    public int TotalLogs { get; set; }
}

public class PerformanceMonitor
{
    private static PerformanceCounter cpuCounter;
    private static PerformanceCounter ramCounter;
    private static PerformanceCounter diskReadCounter;
    private static PerformanceCounter diskWriteCounter;
    private static bool monitoringEnabled = true;
    private static bool inputMode = false;
    private static readonly object consoleLock = new object();

    public static void Initialize()
    {
        try
        {
            cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            diskReadCounter = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", "_Total");
            diskWriteCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", "_Total");

            cpuCounter.NextValue();
            diskReadCounter.NextValue();
            diskWriteCounter.NextValue();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur initialisation moniteur: {ex.Message}");
        }
    }

    public static void SetInputMode(bool isInputMode)
    {
        inputMode = isInputMode;
    }

    public static void StartMonitoring()
    {
        Thread monitorThread = new Thread(() =>
        {
            while (monitoringEnabled)
            {
                try
                {
                  
                    if (!inputMode)
                    {
                        DisplayPerformanceInfo();
                    }
                    Thread.Sleep(2000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erreur monitoring: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true
        };

        monitorThread.Start();
    }

    private static void DisplayPerformanceInfo()
    {
        if (cpuCounter == null || inputMode) return;

        lock (consoleLock)
        {
            int currentTop = Console.CursorTop;
            bool cursorVisible = Console.CursorVisible;
            Console.CursorVisible = false;

            Console.SetCursorPosition(0, 0);

            Console.ForegroundColor = ConsoleColor.Cyan;
            string header = "=============== PERFORMANCE MONITOR ===============";
            Console.WriteLine(header.PadRight(Console.WindowWidth - 1));

            Console.SetCursorPosition(0, 1);
            float cpu = cpuCounter.NextValue();
            string cpuLine = $"| CPU: {cpu:F1}%";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(cpuLine.PadRight(20));
            DrawMiniBar(cpu, 100, 20);

            Console.SetCursorPosition(0, 2);
            Console.ForegroundColor = ConsoleColor.Cyan;
            float availableRam = ramCounter.NextValue();
            string ramLine = $"| RAM Libre: {availableRam:F0} MB";
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(ramLine.PadRight(Console.WindowWidth - 1));

            Console.SetCursorPosition(0, 3);
            Console.ForegroundColor = ConsoleColor.Cyan;
            float diskRead = diskReadCounter.NextValue() / 1024 / 1024;
            float diskWrite = diskWriteCounter.NextValue() / 1024 / 1024;
            string diskLine = $"| Disque: R:{diskRead:F1} MB/s W:{diskWrite:F1} MB/s";
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(diskLine.PadRight(Console.WindowWidth - 1));

            Console.SetCursorPosition(0, 4);
            Console.ForegroundColor = ConsoleColor.Cyan;
            string processLine = $"| Processus: {Process.GetProcesses().Length}       Threads App: {Process.GetCurrentProcess().Threads.Count}";
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(processLine.PadRight(Console.WindowWidth - 1));

            Console.SetCursorPosition(0, 5);
            Console.ForegroundColor = ConsoleColor.Cyan;
            string footer = "===================================================";
            Console.WriteLine(footer.PadRight(Console.WindowWidth - 1));
            Console.ResetColor();

            Console.CursorVisible = cursorVisible;

            if (currentTop > 6)
                Console.SetCursorPosition(0, currentTop);
            else
                Console.SetCursorPosition(0, 7);
        }
    }

    public static void WriteToConsole(Action writeAction)
    {
        lock (consoleLock)
        {
            writeAction();
        }
    }

    private static void DrawMiniBar(float value, float max, int length)
    {
        int filled = (int)((value / max) * length);
        Console.Write(" [");

        for (int i = 0; i < length; i++)
        {
            if (i < filled)
            {
                if (value >= 90) Console.ForegroundColor = ConsoleColor.Red;
                else if (value >= 80) Console.ForegroundColor = ConsoleColor.Yellow;
                else Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("#");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("-");
            }
        }
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("]");
    }

    public static void Stop()
    {
        monitoringEnabled = false;
        cpuCounter?.Dispose();
        ramCounter?.Dispose();
        diskReadCounter?.Dispose();
        diskWriteCounter?.Dispose();
    }
}

class Program
{
    private static readonly ConcurrentQueue<LogEntry> logQueue = new ConcurrentQueue<LogEntry>();
    private static bool running = true;
    private static int totalCalls = 0;
    private static Dictionary<string, int> methodCallCount = new Dictionary<string, int>();

    private static readonly string connectionString = GetConnectionString();

    private static string GetConnectionString()
    {
        try
        {
            var configConnectionString = ConfigurationManager.ConnectionStrings["DefaultConnection"]?.ConnectionString;
            if (!string.IsNullOrEmpty(configConnectionString))
            {
                return configConnectionString;
            }
        }
        catch
        {
            // Ignore config errors
        }

        return "Data Source=.;Initial Catalog=SolutionTest;Integrated Security=True;";
    }

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Clear();

        Console.WriteLine("TESTEUR WCF UNIVERSEL - Démarrage...");

        PerformanceMonitor.Initialize();
        PerformanceMonitor.StartMonitoring();

        Thread.Sleep(1000);

        Console.SetCursorPosition(0, 7);

        Thread processingThread = new Thread(ProcessLogs);
        processingThread.Start();

        StartHttpServer("http://localhost:5000/log/");
        Console.WriteLine("Logger démarré sur http://localhost:5000/log/\n");

        PerformanceMonitor.SetInputMode(true);

        Console.Write("Nombre d'utilisateurs virtuels: ");
        var usersInput = Console.ReadLine();
        int virtualUsers = int.TryParse(usersInput, out int parsed) ? parsed : 5;

        Console.Write("Nombre d'appels par utilisateur: ");
        var callsInput = Console.ReadLine();
        int callsPerUser = int.TryParse(callsInput, out int parsedCalls) ? parsedCalls : 10;

        // DÉSACTIVER LE MODE SAISIE
        PerformanceMonitor.SetInputMode(false);

        try
        {
            var binding = new WebHttpBinding();
            binding.Security.Mode = WebHttpSecurityMode.None;
            binding.MaxReceivedMessageSize = 2147483647;
            binding.ReaderQuotas.MaxDepth = 32;
            binding.ReaderQuotas.MaxStringContentLength = 8192;
            binding.ReaderQuotas.MaxArrayLength = 16384;
            binding.ReaderQuotas.MaxBytesPerRead = 4096;
            binding.ReaderQuotas.MaxNameTableCharCount = 16384;

            var endpoint = new EndpointAddress("http://localhost/Host/UserService.svc");

            var client = new UserServiceClient(binding, endpoint);

            if (client.Endpoint.Behaviors.Find<WebHttpBehavior>() == null)
            {
                client.Endpoint.Behaviors.Add(new WebHttpBehavior());
            }

            client.Endpoint.Contract.SessionMode = SessionMode.NotAllowed;

            var testeur = new TesteurWCF(client);

            Console.WriteLine($"\n--- DÉMARRAGE DES TESTS ---");
            Console.WriteLine($"Utilisateurs virtuels: {virtualUsers}");
            Console.WriteLine($"Appels par utilisateur: {callsPerUser}");
            Console.WriteLine($"URL du service: {client.Endpoint.Address.Uri}");
            Console.WriteLine("-------------------------------\n");

            await testeur.AppelerToutesLesMethodes(callsPerUser, virtualUsers);

            try
            {
                if (client.State == CommunicationState.Faulted)
                {
                    client.Abort();
                }
                else
                {
                    client.Close();
                }
            }
            catch (CommunicationException)
            {
                client.Abort();
            }
            catch (TimeoutException)
            {
                client.Abort();
            }
            catch (Exception)
            {
                client.Abort();
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erreur WCF : " + ex.Message);
            if (ex.InnerException != null)
            {
                Console.WriteLine("Détail : " + ex.InnerException.Message);
            }
        }
        Console.WriteLine("\nTests terminés.");

        running = false;
        DetectAndSavePeakHour();

        PerformanceMonitor.Stop();
        processingThread.Join();

        Console.WriteLine("Appuyez sur ENTER pour quitter...");
        Console.ReadLine();
    }

    private static void StartHttpServer(string prefix)
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        listener.BeginGetContext(new AsyncCallback(ListenerCallback), listener);
    }

    private static void ListenerCallback(IAsyncResult result)
    {
        var listener = (HttpListener)result.AsyncState;

        try
        {
            HttpListenerContext context = listener.EndGetContext(result);
            listener.BeginGetContext(new AsyncCallback(ListenerCallback), listener);

            if (context.Request.HttpMethod == "POST")
            {
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    string json = reader.ReadToEnd();
                    var logEntry = JsonConvert.DeserializeObject<LogEntry>(json);

                    if (logEntry != null)
                    {
                        logEntry.Protocol = context.Request.Url.Scheme;

                        if (logEntry.Code == 2)
                        {
                            if (string.IsNullOrEmpty(logEntry.Exception))
                                logEntry.Exception = logEntry.Message;

                            logEntry.Message = "Probleme technique";
                        }

                        logQueue.Enqueue(logEntry);
                    }
                }
                context.Response.StatusCode = 200;
                byte[] buffer = Encoding.UTF8.GetBytes("Log reçu");
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
            }
            else
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur serveur : {ex.Message}");
        }
    }

    private static void ProcessLogs()
    {
        while (running)
        {
            while (logQueue.TryDequeue(out LogEntry log))
            {
                totalCalls++;

                if (!methodCallCount.ContainsKey(log.Methode))
                    methodCallCount[log.Methode] = 0;

                methodCallCount[log.Methode]++;

                log.MachineName = Environment.MachineName;
                log.ThreadCount = GetThreadCount();

                string displayMessage = log.Message;
                if (log.Code == 1 && (string.IsNullOrEmpty(displayMessage) || displayMessage == "Succès"))
                {
                    displayMessage = "Opération réussie";
                }

                PerformanceMonitor.WriteToConsole(() =>
                {
                    Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] +Requête #{totalCalls}");
                    Console.WriteLine($"-Méthode              : {log.Methode}");
                    Console.WriteLine($"-Code                 : {log.Code}");
                    Console.WriteLine($"-Message              : {displayMessage}");
                    Console.WriteLine($"-Temps d'exécution    : {log.ExecutionTimeMs} ms");
                    Console.WriteLine($"-Latence réseau       : {log.NetworkLatencyMs} ms");
                    Console.WriteLine($"-Utilisateurs virtuels: {log.VirtualUsers}");
                    Console.WriteLine($"-Machine              : {log.MachineName}");
                    Console.WriteLine($"-Threads actifs       : {log.ThreadCount}");
                    Console.WriteLine($"-Lignes retournées    : {log.ReturnedLines}");
                    Console.WriteLine($"-Protocole            : {log.Protocol}");
                    Console.WriteLine($"-Total appels méthode : {methodCallCount[log.Methode]}");

                    if (log.Code == 2)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(" Problème technique détecté !");
                        Console.WriteLine($"+ Détail : {log.Exception}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("Aucun problème technique.");
                        Console.ResetColor();
                    }
                });

                try
                {
                    using (SqlConnection conn = new SqlConnection(connectionString))
                    {
                        conn.Open();
                        string insertQuery = @"
                            INSERT INTO Logs 
                            (Methode, Code, Message, ExecutionTimeMs, NetworkLatencyMs, VirtualUsers, Exception, MachineName, Protocol, ThreadCount, ReturnedLines)
                            VALUES 
                            (@Methode, @Code, @Message, @ExecutionTimeMs, @NetworkLatencyMs, @VirtualUsers, @Exception, @MachineName, @Protocol, @ThreadCount, @ReturnedLines)";

                        using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
                        {
                            cmd.Parameters.AddWithValue("@Methode", log.Methode ?? "N/A");
                            cmd.Parameters.AddWithValue("@Code", log.Code);
                            cmd.Parameters.AddWithValue("@Message", displayMessage ?? "N/A");
                            cmd.Parameters.AddWithValue("@ExecutionTimeMs", log.ExecutionTimeMs);
                            cmd.Parameters.AddWithValue("@NetworkLatencyMs", log.NetworkLatencyMs);
                            cmd.Parameters.AddWithValue("@VirtualUsers", log.VirtualUsers);
                            cmd.Parameters.AddWithValue("@Exception", log.Exception ?? "");
                            cmd.Parameters.AddWithValue("@MachineName", log.MachineName ?? "N/A");
                            cmd.Parameters.AddWithValue("@Protocol", log.Protocol ?? "N/A");
                            cmd.Parameters.AddWithValue("@ThreadCount", log.ThreadCount);
                            cmd.Parameters.AddWithValue("@ReturnedLines", log.ReturnedLines);

                            cmd.ExecuteNonQuery();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Erreur BDD : " + ex.Message);
                }
            }
            Thread.Sleep(100);
        }
    }

    private static int GetThreadCount()
    {
        try
        {
            return Process.GetCurrentProcess().Threads.Count;
        }
        catch
        {
            return -1;
        }
    }
    private static void DetectAndSavePeakHour()
    {
        try
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string query = @"
                SELECT TOP 1 DATEPART(HOUR, Timestamp) AS HourOfDay, COUNT(*) AS Total
                FROM Logs
                WHERE CAST(Timestamp AS DATE) = CAST(GETDATE() AS DATE)
                GROUP BY DATEPART(HOUR, Timestamp)
                ORDER BY Total DESC";

                using (SqlCommand cmd = new SqlCommand(query, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        int heurePointe = reader.GetInt32(0);
                        int totalLogs = reader.GetInt32(1);

                        reader.Close();

                        string insertQuery = @"
                        INSERT INTO PeakHours (DateAnalyse, HeurePointe, TotalLogs)
                        VALUES (@DateAnalyse, @HeurePointe, @TotalLogs)";

                        using (SqlCommand insertCmd = new SqlCommand(insertQuery, conn))
                        {
                            insertCmd.Parameters.AddWithValue("@DateAnalyse", DateTime.Today);
                            insertCmd.Parameters.AddWithValue("@HeurePointe", heurePointe);
                            insertCmd.Parameters.AddWithValue("@TotalLogs", totalLogs);

                            int rowsAffected = insertCmd.ExecuteNonQuery();

                            if (rowsAffected > 0)
                                Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"\n Heure de pointe enregistrée : {heurePointe}h avec {totalLogs} logs.");
                            Console.ResetColor();
                        }
                    }
                    else
                    {
                        Console.WriteLine("Aucune donnée de log pour aujourd'hui.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erreur lors de la détection de l'heure de pointe : " + ex.Message);
        }
    }
}