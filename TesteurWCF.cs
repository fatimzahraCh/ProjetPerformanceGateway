using LoggerConsoleApp.UserServiceReference;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


//ce fichier est un testeur pour les services WCF, il permet d'appeler toutes les méthodes d'un service WCF et de mesurer les performances
//ainsi qu'il mesure les performances 
public class TesteurWCF
{
    private readonly object clientProxy;
    private static readonly HttpClient httpClient = new HttpClient();
    private static readonly string loggerUrl = "http://localhost:5000/log/";
    private static readonly Random random = new Random();

    private static string connectionString;

    public TesteurWCF(object proxy)
    {
        clientProxy = proxy;
        InitializeConfiguration();
    }

    private void InitializeConfiguration()
    {
        try
        {
            connectionString = ConfigurationManager.ConnectionStrings["DefaultConnection"]?.ConnectionString;
        }
        catch
        {
            connectionString = null;
        }

        if (string.IsNullOrEmpty(connectionString))
        {
            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine(" Aucune connection string trouvée dans app.config");
                Console.WriteLine("Les données seront par défaut (pas de vraies données depuis BDD)");
            });
        }
        else
        {
            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine("Connection string détectée - utilisation des vraies données BDD");
            });
        }
    }

    public async Task AppelerToutesLesMethodes(int nombreAppels = 1, int utilisateursVirtuels = 1)
    {
        var methods = GetServiceMethods();

        PerformanceMonitor.WriteToConsole(() =>
        {
            Console.WriteLine($"Méthodes trouvées : {methods.Count}");
            Console.WriteLine("Méthodes détectées:");
            foreach (var method in methods)
            {
                Console.WriteLine($"  - {method.Name}");
            }
        });

        var tasks = new List<Task>();

        for (int user = 1; user <= utilisateursVirtuels; user++)
        {
            int currentUser = user;

            var task = Task.Run(async () =>
            {
                for (int i = 0; i < nombreAppels; i++)
                {
                    PerformanceMonitor.WriteToConsole(() =>
                    {
                        Console.WriteLine($"\n---UTILISATEUR #{currentUser} - SÉRIE D'APPELS #{i + 1}---");
                    });

                    foreach (var method in methods)
                    {
                        await AppelerMethodeAvecLogging(method, currentUser);
                        await Task.Delay(random.Next(500, 1500));
                    }
                }
            });

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        PerformanceMonitor.WriteToConsole(() =>
        {
            Console.WriteLine($"\n=== TOUS LES {utilisateursVirtuels} UTILISATEURS ONT TERMINÉ ===");
        });
    }
    private List<MethodInfo> GetServiceMethods()
    {
        var allMethods = clientProxy.GetType()
                           .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                           .ToList();

        var serviceMethods = allMethods
            .Where(m => IsServiceMethod(m))
            .ToList();

        return serviceMethods;
    }

    private bool IsServiceMethod(MethodInfo method)
    {
        if (method.IsSpecialName) return false;
        if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")) return false;
        if (method.DeclaringType == typeof(object)) return false;

        string[] excludedMethods = {
            "GetHashCode", "ToString", "Equals", "GetType",
            "Close", "Abort", "Open", "CreateChannel", "DisplayInitializationUI",
            "InnerChannel", "ChannelFactory", "ClientCredentials", "Endpoint",
            "State", "BeginDisplayInitializationUI", "EndDisplayInitializationUI"
        };

        if (excludedMethods.Contains(method.Name)) return false;

        if (method.Name.Contains("Async")) return false;
        if (method.Name.StartsWith("Begin") || method.Name.StartsWith("End")) return false;

        if (method.DeclaringType?.Name.Contains("ClientBase") == true) return false;
        if (method.DeclaringType?.Name.Contains("ServiceContract") == true) return false;

        var interfaces = clientProxy.GetType().GetInterfaces();
        foreach (var iface in interfaces)
        {
            if (iface.Name.Contains("Service") || iface.GetCustomAttributes().Any(attr => attr.GetType().Name.Contains("ServiceContract")))
            {
                var interfaceMethod = iface.GetMethod(method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
                if (interfaceMethod != null)
                {
                    return true;
                }
            }
        }
        return !method.DeclaringType?.Assembly.GlobalAssemblyCache == true &&
               method.DeclaringType?.Namespace?.StartsWith("System") != true &&
               method.DeclaringType?.Namespace?.StartsWith("Microsoft") != true;
    }
    private async Task AppelerMethodeAvecLogging(MethodInfo method, int numeroUtilisateur)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var networkStopwatch = new Stopwatch();

        string exception = null;
        int code = 1;
        string message = "Succès";
        object result = null;

        PerformanceMonitor.WriteToConsole(() =>
        {
            Console.WriteLine($"\n==> [User #{numeroUtilisateur}] Appel de la méthode: {method.Name}");
        });

        try
        {
            var parameters = method.GetParameters();
            object[] paramValues = parameters.Select(p => GetDefaultValue(p.ParameterType)).ToArray();

            PerformanceMonitor.WriteToConsole(() =>
            {
                if (paramValues.Length > 0)
                {
                    Console.WriteLine($"[User #{numeroUtilisateur}] Paramètres utilisés: {string.Join(", ", paramValues.Select(p => p?.ToString() ?? "null"))}");
                }
                else
                {
                    Console.WriteLine($"[User #{numeroUtilisateur}] Paramètres utilisés: aucun");
                }
            });

            networkStopwatch.Start();
            result = method.Invoke(clientProxy, paramValues);
            networkStopwatch.Stop();

            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"[User #{numeroUtilisateur}] Résultat: {result}");
            });

            if (result != null)
            {
                var responseMessage = ExtractMessageFromUserResponse(result);
                if (!string.IsNullOrEmpty(responseMessage))
                {
                    message = responseMessage;
                }

                var responseCode = ExtractCodeFromUserResponse(result);
                if (responseCode.HasValue)
                {
                    code = responseCode.Value;
                }
            }
            else
            {
                message = "Opération réussie";
            }
        }
        catch (TargetInvocationException ex)
        {
            networkStopwatch.Stop();
            code = 2;
            message = "Erreur";
            exception = ex.InnerException?.Message ?? ex.Message;

            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"[User #{numeroUtilisateur}] Erreur: {exception}");
            });
        }
        catch (Exception ex)
        {
            networkStopwatch.Stop();
            code = 2;
            message = "Erreur";
            exception = ex.Message;

            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"[User #{numeroUtilisateur}] Erreur: {ex.Message}");
            });
        }
        finally
        {
            totalStopwatch.Stop();
        }

        var logEntry = new
        {
            Methode = method.Name,
            Code = code,
            Message = message,
            ExecutionTimeMs = (long)totalStopwatch.ElapsedMilliseconds,
            NetworkLatencyMs = (long)networkStopwatch.ElapsedMilliseconds,
            VirtualUsers = (int)numeroUtilisateur,
            Exception = exception ?? "",
            ReturnedLines = CalculateReturnedLines(result)
        };

        await EnvoyerLogAsync(logEntry);
    }

    private int CalculateReturnedLines(object result)
    {
        if (result == null) return 0;
        return 1;
    }

    private string ExtractMessageFromUserResponse(object response)
    {
        try
        {
            var headProperty = response.GetType().GetProperty("Head");
            if (headProperty != null)
            {
                var head = headProperty.GetValue(response);
                if (head != null)
                {
                    var messageProperty = head.GetType().GetProperty("Message");
                    if (messageProperty != null)
                    {
                        return messageProperty.GetValue(head)?.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"Erreur extraction message: {ex.Message}");
            });
        }
        return null;
    }

    private int? ExtractCodeFromUserResponse(object response)
    {
        try
        {
            var headProperty = response.GetType().GetProperty("Head");
            if (headProperty != null)
            {
                var head = headProperty.GetValue(response);
                if (head != null)
                {
                    var codeProperty = head.GetType().GetProperty("Code");
                    if (codeProperty != null)
                    {
                        var codeValue = codeProperty.GetValue(head);
                        if (codeValue != null && int.TryParse(codeValue.ToString(), out int code))
                        {
                            return code == 0 ? 1 : 2;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"Erreur extraction code: {ex.Message}");
            });
        }
        return null;
    }

    private async Task EnvoyerLogAsync(object logEntry)
    {
        try
        {
            var json = JsonConvert.SerializeObject(logEntry);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(loggerUrl, content);

            PerformanceMonitor.WriteToConsole(() =>
            {
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Log envoyé avec succès");
                }
                else
                {
                    Console.WriteLine($"Erreur envoi log: {response.StatusCode}");
                }
            });
        }
        catch (Exception ex)
        {
            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"Erreur lors de l'envoi du log: {ex.Message}");
            });
        }
    }

    private object GetDefaultValue(Type t)
    {
        if (t.Name == "UserRequest")
        {
            return CreateUserRequest(t);
        }

        try
        {
            return Activator.CreateInstance(t);
        }
        catch
        {
            return null;
        }
    }

    private object CreateUserRequest(Type userRequestType)
    {
        try
        {
            var userRequest = Activator.CreateInstance(userRequestType);

            var headProperty = userRequestType.GetProperty("Head");
            if (headProperty != null)
            {
                var headType = headProperty.PropertyType;
                var head = Activator.CreateInstance(headType);
                headProperty.SetValue(userRequest, head);
            }

            var bodyProperty = userRequestType.GetProperty("Body");
            if (bodyProperty != null)
            {
                var bodyType = bodyProperty.PropertyType;
                var body = Activator.CreateInstance(bodyType);

                var mainEntityProperty = FindMainEntityProperty(bodyType);
                if (mainEntityProperty != null)
                {
                    var entityType = mainEntityProperty.PropertyType;
                    var entity = Activator.CreateInstance(entityType);

                    FillEntityWithUsersData(entity);

                    mainEntityProperty.SetValue(body, entity);
                }

                bodyProperty.SetValue(userRequest, body);
            }

            return userRequest;
        }
        catch (Exception ex)
        {
            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"Erreur création Request: {ex.Message}");
            });
            return Activator.CreateInstance(userRequestType);
        }
    }

    private PropertyInfo FindMainEntityProperty(Type bodyType)
    {
        var properties = bodyType.GetProperties();

        var userProp = properties.FirstOrDefault(p => p.Name.Contains("User"));
        if (userProp != null) return userProp;

        return properties.FirstOrDefault(p => !IsSimpleType(p.PropertyType));
    }

    private void FillEntityWithUsersData(object entity)
    {
        var properties = entity.GetType().GetProperties();

        foreach (var prop in properties)
        {
            if (!prop.CanWrite) continue;

            try
            {
                var value = GetValueFromUsers(prop.Name, prop.PropertyType);
                if (value != null)
                {
                    prop.SetValue(entity, value);
                }
            }
            catch
            {
            
            }
        }
    }

    private object GetValueFromUsers(string propertyName, Type propertyType)
    {
        var lowerName = propertyName.ToLower();

        if (lowerName.Contains("id") && propertyType == typeof(int))
        {
            var realId = GetRealIdFromUsers(propertyName);
            if (realId > 0)
            {
                PerformanceMonitor.WriteToConsole(() =>
                {
                    Console.WriteLine($" ID réel récupéré: {propertyName} = {realId}");
                });
                return realId;
            }

            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"Aucun ID trouvé en BDD pour {propertyName}, utilisation de 0");
            });
            return 0;
        }

        if (propertyType == typeof(string))
        {
            var realValue = GetRealStringFromUsers(propertyName);
            if (!string.IsNullOrEmpty(realValue))
            {
                PerformanceMonitor.WriteToConsole(() =>
                {
                    Console.WriteLine($"Valeur réelle récupérée: {propertyName} = {realValue}");
                });
                return realValue;
            }

            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"Aucune valeur trouvée en BDD pour {propertyName}, utilisation de NULL");
            });
            return null;
        }

        if (propertyType == typeof(DateTime) || propertyType == typeof(DateTime?))
        {
            return propertyType == typeof(DateTime) ? DateTime.Now : (DateTime?)null;
        }

        if (propertyType == typeof(bool) || propertyType == typeof(bool?))
        {
            return propertyType == typeof(bool) ? false : (bool?)null;
        }

        if (propertyType == typeof(decimal))
        {
            return 0m;
        }

        if (propertyType == typeof(int) && !lowerName.Contains("id"))
        {
            return 0;
        }

        try
        {
            return Activator.CreateInstance(propertyType);
        }
        catch
        {
            return null;
        }
    }
    private int GetRealIdFromUsers(string propertyName)
    {
        if (string.IsNullOrEmpty(connectionString)) return 0;

        try
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var query = $"SELECT TOP 1 {propertyName} FROM Users WHERE {propertyName} IS NOT NULL ORDER BY NEWID()";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandTimeout = 3;
                    var result = cmd.ExecuteScalar();
                    if (result != null && int.TryParse(result.ToString(), out int id))
                    {
                        return id;
                    }
                }
            }
        }
        catch
        {
          
        }

        return 0;
    }

    private string GetRealStringFromUsers(string propertyName)
    {
        if (string.IsNullOrEmpty(connectionString)) return null;

        try
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                var query = $"SELECT TOP 1 {propertyName} FROM Users WHERE {propertyName} IS NOT NULL ORDER BY NEWID()";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandTimeout = 3;
                    var result = cmd.ExecuteScalar();
                    if (result != null && !string.IsNullOrEmpty(result.ToString()))
                    {
                        return result.ToString();
                    }
                }
            }
        }
        catch
        {
          
        }

        return null;
    }

    private bool IsSimpleType(Type type)
    {
        return type.IsPrimitive ||
               type == typeof(string) ||
               type == typeof(DateTime) ||
               type == typeof(decimal) ||
               type == typeof(Guid) ||
               type.IsEnum;
    }

    public async Task TesterMethodeSpecifique(string nomMethode, int nombreAppels = 10, int utilisateursVirtuels = 1)
    {
        var methods = GetServiceMethods();
        var method = methods.FirstOrDefault(m => m.Name.Equals(nomMethode, StringComparison.OrdinalIgnoreCase));

        if (method == null)
        {
            PerformanceMonitor.WriteToConsole(() =>
            {
                Console.WriteLine($"Méthode '{nomMethode}' non trouvée");
                Console.WriteLine("Méthodes disponibles:");
                foreach (var m in methods)
                {
                    Console.WriteLine($"  - {m.Name}");
                }
            });
            return;
        }

        PerformanceMonitor.WriteToConsole(() =>
        {
            Console.WriteLine($"\n=== TEST INTENSIF DE LA MÉTHODE: {method.Name} ===");
            Console.WriteLine($"Nombre d'appels: {nombreAppels}");
            Console.WriteLine($"Utilisateurs virtuels: {utilisateursVirtuels}");
        });

        var tasks = new List<Task>();

        for (int user = 1; user <= utilisateursVirtuels; user++)
        {
            int currentUser = user;

            var task = Task.Run(async () =>
            {
                for (int i = 0; i < nombreAppels; i++)
                {
                    PerformanceMonitor.WriteToConsole(() =>
                    {
                        Console.WriteLine($"\n[User #{currentUser}] Appel #{i + 1}");
                    });
                    await AppelerMethodeAvecLogging(method, currentUser);
                    await Task.Delay(random.Next(100, 500));
                }
            });

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        PerformanceMonitor.WriteToConsole(() =>
        {
            Console.WriteLine($"\n=== TEST TERMINÉ POUR TOUS LES {utilisateursVirtuels} UTILISATEURS ===");
        });
    }
}